# Changelog

All notable changes to this project will be documented in this file.

---

## [Unreleased]

### Added
- **Recursive subfolder scanning** — a new *Subfolders* checkbox next to the input folder path allows scanning subfolders recursively when loading frames.
- **Relative path preservation on move** — when rejected frames are moved to the rejected folder, their subfolder structure relative to the input root is preserved (e.g. `input/night1/frame.fits` → `rejected/night1/frame.fits`).
- **`IncludeSubfolders` setting persisted** — the checkbox state is saved to `settings.json` and restored on next launch.

---

