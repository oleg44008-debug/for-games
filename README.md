# DUSTORE eX: macOS validation

Private validation repository for the portable eX conversion core used by DUSTORE LAUNCHER V.

GitHub Actions checks the pure .NET 8 core and the supplied PODIEZD macOS package on Apple Silicon and Intel runners. Each run retains logs and machine-readable evidence. The game package is included here with the user's authorization for this private test repository.

The Windows launcher uses WinForms. This workflow does not build a macOS launcher UI. A successful headless game startup confirms engine initialization and loading the game data; it does not confirm graphics, audio, input, or full gameplay. Local ad-hoc signing is for the test copy and does not constitute Developer ID signing or notarization.

The game payload expected SHA-256 is `9b313199d2383715db7a0fb1c9bdee8ddeff67b9f21ddaee84ce7d9a56faee6b`.

Run the `PODIEZD macOS validation` workflow in Actions, or push a change to `main`. See the workflow and `ci/` scripts for the exact validation steps.
