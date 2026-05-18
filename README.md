# blink-o-mat

Blinker and subframe quality sorter for astronomical FITS/XISF images.

Designed for fast proofing of weak astro subframes on Windows with a dark-room-friendly UI, fast blinking workflow, ROI inspection, and automatic rejection support.

## Features

### File loading and session workflow

- Browse a folder of light frames (`.fit`, `.fits`, `.xisf`)
- Native .NET FITS/XISF loading on Windows
  - FITS via the built-in parser in `RustafitsService`
  - XISF via `XisfSharp`
- Async and batched frame loading with progress/status updates
- Early thumbnail/ROI availability while the session is still loading
- Remembers the last used input and rejected folders between runs

### Display and preview

- Dark, mid-gray UI theme for low-glare night-time use
- Main list with per-frame thumbnail and ROI preview
- Dedicated frame preview window for blinking and inspection
  - Default zoom-to-fit
  - Prev/Next buttons
  - Keyboard navigation with `Left` / `Right`
  - Reject toggle with `R`
  - Optional `Skip rejected when blinking`
  - Keeps zoom level and pan position while stepping between frames
  - Left mouse drag pans the image
  - `Ctrl + Mouse Wheel` zooms
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
- Automatic orientation normalization to align meridian-flipped / rotated frames

### Per-frame quality metrics

- FWHM (px)
- FWHM (arcsec), when focal length and pixel size are available
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

### Metadata extraction

- Focal length
- Pixel size
- Exposure date/time
- Exposure length
- Filter

### Rejection and sorting

- Automatic rejection using adjustable thresholds
  - Max FWHM
  - Max HFR
  - Max eccentricity
  - Max mean background
  - Min stars
  - Optional reject on possible satellite trails
- One-click move rejected subframes to another folder
- Collision-safe renaming during move operations

### Satellite trail detection

- Heuristic satellite trail detection
- Trail state shown in the metrics
- Trail overlay shown on thumbnails when detected

### Performance-oriented behavior

- Async processing to keep the UI responsive
- Batched collection updates and virtualized frame list
- Preview caching for faster frame-to-frame blinking
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
- Rejected-frame move operations rename on collision to avoid overwrite.
