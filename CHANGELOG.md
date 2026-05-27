# Changelog

All notable changes to this project will be documented in this file.

## 1.0.25

### Changed
- **Smarter metric indicator colours in mixed-filter sessions.** FWHM, HFR and Eccentricity reflect seeing, optics and tracking — they are filter-independent, so their chip colours are now compared globally across the whole session. Stars and Mean Background vary by filter (L frames detect far more stars than RGB), so those two chips continue to be compared within each filter group only. In a typical LRGB session this means: a blurry R frame correctly shows a red FWHM chip relative to all frames, while its Stars chip is judged only against other R frames.
- **Redesigned top bar and Session card for better workflow discoverability.** The top bar now prominently features:
  - **"Open Folder" button** (green, far left) — opens a dropdown panel to configure input folder, rejected folder, subfolders option, and **Load Frames**. This is the primary entry point.
  - **Save and Load session buttons** (dark blue icons+text, left side) — quick access to session save/load workflow.
  - **Reject button** (red, far right) — batch frame rejection with no overlap.
  - The Session card in the left panel is now simplified: the Save/Load icons and Folders button have been removed and consolidated into the "Open Folder" panel, keeping the Session card focused on session statistics and metrics.

## 1.0.24

### Performance
- **Faster application startup.** Reduced the update check HTTP timeout from 10 seconds to 3 seconds and deferred the check by 100ms to allow the main window to appear immediately. The app now starts near-instantly instead of waiting up to 10 seconds for the GitHub API response.

### Fixed
- **Curvature preview no longer turns black when navigating frames.** In the frame preview, when **Curvature** view is enabled, moving to the next/previous image could occasionally leave the canvas black due to an image-refresh timing gap. Curvature rendering now stays synchronized with the active preview image during navigation.
- **Preview no longer jumps to the first frame after reject with visibility filters.** In frame preview, when a reject/keep toggle makes the current frame leave the visible set (for example: reject while only accepted frames are shown), the selection now stays at the same relative slider position and continues to the expected next visible frame instead of jumping to frame 1.
- **"What's new?" dialog no longer crashes when opening release notes.** Removed a broken external markdown style resource reference that could throw a XAML parse exception on startup of the release-notes window.


### Changed
- **Debug update banner now shows live GitHub release notes.** The debug shortcut (`Ctrl+Alt+W`) now fetches the latest release tag and markdown body from GitHub instead of showing a static placeholder message, with a local fallback when offline.

## 1.0.23

### Changed
- **Revised FWHM measurement.** Per-star FWHM is now derived from a flux-weighted Gaussian fit of `ln(F)` vs `r²` over the inner FWHM core, instead of binned half-maximum interpolation on the full star aperture. The estimator recenters on the intensity-weighted centroid, ignores saturated cores and noisy wings, and uses a tighter measurement aperture, which removes a systematic over-estimation of FWHM that was most visible on undersampled and bright stars. Single-star measurements now agree with PixInsight's PSF fitter to within measurement noise.
- **Center-weighted FWHM aggregation.** The per-frame FWHM, HFR and eccentricity are now produced by a *radially weighted median* of the surviving stars instead of a plain median. The weight of each star depends only on its distance `r` from the image center, normalized by the half-diagonal `d`:
  - stars in the outer 25% of the field (`r/d ≥ 0.75`) are excluded entirely;
  - inside that cutoff, the normalized radius `r_n = (r/d) / 0.75` is mapped through `w(r_n) = cos⁴(r_n · π/2)`, giving weight 1.0 at the center, ~0.7 at quarter-radius, ~0.25 at half-radius, and ~0 at the cutoff;
  - the weighted median is the value at which the cumulative weight first reaches half of the total weight, i.e. `argmin_k { Σᵢ≤k wᵢ ≥ ½ Σ wᵢ }` after sorting stars by the metric being aggregated.

  This mirrors how CCDInspector reports a "representative" image FWHM that is not skewed by field curvature at the periphery, as long as the majority of stars are not on the periphery. It removes the bias from elongated edge stars (coma/field curvature) without discarding their existence — they still appear in the star count and in the debug overlay, they just no longer drag the median up. Aggregate FWHM values now agree with CCDInspector to within ~0.05″ on the test data set, and also track PixInsight's SubframeSelector across a full session: relative ranking of frames matches, and absolute FWHM values are within measurement noise.
