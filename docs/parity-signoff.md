# Avalonia Migration Signoff

Date: 2026-08-15
Status: IN_PROGRESS

## Scope

Epic 5 release cutover and migration exit checklist for the Avalonia desktop workflow.

## Validation

| Check | Result | Evidence |
|---|---|---|
| Avalonia project build | PASS | `scripts/verify-parity.sh` |
| Cross-platform solution build | PASS | `scripts/verify-parity.sh` |
| Core tests | PASS, 39 tests | `scripts/verify-parity.sh` |
| macOS runtime validation | PARTIAL | Existing runtime and screenshots in `src/shots/` |
| Windows runtime validation | PENDING | Requires Windows runner or local Windows machine |
| Release workflow syntax | PASS | Local YAML parse; `actionlint` unavailable locally |
| GitHub Actions release run | PENDING | Requires pushing a release tag or dispatching the workflow |

## Existing Visual Evidence

- Preview parity: `src/shots/1.0-preview-layout-current.png`
- Full-resolution preview: `src/shots/1.7-fullres-zoom-current.png`
- Settings and folder surfaces: `src/shots/2.0-settings-current.png`, `src/shots/2.0-open-folder-current.png`
- Sort and filter behavior: `src/shots/3.1-multi-rule-sort-parity.png`, `src/shots/3.2-filter-scope-statistics-parity.png`
- Watch-folder behavior: `src/shots/4.1-watch-folder-live-validation.png`

## Release Cutover

The release workflow now builds the Avalonia project as the primary application for:

- Windows `win-x64`: self-contained publish, Inno Setup installer, and portable ZIP.
- macOS `osx-arm64`: self-contained publish wrapped in a DMG by `scripts/build-macos-dmg.sh`.

The workflow uploads platform artifacts and publishes them together from a dedicated release job.

## Remaining Deltas

- Run the release workflow on GitHub and confirm both matrix jobs complete.
- Install and launch the Windows installer on a Windows machine.
- Install and launch the macOS DMG on a clean macOS machine.
- Capture final Windows and macOS release screenshots.
- Mark this signoff and Epic 5 tasks `DONE` only after those checks pass.
