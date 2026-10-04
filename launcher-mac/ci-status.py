"""Print the Mac launcher workflow runs for a commit, using the existing Git credential.

The credential goes only to api.github.com and is never printed.
Usage: python ci-status.py <commit-sha> [--jobs] [--artifacts] [--evidence <file>]
"""
import json
import os
import subprocess
import sys
import urllib.request

REPO = "oleg44008-debug/for-games"


def token():
    env = dict(os.environ, GIT_TERMINAL_PROMPT="0", GCM_INTERACTIVE="Never")
    filled = subprocess.run([r"C:\Program Files\Git\cmd\git.exe", "credential", "fill"],
                            input=f"url=https://github.com/{REPO}\n\n", capture_output=True, text=True, env=env, check=True)
    values = dict(line.split("=", 1) for line in filled.stdout.splitlines() if "=" in line)
    if not values.get("password"):
        raise SystemExit("The existing authorized Git credential is unavailable.")
    return values["password"]


def api(path, secret):
    request = urllib.request.Request("https://api.github.com/repos/" + REPO + path, headers={
        "Authorization": "Bearer " + secret, "Accept": "application/vnd.github+json", "User-Agent": "DUSTORE-ci-status"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def main():
    sha = sys.argv[1]
    secret = token()
    runs = api(f"/actions/runs?head_sha={sha}&per_page=20", secret)["workflow_runs"]
    for run in runs:
        print(json.dumps({"id": run["id"], "name": run["name"], "status": run["status"], "conclusion": run["conclusion"],
                          "url": run["html_url"]}, ensure_ascii=False))
        if "--jobs" in sys.argv:
            for job in api(f"/actions/runs/{run['id']}/jobs", secret)["jobs"]:
                failed = [s["name"] for s in job.get("steps", []) if s.get("conclusion") == "failure"]
                print("   job", json.dumps({"id": job["id"], "name": job["name"], "status": job["status"],
                                            "conclusion": job["conclusion"], "failedSteps": failed}, ensure_ascii=False))
        if "--evidence" in sys.argv and run["name"] == "DUSTORE Launcher V macOS app":
            target = sys.argv[sys.argv.index("--evidence") + 1]
            evidence = {"run": {"id": run["id"], "conclusion": run["conclusion"], "status": run["status"], "head_sha": run["head_sha"]},
                        "artifacts": [{"id": x["id"], "name": x["name"], "digest": x.get("digest"), "head_sha": x["workflow_run"]["head_sha"]}
                                      for x in api(f"/actions/runs/{run['id']}/artifacts", secret)["artifacts"]],
                        "jobs": [{"id": j["id"], "name": j["name"], "status": j["status"], "conclusion": j["conclusion"]}
                                 for j in api(f"/actions/runs/{run['id']}/jobs", secret)["jobs"]]}
            with open(target, "w", encoding="utf-8") as stream:
                json.dump(evidence, stream, ensure_ascii=False, indent=2)
        if "--artifacts" in sys.argv:
            for artifact in api(f"/actions/runs/{run['id']}/artifacts", secret)["artifacts"]:
                print("   artifact", json.dumps({"id": artifact["id"], "name": artifact["name"], "digest": artifact.get("digest"),
                                                 "size": artifact["size_in_bytes"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