- The per-frame measured-star cap has been raised from 500 to 2000 so the weighted median has a larger, more representative sample from the central field. Measurement is parallel and per-star cost is small, so this has no measurable impact on frame-loading throughput.
- **Stellar-only detections for FWHM.** The Gaussian-core fit now also gates detections on a weighted coefficient of determination (R² ≥ 0.75) and a plausible point-spread σ in the range 0.4–6 px (FWHM ~1–14 px). For a real star, `ln(F)` is essentially linear in `r²` so R² is very close to 1, while extended sources such as galaxy nebulosity, dust knots and hot-pixel clusters produce a markedly worse linear fit and are rejected. Stars that fail the gate are now dropped entirely instead of falling back to the old half-maximum / second-moment estimators, which used to happily measure non-stellar bright patches in galaxy and nebula fields and inflate the per-frame FWHM. The per-star measurement aperture was retuned (radius 7, annulus 9–13 px) so larger / out-of-focus stars retain enough samples above half-peak for the fit to remain robust.

### Added
- **FWHM debug overlay (Ctrl+F).** The frame preview now has a toggleable debug overlay that draws a ring around every star that contributed to the FWHM/HFR statistics, labels each ring with its measured FWHM in pixels and arcseconds (e.g. `4.05 (1.21")`), and shows a summary readout (star count and median FWHM in px and arcsec) in the upper-left of the image. Useful for spotting why a particular frame's FWHM looks high — saturated stars, faint detections or trailed sources are now immediately visible. Toggle with Ctrl+F.
- **Curvature heatmap view.** A new **Curvature** toggle in the frame preview header (next to the zoom level) replaces the raw image with a  FWHM heatmap, so you can see at a glance how sharpness varies across the field. The center is marked with a small white target, and the overlay shows Min/Max/Mean FWHM, the curvature percentage (corners vs. center) and how many stars contributed. Hover anywhere on the heatmap to see the FWHM at that spot in pixels and arcseconds.

## 1.0.22

### Performance
- **Memory-aware frame loading parallelism.** Background frame loading no longer blindly uses all CPU cores after the first successful frame. The app now measures that first frame's true in-memory size, checks currently free physical RAM, reserves safety headroom, and derives a bounded worker count from the remaining memory budget. This keeps loading fast while significantly reducing memory pressure and GC churn on large sessions.


### Changed
- Frame loading now skips non-LIGHT images (BIAS, Dark, Flat)

### Added
 - Added option to select multiple input folders
	
## 1.0.19

### Added
- **"What's new?" link in the update banner.** The *New version available* popup now includes a **What's new?** link that opens a dedicated dialog showing the GitHub release notes with their markdown formatting preserved, so you can read the release summary without leaving the app.


- **Frame alignment.** When loading frames, the app now also detects an integer per-frame pixel shift in addition to the existing 180° orientation check, and remembers it for each frame. Full-frame previews, list thumbnails and ROI crops are translated by that shift so all images line up across the session, making it much easier to spot blinking, satellite trails, focus changes and field rotation at a glance. The alignment is quick (integer pixels, no subpixel interpolation) and has negligible runtime cost.
- **Align toggle in the frame preview.** A new "Align" chip in the preview window's visibility toolbar lets you switch alignment on or off on the fly. Turning it off shows each frame in its raw, un-shifted position, which is useful for inspecting actual guiding/centering behavior. Toggling rebuilds the preview canvas immediately and refreshes list thumbnails / ROI in the background.
- **Align toggle on the main window.** The same "Align" toggle is also available in the main window's visibility toolbar next to Accepted/Rejected, so alignment can be switched without opening the frame preview. Both toggles stay in sync.
- **Persisted alignment in sessions.** The detected per-frame shift is now written into `.boms` session files alongside the rotate-180 flag, so reopening a session preserves the alignment exactly without re-detecting.
- **Alignment is off by default.** Frames load and display in their original position; enable the new "Align" toggle in the main window or frame preview to line them up using the detected shifts.


