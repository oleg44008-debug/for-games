#!/usr/bin/env python3
"""Extract only the existing Godot executable and notices into an offline CI runtime ZIP."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import zipfile


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--output", type=Path, default=Path("ci/game/godot-4.7.1-windows-runtime.zip"))
    parser.add_argument("--official-lock", type=Path)
    args = parser.parse_args()
    source = args.source.resolve()
    output = args.output.resolve()
    if output.exists():
        raise FileExistsError("Refusing to replace an existing runtime ZIP")
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(source) as incoming:
        manifest = json.loads(incoming.read("package-manifest.json"))
        if manifest.get("engine") != "Godot" or manifest.get("engineVersion") != "4.7.1":
            raise ValueError("Need the existing matching Godot 4.7.1 package")
        executables = [entry for entry in incoming.infolist() if entry.filename.lower().endswith(".exe")]
        if len(executables) != 1:
            raise ValueError("Expected one standalone Windows executable")
        executable = incoming.read(executables[0])
        if executable[:2] != b"MZ":
            raise ValueError("Runtime is not Windows PE")
        pe = struct.unpack_from("<I", executable, 60)[0]
        if executable[pe:pe + 4] != b"PE\0\0" or struct.unpack_from("<H", executable, pe + 4)[0] != 0x8664:
            raise ValueError("Expected a Windows x86_64 PE runtime")
        notices = {Path(entry.filename).name: incoming.read(entry) for entry in incoming.infolist()
                   if re.search(r"(?:LICENSE|COPYRIGHT)\.txt$", entry.filename, re.I)}
        if not notices:
            raise ValueError("Runtime license notices are required")
        version = manifest.get("templatesDeclaredVersion") or "4.7.1.stable"
        entries = {"windows_release_x86_64.exe": executable, "version.txt": (version + "\n").encode()}
        entries.update(notices)
        with zipfile.ZipFile(output, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as outgoing:
            for name, data in entries.items():
                info = zipfile.ZipInfo(name, date_time=(2026, 10, 1, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                info.create_system = 3
                info.external_attr = (0o100755 if name.endswith(".exe") else 0o100644) << 16
                outgoing.writestr(info, data)
    provenance = {
        "schemaVersion": 1,
        "engine": "Godot", "engineVersion": "4.7.1", "target": "Windows x86_64",
        "sourcePackageFile": source.name, "sourcePackageSha256": digest(source),
        "sourcePackageRuntimeSha256": manifest.get("runtimeSha256"),
        "runtimeZipFile": output.name, "runtimeZipSha256": digest(output),
        "executableSha256": hashlib.sha256(executable).hexdigest(),
        "files": sorted(entries), "gamePayloadIncluded": False,
        "method": "Copied matching runtime bytes and license notices from the already produced Windows package; no executable launched",
    }
    if args.official_lock:
        lock = json.loads(args.official_lock.read_text(encoding="utf-8-sig"))
        if not lock.get("officialChecksumVerified") or lock.get("sha256") != manifest.get("runtimeSha256"):
            raise ValueError("Official runtime cache checksum does not match package provenance")
        provenance["officialSource"] = {key: lock.get(key) for key in
                                        ("url", "sha256", "officialChecksum", "officialChecksumVerified", "version")}
    output.with_suffix(".provenance.json").write_text(json.dumps(provenance, indent=2), encoding="utf-8")
    print(json.dumps(provenance, indent=2))


if __name__ == "__main__":
    main()
