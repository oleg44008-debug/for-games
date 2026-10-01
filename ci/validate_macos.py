#!/usr/bin/env python3
"""Validate the actual PODIEZD package, then launch its signed copy headlessly on macOS."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import plistlib
import re
import signal
import stat
import struct
import subprocess
import sys
import tempfile
import traceback
import zipfile

EXPECTED_PAYLOAD = "9b313199d2383715db7a0fb1c9bdee8ddeff67b9f21ddaee84ce7d9a56faee6b"
EXPECTED_ARCHIVE = "e53f092545e0f15c01b10765b79dce7e9c5f9175c7e7ad57830849811fb4b75a"
ENGINE_VERSION = "4.7.1"
ARCHITECTURES = {0x01000007: "x86_64", 0x0100000C: "arm64"}


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def ensure(condition, message):
    if not condition:
        raise ValueError(message)


def validate_entry(entry):
    # ZipInfo may normalize backslashes on Windows or truncate a NUL. Validate the
    # original central-directory spelling before trusting its normalized filename.
    name = entry.orig_filename
    path = PurePosixPath(name)
    ensure(name and "\\" not in name and "\0" not in name and not path.is_absolute(), "Unsafe ZIP path")
    ensure(not re.match(r"^[A-Za-z]:", name) and all(part not in ("", ".", "..") for part in name.rstrip("/").split("/")), "Unsafe ZIP component")
    ensure(not stat.S_ISLNK(entry.external_attr >> 16), "Simple Godot CI package must not contain symbolic links")
    ensure(entry.file_size >= 0 and entry.file_size <= 512 * 1024 * 1024, "ZIP entry exceeds bounded validation budget")


def universal_architectures(header):
    ensure(len(header) >= 8, "Truncated Mach-O header")
    magic, count = struct.unpack_from(">II", header)
    ensure(magic in (0xCAFEBABE, 0xCAFEBABF), "Expected a big-endian universal Mach-O executable")
    size = 32 if magic == 0xCAFEBABF else 20
    ensure(1 <= count <= 8 and len(header) >= 8 + count * size, "Invalid universal Mach-O architecture table")
    result = []
    for index in range(count):
        cpu = struct.unpack_from(">I", header, 8 + index * size)[0]
        ensure(cpu in ARCHITECTURES, "Unexpected universal Mach-O architecture")
        result.append(ARCHITECTURES[cpu])
    ensure(len(set(result)) == len(result), "Duplicate Mach-O architecture")
    return result


def validate_archive(archive, expected_payload=EXPECTED_PAYLOAD):
    with zipfile.ZipFile(archive) as package:
        entries = package.infolist()
        ensure(0 < len(entries) <= 10000, "ZIP entry count outside bounded budget")
        names = set()
        total = 0
        for entry in entries:
            validate_entry(entry)
            normalized = entry.filename.rstrip("/").casefold()
            ensure(normalized not in names, "Duplicate ZIP destination")
            names.add(normalized)
            total += entry.file_size
        ensure(total <= 1024 * 1024 * 1024, "ZIP expansion exceeds 1 GiB validation budget")
        roots = {name.split("/", 1)[0] for name in package.namelist() if ".app/Contents/" in name}
        ensure(len(roots) == 1, "Expected exactly one application bundle")
        bundle = roots.pop()
        ensure(bundle.endswith(".app"), "Application root must end in .app")
        plist_name = bundle + "/Contents/Info.plist"
        info = plistlib.loads(package.read(plist_name))
        executable = info.get("CFBundleExecutable")
        ensure(isinstance(executable, str) and executable not in ("", ".", "..") and "/" not in executable and "\\" not in executable,
               "Unsafe CFBundleExecutable")
        executable_name = bundle + "/Contents/MacOS/" + executable
        executable_entry = package.getinfo(executable_name)
        ensure(executable_entry.create_system == 3 and (executable_entry.external_attr >> 16) & 0o111,
               "ZIP lost Unix executable permissions")
        with package.open(executable_entry) as binary:
            architectures = universal_architectures(binary.read(8 + 8 * 32))
        ensure(set(architectures) == {"arm64", "x86_64"}, "PODIEZD runtime must remain universal arm64 + x86_64")
        pcks = [entry for entry in entries if entry.filename.lower().endswith(".pck")]
        ensure(len(pcks) == 1, "Expected exactly one original Godot PCK")
        payload_name = bundle + "/Contents/Resources/" + executable + ".pck"
        ensure(pcks[0].filename == payload_name, "PCK must match the bundle executable name")
        with package.open(pcks[0]) as payload:
            payload_sha = hashlib.file_digest(payload, "sha256").hexdigest()
        ensure(payload_sha == expected_payload, "Original PODIEZD payload SHA-256 changed")
        with package.open(pcks[0]) as payload:
            header = payload.read(24)
        ensure(len(header) == 24, "Truncated Godot PCK header")
        magic, pack_format, major, minor, patch, flags = struct.unpack("<6I", header)
        ensure(magic == 0x43504447 and pack_format == 4 and flags & 1 == 0, "Expected the unencrypted original PCK format 4")
        ensure(f"{major}.{minor}.{patch}" == ENGINE_VERSION, "Godot engine version changed")
        manifest = json.loads(package.read("package-manifest.json"))
        ensure(manifest.get("engine") == "Godot" and manifest.get("engineVersion") == ENGINE_VERSION,
               "Package manifest has the wrong engine/version")
        ensure(manifest.get("payloadSha256") == expected_payload, "Manifest payload hash differs from actual bytes")
        return {"bundle": bundle, "executable": executable, "executableEntry": executable_name,
                "payloadEntry": payload_name, "payloadSha256": payload_sha, "payloadPreserved": True,
                "engineVersion": ENGINE_VERSION, "packFormat": pack_format, "architectures": architectures,
                "unixExecutableMode": oct((executable_entry.external_attr >> 16) & 0o777),
                "bundleIdentifier": info.get("CFBundleIdentifier"), "entryCount": len(entries)}


def fingerprint(directory):
    result = {}
    for path in sorted(Path(directory).rglob("*")):
        ensure(not path.is_symlink(), "Unexpected extracted application symlink")
        if path.is_file():
            result[str(path.relative_to(directory))] = {"sha256": digest(path), "mode": stat.S_IMODE(path.stat().st_mode)}
    return result


def run_process(command, name, artifacts, timeout=60, environment=None, cwd=None):
    """No shell; timeout terminates only this owned process and its process group."""
    started = datetime.now(timezone.utc).isoformat()
    process = subprocess.Popen([str(value) for value in command], cwd=cwd, env=environment,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               start_new_session=platform.system() != "Windows")
    timed_out = False
    try:
        stdout, stderr = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        if platform.system() == "Windows":
            process.kill()
        else:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
        stdout, stderr = process.communicate()
    (artifacts / (name + ".stdout.log")).write_bytes(stdout)
    (artifacts / (name + ".stderr.log")).write_bytes(stderr)
    text_out, text_err = stdout.decode("utf-8", "replace"), stderr.decode("utf-8", "replace")
    result = {"startedAtUtc": started, "command": [str(value) for value in command],
              "exitCode": process.returncode, "timedOut": timed_out,
              "stdoutLog": name + ".stdout.log", "stderrLog": name + ".stderr.log"}
    print(text_out, end="" if text_out.endswith("\n") else "\n", flush=True)
    print(text_err, end="" if text_err.endswith("\n") else "\n", file=sys.stderr, flush=True)
    return result, text_out, text_err


def successful(result, stage):
    ensure(not result["timedOut"] and result["exitCode"] == 0, stage + " failed; inspect retained stdout/stderr logs")


def validate_reverse_windows(args, root, signed_app, work, artifacts, expected_payload):
    runtime = args.windows_runtime.resolve()
    ensure(runtime.is_file(), "Offline matching Windows runtime ZIP is required for reverse packaging")
    provenance = json.loads(runtime.with_suffix(".provenance.json").read_text(encoding="utf-8"))
    ensure(digest(runtime) == provenance["runtimeZipSha256"], "Offline Windows runtime ZIP changed")
    ensure(provenance.get("gamePayloadIncluded") is False, "Offline runtime must not contain a game payload")
    output = work / "PODIEZD-mac-to-windows.zip"
    project = root / "tests/CoreChecks/Godot/Godot.CoreChecks.csproj"
    command = [args.dotnet, "run", "--project", project, "-c", "Release", "--no-build", "--",
               "--package", signed_app, runtime, output, "windows", "PODIEZD", ENGINE_VERSION]
    result, _, _ = run_process(command, "reverse-windows-package", artifacts, timeout=180, cwd=root)
    successful(result, "Mac-to-Windows Core packaging")
    with zipfile.ZipFile(output) as package:
        packs = [entry for entry in package.infolist() if entry.filename.lower().endswith(".pck")]
        ensure(len(packs) == 1, "Reverse Windows package lost the single main PCK")
        with package.open(packs[0]) as stream:
            payload_sha = hashlib.file_digest(stream, "sha256").hexdigest()
        ensure(payload_sha == expected_payload, "Mac-to-Windows packaging changed PODIEZD data")
        binaries = [entry for entry in package.infolist() if entry.filename.lower().endswith(".exe")]
        ensure(len(binaries) == 1, "Reverse Windows package needs exactly one executable")
        with package.open(binaries[0]) as stream:
            executable = stream.read()
        ensure(executable[:2] == b"MZ" and len(executable) >= 64, "Reverse executable is not Windows PE")
        pe = struct.unpack_from("<I", executable, 60)[0]
        ensure(len(executable) >= pe + 6 and executable[pe:pe + 4] == b"PE\0\0"
               and struct.unpack_from("<H", executable, pe + 4)[0] == 0x8664, "Reverse executable is not x86_64 PE")
        ensure(hashlib.sha256(executable).hexdigest() == provenance["executableSha256"], "Reverse package changed the matching official runtime executable")
    return {"status": "Pass", "source": "Actual macOS game bundle payload",
            "output": str(output), "outputSha256": digest(output), "payloadSha256": payload_sha,
            "payloadPreserved": True, "windowsArchitecture": "x86_64", "windowsExecutionTested": False,
            "process": result, "runtimeZipSha256": provenance["runtimeZipSha256"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, default=Path("ci/game/PODIEZD-macOS.zip"))
    parser.add_argument("--artifacts", type=Path, default=Path("ci/artifacts/macos"))
    parser.add_argument("--expected-archive-sha256", default=EXPECTED_ARCHIVE)
    parser.add_argument("--expected-payload-sha256", default=EXPECTED_PAYLOAD)
    parser.add_argument("--expected-arch", choices=("arm64", "x86_64"))
    parser.add_argument("--structure-only", action="store_true", help="Validate archive bytes only; never sign or launch a game")
    parser.add_argument("--windows-runtime", type=Path, default=Path("ci/game/godot-4.7.1-windows-runtime.zip"))
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    artifacts = args.artifacts.resolve()
    artifacts.mkdir(parents=True, exist_ok=True)
    archive = args.archive.resolve()
    report = {"schemaVersion": 1, "testedAtUtc": datetime.now(timezone.utc).isoformat(),
              "hostSystem": platform.system(), "hostArchitecture": platform.machine(),
              "expectedRunnerArchitecture": args.expected_arch, "status": "Fail",
              "mode": "StructureOnly" if args.structure_only else "MacOSHeadless",
              "scope": "Actual game package and pure Core; Windows WinForms launcher is not built or tested on Mac",
              "archive": str(archive), "macLaunchTested": False, "interactiveGameplayTested": False,
              "launcherGuiTested": False, "notarizationTested": False, "gatekeeperAcceptanceTested": False,
              "reverseMacToWindows": {"status": "NotRun", "windowsExecutionTested": False}, "processes": {}}
    source_fingerprint = None
    source_app = None
    initial_archive_sha = None
    try:
        initial_archive_sha = digest(archive)
        report["archiveSha256"] = initial_archive_sha
        ensure(initial_archive_sha == args.expected_archive_sha256.lower(), "CI input archive SHA-256 differs from the supplied PODIEZD build")
        package = validate_archive(archive, args.expected_payload_sha256.lower())
        report["package"] = package
        if args.structure_only:
            report["status"] = "Pass"
            report["limitations"] = "ZIP/PCK/permissions/universal headers only; macOS code signing, startup and reverse packaging not executed"
        else:
            ensure(platform.system() == "Darwin", "Headless execution validation requires a real macOS runner")
            if args.expected_arch:
                ensure(platform.machine() == args.expected_arch, "Runner did not provide the expected native CPU architecture")
            work = Path(tempfile.mkdtemp(prefix="owned-run-", dir=artifacts))
            extracted = work / "source"
            extracted.mkdir()
            extraction, _, _ = run_process(["/usr/bin/ditto", "-x", "-k", archive, extracted], "extract", artifacts)
            report["processes"]["extraction"] = extraction
            successful(extraction, "Native ZIP extraction")
            source_app = extracted / package["bundle"]
            source_binary = source_app / "Contents/MacOS" / package["executable"]
            ensure(source_binary.is_file() and os.access(source_binary, os.X_OK), "Native extraction lost executable permission")
            source_fingerprint = fingerprint(source_app)
            architectures, arch_text, _ = run_process(["/usr/bin/lipo", "-archs", source_binary], "architectures", artifacts)
            report["processes"]["architectures"] = architectures
            successful(architectures, "Native Mach-O architecture inspection")
            ensure(set(arch_text.split()) == {"arm64", "x86_64"}, "lipo did not confirm both universal runtime architectures")
            file_check, file_text, _ = run_process(["/usr/bin/file", source_binary], "executable-format", artifacts)
            report["processes"]["executableFormat"] = file_check
            successful(file_check, "Native executable format inspection")
            ensure("Mach-O" in file_text, "Native executable is not Mach-O")
            signer = root / "tools/macos-sign.command"
            lf_signer = work / "macos-sign.command"
            lf_signer.write_text(signer.read_text(encoding="utf-8-sig"), encoding="utf-8", newline="\n")
            signed_app = work / "PODIEZD-ci-signed.app"
            signing, _, _ = run_process(["/bin/bash", lf_signer, source_app, signed_app], "local-ad-hoc-signing", artifacts, timeout=90)
            report["processes"]["signing"] = signing
            successful(signing, "Ad-hoc signing of a new copy")
            report["adHocSignatureVerified"] = True
            signed_payload = signed_app / "Contents/Resources" / (package["executable"] + ".pck")
            ensure(digest(signed_payload) == args.expected_payload_sha256.lower(), "Signing copy changed game data")
            ensure(fingerprint(source_app) == source_fingerprint, "Signing unexpectedly changed the original extracted application")
            report["originalExtractedAppUnchanged"] = True
            user_data = work / "isolated-user-data"
            user_data.mkdir()
            environment = dict(os.environ, HOME=str(user_data), XDG_DATA_HOME=str(user_data / "data"),
                               XDG_CONFIG_HOME=str(user_data / "config"), XDG_STATE_HOME=str(user_data / "state"),
                               DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1")
            signed_binary = signed_app / "Contents/MacOS" / package["executable"]
            version, version_text, _ = run_process([signed_binary, "--headless", "--version"], "runtime-version", artifacts,
                                                 timeout=30, environment=environment, cwd=signed_binary.parent)
            report["processes"]["version"] = version
            successful(version, "Actual signed runtime version query")
            ensure(re.search(r"^4\.7\.1(?:\.|\s|$)", version_text, re.M), "Actual executable version is not Godot 4.7.1")
            engine_log = artifacts / "game-engine.log"
            report["macLaunchTested"] = True
            startup, stdout, stderr = run_process([signed_binary, "--headless", "--quit-after", "120", "--log-file", engine_log],
                                                 "game-startup", artifacts, timeout=90, environment=environment, cwd=signed_binary.parent)
            report["processes"]["gameStartup"] = startup
            successful(startup, "Actual PODIEZD headless startup")
            engine_text = engine_log.read_text(encoding="utf-8", errors="replace") if engine_log.exists() else ""
            errors = [line for line in (stdout + "\n" + stderr + "\n" + engine_text).splitlines()
                      if re.search(r"(?:^|\s)(?:ERROR:|SCRIPT ERROR:|FATAL:|dyld\[|dyld:)", line)]
            report["startupErrors"] = sorted(set(errors))
            ensure(not errors, "Headless startup reported engine/script errors; inspect retained logs")
            report["headlessStartupPassed"] = True
            report["reverseMacToWindows"] = {"status": "Running", "windowsExecutionTested": False}
            report["reverseMacToWindows"] = validate_reverse_windows(args, root, signed_app, work, artifacts, args.expected_payload_sha256.lower())
            report["status"] = "Pass"
            report["limitations"] = "Headless startup only; rendering, controls, full gameplay, Gatekeeper/notarization and launcher GUI were not tested"
    except Exception as error:
        report["error"] = str(error)
        if report["reverseMacToWindows"]["status"] == "Running":
            report["reverseMacToWindows"]["status"] = "Fail"
            report["reverseMacToWindows"]["error"] = str(error)
        (artifacts / "validation-error.log").write_text(traceback.format_exc(), encoding="utf-8")
        print("Validation failed: " + str(error), file=sys.stderr, flush=True)
    finally:
        if initial_archive_sha is not None:
            try:
                report["originalArchiveUnchanged"] = digest(archive) == initial_archive_sha
            except Exception as integrity_error:
                report["originalArchiveUnchanged"] = False
                report["archiveIntegrityReadError"] = str(integrity_error)
            if not report["originalArchiveUnchanged"]:
                report["status"] = "Fail"
                report["error"] = "Original input ZIP changed during validation"
        if source_fingerprint is not None and source_app is not None:
            try:
                report["originalExtractedAppUnchanged"] = fingerprint(source_app) == source_fingerprint
            except Exception:
                report["originalExtractedAppUnchanged"] = False
            if not report["originalExtractedAppUnchanged"]:
                report["status"] = "Fail"
                report["error"] = "Original extracted application changed during validation"
        (artifacts / "macos-validation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report, indent=2))
    return 0 if report["status"] == "Pass" else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main())
