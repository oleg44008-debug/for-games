#!/usr/bin/env python3
"""Publish a real, self-contained Avalonia macOS application bundle.

Runs on Windows for a structural cross-build; runs on macOS to produce the
ad-hoc signed deliverable. It never starts the launcher or a downloaded game.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import plistlib
import shutil
import stat
import struct
import subprocess
import uuid
import xml.etree.ElementTree as ET
import zipfile
from macho_minimums import inspect_macho, version_tuple

APP_NAME = "DUSTORE LAUNCHER V.app"
ASSEMBLY_NAME = "DustoreLauncherV.Mac"
LOGO_SHA256 = "69cdb26a75f82302b8f476788a705bbdd6c1ed8d74934e3f05cb4df6a41468d4"
CPU_TYPES = {0x01000007: "x64", 0x0100000C: "arm64"}


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def macho_architectures(path: Path) -> list[str]:
    with path.open("rb") as stream:
        header = stream.read(4096)
    if len(header) < 8:
        return []
    magic = header[:4]
    thin = {b"\xcf\xfa\xed\xfe": "<", b"\xce\xfa\xed\xfe": "<",
            b"\xfe\xed\xfa\xcf": ">", b"\xfe\xed\xfa\xce": ">"}
    if magic in thin:
        cpu = struct.unpack_from(thin[magic] + "I", header, 4)[0]
        return [CPU_TYPES.get(cpu, f"cpu-{cpu:08x}")]
    fat = {b"\xca\xfe\xba\xbe": (">", 20), b"\xca\xfe\xba\xbf": (">", 32),
           b"\xbe\xba\xfe\xca": ("<", 20), b"\xbf\xba\xfe\xca": ("<", 32)}
    if magic not in fat:
        return []
    endian, stride = fat[magic]
    count = struct.unpack_from(endian + "I", header, 4)[0]
    if count < 1 or count > 32 or len(header) < 8 + count * stride:
        raise ValueError(f"Invalid universal Mach-O header: {path}")
    return [CPU_TYPES.get(struct.unpack_from(endian + "I", header, 8 + i * stride)[0], "unknown") for i in range(count)]


def native_files(bundle: Path) -> list[Path]:
    return sorted((path for path in bundle.rglob("*") if path.is_file() and not path.is_symlink()
                   and macho_architectures(path)), key=lambda path: (len(path.parts), str(path)), reverse=True)


def validate_bundle(bundle: Path, rid: str) -> dict:
    expected = {"osx-arm64": "arm64", "osx-x64": "x64"}[rid]
    contents = bundle / "Contents"
    with (contents / "Info.plist").open("rb") as stream:
        metadata = plistlib.load(stream)
    executable = contents / "MacOS" / metadata["CFBundleExecutable"]
    if metadata["CFBundleExecutable"] != ASSEMBLY_NAME or metadata["CFBundlePackageType"] != "APPL":
        raise ValueError("The bundle manifest does not identify the launcher apphost.")
    if expected not in macho_architectures(executable):
        raise ValueError(f"Launcher apphost is not a native {expected} Mach-O executable.")
    icon = contents / "Resources" / metadata["CFBundleIconFile"]
    icon_header = icon.read_bytes()[:8]
    if len(icon_header) != 8 or icon_header[:4] != b"icns" or struct.unpack(">I", icon_header[4:])[0] != icon.stat().st_size:
        raise ValueError("The application icon is not a complete ICNS file.")
    logo = contents / "Resources" / "dustore-logo-original.png"
    if sha256(logo) != LOGO_SHA256:
        raise ValueError("The original DUSTORE logo was changed.")
    required = [ASSEMBLY_NAME + ".dll", "DustoreX.AutoConverter.Core.dll", "libcoreclr.dylib",
                "libhostfxr.dylib", "libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib"]
    missing = [name for name in required if not (contents / "MacOS" / name).is_file()]
    if missing:
        raise ValueError("Self-contained launcher dependencies are missing: " + ", ".join(missing))
    libraries = native_files(bundle)
    for path in libraries:
        if expected not in macho_architectures(path):
            raise ValueError(f"Wrong native architecture in {path.relative_to(bundle)}")
    for path in bundle.rglob("*"):
        if path.is_symlink() and not path.resolve().is_relative_to(bundle.resolve()):
            raise ValueError(f"Bundle symlink escapes the app: {path}")
    return {"bundle": str(bundle), "runtimeIdentifier": rid, "architecture": expected,
            "metadata": metadata, "originalLogoSha256": sha256(logo), "iconSha256": sha256(icon),
            "apphostSha256": sha256(executable), "nativeFiles": [
                {"path": str(path.relative_to(bundle)), "architectures": macho_architectures(path), "sha256": sha256(path)} for path in libraries],
            "requiredDependencies": required}


def zip_bundle(bundle: Path, archive: Path) -> None:
    """Preserve POSIX executable and symlink attributes even when built on Windows."""
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as result:
        paths = [bundle, *sorted(bundle.rglob("*"))]
        for path in paths:
            relative = path.relative_to(bundle.parent).as_posix()
            info = zipfile.ZipInfo(relative + ("/" if path.is_dir() and not path.is_symlink() else ""))
            info.create_system = 3
            info.compress_type = zipfile.ZIP_DEFLATED
            if path.is_symlink():
                info.external_attr = (stat.S_IFLNK | 0o777) << 16
                data = os.readlink(path).encode("utf-8")
            elif path.is_dir():
                info.external_attr = ((stat.S_IFDIR | 0o755) << 16) | 0x10
                data = b""
            else:
                executable = path.name == ASSEMBLY_NAME or bool(macho_architectures(path))
                info.external_attr = (stat.S_IFREG | (0o755 if executable else 0o644)) << 16
                data = path.read_bytes()
            result.writestr(info, data)


def run(command: list[str]) -> None:
    subprocess.run(command, check=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, default=Path(__file__).resolve().parent.parent / "DustoreLauncherV.Mac.csproj")
    parser.add_argument("--rid", choices=("osx-arm64", "osx-x64"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--converter-core", type=Path)
    parser.add_argument("--publish-dir", type=Path, help="Package an already published output instead of invoking dotnet.")
    args = parser.parse_args()
    project, output = args.project.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    workspace = output / ("packaging-work-" + uuid.uuid4().hex)
    workspace.mkdir()
    publish = args.publish_dir.resolve() if args.publish_dir else workspace / "publish"
    if args.publish_dir is None:
        command = [args.dotnet, "publish", str(project), "-c", "Release", "-r", args.rid, "--self-contained", "true",
                   "-p:UseAppHost=true", "-p:PublishSingleFile=false", "-p:PublishTrimmed=false", "-p:PublishAot=false",
                   "-p:DebugType=None", "-p:DebugSymbols=false", "-o", str(publish)]
        if args.converter_core:
            command.append("-p:ConverterCoreProject=" + str(args.converter_core.resolve()))
        run(command)
    bundle = workspace / APP_NAME
    contents = bundle / "Contents"
    shutil.copytree(publish, contents / "MacOS", symlinks=True)
    resources = contents / "Resources"
    resources.mkdir()
    packaging = Path(__file__).resolve().parent
    shutil.copy2(packaging / "DustoreLauncherV.icns", resources)
    shutil.copy2(project.parent / "Assets" / "dustore-logo-original.png", resources)
    version = ET.parse(project).findtext(".//Version") or "5.2.0"
    metadata = {"CFBundleExecutable": ASSEMBLY_NAME, "CFBundleName": "DUSTORE V", "CFBundleDisplayName": "DUSTORE LAUNCHER V",
                "CFBundleIdentifier": "io.dustore.launcher.v", "CFBundleVersion": version, "CFBundleShortVersionString": version,
                "CFBundleIconFile": "DustoreLauncherV.icns", "CFBundleInfoDictionaryVersion": "6.0", "CFBundlePackageType": "APPL",
                "NSHighResolutionCapable": True, "LSMinimumSystemVersion": {"osx-x64": "10.15", "osx-arm64": "11.0"}[args.rid]}
    minimum_records = []
    for path in native_files(bundle):
        minimum_record = inspect_macho(path.read_bytes(), {"osx-x64": "x64", "osx-arm64": "arm64"}[args.rid])
        if not minimum_record or not minimum_record["minimumOS"]:
            raise ValueError("A native component has no auditable macOS deployment minimum: " + str(path))
        if version_tuple(minimum_record["minimumOS"]) > version_tuple(metadata["LSMinimumSystemVersion"]):
            raise ValueError("A native component requires newer macOS than the bundle declares: " + str(path))
        minimum_records.append({"path": str(path.relative_to(bundle)), **minimum_record})
    with (contents / "Info.plist").open("wb") as stream:
        plistlib.dump(metadata, stream, fmt=plistlib.FMT_XML, sort_keys=False)
    native = native_files(bundle)
    for path in native:
        path.chmod(0o755)
    signed = platform.system() == "Darwin"
    if signed:
        executable = contents / "MacOS" / ASSEMBLY_NAME
        # Apple's codesign treats files inside Contents/MacOS as nested code.
        # The documented Avalonia manual layout keeps managed DLL/JSON output
        # beside apphost, so seal those files before signing native binaries and
        # the bundle. Non-Mach-O seals use xattrs; ditto preserves them in the ZIP.
        native_set = set(native)
        resources_in_code = sorted((path for path in (contents / "MacOS").rglob("*")
                                    if path.is_file() and not path.is_symlink() and path not in native_set),
                                   key=lambda path: (len(path.parts), str(path)), reverse=True)
        for path in resources_in_code:
            run(["codesign", "--force", "--sign", "-", str(path)])
        for path in native:
            command = ["codesign", "--force", "--sign", "-"]
            if path == executable:
                command += ["--options", "runtime", "--entitlements", str(packaging / "launcher.entitlements")]
            run(command + [str(path)])
        run(["codesign", "--force", "--sign", "-", "--options", "runtime", "--entitlements",
             str(packaging / "launcher.entitlements"), str(bundle)])
        run(["codesign", "--verify", "--deep", "--strict", "--verbose=2", str(bundle)])
    report = validate_bundle(bundle, args.rid)
    report["nativeDeploymentTargets"] = minimum_records
    report["compatibilityScope"] = "Intel targets Catalina 10.15+, Apple Silicon Big Sur 11+. Embedded native minima are audited; older untested operating systems are not runtime-verified by this package report."
    report["signing"] = {"adHoc": signed, "developerId": False, "notarized": False}
    archive = output / f"DUSTORE-LAUNCHER-V-{version}-{args.rid}.app.zip"
    if signed:
        run(["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", str(bundle), str(archive)])
    else:
        zip_bundle(bundle, archive)
    report["archive"] = str(archive)
    report["archiveSha256"] = sha256(archive)
    report_path = output / f"package-{args.rid}.json"
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    (output / f"DUSTORE-LAUNCHER-V-{args.rid}.sha256").write_text(report["archiveSha256"] + "  " + archive.name + "\n", encoding="utf-8")
    print(json.dumps({"archive": str(archive), "report": str(report_path), "adHocSigned": signed}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