### Changed
- **Update banner waits for the actual installer.** The app no longer shows the *New version available* popup immediately when a new Git tag appears on GitHub. It now waits until the final downloadable `.exe` asset has been published, avoiding false-early notifications while the release build is still being generated and uploaded.

## 1.0.18


### Added
- **Color-coded filter chips.** Filters in the toolbar are now color-coded by type: red for Ha and R, teal for OIII, gold for SII, white for L (Luminance), green for G, and blue for B. Unknown filter names (such as IRCUT) keep a neutral color. The chip label is shortened to the canonical short name (Ha, Oiii, Sii, L, R, G, B) so the same filter looks the same no matter how it was named in the FITS/XISF header (e.g. *Halpha*, *H_a*, *Lum*, *Red* all collapse to a single recognizable chip).
- **Filter indicator dot in the frame list.** Each frame row now shows a small colored dot in front of its filter name, using the same palette as the chips, so you can scan the list and tell narrowband from broadband at a glance.
- **Grouped rejection scope dropdown.** The filter-scope dropdown in the *Automatic rejection* panel now groups filters into **Narrowband** (Ha, Oiii, Sii), **LRGB** (L, R, G, B), and **Other** (anything that doesn't match), each with its own colored swatch. This makes it much faster to select, say, "all narrowband" or "just L" when tuning thresholds.

### Changed
- **Much more accurate star count.** Star detection now runs on the full-resolution image instead of a 1536-px downsampled buffer, with a 3σ detection threshold, ≥3-of-8 bright-neighbour support, strict 3×3 local-max test, and a 4-px spatial-hash deduplication grid. On large sensors (e.g. IMX411) the reported `StarCount` jumps from a few hundred to several thousand, in line with NINA/Hocus Focus. FWHM/HFR/eccentricity statistics are still measured on the brightest 500 stars to keep the cost bounded.
- **Filter detection is forgiving of naming.** Filter classification now uses the first letter of the filter name, so common variations (`Halpha`, `H-alpha`, `OIII_3nm`, `Sii_5nm`, `Lum`, `Red`, `Green`, `Blue`) are all recognized automatically. Anything that doesn't match a known letter is shown unchanged.


### Performance
- **Faster star detection on dense fields.** O(N²) duplicate suppression was replaced by a spatial-hash grid, and candidate collection switched from a `ConcurrentBag` + `OrderByDescending` to per-stripe parallel lists with a single in-place sort. Detection on 150 MP frames is materially faster despite finding 10×+ more stars.

---
## 1.0.17

### Added
- **Per-filter automatic rejection.** Each filter (Ha, OIII, L, R, G, B, …) now has its own independent set of rejection thresholds. Sessions with mixed filters are no longer forced to share one global setting — Ha frames can be judged against Ha frames, OIII against OIII, and so on.
- **Filter scope selector.** A new dropdown next to the *Automatic rejection* header lets you pick which filter group(s) the sliders apply to. It is multi-select: tick one filter to tune that filter alone, tick several to edit them together, or leave all ticked to apply changes to every filter at once. The button label shows the current scope (e.g. *All filters*, *Ha*, or *3 filters*).
- **Reset thresholds button.** A small ⟳ button next to the scope selector restores the sliders for the currently selected filter group(s) to the maxima/minima of the loaded frames — the same "everything passes" starting point you get right after loading, but scoped to just the filters you choose.

### Changed
- **Quality score is now computed per filter.** The 0–5 star rating used to compare every frame against every other frame in the session, which made narrowband frames look artificially worse than broadband ones (or vice-versa). Scores are now ranked within each filter group, so each frame is judged against its true peers.
- **Remembered per-filter settings.** Your slider positions for each filter are saved in the session file and restored when you reload it, so you don't have to re-tune Ha thresholds every time you reopen a project.

### Fixed
- **UI no longer freezes while loading large folders.** Loading 100+ frames previously made the app appear hung: the first image showed up, then nothing seemed to happen for a long time, and the list suddenly filled in one burst at the end. Frames now appear in the list as soon as each one finishes, the status bar updates continuously, and the window stays responsive throughout the load.

### Changed
- **Clearer loading progress.** The status bar now shows a single live line of the form `Loading 47/120 • active: 32 • current: NGC7000_L_001.xisf • skipped: 1`, and the progress bar advances steadily from 0 to 100% as frames complete, instead of jumping at the very end.

### Performance
- **Faster XISF loading.** XISF files are now decoded directly from the source buffer using typed pixel reads (with optimized fast paths for the common 16-bit and 32-bit float formats, both mono and color). This removes a full duplicate copy of every image's pixel data and noticeably reduces the time and memory needed to load large XISF sessions.

---
## 1.0.16

### Added
- **Interactive ROI overlay in the preview window.** A new "Show / edit ROI" toggle in the ROI section draws the current ROI as a golden dotted square directly over the preview image. While visible, you can drag the rectangle to move it and drag any corner to resize it (locked to a 1:1 pixel-square aspect). Releasing the mouse — or right-clicking the ROI — applies it to every frame and immediately regenerates all ROI thumbnails. The static keyboard-shortcut hint has been removed since the same actions live in the shortcuts list.

### Changed
- **Automatic ROI selection is now center-aware and content-aware.** The ROI finder now prefers high-contrast, extended structure near the image center, so it is far less likely to lock onto empty background or a single bright star.
- **Better target detection for galaxies, globular clusters, and nebulae.** ROI scoring now favors regions that combine structure and local contrast, which improves subject selection across common deep-sky object types.
- **ROI size now adapts to image scale.** The automatic ROI is sized in preview-pixel terms (~2.5× downsample into the 160 px thumbnail), so stars and fine structure remain visible in the list preview regardless of sampling. 

---
## 1.0.15

### Fixed
- **Shadows / Midtones / Highlights sliders now actually change the preview.** Previously these three sliders had almost no visible effect because the preview kept re-applying its automatic stretch on every render. Moving any of the three sliders now switches the preview into manual stretch mode so your adjustments are applied immediately. The Target Background slider still drives the automatic stretch as before.
- **Pre-cached frames now match the order of the vertical slider.** The pre-ahead cache was warming up whatever frames happened to load next from disk, which often didn't match what you'd see next when scrolling. The next/previous cached frame now always corresponds to the next/previous frame on the slider, and changing the sort order immediately re-warms the cache against the new order.

### Performance
- **Faster frame loading.** FITS files are now read with large sequential I/O, and pixel decoding, OSC debayering, star measurement, and image resampling all run in parallel across CPU cores. Disk and CPU utilization during loading is now much closer to the hardware's capability.
- **Realtime STF sliders.** Moving the Shadows / Midtones / Highlights / Target Background sliders no longer reloads the file from disk on every tick — the preview updates live as you drag, and snaps back to full resolution as soon as you release the slider.
- **Faster preview pre-caching.** Neighbouring frames are now warmed nearest-first with two workers running in parallel, roughly halving the time it takes to fill the cache around the current frame. At least one neighbour is always scheduled for caching, even when free memory is tight.
- **Faster thumbnail and ROI regeneration.** When you change the stretch, ROI, or target background, all thumbnails are now rebuilt in parallel across CPU cores instead of one at a time.
- **Lower memory pressure during loading.** Reduced redundant pixel sampling and buffer allocations during frame analysis, so loading large sessions creates less GC churn.

### Changed
- **Default STF target background lowered to 0.15.** The automatic stretch now produces a darker, less aggressive background by default, closer to a typical PixInsight STF preset. You can still adjust this with the Target Background slider.
- **Changing the STF target background refreshes everything.** Adjusting `Target background` in the main window now immediately regenerates the full-resolution preview and all thumbnails to match.
- **New ROI applies instantly.** Drawing a new manual ROI in the preview window now regenerates the per-frame ROI thumbnails right away, even with the preview window still open.

---
## 1.0.14
- Added performance indicators (CPU, RAM, Disk and Network).
- Rejected frames will now be removed from the current list after moving the actual files.

---
## 1.0.10

### Added
- **Auto-update check** — on startup, Rejector silently queries the GitHub Releases API and compares the latest release tag to the running assembly version.
  - When a newer version is available, a non-intrusive green banner appears between the toolbar and the main content area.
  - The banner shows the available version number. Clicking the message or the **View release** button opens the GitHub releases page in the default browser.
  - The banner can be dismissed with the **✕** button and will not reappear until the next launch.
  - The check is fire-and-forget: it runs asynchronously in the background and never blocks the UI or throws an error if the network is unavailable.


---
## 1.0.9

### Changed
- **Per-filter reject move** — the *Move Rejected* confirmation dialog now shows the rejected frame count broken down by filter when a session contains frames from multiple filters.
  - Each filter is shown as a toggleable chip (e.g. `Ha  (12)`). Deselecting a chip excludes that filter's frames from the move.
  - The *Frames to move* count updates live as chips are toggled. The *Proceed* button is disabled when zero frames would be moved.
  - When only one filter is present the chip panel is hidden and the dialog behaves exactly as before.
- **ROI center detection rewritten — blur-then-peak algorithm**
- **ROI bias presets removed** — the *Galaxy / Core / Starfield* bias selector has been removed from both the main window and the preview window. The new center-focused algorithm does not require a bias hint and produces a better result across all target types without user input. The `RoiBias` field is no longer written to or read from session files.

---
## 1.0.8

### Changed
- **"Reject" button moved to the top toolbar** — the *One-click move rejected subframes* action is now a **Reject** button placed directly next to the *Load Frames* button in the top bar. The *Actions* card in the left sidebar has been removed.
- **Confirmation dialog before moving frames** — clicking *Reject* now opens a summary dialog that shows how many frames will be moved and the destination folder path. The action only proceeds after the user confirms with the **Proceed** button; **Cancel** aborts without making any changes.

---
## 1.0.7

### Added
- **Extended frame summary panel** — the *Frame summary* card in the left sidebar now shows much richer at-a-glance statistics:
  - **Accepted / rejected ratio bar** — a compact two-tone horizontal bar (green = accepted, red = rejected) gives an instant visual read of session health.
  - **Total integration time** — the sum of all frame exposure times is displayed next to the rejected count (e.g. `total: 3.2 h`). If exposure data is unavailable the field is hidden.
  - **Accepted integration time** — the integration time contributed by accepted frames only is shown inline with the accepted count (e.g. `2.4 h`).
  - **Per-filter breakdown** — when filter chips are active, each filter gets its own compact row showing: filter name + accepted integration time, a mini ratio bar, and an accepted/total/% summary line. Rows update live as frames are accepted or rejected.

### Fixed
- **Frame summary counts unaffected by visibility toggles** — the *Total*, *Accepted*, and *Rejected* counts in the summary panel now always reflect all frames that match the active filter chip selection, regardless of whether the *Accepted* or *Rejected* visibility toggle is switched off. The toggles only control what is shown in the list; they no longer skew the numbers.

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
  - Saves: input/rejected folder paths, subfolder toggle, all rejection thresholds, STF settings, manual ROI rectangle, sort rules, filter chip selection, and accepted/rejected state per frame.
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



