# blink-o-mat

> **Subframe quality analyser and blinker for astrophotography FITS/XISF images.**

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/github/license/photon1503/blink-o-mat)](LICENSE.txt)
[![Changelog](https://img.shields.io/badge/changelog-CHANGELOG.md-blue)](CHANGELOG.md)

**blink-o-mat** helps you cull bad subframes from an astrophotography session before stacking.
It batch-loads FITS and XISF light frames, extracts quality metrics from each one (FWHM, HFR, eccentricity, star count, background, and more), scores and ranks them, and lets you step through the frames one by one — or blink them at speed — to spot satellites, clouds, tracking failures, and poor seeing at a glance.
Frames that don't meet your thresholds are automatically flagged; you can review, override, and move them to a reject folder in a single click.

---

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Build](#build)
- [Usage — GUI](#usage--gui)
- [Usage — Headless / CLI](#usage--headless--cli)
- [Headless Arguments](#headless-arguments)
- [Default Thresholds](#default-thresholds)
- [Quality Score](#quality-score)
- [Notes](#notes)
- [Changelog](#changelog)

---

## Features

### Loading & Session Workflow

| Capability | Details |
|---|---|
| Supported formats | `.fit`, `.fits`, `.xisf` (mono and OSC colour) |
| Loading strategy | First frame sync, remaining frames in parallel background tasks |
| Subfolder scanning | Optional recursive scan via the **Subfolders** checkbox; subfolder structure is preserved when moving rejected frames |
| Progress feedback | Status updates during scanning, loading, stretching, and preview refresh |
| Fault tolerance | Unreadable frames are skipped; loading continues |
| Persistence | Last-used input and rejected folder paths are saved between runs; main and preview window size/position are restored on next launch |
| **Save Session** | Saves all current settings, thresholds, sort rules, filter chip selection, and per-frame accepted/rejected state (including thumbnails and ROI previews) to a `.boms` file via the 💾 toolbar icon |
| **Load Session** | Restores a previously saved session instantly from a `.boms` file via the 📂 toolbar icon; the input folder is then rescanned and any new files not present in the session are loaded and appended automatically |

### Display & Preview

- **Dark, mid-gray UI** — low-glare theme for night-time use
- **OSC colour rendering** — FITS files from one-shot colour sensors are bilinear-debayered at load time and displayed in full colour across every preview surface (thumbnail, ROI, interactive window, full-resolution view)
- **Main frame list** — per-frame thumbnail, ROI preview, metric indicators, star rating, numeric score, and quality label at a glance
- **Dedicated preview window** for blinking and close inspection:
  - Default zoom-to-fit; `1:1`, zoom-in, zoom-out controls
  - Pan with **left mouse drag**, zoom with **Mouse Wheel**
  - Prev / Next navigation buttons and a **vertical frame slider** for direct scrubbing
  - **Accepted / Rejected filter chips** — list and slider update instantly when chip selection changes
  - Keyboard: `←` / `→` to step frames, `R` to toggle reject on the current frame
  - Zoom level and pan position are preserved while stepping between frames
  - Window size, position, and maximized state are remembered between runs
  - Cache coverage indicator beside the frame slider
  - Transient status message when a full-resolution image is loaded from disk
- **Right-click loupe** — visible while RMB is held; follows the cursor and displays local pixel stats: `X`, `Y`, `K` (raw ADU), `Min`, `Max`, `Mean`

### Stretch & Normalization

- Per-frame STF (Screen Transfer Function) controls: **Shadows**, **Midtones**, **Highlights**, **Target background**
- **Auto-stretch per frame** toggle — when enabled, STF parameters are computed independently for each frame instead of a fixed global stretch
- **Same-background normalization** — aligns all frames to a common target background level so the blink comparison is not confused by varying sky background; the target level is adjustable
- **Unlinked colour stretch for OSC frames** — shadows, midtones, and highlights are computed independently per channel (R, G, B) so colour balance is retained without manual adjustment
- STF and ROI controls are mirrored in both the main window and the preview window

### ROI (Region of Interest)

- ROI preview rendered alongside the full-frame thumbnail for every frame
- ROI bias presets: **Galaxy** · **Core** · **Starfield** — shifts the crop toward the region that matters most for your target type
- **Ctrl + left mouse drag** in the preview window to draw a custom square ROI; the selection is committed on mouse release and persists for the session
- Press **Escape** to cancel an in-progress ROI drag
- Automatic orientation normalization to align meridian-flipped and rotated frames

### Quality Metrics

Each frame is measured and displayed for:

| Metric | Notes |
|---|---|
| FWHM (px) | Full width at half maximum of stars |
| FWHM (arcsec) | Requires focal length and pixel size in FITS header |
| HFR (px) | Half-flux radius |
| Star count | |
| Eccentricity | Star elongation |
| SQM | Sky quality meter value — parsed from filename when present |
| Sky temperature | From FITS/XISF `SKYTEMP` keyword |
| Mean background | Average background level in ADU |
| Median | |
| MAD | Median absolute deviation |
| Min / Max | Including per-value occurrence counters |
| Satellite trail confidence | Heuristic detection, 0–100 % |

Each metric indicator is colour-coded (🟢 / 🟡 / 🔴) relative to the session average.
A weighted overall score (0.0–5.0) is shown as a numeric value, a star rating (★☆☆☆☆–★★★★★), and a quality label (**GOOD** / **FAIR** / **POOR**).

### Metadata Extraction

Focal length · Pixel size · Exposure date/time · Exposure length · Filter · Sky temperature · Bayer pattern (`BAYERPAT` / `XBAYROFF` / `YBAYROFF`)

The filter name is shown in the preview window metrics card, directly below the date/time entry.

### Rejection & Sorting

**Automatic rejection** is driven by configurable per-threshold sliders:

| Threshold | Direction | Default |
|---|---|---|
| FWHM | max | 8.0 px |
| HFR | max | 4.5 px |
| Eccentricity | max | 0.60 |
| Mean background | max | 2000 ADU |
| Sky temperature | max | 40 °C |
| SQM | min | 0 (disabled) |
| Stars | min | 0 (disabled) |
| Satellite trail confidence | min (0 = disabled) | 80 % |

Each slider shows a live **Rejects: N** counter that updates as you move it.

- **Rejection reasons tooltip** — automatically rejected frames display a small **?** badge next to the Reject/Keep button. Hovering the badge shows a list of every violated threshold with the frame's actual value vs. the configured limit (e.g. *FWHM 4.21 px  >  limit 3.50 px*).
- **Manual keep/reject override** — the per-frame Reject/Keep button overrides the automatic decision without erasing the underlying automatic state. Changing a threshold later does not clear a manual override.
- **Show Accepted / Show Rejected** toggle chips filter the visible list instantly.
- **Skip rejected in preview** — optionally skip automatically rejected frames when stepping through the preview window.
- **One-click Move Rejected** — relocates all currently rejected frames to the configured rejected folder, preserving subfolder structure; collisions are resolved by appending `_1`, `_2`, … suffixes.

**Sorting** is available on any metric column or by Score; multiple sort rules can be stacked.

### Satellite Trail Detection

- Heuristic trail detection built in; no external dependency required
- Trail confidence shown as 0–100 % in the per-frame metrics
- Trail overlay rendered on thumbnails when a trail is detected

### Performance

- Fully async processing — UI stays responsive throughout
- Batched collection updates and virtualized frame list for large sessions (hundreds of frames)
- Adaptive preview cache for low-latency frame-to-frame blinking
- Automatic eviction of cached full-resolution previews to cap memory use

---

## Requirements

- **Windows** (WPF-based UI; headless mode also Windows-only)
- **.NET 10** runtime (or SDK to build from source)

---

## Build

```powershell
dotnet build
```

---

## Usage — GUI

```powershell
dotnet run --project .\blink-o-mat.csproj
```

1. Click **Browse** next to the input folder and select the folder containing your light frames.
2. Optionally enable **Subfolders** to scan recursively.
3. Wait for loading to complete (progress shown in the status bar).
4. Adjust the STF stretch, ROI bias, and automatic rejection thresholds as needed.
5. Open the **Preview window** and blink through frames; press `R` to reject individual frames.
6. Click **Move Rejected** to relocate all rejected frames to the configured rejected folder.
7. Click the **💾 Save Session** icon (top-left) to save all settings, thresholds, sort rules, and per-frame state to a `.boms` file.
8. Next time, click **📂 Load Session** to restore the session instantly; any new files added to the folder since the last save are detected and appended automatically.

---

## Usage — Headless / CLI

Run without a UI — useful for scripted pipelines or CI workflows.

```powershell
dotnet run --project .\blink-o-mat.csproj -- `
  --headless `
  --input    "D:\lights" `
  --rejected "D:\lights\rejected" `
  --max-fwhm 5.5 `
  --max-hfr  3.5 `
  --max-ecc  0.55 `
  --max-bg   1800
```

All threshold arguments are optional; omitting one keeps its default value.

---

## Headless Arguments

| Argument | Description | Default |
|---|---|---|
| `--headless` | Run without UI (required) | — |
| `--input <folder>` | Source frames folder | — |
| `--rejected <folder>` | Destination folder for rejected frames | — |
| `--max-fwhm <value>` | Reject frames with FWHM above this value (px) | `8.0` |
| `--max-hfr <value>` | Reject frames with HFR above this value (px) | `4.5` |
| `--max-ecc <value>` | Reject frames with eccentricity above this value | `0.6` |
| `--max-bg <value>` | Reject frames with mean background above this value (ADU) | `2000` |
| `--allow-trails` | Disable satellite trail rejection (sets confidence threshold to 0) | trails rejected at ≥ 80 % |

> **Note:** `--min-sqm`, `--min-stars`, `--max-sky-temp`, and `--min-trail-confidence` are GUI-only thresholds not yet exposed as CLI arguments.

---

## Default Thresholds

| Threshold | Default | Notes |
|---|---|---|
| Max FWHM | 8.0 px | |
| Max HFR | 4.5 px | |
| Max eccentricity | 0.60 | |
| Max mean background | 2000 ADU | |
| Max sky temperature | 40 °C | Requires `SKYTEMP` FITS keyword |
| Min SQM | 0 | 0 = disabled; parsed from filename |
| Min stars | 0 | 0 = disabled |
| Min satellite trail confidence | 80 % | 0 = disabled |

---

## Quality Score

Every frame receives a quality score from **0.0** (worst in session) to **5.0** (best in session), displayed as a numeric value, a star rating (★☆☆☆☆ – ★★★★★), and a colour-coded label:

| Label | Score range | Meaning |
|-------|-------------|---------|
| **GOOD** | ≥ 4.0 | Top tier of the session |
| **FAIR** | 2.0 – 3.9 | Average quality |
| **POOR** | < 2.0 | Bottom tier of the session |

### Algorithm — weighted rank-percentile

The score is **relative within the session**, not absolute. This design ensures the full 0–5 scale is always used, regardless of whether your seeing was excellent or poor on a given night.

**Step 1 — rank each metric independently**

All frames are sorted from best to worst for each metric independently. Ties receive the average rank of their group.

**Step 2 — convert rank to [0, 1] percentile**

```
percentile = 1 − (rank / (N − 1))
```

`1.0` = best frame in the session · `0.0` = worst frame · `0.5` = median

**Step 3 — weighted combination**

| Metric | Weight | Rationale |
|--------|--------|-----------|
| FWHM | **3.0** | Seeing quality / sharpness — most impactful on resolved detail |
| Eccentricity | **2.5** | Star roundness — reflects tracking errors, collimation, and sensor tilt |
| Satellite trail confidence | **2.0** | Contamination — frames with detected trails score lower |
| HFR | **1.5** | Half-flux radius — correlated with FWHM, provides secondary confirmation |
| Star count | **1.5** | Cloud cover / transparency proxy |
| Mean background | **0.5** | Light pollution / gradient level — affects SNR but rarely sufficient alone to reject |

**Step 4 — scale to 0–5**

```
score = (Σ (percentile × weight) / Σ weights) × 5
```

> **Transparency note:** the score reflects *relative* quality within the current session.
> The same physical frame can receive a different score when more or fewer frames are loaded —
> just as a podium position depends on who else is competing.
> Scores are not comparable across different sessions.

---

## Notes

- All metrics, trail detection, ROI selection, scoring, and orientation normalization are **heuristic** and optimized for speed rather than scientific precision. They are intended for fast subframe culling, not final calibration.
- Loupe pixel statistics are derived from the currently rendered (stretched) preview image, not raw sensor data.
- Manual keep/reject overrides are independent of automatic thresholds — changing a threshold after a manual override does not clear the override.
- Move operations rename files on collision (`_1`, `_2`, …) to prevent accidental overwrites.

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for a full history of changes.
