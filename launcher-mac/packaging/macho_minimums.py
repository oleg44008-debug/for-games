#!/usr/bin/env python3
"""Audit actual deployment targets and imported dylibs for one macOS Mach-O slice."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import plistlib
import struct
import zipfile

CPU_TYPES = {0x01000007: "x64", 0x0100000C: "arm64"}
THIN = {b"\xcf\xfa\xed\xfe": ("<", 32), b"\xce\xfa\xed\xfe": ("<", 28),
        b"\xfe\xed\xfa\xcf": (">", 32), b"\xfe\xed\xfa\xce": (">", 28)}
FAT = {b"\xca\xfe\xba\xbe": (">", 20), b"\xca\xfe\xba\xbf": (">", 32),
       b"\xbe\xba\xfe\xca": ("<", 20), b"\xbf\xba\xfe\xca": ("<", 32)}
LOAD_DYLIBS = {0xC, 0x80000018, 0x8000001F, 0x80000023, 0x20}


def version_tuple(version: str) -> tuple[int, int, int]:
    parts = [int(part) for part in version.split(".")]
    if not 1 <= len(parts) <= 3 or any(part < 0 for part in parts):
        raise ValueError("Invalid macOS deployment version: " + version)
    return tuple((parts + [0, 0])[:3])


def packed_version(number: int) -> str:
    return f"{number >> 16}.{(number >> 8) & 255}.{number & 255}"


def inspect_macho(data: bytes, architecture: str) -> dict | None:
    if len(data) < 8:
        return None
    offset, size = 0, len(data)
    magic = data[:4]
    if magic in FAT:
        endian, stride = FAT[magic]
        count = struct.unpack_from(endian + "I", data, 4)[0]
        if not 1 <= count <= 32 or 8 + stride * count > len(data):
            raise ValueError("Invalid universal Mach-O architecture table.")
        found = False
        for index in range(count):
            entry = 8 + index * stride
            cpu = struct.unpack_from(endian + "I", data, entry)[0]
            if CPU_TYPES.get(cpu) != architecture:
                continue
            offset, size = struct.unpack_from(endian + ("QQ" if stride == 32 else "II"), data, entry + 8)
            found = True
            break
        if not found:
            raise ValueError("The universal Mach-O has no requested architecture.")
        if offset + size > len(data):
            raise ValueError("The universal Mach-O slice exceeds the file.")
        data = data[offset:offset + size]
        magic = data[:4]
    if magic not in THIN:
        return None
    endian, header_size = THIN[magic]
    if len(data) < header_size:
        raise ValueError("Truncated native Mach-O header.")
    cpu = struct.unpack_from(endian + "I", data, 4)[0]
    if CPU_TYPES.get(cpu) != architecture:
        raise ValueError("The native Mach-O has the wrong requested architecture.")
    count, command_bytes = struct.unpack_from(endian + "II", data, 16)
    if count > 4096 or header_size + command_bytes > len(data):
        raise ValueError("Invalid native Mach-O load command table.")
    targets, dependencies = [], []
    position = header_size
    for _ in range(count):
        if position + 8 > header_size + command_bytes:
            raise ValueError("Truncated native Mach-O load command.")
        command, command_size = struct.unpack_from(endian + "II", data, position)
        if command_size < 8 or position + command_size > header_size + command_bytes:
            raise ValueError("Invalid native Mach-O load command size.")
        if command == 0x24 and command_size >= 16:  # LC_VERSION_MIN_MACOSX
            minimum, sdk = struct.unpack_from(endian + "II", data, position + 8)
            targets.append({"command": "LC_VERSION_MIN_MACOSX", "platform": "macOS",
                            "minimumOS": packed_version(minimum), "sdk": packed_version(sdk)})
        elif command == 0x32 and command_size >= 24:  # LC_BUILD_VERSION
            native_platform, minimum, sdk = struct.unpack_from(endian + "III", data, position + 8)
            if native_platform != 1:
                raise ValueError("A native component targets a different Apple platform.")
            targets.append({"command": "LC_BUILD_VERSION", "platform": "macOS",
                            "minimumOS": packed_version(minimum), "sdk": packed_version(sdk)})
        elif command in LOAD_DYLIBS and command_size >= 24:
            name_offset = struct.unpack_from(endian + "I", data, position + 8)[0]
            if not 24 <= name_offset < command_size:
                raise ValueError("Invalid native imported-library name offset.")
            name = data[position + name_offset:position + command_size].split(b"\0", 1)[0].decode("utf-8")
            dependencies.append({"name": name, "weak": command == 0x80000018})
        position += command_size
    minimum = max((target["minimumOS"] for target in targets), key=version_tuple, default=None)
    return {"architecture": architecture, "minimumOS": minimum, "deploymentTargets": targets,
            "requiredSystemLibraries": [item for item in dependencies if item["name"].startswith(("/System/Library/", "/usr/lib/"))]}


def audit_archive(archive: Path, rid: str) -> dict:
    architecture = {"osx-arm64": "arm64", "osx-x64": "x64"}[rid]
    records = []
    metadata = None
    with zipfile.ZipFile(archive) as source:
        for entry in source.infolist():
            if entry.filename.startswith("__MACOSX/") or entry.is_dir():
                continue
            if entry.filename.endswith(".app/Contents/Info.plist"):
                metadata = plistlib.loads(source.read(entry))
            with source.open(entry) as stream:
                magic = stream.read(4)
                if magic not in THIN and magic not in FAT:
                    continue
                data = magic + stream.read()
            record = inspect_macho(data, architecture)
            if record:
                records.append({"path": entry.filename, **record})
    minimum = max((record["minimumOS"] for record in records if record["minimumOS"]), key=version_tuple, default=None)
    return {"runtimeIdentifier": rid, "bundleMinimumOS": metadata.get("LSMinimumSystemVersion") if metadata else None,
            "maximumNativeMinimumOS": minimum, "nativeComponents": records,
            "scope": "Embedded deployment targets and imported system libraries. This audit alone does not prove every managed/native API runs on untested older macOS versions."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--rid", choices=("osx-arm64", "osx-x64"), required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = audit_archive(args.archive, args.rid)
    content = json.dumps(result, indent=2, ensure_ascii=False)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(content, encoding="utf-8")
    print(content)
