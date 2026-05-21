# Changelog

All notable changes to this project will be documented in this file.

---

## [Unreleased]

### Added
- **OSC (One-Shot Colour) camera support** — FITS frames from colour sensors are now debayered and displayed in full colour.
  - Bayer pattern is read from the FITS keywords `BAYERPAT`, `XBAYROFF`, and `YBAYROFF`. All standard patterns are supported: **RGGB**, **BGGR**, **GRBG**, **GBRG**.
  - Bilinear demosaicing is applied at load time to produce separate R, G, B channel arrays.
  - **Unlinked (per-channel) auto-stretch** — shadows, midtones, and highlights are computed independently for each colour channel using the PixInsight-style STF algorithm, so colour balance is preserved naturally without manual white-balance adjustment.
  - All preview surfaces (list thumbnail, ROI crop, interactive preview window, full-resolution view) render OSC frames in **Rgb24** colour automatically. Monochrome frames are unaffected.
- **Recursive subfolder scanning** — a new *Subfolders* checkbox next to the input folder path allows scanning subfolders recursively when loading frames.
  - **Relative path preservation on move** — when rejected frames are moved to the rejected folder, their subfolder structure relative to the input root is preserved (e.g. `input/night1/frame.fits` → `rejected/night1/frame.fits`).
  - **`IncludeSubfolders` setting persisted** — the checkbox state is saved to `settings.json` and restored on next launch.
- **Preview window "Open in Explorer" opens a Windows Explorer instance, with the actual file selected.
- **Window placement persistence** — the main window and frame preview window now restore their last size, position, and maximized state on startup.

### Fixed
- **Long filename truncation** — frame item filenames that exceeded the available width now truncate with an ellipsis (`…`). The full filename is shown in a tooltip on hover. Root cause was the `ListView` allowing unlimited horizontal scroll, preventing `TextTrimming` from ever activating; fixed by setting `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` on the list.
- **ROI aspect ratio on initial load** — ROI preview images were squashed on first load due to a non-square pixel crop being stretched into a square bitmap. The crop is now forced square (centered on the shorter axis) before downsampling, so the ROI is never distorted.
- Fixed µm label.



