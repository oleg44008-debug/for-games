#!/usr/bin/env python3
"""Validate only the launcher app in an owned temporary profile on a real Mac."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path, PurePosixPath
import platform
import plistlib
import signal
import stat
import struct
import subprocess
import time
import uuid
import zipfile

from build_macos import APP_NAME, ASSEMBLY_NAME, native_files, sha256, validate_bundle
from macho_minimums import audit_archive, version_tuple


def extract_owned_archive(archive: Path, destination: Path) -> Path:
    destination.mkdir(parents=True, exist_ok=False)
    with zipfile.ZipFile(archive) as source:
        entries = source.infolist()
        if len(entries) > 20_000 or sum(entry.file_size for entry in entries) > 1_500_000_000:
            raise ValueError("The launcher archive exceeds its extraction limits.")
        for entry in entries:
            name = entry.filename
            parts = PurePosixPath(name).parts
            if not parts or name.startswith("/") or "\\" in name or ".." in parts or ":" in parts[0]:
                raise ValueError("Unsafe archive member: " + name)
            if parts[0] == "__MACOSX":
                if len(parts) > 1 and parts[1] not in (APP_NAME, "._" + APP_NAME):
                    raise ValueError("Resource-fork metadata does not belong to the launcher app.")
                continue  # ditto resource-fork metadata is not executable app content.
            if parts[0] != APP_NAME:
                raise ValueError("The archive contains an unexpected application/root: " + name)
            target = destination.joinpath(*parts)
            if not target.resolve().is_relative_to(destination.resolve()):
                raise ValueError("The archive member escapes the owned extraction directory.")
            mode = (entry.external_attr >> 16) & 0xFFFF
            if entry.is_dir():
                target.mkdir(parents=True, exist_ok=True)
                target.chmod(0o755)
            elif stat.S_ISLNK(mode):
                link = source.read(entry).decode("utf-8")
                if Path(link).is_absolute() or not (target.parent / link).resolve().is_relative_to((destination / APP_NAME).resolve()):
                    raise ValueError("Bundle symlink escapes the app: " + name)
                target.parent.mkdir(parents=True, exist_ok=True)
                target.symlink_to(link)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                with source.open(entry) as input_stream, target.open("wb") as output_stream:
                    import shutil
                    shutil.copyfileobj(input_stream, output_stream)
                target.chmod(mode & 0o777 or 0o644)
    if platform.system() == "Darwin":
        # All member paths/symlinks have been checked above. Restore AppleDouble
        # metadata with the OS archiver too: generic-code signatures on managed
        # DLLs in the Avalonia MacOS layout live in xattrs, not DLL file bytes.
        subprocess.run(["ditto", "-x", "-k", "--rsrc", str(archive.resolve()), str(destination.resolve())],
                       check=True, capture_output=True)
    return destination / APP_NAME


def run_owned_process(command: list[str], cwd: Path, env: dict[str, str], log: Path, timeout: float) -> dict:
    start = time.monotonic()
    process = subprocess.Popen(command, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True, encoding="utf-8", errors="replace", start_new_session=True)
    try:
        output, _ = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        # This process group was created by this verifier; never kill by application name.
        if os.name == "posix":
            os.killpg(process.pid, signal.SIGTERM)
        else:
            process.terminate()
        try:
            output, _ = process.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            if os.name == "posix":
                os.killpg(process.pid, signal.SIGKILL)
            else:
                process.kill()
            output, _ = process.communicate()
        log.write_text(output, encoding="utf-8")
        raise TimeoutError("The owned launcher smoke process exceeded its deadline.")
    log.write_text(output, encoding="utf-8")
    if process.returncode != 0:
        raise RuntimeError(f"Launcher smoke exited {process.returncode}; see {log.name}.")
    return {"exitCode": process.returncode, "elapsedSeconds": round(time.monotonic() - start, 3), "log": log.name}


def inspect_dependencies(bundle: Path, rid: str) -> list[dict]:
    records = []
    natives = native_files(bundle)
    names = {path.name for path in natives}
    architecture = {"osx-arm64": "arm64", "osx-x64": "x86_64"}[rid]
    load_commands = {"LC_LOAD_DYLIB", "LC_LOAD_WEAK_DYLIB", "LC_REEXPORT_DYLIB", "LC_LOAD_UPWARD_DYLIB", "LC_LAZY_LOAD_DYLIB"}
    for library in natives:
        # Avalonia dependencies can be universal binaries; inspect the architecture
        # actually delivered instead of interpreting a second slice's header as a dependency.
        result = subprocess.run(["otool", "-arch", architecture, "-l", str(library)], check=True, capture_output=True, text=True)
        dependencies = []
        install_name = None
        command = ""
        for line in result.stdout.splitlines():
            field = line.strip()
            if field.startswith("cmd "):
                command = field[4:]
            if not field.startswith("name "):
                continue
            dependency = field[5:].split(" (offset ", 1)[0]
            if command == "LC_ID_DYLIB":
                # A dylib's own identification name is not a dependency loaded
                # from disk. AvaloniaNative retains its upstream build ID while
                # .NET loads the packaged dylib by its actual bundle path.
                install_name = dependency
                continue
            if command not in load_commands:
                continue
            dependencies.append(dependency)
            if dependency.startswith(("/usr/lib/", "/System/Library/")):
                continue
            if dependency.startswith("@loader_path/"):
                resolved = library.parent / dependency[len("@loader_path/"):]
                if not resolved.resolve().is_relative_to(bundle.resolve()) or not resolved.exists():
                    raise ValueError("Missing/outside loader dependency: " + dependency)
            elif dependency.startswith(("@rpath/", "@executable_path/")):
                if Path(dependency).name not in names:
                    raise ValueError("A native dependency is absent from the self-contained bundle: " + dependency)
            else:
                raise ValueError("The app depends on a developer-machine library: " + dependency)
        records.append({"path": str(library.relative_to(bundle)), "installName": install_name, "dependencies": dependencies})
    return records


def validate_service_report(report: dict, output: Path, require_input: bool) -> None:
    checks = report.get("report", {})
    if report.get("status") != "Pass" or checks.get("Success") is not True or report.get("originalLogoUnchanged") is not True:
        raise ValueError("The launcher did not report successful service checks and the original logo.")
    profile = Path(checks.get("ProfileDirectory", "")).resolve()
    if profile == output.resolve() or not profile.is_relative_to(output.resolve()) or not profile.is_dir():
        raise ValueError("The launcher smoke profile is not an owned verification subdirectory.")
    if require_input and (checks.get("SourcePreserved") is not True or not checks.get("PreparedMacAppPath")):
        raise ValueError("The source-game import was not verified in an owned managed app.")
    prepared = checks.get("PreparedMacAppPath")
    if prepared:
        path = Path(prepared).resolve()
        if not path.is_relative_to(profile) or not path.is_dir():
            raise ValueError("The prepared game is outside the owned smoke profile.")


def validate_ui_report(report: dict, require_input: bool) -> None:
    if report.get("status") != "Pass" or any(report.get(key) is not True for key in
            ("windowOpened", "viewModelLoaded", "originalLogoUnchanged")):
        raise ValueError("The native launcher window did not complete real UI initialization.")
    if report.get("clientWidth", 0) < 800 or report.get("clientHeight", 0) < 500:
        raise ValueError("The native launcher did not have a usable client viewport.")
    if require_input and (report.get("libraryEntryCount", 0) < 1 or report.get("exAnalysisReady") is not True):
        raise ValueError("The actual game was not imported and analyzed in the native library/eX UI.")


def inspect_rendered_image(image: Path, output: Path) -> dict:
    image = image.resolve()
    if not image.is_relative_to(output.resolve()):
        raise ValueError("The native UI image is outside the owned verification directory.")
    image_header = image.read_bytes()[:24]
    if len(image_header) != 24 or image_header[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("The native UI smoke did not produce a PNG client image.")
    width, height = struct.unpack(">II", image_header[16:24])
    if width < 800 or height < 500:
        raise ValueError("The initialized launcher UI client did not have a usable viewport.")
    return {"file": image.name, "width": width, "height": height, "sha256": sha256(image)}


def observe_normal_launch(bundle: Path, output: Path, version: str) -> dict:
    helper = output / "observe-launchservices"
    swift_source = Path(__file__).resolve().parent / "observe_launchservices.swift"
    compiled = subprocess.run(["xcrun", "swiftc", "-parse-as-library", "-O", "-framework", "AppKit", "-framework", "CoreGraphics",
                               str(swift_source), "-o", str(helper)], capture_output=True, text=True, timeout=120)
    (output / "launchservices-compile.log").write_text(compiled.stdout + compiled.stderr, encoding="utf-8")
    compiled.check_returncode()
    startup = Path.home() / "Library" / "Logs" / "DUSTORE Launcher V" / "last-startup.json"
    report_path = output / "launcher-normal-startup.json"
    env = os.environ.copy()
    for key in list(env):
        if key.startswith("DUSTOREV_") or key == "DOTNET_ROOT" or key.startswith("DOTNET_ROOT_"):
            env.pop(key, None)
    started = time.time()
    result = {}
    try:
        result = run_owned_process([str(helper), str(bundle), str(report_path), str(startup), version],
                                   output, env, output / "launcher-normal-startup.log", 90)
        observation = json.loads(report_path.read_text(encoding="utf-8"))
        if observation.get("status") != "passed" or observation.get("smokeBranch") is not False or len(observation.get("samples", [])) < 3:
            raise ValueError("Normal LaunchServices startup did not preserve a real window.")
        result["observation"] = observation
        return result
    finally:
        # Preserve the actual normal-startup report and crash evidence even when
        # the observer fails. Reports from unrelated installed apps are excluded.
        process_id = None
        if startup.is_file() and startup.stat().st_mtime >= started:
            try:
                diagnostic = json.loads(startup.read_text(encoding="utf-8"))
                actual_base = Path(diagnostic.get("applicationBaseDirectory", "")).resolve()
                expected_base = (bundle / "Contents" / "MacOS").resolve()
                if actual_base == expected_base:
                    (output / "normal-app-last-startup.json").write_text(json.dumps(diagnostic, indent=2), encoding="utf-8")
                    process_id = diagnostic.get("processId")
            except (ValueError, OSError):
                pass
        crashes = Path.home() / "Library" / "Logs" / "DiagnosticReports"
        if crashes.is_dir():
            for path in crashes.glob(ASSEMBLY_NAME + "*"):
                if not path.is_file() or path.stat().st_mtime < started or path.stat().st_size > 10_000_000:
                    continue
                text = path.read_text(encoding="utf-8", errors="replace")
                if str(bundle) in text or (process_id and (f'"pid" : {process_id}' in text or f'"pid":{process_id}' in text)):
                    (output / ("owned-crash-" + path.name)).write_text(text, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--rid", choices=("osx-arm64", "osx-x64"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--smoke-input", type=Path)
    parser.add_argument("--ui-smoke", action="store_true")
    parser.add_argument("--normal-launch", action="store_true", help="Require real LaunchServices open with default arguments/profile and a persistent native window.")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    record = {"status": "failed", "archive": str(args.archive.resolve()), "runtimeIdentifier": args.rid,
              "host": {"os": platform.system(), "architecture": platform.machine(), "version": platform.mac_ver()[0]},
              "scope": "Actual normal LaunchServices startup uses the default profile when requested; smoke checks use owned temporary profiles. No downloaded game is launched. UI image is the initialized Avalonia client rendered by the app, not a gameplay or live click test."}
    try:
        if platform.system() != "Darwin":
            raise RuntimeError("Native startup verification must run on macOS, not a Windows cross-build.")
        expected = {"osx-arm64": "arm64", "osx-x64": "x86_64"}[args.rid]
        if platform.machine() != expected:
            raise RuntimeError("Use a native runner matching the deliverable architecture.")
        record["archiveSha256"] = sha256(args.archive)
        record["nativeDeploymentTargets"] = audit_archive(args.archive, args.rid)
        if version_tuple(record["nativeDeploymentTargets"]["maximumNativeMinimumOS"]) > version_tuple(record["nativeDeploymentTargets"]["bundleMinimumOS"]):
            raise ValueError("The bundle declares support below its actual native deployment target.")
        bundle = extract_owned_archive(args.archive.resolve(), output / ("extracted-" + uuid.uuid4().hex))
        record["structure"] = validate_bundle(bundle, args.rid)
        executable = bundle / "Contents" / "MacOS" / ASSEMBLY_NAME
        if not os.access(executable, os.X_OK):
            raise ValueError("The archive did not preserve the launcher execute bit.")
        signature = subprocess.run(["codesign", "--verify", "--deep", "--strict", "--verbose=2", str(bundle)], capture_output=True, text=True)
        (output / "codesign.log").write_text(signature.stdout + signature.stderr, encoding="utf-8")
        if signature.returncode != 0:
            raise ValueError("The extracted app bundle has an invalid signature.")
        entitlements = subprocess.run(["codesign", "-d", "--entitlements", ":-", str(bundle)], check=True, capture_output=True)
        properties = plistlib.loads(entitlements.stdout)
        if properties.get("com.apple.security.cs.allow-jit") is not True:
            raise ValueError("The .NET/Avalonia apphost lacks its required JIT entitlement.")
        record["signing"] = {"verifiedAdHoc": True, "developerId": False, "notarized": False, "entitlements": properties}
        record["nativeDependencies"] = inspect_dependencies(bundle, args.rid)
        icon = bundle / "Contents" / "Resources" / "DustoreLauncherV.icns"
        subprocess.run(["iconutil", "-c", "iconset", str(icon), "-o", str(output / "validated-icon.iconset")], check=True, capture_output=True)
        record["nativeIconDecoded"] = True
        assessment = subprocess.run(["spctl", "--assess", "--type", "execute", "--verbose=4", str(bundle)], capture_output=True, text=True)
        (output / "gatekeeper-assessment.log").write_text(assessment.stdout + assessment.stderr, encoding="utf-8")
        record["gatekeeperAssessment"] = {"exitCode": assessment.returncode, "notarized": False,
                                         "scope": "Advisory assessment only. No policies or quarantine attributes are changed."}
        if args.normal_launch:
            record["normalStartup"] = observe_normal_launch(bundle, output, record["structure"]["metadata"]["CFBundleShortVersionString"])
        profile = output / ("profile-" + uuid.uuid4().hex)
        profile.mkdir()
        working = output / "foreign-working-directory"
        working.mkdir(exist_ok=True)
        env = os.environ.copy()
        for name in ("DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64"):
            env.pop(name, None)
        env["DUSTOREV_PROFILE_DIRECTORY"] = str(profile)
        env["DOTNET_MULTILEVEL_LOOKUP"] = "0"
        smoke_report = output / "launcher-smoke.json"
        command = [str(executable), "--smoke-test", "--smoke-report", str(smoke_report)]
        source_hash = sha256(args.smoke_input.resolve()) if args.smoke_input else None
        if args.smoke_input:
            command += ["--smoke-input", str(args.smoke_input.resolve())]
        record["headlessStartup"] = run_owned_process(command, working, env, output / "launcher-smoke.log", 90)
        record["headlessStartup"]["checks"] = json.loads(smoke_report.read_text(encoding="utf-8"))
        validate_service_report(record["headlessStartup"]["checks"], output, bool(args.smoke_input))
        prepared = record["headlessStartup"]["checks"]["report"].get("PreparedMacAppPath")
        if prepared:
            subprocess.run(["codesign", "--verify", "--deep", "--strict", str(prepared)], check=True, capture_output=True)
            record["headlessStartup"]["preparedGameSignatureVerified"] = True
        if args.ui_smoke:
            ui_report, image = output / "launcher-ui-smoke.json", output / "launcher-ui.png"
            command = [str(executable), "--ui-smoke", "--smoke-report", str(ui_report), "--smoke-screenshot", str(image)]
            if args.smoke_input:
                command += ["--smoke-input", str(args.smoke_input.resolve())]
            record["uiStartup"] = run_owned_process(command, working, env, output / "launcher-ui-smoke.log", 90)
            record["uiStartup"]["checks"] = json.loads(ui_report.read_text(encoding="utf-8"))
            validate_ui_report(record["uiStartup"]["checks"], bool(args.smoke_input))
            record["uiStartup"]["images"] = {}
            for section, field in (("library", "renderedControlsImage"), ("ex", "exControlsImage"), ("settings", "settingsControlsImage")):
                image_path = record["uiStartup"]["checks"].get(field)
                if not image_path:
                    raise ValueError("The native UI did not render the " + section + " section.")
                record["uiStartup"]["images"][section] = inspect_rendered_image(Path(image_path), output)
        if args.smoke_input:
            if sha256(args.smoke_input.resolve()) != source_hash:
                raise ValueError("The verification changed the supplied game archive.")
            record["sourceGamePreserved"] = {"sha256": source_hash, "path": str(args.smoke_input.resolve())}
        record["headlessProfile"] = record["headlessStartup"]["checks"]["report"]["ProfileDirectory"]
        record["status"] = "passed"
    except Exception as exception:
        record["error"] = str(exception)
        raise
    finally:
        (output / "macos-launcher-verification.json").write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"status": record["status"], "report": str(output / "macos-launcher-verification.json")}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
