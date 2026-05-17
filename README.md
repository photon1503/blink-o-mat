# blink-o-mat

Blinker and subframe quality sorter for astronomical RAW/FITS/XISF images.

## Features

- Browse a folder of light frames (`.fit`, `.fits`, `.xisf`)
- Generate stretched thumbnails with native .NET processing
- Compute/display per-frame quality metrics:
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
- No external converter required (native FITS/XISF reader stack)

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

- FITS reading uses `NETStdFITS`; XISF reading uses `XisfSharp`.
- Metrics and trail detection are currently heuristic and intended for quick subframe sorting.
- Temporary thumbnails are generated in the system temp directory.
- Rejected-frame move operation renames on collision to avoid overwrite.
