# PODIEZD validation on real macOS runners

Workflow name: **PODIEZD macOS validation**. It runs the same source on `macos-latest` (native arm64) and `macos-15-intel` (native x86_64). Each job has a 20-minute limit. Logs and JSON reports are uploaded even after failure, with 14-day retention.

The repository needs only the paths listed in `source-manifest.json`: the seven pure Core implementation files, its project, four regression projects and their linked test sources, the signing helper, CI scripts, and the two input ZIPs. No browser server, WebView2, WinForms launcher, local account data or runtime cache is needed.

Validation sequence:

1. Run the four existing pure Core suites (148 checks on the local Windows run).
2. Verify the supplied Mac ZIP, its original PCK SHA-256, Godot 4.7.1 version, universal arm64/x86_64 Mach-O header, and Unix executable permission.
3. Extract with macOS `ditto`, inspect with `lipo` and `file`, and run `tools/macos-sign.command` on a **new copy**. The original archive and extracted source app must stay unchanged.
4. Query the actual signed executable version and run the real game with `--headless --quit-after 120`, isolated user data and a 90-second owned-process timeout. Nonzero exit, timeout, and engine/script startup errors fail the job.
5. Package the actual Mac app back to Windows with `Godot.CoreChecks --package` and the offline matching Windows runtime. Check the unchanged PCK, x86_64 PE architecture and exact runtime bytes. The Windows executable is not launched on Mac.

This proves the reported checks only. It does not test the Windows launcher GUI on Mac, rendering, controls, complete interactive gameplay, Gatekeeper acceptance or notarization. Ad-hoc signing is not a Developer ID signature.

The exact Godot exit messages about resources/ObjectDB instances still alive are retained in `shutdownDiagnostics`, with `headlessShutdownClean: false`. They are emitted during [core type shutdown](https://github.com/godotengine/godot/blob/4.7.1-stable/core/register_core_types.cpp) by [ResourceCache::clear](https://github.com/godotengine/godot/blob/4.7.1-stable/core/io/resource.cpp). They do not alone fail a startup check with exit code 0. Every other engine/script error, native loader failure, nonzero exit and timeout continues to fail validation. A passing startup report does not assert leak-free shutdown.

Commands from the repository root:

```sh
python3 -m unittest discover -s ci/tests -v
python3 ci/run_core_checks.py --artifacts ci/artifacts/core
python3 ci/validate_macos.py --expected-arch arm64 --artifacts ci/artifacts/macos
# Use --expected-arch x86_64 on the Intel runner.
```

Archive-only verification on any OS, including Windows:

```sh
python3 ci/validate_macos.py --structure-only --artifacts ci/artifacts/structure
```

`--structure-only` never signs or launches an executable and reports `macLaunchTested: false`. Its success is not evidence of macOS startup.

The offline Windows runtime ZIP contains only the already verified matching executable, notices and version metadata. Its provenance JSON records the original package, official template source checksum, executable SHA-256 and the new runtime ZIP SHA-256; it contains no PCK or other game payload. The ZIP can be reproduced with:

```sh
python3 ci/prepare_windows_runtime.py /path/PODIEZD-Windows-roundtrip.zip \
  --output ci/game/godot-4.7.1-windows-runtime.zip \
  --official-lock /path/Godot_v4.7.1-stable_export_templates.tpz.lock.json
```

The creator refuses to replace an existing ZIP. macOS validation does not download another 1.28 GiB template archive.

Pinned Actions were checked against official releases on 2026-10-01: [checkout v7.0.1](https://github.com/actions/checkout/releases/tag/v7.0.1), [setup-dotnet v6.0.0](https://github.com/actions/setup-dotnet/releases/tag/v6.0.0), and [upload-artifact v7.0.1](https://github.com/actions/upload-artifact/releases/tag/v7.0.1). Runner architecture labels follow [GitHub's hosted runner reference](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
