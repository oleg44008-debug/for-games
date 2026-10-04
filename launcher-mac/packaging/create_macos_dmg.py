#!/usr/bin/env python3
"""Create a read-only Mac installer image without changing the signed app."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import platform
import subprocess
import time
import uuid


def create_installer(bundle: Path, destination: Path, version: str) -> dict:
    if platform.system() != "Darwin":
        raise RuntimeError("A signed-metadata-preserving DMG must be created on macOS.")
    if not bundle.is_dir() or bundle.suffix != ".app":
        raise ValueError("The installer source must be the verified launcher .app bundle.")
    if destination.exists():
        raise ValueError("Refusing to replace an existing installer image.")
    subprocess.run(["codesign", "--verify", "--deep", "--strict", str(bundle)], check=True, capture_output=True)
    stage = destination.parent / ("dmg-stage-" + uuid.uuid4().hex)
    stage.mkdir()
    copied = stage / bundle.name
    subprocess.run(["ditto", "--rsrc", str(bundle), str(copied)], check=True, capture_output=True)
    subprocess.run(["codesign", "--verify", "--deep", "--strict", str(copied)], check=True, capture_output=True)
    (stage / "Applications").symlink_to("/Applications", target_is_directory=True)
    # GitHub's macOS runners intermittently answer "create failed - Resource busy" while a
    # scanner still holds the fresh staging folder; the same command succeeds moments later.
    for attempt in range(1, 6):
        created = subprocess.run(["hdiutil", "create", "-fs", "HFS+", "-format", "UDZO", "-volname",
                                  "DUSTORE Launcher V " + version, "-srcfolder", str(stage), str(destination)])
        if created.returncode == 0:
            break
        if attempt == 5:
            raise subprocess.CalledProcessError(created.returncode, created.args)
        destination.unlink(missing_ok=True)
        time.sleep(10 * attempt)
    subprocess.run(["hdiutil", "verify", str(destination)], check=True, capture_output=True)
    with destination.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    return {"image": str(destination), "sha256": digest, "format": "UDZO read-only HFS+",
            "appSignatureVerifiedBeforeImaging": True, "imageVerified": True,
            "notarized": False, "developerId": False,
            "scope": "Signed app copied with native metadata intact; the app is not re-signed or modified."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    print(json.dumps(create_installer(args.bundle.resolve(), args.output.resolve(), args.version), indent=2))
