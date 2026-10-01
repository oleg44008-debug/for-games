#!/usr/bin/env python3
"""Run the four platform-neutral Core regression executables and retain every log."""
import argparse
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
from datetime import datetime, timezone


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--core-dll", help="Optional existing Core DLL for isolated local checks")
    parser.add_argument("--artifacts", type=Path, default=Path("ci/artifacts/core"))
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    artifacts = args.artifacts.resolve()
    artifacts.mkdir(parents=True, exist_ok=True)
    report = {
        "schemaVersion": 1,
        "testedAtUtc": datetime.now(timezone.utc).isoformat(),
        "hostSystem": platform.system(),
        "hostArchitecture": platform.machine(),
        "scope": "Pure Core packaging/analysis checks; no launcher, webserver or game execution",
        "suites": [],
        "status": "Fail",
    }
    # macOS /var and /tmp commonly resolve through symlinks. Core deliberately rejects
    # reparse-point ancestors, so fixtures use an owned real path inside the workspace.
    fixture_temp = (artifacts / "owned-fixtures").resolve()
    fixture_temp.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
                       TMPDIR=str(fixture_temp) + os.sep, TMP=str(fixture_temp), TEMP=str(fixture_temp))
    for name in ("Converter", "Godot", "Runtime", "System"):
        project = root / "tests" / "CoreChecks" / name / (name + ".CoreChecks.csproj")
        command = [args.dotnet, "run", "--project", str(project), "-c", "Release"]
        if args.core_dll:
            command += ["-p:CoreReferencePath=" + str(Path(args.core_dll).resolve())]
        result = {"name": name, "project": str(project.relative_to(root)), "passed": 0, "failed": None}
        output = ""
        try:
            completed = subprocess.run(command, cwd=root, env=environment, text=True,
                                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                       encoding="utf-8", errors="replace", timeout=180)
            output = completed.stdout
            result["exitCode"] = completed.returncode
            result["timedOut"] = False
            summaries = re.findall(r"RESULT:?\s+(\d+)\s+passed;\s+(\d+)\s+failed", output)
            if summaries:
                result["passed"], result["failed"] = map(int, summaries[-1])
            result["status"] = "Pass" if completed.returncode == 0 and summaries and result["failed"] == 0 else "Fail"
        except subprocess.TimeoutExpired as error:
            captured = error.stdout or b""
            output = captured.decode("utf-8", "replace") if isinstance(captured, bytes) else captured
            result.update(status="Fail", exitCode=None, timedOut=True, error="Suite exceeded 180 seconds")
        except Exception as error:
            result.update(status="Fail", exitCode=None, timedOut=False, error=str(error))
        log = artifacts / (name + ".log")
        log.write_text(output, encoding="utf-8")
        result["log"] = log.name
        report["suites"].append(result)
        print(output, end="" if output.endswith("\n") else "\n", flush=True)
        (artifacts / "core-checks.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    report["passed"] = sum(suite["passed"] for suite in report["suites"])
    report["status"] = "Pass" if all(suite["status"] == "Pass" for suite in report["suites"]) else "Fail"
    (artifacts / "core-checks.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0 if report["status"] == "Pass" else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    sys.exit(main())
