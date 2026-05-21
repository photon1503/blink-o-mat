# Changelog

All notable changes to this project will be documented in this file.

---

## [Unreleased]

### Fixed
- **Long filename truncation** — frame item filenames that exceeded the available width now truncate with an ellipsis (`…`). The full filename is shown in a tooltip on hover. Root cause was the `ListView` allowing unlimited horizontal scroll, preventing `TextTrimming` from ever activating; fixed by setting `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` on the list.
- **ROI aspect ratio on initial load** — ROI preview images were squashed on first load due to a non-square pixel crop being stretched into a square bitmap. The crop is now forced square (centered on the shorter axis) before downsampling, so the ROI is never distorted.


### Added
- **Recursive subfolder scanning** — a new *Subfolders* checkbox next to the input folder path allows scanning subfolders recursively when loading frames.
  - **Relative path preservation on move** — when rejected frames are moved to the rejected folder, their subfolder structure relative to the input root is preserved (e.g. `input/night1/frame.fits` → `rejected/night1/frame.fits`).
  - **`IncludeSubfolders` setting persisted** — the checkbox state is saved to `settings.json` and restored on next launch.



