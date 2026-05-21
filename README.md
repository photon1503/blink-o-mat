# Rejector

> **Fast subframe quality sorter and blinker for astronomical FITS/XISF images.**

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/github/license/photon1503/blink-o-mat)](LICENSE.txt)
[![Changelog](https://img.shields.io/badge/changelog-CHANGELOG.md-blue)](CHANGELOG.md)

Rejector is a Windows desktop tool for rapidly reviewing and culling astrophotography light frames. It loads FITS and XISF subframes, measures quality metrics on each frame, lets you blink through them side-by-side, and moves rejects to a separate folder — all with a dark-room-friendly UI.

---

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Build](#build)
- [Usage — GUI](#usage--gui)
- [Usage — Headless](#usage--headless)
- [Headless Arguments](#headless-arguments)
- [Default Thresholds](#default-thresholds)
- [Notes](#notes)
- [Changelog](#changelog)

---

## Features

### Loading & Session Workflow

| Capability | Details |
|---|---|
| Supported formats | `.fit`, `.fits`, `.xisf` |
| Loading strategy | First frame sync, remaining frames in parallel background tasks |
| Subfolder scanning | Optional recursive scan via the **Subfolders** checkbox; subfolder structure is preserved when moving rejected frames |
| Progress feedback | Status updates during scanning, loading, stretching, and preview refresh |
| Fault tolerance | Unreadable frames are skipped; loading continues |
| Persistence | Last-used input and rejected folder paths are remembered between runs |

### Display & Preview

- **Dark, mid-gray UI** — low-glare theme for night-time use
- **Main list** — per-frame thumbnail, ROI preview, star rating, and numeric score at a glance
- **Dedicated preview window** for blinking and close inspection:
  - Default zoom-to-fit; `1:1`, zoom-in, zoom-out controls
  - Pan with **left mouse drag**, zoom with **Ctrl + Mouse Wheel**
  - Prev / Next buttons and a **vertical frame slider** for direct scrubbing
  - **Accepted / Rejected filter chips** — list and slider update instantly when filters change
  - Keyboard navigation: `←` / `→` to step, `R` to toggle reject
  - Zoom level and pan position are preserved while stepping between frames
  - Cache coverage indicator beside the slider
  - Transient status message when a full-resolution frame is loaded from disk
- **Right-click loupe** — visible while RMB is held, follows the cursor, shows local pixel stats (`X`, `Y`, `K`, `Min`, `Max`, `Mean`)

### Stretch & Normalization

- Global stretch factor: `0.25×` to `5.0×`
- Stretch modes: **Default** · **NinaStyle**
- **Same-background normalization** — aligns all frames to a common target background level (enabled by default, target level adjustable)
- Stretch and ROI controls are mirrored in both the main window and the preview window

### ROI & Orientation

- ROI preview alongside a full-frame preview for every frame
- ROI bias presets: **Galaxy** · **Core** · **Starfield**
- **Ctrl+Click** in the preview window to set a manual ROI (persists for the session)
- Automatic orientation normalization to align meridian-flipped and rotated frames

### Quality Metrics

Each frame is measured for:

| Metric | Notes |
|---|---|
| FWHM (px) | |
| FWHM (arcsec) | When focal length and pixel size are available |
| HFR | |
| Star count | |
| Eccentricity | |
| SQM | Parsed from filename when present |
| Sky temperature | From FITS/XISF `SKYTEMP` keyword |
| Mean background | |
| Median | |
| MAD | Median absolute deviation |
| Min / Max | Including per-value occurrence counters |
| Satellite trail flag | Heuristic detection |

The UI color-codes each metric (🟢 / 🟡 / 🔴) relative to the session, shows a weighted overall score as a star rating, and labels each frame **GOOD**, **FAIR**, or **POOR**.

### Metadata Extraction

Focal length · Pixel size · Exposure date/time · Exposure length · Filter · Sky temperature

### Rejection & Sorting

- **Automatic rejection** with per-threshold sliders:
  - Max FWHM · Min SQM · Max sky temperature · Max HFR · Max eccentricity · Max mean background · Min stars · Min satellite trail confidence (0 = disabled, 1–100)
- **Manual keep/reject override** per frame — does not erase the underlying automatic state
- Color-coded keep/reject labels in the preview window
- **One-click move** of all rejected frames to the rejected folder
- Collision-safe rename on move to prevent overwrite

### Satellite Trail Detection

- Heuristic trail detection built in
- Trail state visible in per-frame metrics
- Trail overlay rendered on thumbnails when a trail is detected

### Performance

- Fully async processing — UI stays responsive throughout
- Batched collection updates and virtualized frame list for large sessions
- Adaptive preview cache for low-latency frame-to-frame blinking
- Reduced redraw and allocation overhead during fast preview stepping
- Automatic eviction of cached full-resolution previews to cap memory use

### Headless / CLI Mode

Automated filtering and rejection without launching the UI — scriptable from any shell or CI pipeline.

---

## Requirements

- **Windows** (WPF-based UI; headless mode also Windows-only)
- **.NET 10** SDK or runtime

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

1. Click **Browse** and select the folder containing your light frames.
2. Wait for the session to finish loading (progress shown in the status bar).
3. Adjust stretch, ROI bias, and rejection thresholds as needed.
4. Blink through frames in the preview window; press `R` to reject individual frames.
5. Click **Move Rejected** to relocate all rejected frames to the configured rejected folder.

---

## Usage — Headless

```powershell
dotnet run --project .\blink-o-mat.csproj -- --headless --input "D:\lights" --rejected "D:\lights\rejected"
```

All rejection thresholds are optional; omitting one leaves it at its default value.

### Headless Arguments

| Argument | Description | Default |
|---|---|---|
| `--headless` | Run without UI (required for headless mode) | — |
| `--input <folder>` | Source frames folder | — |
| `--rejected <folder>` | Destination folder for rejected frames | — |
| `--max-fwhm <value>` | Reject frames with FWHM above this value | `8.0` |
| `--max-hfr <value>` | Reject frames with HFR above this value | `4.5` |
| `--max-ecc <value>` | Reject frames with eccentricity above this value | `0.6` |
| `--max-bg <value>` | Reject frames with mean background above this value | `2000` |
| `--min-trail-confidence <value>` | Reject frames whose trail confidence meets or exceeds this value (0 = disabled) | `80` |

### Example

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

---

## Default Thresholds

| Threshold | Default |
|---|---|
| Max FWHM | 8.0 px |
| Max HFR | 4.5 |
| Max eccentricity | 0.60 |
| Max mean background | 2000 |
| Max sky temperature | 40 °C |
| Min SQM | 0 (disabled) |
| Min stars | 0 (disabled) |
| Min satellite trail confidence | 80 |

---

## Notes

- All metrics, trail detection, ROI selection, scoring, and orientation normalization are **heuristic** and optimized for speed rather than scientific precision. They are intended for fast subframe culling, not final calibration.
- Loupe pixel statistics are derived from the currently rendered (stretched) preview image, not the raw sensor data.
- Manual keep/reject overrides are independent of automatic thresholds — changing a threshold after a manual override does not clear the override.
- Move operations rename files on collision (`_1`, `_2`, …) to prevent accidental overwrites.

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for a full history of changes.
