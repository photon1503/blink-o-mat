# blink-o-mat

Blinker and subframe quality sorter for astronomical RAW/FITS/XISF images.

## Features

- Browse a folder of light frames (`.fit`, `.fits`, `.xisf`)
- Native .NET FITS/XISF loading (no external converter needed)
- Async + multithreaded frame loading with progress bar
- Stretched preview rendering with global stretch control (`0.25` to `5.0`)
- Dedicated preview window with zoom controls and per-frame inspection
- ROI preview + full-frame preview for each subframe
- Automatic orientation normalization (meridian-flip / 180° alignment)
- Per-frame quality metrics:
  - FWHM
  - HFR
  - Eccentricity
  - Mean background
  - Possible satellite trail flag
- Auto-reject bad subframes using slider thresholds
- One-click move rejected frames to another folder
- Headless CLI mode for automation/pipelines

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

- FITS reading uses a native parser in `RustafitsService`; XISF reading uses `XisfSharp`.
- Metrics, trail detection, and orientation normalization are heuristic and intended for fast subframe sorting.
- Rejected-frame move operation renames on collision to avoid overwrite.
