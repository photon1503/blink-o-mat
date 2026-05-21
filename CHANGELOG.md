# Changelog

All notable changes to this project will be documented in this file.

---

## [Unreleased] — 1.0.4

### Added
- **Rejection reasons tooltip** — each automatically rejected frame now displays a small **?** badge next to the Reject/Keep button.
  - Hovering the badge shows a dark tooltip listing every threshold that was violated, with the frame's actual measured value and the configured limit (e.g. *FWHM 4.21 px  >  limit 3.50 px*).
  - The badge is hidden for accepted frames or manually overridden frames that are not auto-rejected.


- **Save / Load Session**
  - Saves: input/rejected folder paths, subfolder toggle, all rejection thresholds, STF settings, ROI bias and manual ROI rectangle, sort rules, filter chip selection, and accepted/rejected state per frame.
  - Per-frame cache: all metrics, FITS metadata (focal length, pixel size, exposure date/time, filter), thumbnail images, and ROI preview images are stored as base64-encoded PNGs inside the session file — no re-analysis required on load.
  - **Incremental rescan on load**: after restoring the session, the input folder is scanned and any files not already present in the session are loaded, analyzed, and appended automatically.
  - **Save Session** and **Load Session** icon buttons are placed in the top-left corner of the main window toolbar.


- **Modern metric slider control** — automatic rejection thresholds and STF controls now use a shared slim vertical slider control with editable values and configurable decimal precision.
  - Rejection sliders keep their **Rejects: N** indicator.
  - STF sliders reuse the same control without the reject counter.
  - Clicking a value switches it into inline edit mode with a slightly enlarged editor for readability.

## 1.0.1

### Added
- **OSC (One-Shot Colour) camera support** — FITS frames from colour sensors are now debayered and displayed in full colour.
  - Bayer pattern is read from the FITS keywords `BAYERPAT`, `XBAYROFF`, and `YBAYROFF`. All standard patterns are supported: **RGGB**, **BGGR**, **GRBG**, **GBRG**.
  - Bilinear demosaicing is applied at load time to produce separate R, G, B channel arrays.
  - **Unlinked (per-channel) auto-stretch** — shadows, midtones, and highlights are computed independently for each colour channel using the PixInsight-style STF algorithm, so colour balance is preserved naturally without manual white-balance adjustment.
  - All preview surfaces (list thumbnail, ROI crop, interactive preview window, full-resolution view) render OSC frames in **Rgb24** colour automatically. Monochrome frames are unaffected.
- **Recursive subfolder scanning** — a new *Subfolders* checkbox next to the input folder path allows scanning subfolders recursively when loading frames.
  - **Relative path preservation on move** — when rejected frames are moved to the rejected folder, their subfolder structure relative to the input root is preserved (e.g. `input/night1/frame.fits` → `rejected/night1/frame.fits`).
  - **`IncludeSubfolders` setting persisted** — the checkbox state is saved to `settings.json` and restored on next launch.
- **Preview window "Open in Explorer"** — opens a Windows Explorer window with the current file selected.
- **Window placement persistence** — the main window and frame preview window now restore their last size, position, and maximized state on startup.


### Fixed
- **Long filename truncation** — frame item filenames that exceeded the available width now truncate with an ellipsis (`…`). The full filename is shown in a tooltip on hover. Root cause was the `ListView` allowing unlimited horizontal scroll, preventing `TextTrimming` from ever activating; fixed by setting `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` on the list.
- **ROI aspect ratio on initial load** — ROI preview images were squashed on first load due to a non-square pixel crop being stretched into a square bitmap. The crop is now forced square (centered on the shorter axis) before downsampling, so the ROI is never distorted.
- Fixed µm label.



