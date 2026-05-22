# Changelog

All notable changes to this project will be documented in this file.

---
## 1.0.6

### Added
- **Play / Pause button** in the preview window toolbar — automatically steps through frames at a configurable interval.
  - Press the **▶ / ⏸** button or hit `Space` to toggle playback.
  - Use the **−** / **+** controls next to the button to cycle through preset intervals: 100 ms, 200 ms, 500 ms, **1 s** (default), 2 s, 3 s, 5 s, 10 s.
  - Playback stops automatically when the last frame is reached.

### Changed
- **Preview slider markers redesigned** — the small frame indicators beside the vertical frame slider now convey quality and state at a glance:
  - **Score-driven color** — each marker is colored on a continuous **green → yellow → red** gradient based on the frame's rank-percentile quality score (best = green, worst = red). Colors are normalized per session so the full gradient is always used.
  - **Strikethrough** for rejected frames — a white line is drawn across any marker whose frame is rejected, making it immediately obvious without requiring navigation.
  - **Blue border** for cached frames — frames currently held in the preview image cache are outlined in blue, indicating which frames are ready for instant display.
  - The active (currently displayed) frame marker remains highlighted in gold and is rendered slightly wider.
  - **Clickable** — clicking any marker jumps directly to that frame; the cursor changes to a pointer on hover and a tooltip shows the frame number.

---
## 1.0.5

- Changed default sort order to "Observation time", ascending.
- Keep sorting of main and preview window in sync — changing the sort order in one window now updates the other window to match.

---

##  1.0.4

### Added
- **Rejection reasons tooltip** — each automatically rejected frame now displays a small **?** badge next to the Reject/Keep button.
  - Hovering the badge shows a dark tooltip listing every threshold that was violated, with the frame's actual measured value and the configured limit (e.g. *FWHM 4.21 px  >  limit 3.50 px*).
  - The badge is hidden for accepted frames or manually overridden frames that are not auto-rejected.
- **Filter shown in preview metrics card** — the FITS filter name is now displayed directly below the date/time entry in the right-hand metrics panel of the preview window.
- **Score sort option** — frames can now be sorted by their quality score directly from the sort rule dropdown.
- **Revised quality score — rank-percentile algorithm** — the per-frame score (0–5) is now computed using weighted rank-percentile scoring instead of the previous ratio-to-average approach.
  - The best frame in any session always scores near **5.0**; the worst near **0.0** — the full scale is always used.
  - Each metric is ranked independently across all loaded frames; rank is converted to a [0, 1] percentile (1 = best in session). Weighted percentiles are then combined and scaled to 0–5.
  - Ties receive the average rank of their group.
  - Metric weights: FWHM ×3.0 · Eccentricity ×2.5 · Trail confidence ×2.0 · HFR ×1.5 · Stars ×1.5 · Mean background ×0.5.
  - Quality label thresholds updated to match the new distribution: **GOOD** ≥ 4.0 · **FAIR** ≥ 2.0 · **POOR** < 2.0.


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

## 1.0.0

Initial release



