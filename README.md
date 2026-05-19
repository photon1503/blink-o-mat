# blink-o-mat

Blinker and subframe quality sorter for astronomical FITS/XISF images.

Designed for fast proofing of weak astro subframes on Windows with a dark-room-friendly UI, fast blinking workflow, ROI inspection, and automatic rejection support.

## Features

### File loading and session workflow

- Browse a folder of light frames (`.fit`, `.fits`, `.xisf`)
- Native .NET FITS/XISF loading on Windows
- Async first-frame loading followed by parallel background processing for the remaining frames
- Progress and status updates during scanning, loading, stretching, and preview refresh operations
- Early thumbnail/ROI availability while the session is still loading
- Skips unreadable frames and continues loading the rest of the session
- Remembers the last used input and rejected folders between runs

### Display and preview

- Dark, mid-gray UI theme for low-glare night-time use
- Main list with per-frame full thumbnail and ROI preview
- Active preview frame is highlighted in the main list
- Per-frame score summary in the main list with stars and numeric rating
- Dedicated frame preview window for blinking and inspection
  - Default zoom-to-fit
  - Prev/Next buttons
	- Direct frame scrubbing with a vertical frame slider
  - Keyboard navigation with `Left` / `Right`
  - Reject toggle with `R`
  - Optional `Skip rejected when blinking`
  - Keeps zoom level and pan position while stepping between frames
  - Left mouse drag pans the image
  - `Ctrl + Mouse Wheel` zooms
  - `1:1`, fit, zoom-in, and zoom-out controls
  - Shows preview cache coverage beside the frame slider
  - Shows transient status text when a full frame is loaded from disk
- Right-click loupe in the preview window
  - Visible only while RMB is held down
  - Follows the mouse
  - Shows local pixel stats (`X`, `Y`, `K`, `Min`, `Max`, `Mean`)

### Stretching and normalization

- Global stretch control (`0.25` to `5.0`)
- Stretch mode selector
  - `Default`
  - `NinaStyle`
- Global same-background normalization option
  - `Use same target background for all frames`
  - Adjustable target background slider
  - Enabled by default

### ROI and orientation handling

- ROI preview plus full-frame preview for every frame
- ROI bias modes:
  - `Galaxy`
  - `Core`
  - `Starfield`
- Manual ROI override with `Ctrl+Click` in the preview window
  - Persists for the currently loaded set
- Stretch/ROI controls are available both in the main window and in the preview window
- Automatic orientation normalization to align meridian-flipped / rotated frames

### Per-frame quality metrics

- FWHM (px)
- FWHM (arcsec), when focal length and pixel size are available
- SQM, when available from the filename
- Sky temperature, when available from FITS/XISF metadata
- HFR
- Star count
- Eccentricity
- Mean background
- Median
- MAD (median absolute deviation)
- Min and Max, including occurrence counters
- Possible satellite trail flag

The UI also includes:

- Colored red/yellow/green metric indicators relative to the loaded session
- Weighted overall score with star display
- Quality labeling (`GOOD`, `FAIR`, `POOR`) and score-driven visual emphasis in the preview details

### Metadata extraction

- Focal length
- Pixel size
- Exposure date/time
- Exposure length
- Filter
- Sky temperature (`SKYTEMP`) from FITS/XISF metadata, when present

### Rejection and sorting

- Automatic rejection using adjustable thresholds
  - Max FWHM
	- Min SQM
  - Max sky temperature
  - Max HFR
  - Max eccentricity
  - Max mean background
  - Min stars
  - Optional reject on possible satellite trails
- Manual keep/reject override per frame without losing the underlying automatic rejection state
- Preview window keep/reject action uses clear stateful labeling and color coding
- One-click move rejected subframes to another folder
- Collision-safe renaming during move operations

### Satellite trail detection

- Heuristic satellite trail detection
- Trail state shown in the metrics
- Trail overlay shown on thumbnails when detected

### Performance-oriented behavior

- Async processing to keep the UI responsive
- Batched collection updates and virtualized frame list
- Adaptive preview caching for faster frame-to-frame blinking
- Automatic trimming of cached full-resolution preview images to stay memory-conscious
- Memory-conscious frame handling for large sessions

### Headless mode

- CLI/headless mode for automated filtering and moving of rejected frames

## Requirements

- Windows
- .NET 10 SDK/runtime

## Build

From the solution root:

```powershell
dotnet build
```

## Run (GUI)

```powershell
dotnet run --project .\blink-o-mat.csproj
```

## Run (Headless)

```powershell
dotnet run --project .\blink-o-mat.csproj -- --headless --input "D:\lights" --rejected "D:\lights\rejected"
```

### Headless arguments

- `--headless` : run without UI
- `--input <folder>` : source frames folder
- `--rejected <folder>` : destination for rejected frames
- `--max-fwhm <value>` : rejection threshold
- `--max-hfr <value>` : rejection threshold
- `--max-ecc <value>` : rejection threshold
- `--max-bg <value>` : rejection threshold
- `--allow-trails` : do not reject on detected satellite trails

## Example

```powershell
dotnet run --project .\blink-o-mat.csproj -- --headless --input "D:\lights" --rejected "D:\lights\rejected" --max-fwhm 5.5 --max-hfr 3.5 --max-ecc 0.55 --max-bg 1800
```

## Notes

- Metrics, trail detection, ROI selection, scoring, and orientation normalization are heuristic and intended for fast subframe sorting.
- The preview loupe statistics are based on the currently rendered preview image.
- Manual keep/reject overrides affect the effective rejection state shown in the UI and used for move operations.
- Rejected-frame move operations rename on collision to avoid overwrite.
