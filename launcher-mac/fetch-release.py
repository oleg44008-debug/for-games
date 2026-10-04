"""Download every edition/architecture artifact named in delivery/ci-evidence-<version>.json.
Usage: python fetch-release.py <version>"""
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
version = sys.argv[1]
evidence = json.loads((ROOT / "delivery" / f"ci-evidence-{version}.json").read_text(encoding="utf-8"))
run = evidence["run"]["id"]
for artifact in evidence["artifacts"]:
    name = artifact["name"]  # DUSTORE-LAUNCHER-V-macOS-<edition>-osx-<arch>
    if not name.startswith("DUSTORE-LAUNCHER-V-macOS-"):
        continue
    edition, _, arch = name.removeprefix("DUSTORE-LAUNCHER-V-macOS-").split("-")
    target = ROOT / "delivery" / f"ci-{run}-{edition}-{arch}"
    if target.exists():
        print("already downloaded:", target.name)
        continue
    subprocess.run([sys.executable, str(ROOT / "download-artifact.py"), "--artifact-id", str(artifact["id"]),
                    "--expected-digest", artifact["digest"], "--output", str(target)], check=True)
