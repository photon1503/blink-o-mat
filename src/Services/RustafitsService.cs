using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using blink_o_mat.Models;
using XisfSharp;

namespace blink_o_mat.Services;

public sealed class RustafitsService
{
    private readonly record struct StarPoint(float X, float Y, float Signal);
    private readonly record struct TrailDetectionResult(int Confidence, double X1, double Y1, double X2, double Y2);
    private readonly record struct OrientationReferenceCache(string Key, IReadOnlyList<MeasuredStar> Stars, IReadOnlyList<TriangleSignature> Triangles, double ScaleX, double ScaleY);
    private OrientationReferenceCache? _orientationReferenceCache;
    private readonly record struct OrientationDensityCache(string Key, float[] FineMap, float[] CoarseMap, int FineSize, int CoarseSize, int Width, int Height);
    private OrientationDensityCache? _orientationDensityReferenceCache;
    private static readonly Regex SqmRegex = new(@"(?:%SQM%|SQM[_-])(?<value>\d{1,2}\.\d{1,3})(?:%|(?=[_.-]|$))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record LoadedFrame(
        float[] Pixels,
        int Width,
        int Height,
        double NormalizationMax = 1.0,
        double? FocalLengthMm = null,
        double? PixelSizeUm = null,
        DateTimeOffset? ExposureDateTime = null,
        double? ExposureSeconds = null,
        string? FilterName = null,
        double? Sqm = null,
        double? SkyTemp = null,
        float[][]? ColorChannels = null,
        string? ImageType = null)
    {
        /// <summary>True when this frame was debayered from a single-channel OSC sensor.</summary>
        public bool IsOsc => ColorChannels is { Length: 3 };

        /// <summary>True when the frame type is a light frame (or unspecified).</summary>
        public bool IsLightFrame => string.IsNullOrWhiteSpace(ImageType) || ImageType.Contains("light", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FrameItem> ProcessFrameAsync(string filePath, string thumbnailDirectory, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            var frame = await LoadFrameAsync(filePath, cancellationToken);

            var loadedFrame = new LoadedFrame(frame.Pixels, frame.Width, frame.Height, frame.NormalizationMax, frame.FocalLengthMm, frame.PixelSizeUm, frame.ExposureDateTime, frame.ExposureSeconds, frame.FilterName, ParseSqmFromFileName(filePath), frame.SkyTemp, frame.ColorChannels, frame.ImageType);
            var metrics = ComputeMetrics(loadedFrame);
            var previews = await RenderPreviewBitmapsAsync(loadedFrame, StfParameters.Default, null, metrics, cancellationToken);

            return new FrameItem
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                ExposureDateTime = loadedFrame.ExposureDateTime,
                ExposureSeconds = loadedFrame.ExposureSeconds,
                FilterName = loadedFrame.FilterName,
                ThumbnailImage = previews.Full,
                RoiImage = previews.Roi,
                Metrics = metrics
            };
        }, cancellationToken);
    }

    public Task<LoadedFrame> LoadRawFrameAsync(string filePath, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var frame = await LoadFrameAsync(filePath, cancellationToken);
            return new LoadedFrame(frame.Pixels, frame.Width, frame.Height, frame.NormalizationMax, frame.FocalLengthMm, frame.PixelSizeUm, frame.ExposureDateTime, frame.ExposureSeconds, frame.FilterName, ParseSqmFromFileName(filePath), frame.SkyTemp, frame.ColorChannels, frame.ImageType);
        }, cancellationToken);
    }

    public Task RenderThumbnailsAsync(LoadedFrame frame, string thumbnailPath, string roiThumbnailPath, double stretchStrength, (double X, double Y)? roiNormalizedCenter, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task RenderFullFrameAsync(LoadedFrame frame, string outputPath, double stretchStrength, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<(BitmapSource Full, BitmapSource Roi)> RenderPreviewBitmapsAsync(LoadedFrame frame, StfParameters stf, (double Left, double Top, double Width, double Height)? roiNormalizedRect, AstroMetrics? metrics, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (frame.IsOsc && frame.ColorChannels is { Length: 3 } cc)
            {
                var oscStf = ComputeAutoStretchOsc(frame);
                var full = CreateThumbnailBitmapColor(cc[0], cc[1], cc[2], frame.Width, frame.Height, 160, 160, oscStf[0], oscStf[1], oscStf[2], metrics, frame.NormalizationMax);
                var roi = CreateRoiBitmapColor(cc[0], cc[1], cc[2], frame.Width, frame.Height, 160, oscStf[0], oscStf[1], oscStf[2], roiNormalizedRect, frame.NormalizationMax);
                return (full, roi);
            }

            var monoFull = CreateThumbnailBitmap(frame.Pixels, frame.Width, frame.Height, 160, 160, stf, metrics, frame.NormalizationMax);
            var monoRoi = CreateRoiBitmap(frame.Pixels, frame.Width, frame.Height, 160, stf, roiNormalizedRect, frame.NormalizationMax);
            return (monoFull, monoRoi);
        }, cancellationToken);
    }

    public Task<BitmapSource> RenderFullBitmapAsync(LoadedFrame frame, StfParameters stf, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (frame.IsOsc && frame.ColorChannels is { Length: 3 } cc)
            {
                var oscStf = ComputeAutoStretchOsc(frame);
                return CreateFullFrameBitmapColor(cc[0], cc[1], cc[2], frame.Width, frame.Height, oscStf[0], oscStf[1], oscStf[2], frame.NormalizationMax);
            }

            return CreateFullFrameBitmap(frame.Pixels, frame.Width, frame.Height, stf, frame.NormalizationMax);
        }, cancellationToken);
    }

    public Task<BitmapSource> RenderScaledPreviewBitmapAsync(LoadedFrame frame, int targetWidth, int targetHeight, StfParameters stf, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            if (frame.IsOsc && frame.ColorChannels is { Length: 3 } cc)
            {
                var oscStf = ComputeAutoStretchOsc(frame);
                return CreateScaledFrameBitmapColor(cc[0], cc[1], cc[2], frame.Width, frame.Height, targetWidth, targetHeight, oscStf[0], oscStf[1], oscStf[2], frame.NormalizationMax);
            }

            return CreateScaledFrameBitmap(frame.Pixels, frame.Width, frame.Height, targetWidth, targetHeight, stf, frame.NormalizationMax);
        }, cancellationToken);
    }

    public (double X, double Y) DetectRoiNormalizedCenter(LoadedFrame frame)
    {
        var (x, y) = DetectRoiCenter(GetLuminance(frame), frame.Width, frame.Height);
        return (frame.Width <= 1 ? 0.5 : x / (double)(frame.Width - 1), frame.Height <= 1 ? 0.5 : y / (double)(frame.Height - 1));
    }

    /// <summary>
    /// Detects an automatic ROI rectangle in normalized image coordinates. The center is chosen by
    /// a centrality- and contrast-biased score so galaxies, globular clusters and nebulae are
    /// preferred over plain background or a single bright star. The side length is chosen to keep
    /// the downsample ratio into the 160 px list preview low enough that individual stars remain
    /// visible — at very high resolutions (small arcsec/px) one star can be a single native pixel,
    /// so the ROI must not be so large that stars get downsampled into invisibility.
    /// </summary>
    public (double Left, double Top, double Width, double Height) DetectRoiNormalizedRect(LoadedFrame frame)
    {
        var width = Math.Max(1, frame.Width);
        var height = Math.Max(1, frame.Height);
        var lum = GetLuminance(frame);
        var (cx, cy) = DetectRoiCenter(lum, width, height);

        var shorter = Math.Min(width, height);

        // The list preview thumbnail is rendered at 160 px. To judge focus / star shape / noise the
        // ROI crop must not be downsampled by more than a small factor, otherwise sub-arcsecond stars
        // (e.g. at 0.27"/px sampling) collapse to less than one preview pixel and the thumbnail
        // becomes unreadable. We target roughly a 2.5x downsample (≈ 400 native pixels) which keeps
        // a 1-2 native px star at ~1 preview px and shows enough context around it.
        const double previewSizePx = 160.0;
        const double targetDownsample = 2.5;
        var targetSidePx = previewSizePx * targetDownsample; // 400 px

        // Allow a slight increase for very large sensors so we still get meaningful context, but
        // never more than ~4x downsample (640 native px) and never more than half the shorter side.
        var maxSidePx = Math.Min(previewSizePx * 4.0, shorter * 0.5);
        var minSidePx = Math.Min(shorter, previewSizePx * 1.5); // at least show ~1.5x downsample

        var roiSidePx = Math.Clamp(targetSidePx, minSidePx, maxSidePx);

        // Express as a normalized rectangle. Width/height are computed against their own axis
        // so the rect is a true square in source pixels even on non-square images.
        var sizeX = roiSidePx / width;
        var sizeY = roiSidePx / height;

        var nx = width <= 1 ? 0.5 : cx / (double)(width - 1);
        var ny = height <= 1 ? 0.5 : cy / (double)(height - 1);

        var left = Math.Clamp(nx - sizeX / 2.0, 0.0, Math.Max(0.0, 1.0 - sizeX));
        var top = Math.Clamp(ny - sizeY / 2.0, 0.0, Math.Max(0.0, 1.0 - sizeY));
        return (left, top, sizeX, sizeY);
    }

    private static float[] GetLuminance(LoadedFrame frame)
    {
        if (frame.IsOsc && frame.ColorChannels is { Length: 3 } cc)
        {
            var r = cc[0];
            var g = cc[1];
            var b = cc[2];
            var lum = new float[r.Length];
            for (var i = 0; i < lum.Length; i++)
            {
                lum[i] = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
            }
            return lum;
        }
        return frame.Pixels;
    }

    public AstroMetrics AnalyzeFrame(LoadedFrame frame)
    {
        return ComputeMetrics(frame);
    }

    public StfParameters ComputeAutoStretch(LoadedFrame frame, double targetBackground = 0.25)
    {
        // Use the full pixel array (sampled for speed) to compute statistics in raw ADU
        var sampled = Sample(frame.Pixels);
        if (sampled.Length == 0)
        {
            return StfParameters.Default;
        }

        Array.Sort(sampled);

        // Determine a stable normalisation ceiling that is consistent with DownsampleAndStretch.
        // If the pixel values are raw ADU (> 1.0) we use the actual array maximum so both functions
        // always divide by the same number regardless of sampling differences.
        // Scanning the full pixel array (not just the sample) guarantees we catch sparse bright stars.
        var dataMax = Math.Max(1.0, frame.NormalizationMax);

        // Compute median and MAD in normalised [0,1] space
        var median = PercentileFromSorted(sampled, 0.5) / dataMax;
        var absDeviations = new float[sampled.Length];
        for (var i = 0; i < sampled.Length; i++)
        {
            absDeviations[i] = (float)Math.Abs((sampled[i] / dataMax) - median);
        }
        Array.Sort(absDeviations);
        var mad = PercentileFromSorted(absDeviations, 0.5);
        var sigma = 1.4826 * mad;

        // PixInsight STF defaults: shadowsClipping = -2.8, targetBackground = 0.25
        const double shadowsClipping = -2.8;

        // Shadows clipping point (c0) in normalised space — mirrors PixInsight's c0 = median + k*sigma
        var c0 = Math.Clamp(median + (shadowsClipping * sigma), 0.0, 1.0);

        // Normalised median position after shadow clipping applied
        var medianNorm = Math.Clamp((median - c0) / (1.0 - c0), 1e-9, 1.0 - 1e-9);

        // Midtones: solve MTF(m, medianNorm) = targetBackground  (PixInsight formula)
        var midtones = InverseMidtonesTransfer(targetBackground, medianNorm);
        if (double.IsNaN(midtones) || double.IsInfinity(midtones))
        {
            midtones = 0.25;
        }
        midtones = Math.Clamp(midtones, 0.0, 1.0);

        return new StfParameters(c0, midtones, 1.0);
    }

    private static double? ParseSqmFromFileName(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var match = SqmRegex.Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        if (double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sqm))
        {
            return sqm;
        }

        return null;
    }

    public LoadedFrame NormalizeOrientation(LoadedFrame frame, LoadedFrame reference)
    {
        return ApplyOrientation(frame, ShouldRotate180ForOrientation(frame, reference));
    }

    public bool ShouldRotate180ForOrientation(LoadedFrame frame, LoadedFrame reference)
    {
        return DetectOrientation(frame, reference).Rotate180;
    }

    /// <summary>
    /// Detects whether the frame must be rotated 180° to match the reference and reports the
    /// integer pixel shift (in original-image coordinates, after any required rotation) needed
    /// to align the frame to the reference. Used for quick, non-subpixel canvas alignment.
    /// </summary>
    public (bool Rotate180, int ShiftX, int ShiftY) DetectOrientation(LoadedFrame frame, LoadedFrame reference)
    {
        var analysis = AnalyzeOrientation(frame, reference);
        return (analysis.Rotate180, analysis.ShiftX, analysis.ShiftY);
    }

    public OrientationDebugInfo CreateOrientationReferenceDebugInfo(LoadedFrame frame)
    {
        var referenceCache = GetOrCreateOrientationReferenceCache(frame);
        return new OrientationDebugInfo(
            referenceCache.Stars.Select(s => new MeasuredStar(s.X * referenceCache.ScaleX, s.Y * referenceCache.ScaleY, s.Fwhm, s.Hfr, s.Peak)).ToArray(),
            Array.Empty<int>(),
            false,
            $"reference  stars={referenceCache.Stars.Count}",
            1.0);
    }

    public OrientationDebugInfo CreateOrientationReferenceDebugInfo(LoadedFrame frame, AstroMetrics metrics)
    {
        var stars = SelectOrientationAnchorMeasuredStars(metrics.Stars, frame.Width, frame.Height, maxStars: 50, gridSize: 6);
        return new OrientationDebugInfo(
            stars.ToArray(),
            Array.Empty<int>(),
            false,
            $"reference  stars={stars.Count}",
            1.0);
    }

    public (bool Rotate180, int ShiftX, int ShiftY, OrientationDebugInfo ReferenceDebug, OrientationDebugInfo CandidateDebug) AnalyzeOrientation(LoadedFrame frame, AstroMetrics frameMetrics, LoadedFrame reference, AstroMetrics referenceMetrics)
    {
        // Mixed-filter datasets (LRGB / SHO / RGB) break vertex-based triangle matching:
        // a star bright in L may be invisible in Hα, so the per-vertex correspondence
        // rt.A↔ct.A is wrong even when the geometry looks plausible. Instead, rasterize
        // each star list into a small Gaussian "density map" and cross-correlate the
        // reference map against the candidate map (both as-is and 180°-rotated). A star
        // that only one filter sees just contributes a small blob locally and does NOT
        // poison the matches everywhere else, which is exactly the property we need for
        // mixed-filter sessions.
        //
        // Performance: a naive single-resolution correlation at 256×256 with ±32 cells of
        // shift is ~65² * 65k = ~270M ops per direction. We instead use a coarse-to-fine
        // pyramid: search ±8 cells on a 64×64 map (≈4M ops), then refine ±4 cells on the
        // 256×256 map (≈600k ops). The reference map is also cached across frames in a
        // batch so it is only rasterized once per session.
        const int fineMapSize = 256;
        const int coarseMapSize = 64;
        const double minImprovement = 0.02;
        // Absolute correlation floor: below this the two density maps share essentially
        // no structure (different field of view, no overlap, vastly different star counts)
        // and the flip decision would be noise. Default to "not flipped".
        const double minConfidence = 0.15;
        // Coarse search window covers ~12% of map size: enough for typical post-flip drift.
        const int coarseShiftCells = 8;
        // Fine refinement window only needs to cover the coarse-quantization error
        // (one coarse cell ≈ fineMapSize / coarseMapSize fine cells) plus a small slack.
        const int fineShiftCells = 4;
        const int coarseToFineRatio = fineMapSize / coarseMapSize;

        // We rasterize MANY more stars than triangle matching used (the density map
        // doesn't care about brightness ranking), so even partially overlapping star
        // sets between filters still produce strong correlation peaks.
        const int densityStarCap = 200;
        var referenceDensityStars = SelectOrientationTriangulationStars(referenceMetrics.Stars, reference.Width, reference.Height, densityStarCap);
        var candidateDensityStars = SelectOrientationTriangulationStars(frameMetrics.Stars, frame.Width, frame.Height, densityStarCap);

        // Debug overlay still uses the top-24 brightness-ranked, edge-safe, well-separated
        // stars for visual continuity with the previous version.
        const int debugStarCount = 24;
        var referenceStars = referenceDensityStars.Take(debugStarCount).ToList();
        var originalStars = candidateDensityStars.Take(debugStarCount).ToList();
        var rotatedStars = TransformMeasuredStars(originalStars, frame.Width, frame.Height, rotate180: true).ToList();

        double originalScore = -1;
        double rotatedScore = -1;
        int shiftX = 0;
        int shiftY = 0;
        int rotatedShiftX = 0;
        int rotatedShiftY = 0;

        if (referenceDensityStars.Count >= 5 && candidateDensityStars.Count >= 5)
        {
            // Reference maps (fine + coarse) are cached across frames in a batch so the
            // expensive rasterization only happens once per session, not once per frame.
            var (referenceMapFine, referenceMapCoarse) = GetOrCreateOrientationDensityMaps(reference, referenceDensityStars, fineMapSize, coarseMapSize);
            var candidateMapFine = RasterizeStarDensityMap(candidateDensityStars, frame.Width, frame.Height, fineMapSize);
            var candidateMapCoarse = RasterizeStarDensityMap(candidateDensityStars, frame.Width, frame.Height, coarseMapSize);

            // 180° rotation of a square density map is just an index reversal of the
            // mean-subtracted array (mean is unchanged by rotation). This avoids a full
            // second rasterization for the rotated branch.
            var rotatedCandidateMapFine = RotateMap180(candidateMapFine);
            var rotatedCandidateMapCoarse = RotateMap180(candidateMapCoarse);

            var (origScore, origDx, origDy) = PyramidCorrelate(referenceMapCoarse, candidateMapCoarse, coarseMapSize, coarseShiftCells, referenceMapFine, candidateMapFine, fineMapSize, fineShiftCells, coarseToFineRatio);
            var (rotScore, rotDx, rotDy) = PyramidCorrelate(referenceMapCoarse, rotatedCandidateMapCoarse, coarseMapSize, coarseShiftCells, referenceMapFine, rotatedCandidateMapFine, fineMapSize, fineShiftCells, coarseToFineRatio);

            originalScore = origScore;
            rotatedScore = rotScore;

            // Convert map-space shift (dx, dy) back to image-pixel coordinates.
            var mapDenom = Math.Max(1, fineMapSize - 1);
            var pixelScaleX = (frame.Width - 1) / (double)mapDenom;
            var pixelScaleY = (frame.Height - 1) / (double)mapDenom;
            shiftX = (int)Math.Round(origDx * pixelScaleX);
            shiftY = (int)Math.Round(origDy * pixelScaleY);
            rotatedShiftX = (int)Math.Round(rotDx * pixelScaleX);
            rotatedShiftY = (int)Math.Round(rotDy * pixelScaleY);
        }

        var rotate180 = rotatedScore > originalScore + minImprovement && rotatedScore >= minConfidence;
        if (rotate180)
        {
            shiftX = rotatedShiftX;
            shiftY = rotatedShiftY;
        }

        var chosenScore = rotate180 ? rotatedScore : originalScore;
        var referenceDebug = new OrientationDebugInfo(
            referenceStars.ToArray(),
            Array.Empty<int>(),
            false,
            $"reference  stars={referenceStars.Count}  corr={chosenScore:F3}",
            chosenScore);
        var candidateDebug = new OrientationDebugInfo(
            (rotate180 ? rotatedStars : originalStars).ToArray(),
            Array.Empty<int>(),
            rotate180,
            rotate180 ? $"flipped  corr={chosenScore:F3}" : $"not flipped  corr={chosenScore:F3}",
            chosenScore);

        // The map-space correlation already accounts for which way the candidate is
        // oriented (we correlated the rotated map directly), so do NOT re-invert the
        // shift here as the legacy triangle path did.

        (shiftX, shiftY) = RefineShiftInImagePixels(reference, frame, rotate180, shiftX, shiftY,
            refineRadiusX: 2,
            refineRadiusY: 2);

        var maxShiftX = Math.Max(1, frame.Width / 4);
        var maxShiftY = Math.Max(1, frame.Height / 4);
        shiftX = Math.Clamp(shiftX, -maxShiftX, maxShiftX);
        shiftY = Math.Clamp(shiftY, -maxShiftY, maxShiftY);

        return (rotate180, shiftX, shiftY, referenceDebug, candidateDebug);
    }

    public (bool Rotate180, int ShiftX, int ShiftY, OrientationDebugInfo ReferenceDebug, OrientationDebugInfo CandidateDebug) AnalyzeOrientation(LoadedFrame frame, LoadedFrame reference)
    {
        // Sample size controls the residual alignment quantization: on an image of side W the
        // resulting integer shift is rounded to multiples of roughly W / (sampleSize - 1) pixels.
        // 512 keeps that under ~8 px on a 4K sensor, which is below the visual jitter threshold
        // for the small preview canvas without breaking the "quick, no big perf impact" budget.
        const int sampleSize = 512;
        const double minImprovement = 0.04;

        var referenceCache = GetOrCreateOrientationReferenceCache(reference);
        var originalSample = CreateOrientationSample(frame.Pixels, frame.Width, frame.Height, sampleSize, rotate180: false);
        var rotatedSample = CreateOrientationSample(frame.Pixels, frame.Width, frame.Height, sampleSize, rotate180: true);

        var referenceStars = referenceCache.Stars;
        var originalStars = SelectOrientationAnchorMeasuredStars(GetOrientationMeasuredStars(originalSample, sampleSize, sampleSize), sampleSize, sampleSize, maxStars: 12, gridSize: 4);
        var rotatedStars = SelectOrientationAnchorMeasuredStars(GetOrientationMeasuredStars(rotatedSample, sampleSize, sampleSize), sampleSize, sampleSize, maxStars: 12, gridSize: 4);
        var originalMatchStars = originalStars.Take(10).ToList();
        var rotatedMatchStars = rotatedStars.Take(10).ToList();

        double originalScore;
        double rotatedScore;
        int originalSampleDx, originalSampleDy;
        int rotatedSampleDx, rotatedSampleDy;
        IReadOnlyList<int> originalTriangle;
        IReadOnlyList<int> rotatedTriangle;
        IReadOnlyList<int> referenceTriangleOriginal;
        IReadOnlyList<int> referenceTriangleRotated;
        if (referenceStars.Count >= 5 && originalMatchStars.Count >= 5 && rotatedMatchStars.Count >= 5)
        {
            (originalScore, originalSampleDx, originalSampleDy, referenceTriangleOriginal, originalTriangle) = ComputeTriangleAlignmentScoreWithShift(referenceStars, referenceCache.Triangles, originalMatchStars);
            (rotatedScore, rotatedSampleDx, rotatedSampleDy, referenceTriangleRotated, rotatedTriangle) = ComputeTriangleAlignmentScoreWithShift(referenceStars, referenceCache.Triangles, rotatedMatchStars);
        }
        else
        {
            originalScore = -1;
            rotatedScore = -1;
            originalSampleDx = 0;
            originalSampleDy = 0;
            rotatedSampleDx = 0;
            rotatedSampleDy = 0;
            referenceTriangleOriginal = [];
            referenceTriangleRotated = [];
            originalTriangle = [];
            rotatedTriangle = [];
        }

        var rotate180 = rotatedScore > originalScore + minImprovement;
        var sampleDx = rotate180 ? rotatedSampleDx : originalSampleDx;
        var sampleDy = rotate180 ? rotatedSampleDy : originalSampleDy;

        // Convert the sample-space shift back to original-image pixel coordinates. The sample is
        // a uniform grid spanning [0..width-1] × [0..height-1], so each sample step corresponds
        // to (width-1)/(sampleSize-1) image pixels (and likewise for height).
        var sampleDenom = Math.Max(1, sampleSize - 1);
        var scaleX = (frame.Width - 1) / (double)sampleDenom;
        var scaleY = (frame.Height - 1) / (double)sampleDenom;
        var shiftX = (int)Math.Round(sampleDx * scaleX);
        var shiftY = (int)Math.Round(sampleDy * scaleY);

        // Refine the shift in image-pixel space to remove the sample-grid quantization (which is
        // ~scaleX × scaleY pixels per step). This is a cheap, small search using a center-cropped
        // luminance patch — about one quick correlation pass over an area smaller than the coarse
        // sample, so it has negligible additional cost.
        (shiftX, shiftY) = RefineShiftInImagePixels(reference, frame, rotate180, shiftX, shiftY,
            refineRadiusX: (int)Math.Ceiling(scaleX) + 1,
            refineRadiusY: (int)Math.Ceiling(scaleY) + 1);

        // Clamp shifts to a sensible fraction of the image so a noisy correlation cannot push
        // the canvas completely off-screen.
        var maxShiftX = Math.Max(1, frame.Width / 4);
        var maxShiftY = Math.Max(1, frame.Height / 4);
        shiftX = Math.Clamp(shiftX, -maxShiftX, maxShiftX);
        shiftY = Math.Clamp(shiftY, -maxShiftY, maxShiftY);

        var chosenReferenceTriangle = rotate180 ? referenceTriangleRotated : referenceTriangleOriginal;
        var chosenCandidateTriangle = rotate180 ? rotatedTriangle : originalTriangle;
        var chosenScore = rotate180 ? rotatedScore : originalScore;
        var referenceDebug = new OrientationDebugInfo(
            referenceStars.Select(s => new MeasuredStar(s.X * scaleX, s.Y * scaleY, s.Fwhm, s.Hfr, s.Peak)).ToArray(),
            chosenReferenceTriangle,
            false,
            $"reference  stars={referenceStars.Count}  score={chosenScore:F3}",
            chosenScore);
        var candidateDebug = new OrientationDebugInfo(
            (rotate180 ? rotatedStars : originalStars).Select(s => new MeasuredStar(s.X * scaleX, s.Y * scaleY, s.Fwhm, s.Hfr, s.Peak)).ToArray(),
            chosenCandidateTriangle,
            rotate180,
            rotate180 ? $"flipped  score={chosenScore:F3}" : $"not flipped  score={chosenScore:F3}",
            chosenScore);

        return (rotate180, shiftX, shiftY, referenceDebug, candidateDebug);
    }

    /// <summary>
    /// Refines an integer pixel shift in original-image coordinates by searching a small window
    /// (±refineRadius) around the coarse estimate using a downsampled center patch of both frames.
    /// Returns the best matching (dx, dy) in image pixels.
    /// </summary>
    private static (int Dx, int Dy) RefineShiftInImagePixels(LoadedFrame reference, LoadedFrame frame, bool rotate180, int seedDx, int seedDy, int refineRadiusX, int refineRadiusY)
    {
        // Use a center-aligned square patch that is large enough to contain plenty of stars but
        // small enough to keep the inner loop cheap. The patch is taken at a fixed stride from the
        // full-resolution image so its sampling is independent of the coarse pass.
        const int patchSize = 192;            // patchSize × patchSize comparisons per offset
        const int patchStride = 4;            // sample every 4th pixel inside the patch region
        var sampleSpan = patchSize * patchStride; // image-pixel span the patch covers

        var minDim = Math.Min(frame.Width, frame.Height);
        if (minDim < sampleSpan + (2 * Math.Max(refineRadiusX, refineRadiusY)) + 4)
        {
            return (seedDx, seedDy);
        }

        // Center patch in the reference frame.
        var refCx = reference.Width / 2;
        var refCy = reference.Height / 2;
        var refStartX = refCx - (sampleSpan / 2);
        var refStartY = refCy - (sampleSpan / 2);

        // The candidate patch is taken from the same image-space center but, when rotate180 is
        // true, the source pixels must be read from the rotated frame. We model rotation as a
        // coordinate flip while reading. After rotation, the alignment shift (seedDx, seedDy)
        // is applied: a positive shiftX means the candidate must be moved +shiftX to match the
        // reference, so the source read coordinate is offset by -shiftX.
        var refPatch = ExtractStridedPatch(reference.Pixels, reference.Width, reference.Height,
            refStartX, refStartY, patchSize, patchStride, rotate180: false);

        var best = double.MinValue;
        var bestDx = seedDx;
        var bestDy = seedDy;

        for (var ddy = -refineRadiusY; ddy <= refineRadiusY; ddy++)
        {
            for (var ddx = -refineRadiusX; ddx <= refineRadiusX; ddx++)
            {
                var dx = seedDx + ddx;
                var dy = seedDy + ddy;
                var candStartX = refStartX - dx;
                var candStartY = refStartY - dy;
                if (candStartX < 0 || candStartY < 0 ||
                    candStartX + sampleSpan > frame.Width ||
                    candStartY + sampleSpan > frame.Height)
                {
                    continue;
                }

                var candPatch = ExtractStridedPatch(frame.Pixels, frame.Width, frame.Height,
                    candStartX, candStartY, patchSize, patchStride, rotate180);
                var score = ComputeCorrelation(refPatch, candPatch);
                if (score > best)
                {
                    best = score;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }

        return (bestDx, bestDy);
    }

    private static float[] ExtractStridedPatch(float[] pixels, int width, int height, int startX, int startY, int patchSize, int stride, bool rotate180)
    {
        var patch = new float[patchSize * patchSize];
        for (var py = 0; py < patchSize; py++)
        {
            var sy = startY + (py * stride);
            for (var px = 0; px < patchSize; px++)
            {
                var sx = startX + (px * stride);
                var sourceX = rotate180 ? (width - 1) - sx : sx;
                var sourceY = rotate180 ? (height - 1) - sy : sy;
                if ((uint)sourceX >= (uint)width || (uint)sourceY >= (uint)height)
                {
                    patch[(py * patchSize) + px] = 0f;
                    continue;
                }
                patch[(py * patchSize) + px] = pixels[(sourceY * width) + sourceX];
            }
        }

        return patch;
    }

    public LoadedFrame ApplyOrientation(LoadedFrame frame, bool rotate180)
    {
        return rotate180 ? Rotate180(frame) : frame;
    }

    public AstroMetrics ApplyOrientation(AstroMetrics metrics, int width, int height, bool rotate180)
    {
        if (!rotate180)
        {
            return metrics;
        }

        var rotatedStars = TransformMeasuredStars(metrics.Stars, width, height, rotate180: true).ToArray();
        return metrics with
        {
            Stars = rotatedStars,
            TrailX1 = metrics.TrailX1 is double x1 ? (width - 1) - x1 : null,
            TrailY1 = metrics.TrailY1 is double y1 ? (height - 1) - y1 : null,
            TrailX2 = metrics.TrailX2 is double x2 ? (width - 1) - x2 : null,
            TrailY2 = metrics.TrailY2 is double y2 ? (height - 1) - y2 : null
        };
    }

    /// <summary>
    /// Rasterizes a star list into a small square density map by stamping a small
    /// Gaussian "blob" at every star, weighted by log(1 + Peak). The map is then
    /// mean-subtracted so empty regions contribute nothing to correlation. This
    /// representation is robust for mixed-filter datasets because:
    /// <list type="bullet">
    ///   <item>missing stars in one filter only leave their local cell empty — they
    ///         do NOT change the contribution of any other star;</item>
    ///   <item>log weighting prevents a single saturated star from dominating;</item>
    ///   <item>the blob radius absorbs sub-pixel centroid jitter and small drift.</item>
    /// </list>
    /// </summary>
    private static float[] RasterizeStarDensityMap(IReadOnlyList<MeasuredStar> stars, int imageWidth, int imageHeight, int mapSize)
    {
        var map = new float[mapSize * mapSize];
        if (stars.Count == 0 || imageWidth <= 1 || imageHeight <= 1)
        {
            return map;
        }

        var scaleX = (mapSize - 1) / (double)(imageWidth - 1);
        var scaleY = (mapSize - 1) / (double)(imageHeight - 1);

        // Blob radius ~1.5% of the map side; on a 256-cell map that's ~4 cells, which
        // tolerates a few image-pixels of centroid noise without smearing nearby stars
        // together.
        var sigma = Math.Max(1.5, mapSize * 0.015);
        var twoSigmaSq = 2.0 * sigma * sigma;
        var radius = (int)Math.Ceiling(sigma * 2.5);

        for (var i = 0; i < stars.Count; i++)
        {
            var star = stars[i];
            var weight = (float)Math.Log(1.0 + Math.Max(0.0, star.Peak));
            if (weight <= 0) continue;

            var cx = star.X * scaleX;
            var cy = star.Y * scaleY;
            var x0 = Math.Max(0, (int)Math.Floor(cx) - radius);
            var x1 = Math.Min(mapSize - 1, (int)Math.Ceiling(cx) + radius);
            var y0 = Math.Max(0, (int)Math.Floor(cy) - radius);
            var y1 = Math.Min(mapSize - 1, (int)Math.Ceiling(cy) + radius);

            for (var y = y0; y <= y1; y++)
            {
                var dy = y - cy;
                var rowOffset = y * mapSize;
                for (var x = x0; x <= x1; x++)
                {
                    var dx = x - cx;
                    var distSq = (dx * dx) + (dy * dy);
                    if (distSq > radius * radius) continue;
                    var contribution = weight * (float)Math.Exp(-distSq / twoSigmaSq);
                    map[rowOffset + x] += contribution;
                }
            }
        }

        // Mean-subtract so empty background contributes zero to the correlation numerator
        // and the score becomes a normalized cross-correlation in ComputeCorrelationWithOffset.
        double sum = 0;
        for (var i = 0; i < map.Length; i++) sum += map[i];
        var mean = (float)(sum / map.Length);
        for (var i = 0; i < map.Length; i++) map[i] -= mean;

        return map;
    }

    /// <summary>
    /// In-place-style 180° rotation of a square map. Mean-subtraction is invariant under
    /// rotation, so the result is correctly normalized for cross-correlation without any
    /// extra pass over the data.
    /// </summary>
    private static float[] RotateMap180(float[] map)
    {
        var rotated = new float[map.Length];
        var last = map.Length - 1;
        for (var i = 0; i < map.Length; i++)
        {
            rotated[i] = map[last - i];
        }
        return rotated;
    }

    /// <summary>
    /// Coarse-to-fine pyramid correlation: locate the best shift on a small downsampled
    /// map within a relatively wide window, then refine on the full-resolution map within
    /// a small window around the coarse winner. Same accuracy as a single full search
    /// (the coarse winner is always within ±1 coarse cell of the true peak for smooth
    /// density maps), at a small fraction of the cost.
    /// </summary>
    private static (double Score, int Dx, int Dy) PyramidCorrelate(
        float[] coarseReference, float[] coarseCandidate, int coarseSize, int coarseShift,
        float[] fineReference, float[] fineCandidate, int fineSize, int fineShift,
        int coarseToFineRatio)
    {
        var (_, coarseDx, coarseDy) = ComputeBestCorrelationWithOffsetsAndShift(coarseReference, coarseCandidate, coarseSize, coarseShift);

        // Translate the coarse winner into fine-map coordinates and refine inside a small
        // window. The window must be at least one coarse-cell wide to cover quantization.
        var centerDx = coarseDx * coarseToFineRatio;
        var centerDy = coarseDy * coarseToFineRatio;
        return ComputeBestCorrelationInWindow(fineReference, fineCandidate, fineSize, centerDx, centerDy, fineShift);
    }

    private static (double Score, int Dx, int Dy) ComputeBestCorrelationInWindow(float[] reference, float[] candidate, int size, int centerDx, int centerDy, int radius)
    {
        var best = -1.0;
        var bestDx = centerDx;
        var bestDy = centerDy;
        for (var dy = centerDy - radius; dy <= centerDy + radius; dy++)
        {
            for (var dx = centerDx - radius; dx <= centerDx + radius; dx++)
            {
                var score = ComputeCorrelationWithOffset(reference, candidate, size, dx, dy);
                if (score > best)
                {
                    best = score;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }
        return (best, bestDx, bestDy);
    }

    private (float[] Fine, float[] Coarse) GetOrCreateOrientationDensityMaps(LoadedFrame reference, IReadOnlyList<MeasuredStar> stars, int fineSize, int coarseSize)
    {
        var key = $"{reference.Width}x{reference.Height}:{reference.ExposureDateTime?.Ticks}:{reference.FilterName}:{reference.Pixels.Length}:{reference.Pixels[0]:G9}:{stars.Count}:f{fineSize}c{coarseSize}";
        if (_orientationDensityReferenceCache is { } cached &&
            string.Equals(cached.Key, key, StringComparison.Ordinal) &&
            cached.FineSize == fineSize && cached.CoarseSize == coarseSize)
        {
            return (cached.FineMap, cached.CoarseMap);
        }

        var fine = RasterizeStarDensityMap(stars, reference.Width, reference.Height, fineSize);
        var coarse = RasterizeStarDensityMap(stars, reference.Width, reference.Height, coarseSize);
        _orientationDensityReferenceCache = new OrientationDensityCache(key, fine, coarse, fineSize, coarseSize, reference.Width, reference.Height);
        return (fine, coarse);
    }

    private static IReadOnlyList<MeasuredStar> TransformMeasuredStars(IReadOnlyList<MeasuredStar> stars, int width, int height, bool rotate180)
    {
        if (!rotate180)
        {
            return stars;
        }

        var result = new MeasuredStar[stars.Count];
        for (var i = 0; i < stars.Count; i++)
        {
            var star = stars[i];
            result[i] = new MeasuredStar((width - 1) - star.X, (height - 1) - star.Y, star.Fwhm, star.Hfr, star.Peak);
        }

        return result;
    }

    /// <summary>
    /// Returns a frame whose pixel content is translated by (shiftX, shiftY). Exposed border
    /// pixels are filled with zero. This is a cheap, integer-pixel alignment used for visual
    /// preview only — no subpixel interpolation is performed.
    /// </summary>
    public LoadedFrame ApplyShift(LoadedFrame frame, int shiftX, int shiftY)
    {
        if (shiftX == 0 && shiftY == 0)
        {
            return frame;
        }

        var shiftedPixels = ShiftPixels(frame.Pixels, frame.Width, frame.Height, shiftX, shiftY);
        float[][]? shiftedChannels = null;
        if (frame.ColorChannels is { Length: 3 } cc)
        {
            shiftedChannels =
            [
                ShiftPixels(cc[0], frame.Width, frame.Height, shiftX, shiftY),
                ShiftPixels(cc[1], frame.Width, frame.Height, shiftX, shiftY),
                ShiftPixels(cc[2], frame.Width, frame.Height, shiftX, shiftY)
            ];
        }

        return new LoadedFrame(shiftedPixels, frame.Width, frame.Height, frame.NormalizationMax, frame.FocalLengthMm, frame.PixelSizeUm, frame.ExposureDateTime, frame.ExposureSeconds, frame.FilterName, frame.Sqm, frame.SkyTemp, shiftedChannels, frame.ImageType);
    }

    private static float[] ShiftPixels(float[] source, int width, int height, int shiftX, int shiftY)
    {
        var result = new float[source.Length];
        // For each destination row y, read from source row (y - shiftY); copy a horizontal span.
        for (var y = 0; y < height; y++)
        {
            var srcY = y - shiftY;
            if ((uint)srcY >= (uint)height)
            {
                continue;
            }

            // Destination span [destXStart .. destXStart + spanWidth) maps to source span
            // starting at (destXStart - shiftX). Both must lie in [0, width).
            var destXStart = Math.Max(0, shiftX);
            var srcXStart = Math.Max(0, -shiftX);
            var spanWidth = width - Math.Abs(shiftX);
            if (spanWidth <= 0)
            {
                continue;
            }

            Array.Copy(source, (srcY * width) + srcXStart, result, (y * width) + destXStart, spanWidth);
        }

        return result;
    }

    private static async Task<(float[] Pixels, int Width, int Height, double NormalizationMax, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName, double? SkyTemp, float[][]? ColorChannels, string? ImageType)> LoadFrameAsync(string filePath, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".fits", StringComparison.OrdinalIgnoreCase) || ext.Equals(".fit", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFits(filePath);
        }

        if (ext.Equals(".xisf", StringComparison.OrdinalIgnoreCase))
        {
            var r = await LoadXisfAsync(filePath, cancellationToken);
            return (r.Pixels, r.Width, r.Height, r.NormalizationMax, r.FocalLengthMm, r.PixelSizeUm, r.ExposureDateTime, r.ExposureSeconds, r.FilterName, r.SkyTemp, null, r.ImageType);
        }

        throw new NotSupportedException($"Unsupported file type: {ext}");
    }

    private static (float[] Pixels, int Width, int Height, double NormalizationMax, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName, double? SkyTemp, float[][]? ColorChannels, string? ImageType) LoadFits(string filePath)
    {
        // Use a large sequential-scan buffer: FITS files are read strictly start-to-end in one
        // small header pass followed by one bulk pixel-array read, so we hint the OS to
        // prefetch aggressively and avoid thousands of small 4 KB I/O calls.
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        while (stream.Position < stream.Length)
        {
            var header = ReadFitsHeader(stream);
            if (header.AxisCount < 2)
            {
                SkipFitsData(stream, header);
                continue;
            }

            var decoded = TryDecodeFitsImage(stream, header);
            if (decoded is { } image)
            {
                return image;
            }

            SkipFitsData(stream, header);
        }

        throw new InvalidOperationException("FITS image data not found.");
    }

    private static (float[] Pixels, int Width, int Height, double NormalizationMax, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName, double? SkyTemp, float[][]? ColorChannels, string? ImageType)? TryDecodeFitsImage(Stream stream, FitsHeaderInfo header)
    {
        if (header.Axes.Length < 2)
        {
            return null;
        }

        var widthAxis = header.Axes[0];
        var heightAxis = header.Axes[1];
        if (widthAxis <= 0 || heightAxis <= 0 || widthAxis > int.MaxValue || heightAxis > int.MaxValue)
        {
            return null;
        }

        var width = (int)widthAxis;
        var height = (int)heightAxis;
        var axis3 = header.AxisCount > 2 ? Math.Max(1L, header.Axes[2]) : 1L;
        if (axis3 > int.MaxValue)
        {
            return null;
        }

        var bytesPerSample = Math.Abs(header.BitPix) / 8;
        if (bytesPerSample is not (1 or 2 or 4 or 8))
        {
            return null;
        }

        var pixelCount = width * height;
        var result = new float[pixelCount];
        var channels = (int)axis3;
        var weights = new[] { 0.2126, 0.7152, 0.0722 };

        var extraFrames = 1L;
        for (var i = 3; i < header.AxisCount; i++)
        {
            extraFrames *= Math.Max(1L, header.Axes[i]);
        }

        // Bulk-read all channel data in one allocation and decode in parallel
        var totalSamples = (long)channels * pixelCount;
        var rawBytes = new byte[totalSamples * bytesPerSample];
        ReadExactly(stream, rawBytes);

        // Hoist header values out of the hot path so the closure does not re-read
        // record-property accessors on every iteration.
        var bitPix = header.BitPix;
        var bScale = header.BScale;
        var bZero = header.BZero;

        if (channels == 1)
        {
            // Use a range partitioner so each worker decodes a contiguous slab of pixels,
            // which keeps the byte buffer cache-warm and amortises iteration overhead.
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, pixelCount), range =>
            {
                for (var i = range.Item1; i < range.Item2; i++)
                {
                    result[i] = (float)(ReadFitsSampleFromBuffer(rawBytes, i, bitPix, bytesPerSample) * bScale + bZero);
                }
            });
        }
        else
        {
            var maxChannel = Math.Min(channels, 3);
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, pixelCount), range =>
            {
                for (var i = range.Item1; i < range.Item2; i++)
                {
                    double sum = 0;
                    for (var c = 0; c < maxChannel; c++)
                    {
                        var sampleIndex = (long)c * pixelCount + i;
                        var scaled = ReadFitsSampleFromBuffer(rawBytes, sampleIndex, bitPix, bytesPerSample) * bScale + bZero;
                        sum += scaled * weights[c];
                    }
                    result[i] = (float)sum;
                }
            });
        }

        // Skip any remaining channel data beyond what we decoded
        var remainingSamples = (long)(channels * extraFrames - channels) * pixelCount;
        if (remainingSamples > 0)
        {
            stream.Seek(remainingSamples * bytesPerSample, SeekOrigin.Current);
        }

        var dataBytes = totalSamples * extraFrames * bytesPerSample;
        var paddingBytes = (2880L - (dataBytes % 2880L)) % 2880L;
        if (paddingBytes > 0)
        {
            stream.Seek(paddingBytes, SeekOrigin.Current);
        }

        // Debayer if this is a single-channel OSC frame
        float[][]? colorChannels = null;
        if (channels == 1 && !string.IsNullOrWhiteSpace(header.BayerPattern))
        {
            colorChannels = DebayerBilinear(result, width, height, header.BayerPattern, header.BayerOffsetX, header.BayerOffsetY);
        }

        return (result, width, height, ComputeFitsNormalizationMax(header), header.FocalLengthMm, header.PixelSizeUm, header.ExposureDateTime, header.ExposureSeconds, header.FilterName, header.SkyTemp, colorChannels, header.ImageType);
    }

    private static double ReadFitsSampleFromBuffer(byte[] buffer, long sampleIndex, int bitPix, int bytesPerSample)
    {
        var offset = sampleIndex * bytesPerSample;
        return bitPix switch
        {
            8 => buffer[offset],
            16 => BinaryPrimitives.ReadInt16BigEndian(buffer.AsSpan((int)offset, 2)),
            32 => BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan((int)offset, 4)),
            64 => BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan((int)offset, 8)),
            -32 => BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan((int)offset, 4)),
            -64 => BinaryPrimitives.ReadDoubleBigEndian(buffer.AsSpan((int)offset, 8)),
            _ => throw new NotSupportedException($"Unsupported FITS BITPIX: {bitPix}")
        };
    }

    private static FitsHeaderInfo ReadFitsHeader(Stream stream)
    {
        var cards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = new byte[2880];
        while (true)
        {
            ReadExactly(stream, block);
            for (var i = 0; i < block.Length; i += 80)
            {
                var card = System.Text.Encoding.ASCII.GetString(block, i, 80);
                var keyword = card[..8].Trim();
                if (keyword.Equals("END", StringComparison.OrdinalIgnoreCase))
                {
                    var bitPix = ParseInt(cards, "BITPIX", 0);
                    var axisCount = ParseInt(cards, "NAXIS", 0);
                    var axes = new long[Math.Max(0, axisCount)];
                    for (var a = 0; a < axes.Length; a++)
                    {
                        axes[a] = Math.Max(0L, ParseLong(cards, $"NAXIS{a + 1}", 0));
                    }

                    var bScale = ParseDouble(cards, "BSCALE", 1.0);
                    var bZero = ParseDouble(cards, "BZERO", 0.0);
                    var focalLengthMm = FirstAvailableDouble(cards, "FOCALLEN", "FOCAL", "FOCAL_LENGTH", "FOCLEN");
                    var pixelSizeUm = ResolvePixelSizeUm(cards);
                    var exposureDateTime = ResolveExposureDateTime(cards);
                    var exposureSeconds = FirstAvailableDouble(cards, "EXPTIME", "EXPOSURE", "EXPOSURETIME");
                    var filterName = FirstAvailableString(cards, "FILTER", "INSFLNAM", "FILTERID");
                    var skyTemp = FirstAvailableAnyDouble(cards, "SKYTEMP");
                    var bayerPattern = FirstAvailableString(cards, "BAYERPAT", "COLORTYP", "BAYEROFF");
                    var bayerOffsetX = ParseInt(cards, "XBAYROFF", 0);
                    var bayerOffsetY = ParseInt(cards, "YBAYROFF", 0);
                    var imageType = FirstAvailableString(cards, "IMAGETYP", "FRAME");
                    return new FitsHeaderInfo(bitPix, axisCount, axes, bScale, bZero, focalLengthMm, pixelSizeUm, exposureDateTime, exposureSeconds, filterName, skyTemp, bayerPattern, bayerOffsetX, bayerOffsetY, imageType);
                }

                if (!card.Contains('='))
                {
                    continue;
                }

                var eq = card.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }

                var valuePart = card[(eq + 1)..];
                var slash = valuePart.IndexOf('/');
                var value = (slash >= 0 ? valuePart[..slash] : valuePart).Trim();
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    cards[keyword] = value;
                }
            }
        }
    }

    private static void SkipFitsData(Stream stream, FitsHeaderInfo header)
    {
        var bytesPerSample = Math.Abs(header.BitPix) / 8;
        if (bytesPerSample <= 0)
        {
            return;
        }

        long totalSamples = 1;
        for (var i = 0; i < header.AxisCount; i++)
        {
            totalSamples *= Math.Max(1L, header.Axes[i]);
        }

        var dataBytes = totalSamples * bytesPerSample;
        var paddedBytes = ((dataBytes + 2879L) / 2880L) * 2880L;
        stream.Seek(paddedBytes, SeekOrigin.Current);
    }

    private static double ReadFitsSample(Stream stream, int bitPix)
    {
        Span<byte> buf = stackalloc byte[8];
        switch (bitPix)
        {
            case 8:
                {
                    var b = stream.ReadByte();
                    if (b < 0)
                    {
                        throw new EndOfStreamException();
                    }

                    return b;
                }
            case 16:
                ReadExactly(stream, buf[..2]);
                return BinaryPrimitives.ReadInt16BigEndian(buf[..2]);

            case 32:
                ReadExactly(stream, buf[..4]);
                return BinaryPrimitives.ReadInt32BigEndian(buf[..4]);

            case 64:
                ReadExactly(stream, buf[..8]);
                return BinaryPrimitives.ReadInt64BigEndian(buf[..8]);

            case -32:
                ReadExactly(stream, buf[..4]);
                return BinaryPrimitives.ReadSingleBigEndian(buf[..4]);

            case -64:
                ReadExactly(stream, buf[..8]);
                return BinaryPrimitives.ReadDoubleBigEndian(buf[..8]);

            default:
                throw new NotSupportedException($"Unsupported FITS BITPIX: {bitPix}");
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    private static int ParseInt(Dictionary<string, string> cards, string key, int fallback)
    {
        if (!cards.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static long ParseLong(Dictionary<string, string> cards, string key, long fallback)
    {
        if (!cards.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static double ParseDouble(Dictionary<string, string> cards, string key, double fallback)
    {
        if (!cards.TryGetValue(key, out var raw))
        {
            return fallback;
        }

        var normalized = raw.Trim().Trim('"', '\'', ' ').Replace('D', 'E').Replace('d', 'E');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static double? TryParseDouble(Dictionary<string, string> cards, string key)
    {
        if (!cards.TryGetValue(key, out var raw))
        {
            return null;
        }

        var normalized = raw.Trim().Trim('"', '\'', ' ').Replace('D', 'E').Replace('d', 'E');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? FirstAvailableDouble(Dictionary<string, string> cards, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = TryParseDouble(cards, key);
            if (value is > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static double? FirstAvailableAnyDouble(Dictionary<string, string> cards, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = TryParseDouble(cards, key);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static double? ResolvePixelSizeUm(Dictionary<string, string> cards)
    {
        var pixelSizeUm = FirstAvailableDouble(cards, "XPIXSZ", "PIXSIZE", "PIXELSZ", "PIXSZ", "PIXELSIZE");
        if (pixelSizeUm is > 0)
        {
            return pixelSizeUm;
        }

        var xPixelSize = FirstAvailableDouble(cards, "XPIXSIZE");
        var yPixelSize = FirstAvailableDouble(cards, "YPIXSIZE");
        if (xPixelSize is > 0 && yPixelSize is > 0)
        {
            return (xPixelSize.Value + yPixelSize.Value) / 2.0;
        }

        return xPixelSize ?? yPixelSize;
    }

    private static string? FirstAvailableString(Dictionary<string, string> cards, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!cards.TryGetValue(key, out var value))
            {
                continue;
            }

            var cleaned = value.Trim().Trim('"', '\'', ' ');
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                return cleaned;
            }
        }

        return null;
    }

    private static DateTimeOffset? ResolveExposureDateTime(Dictionary<string, string> cards)
    {
        var raw = FirstAvailableString(cards, "DATE-OBS", "DATEOBS", "DATE_OBS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dto))
        {
            return dto;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            return new DateTimeOffset(dt);
        }

        return null;
    }

    private readonly record struct FitsHeaderInfo(
        int BitPix,
        int AxisCount,
        long[] Axes,
        double BScale,
        double BZero,
        double? FocalLengthMm,
        double? PixelSizeUm,
        DateTimeOffset? ExposureDateTime,
        double? ExposureSeconds,
        string? FilterName,
        double? SkyTemp,
        string? BayerPattern = null,
        int BayerOffsetX = 0,
        int BayerOffsetY = 0,
        string? ImageType = null);

    private static async Task<(float[] Pixels, int Width, int Height, double NormalizationMax, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName, double? SkyTemp, string? ImageType)> LoadXisfAsync(string filePath, CancellationToken cancellationToken)
    {
        var image = await XisfImage.LoadAsync(filePath, cancellationToken);
        var memory = image.Data; // ReadOnlyMemory<byte> — no full-buffer copy
        var width = image.Width;
        var height = image.Height;
        var channels = Math.Max(1, image.Channels);

        var pixelCount = width * height;
        var bytesPerSample = GetBytesPerSample(image.SampleFormat);
        var sampleCount = pixelCount * channels;
        if (memory.Length < sampleCount * bytesPerSample)
        {
            throw new InvalidOperationException("XISF data size mismatch.");
        }

        var luminance = new float[pixelCount];
        var planar = image.PixelStorage == PixelStorage.Planar;
        var sampleFormat = image.SampleFormat;

        if (channels == 1)
        {
            switch (sampleFormat)
            {
                case SampleFormat.UInt16:
                    DecodeMonoUInt16(memory, luminance, pixelCount, cancellationToken);
                    break;

                case SampleFormat.Float32:
                    DecodeMonoFloat32(memory, luminance, pixelCount, cancellationToken);
                    break;

                case SampleFormat.UInt8:
                    DecodeMonoUInt8(memory, luminance, pixelCount, cancellationToken);
                    break;

                default:
                    DecodeMonoGeneric(memory, luminance, pixelCount, sampleFormat, cancellationToken);
                    break;
            }
        }
        else
        {
            if (planar)
            {
                switch (sampleFormat)
                {
                    case SampleFormat.UInt16:
                        DecodeColorPlanarUInt16(memory, luminance, pixelCount, cancellationToken);
                        break;

                    case SampleFormat.Float32:
                        DecodeColorPlanarFloat32(memory, luminance, pixelCount, cancellationToken);
                        break;

                    default:
                        DecodeColorGeneric(memory, luminance, pixelCount, channels, sampleFormat, planar: true, cancellationToken);
                        break;
                }
            }
            else
            {
                switch (sampleFormat)
                {
                    case SampleFormat.UInt16:
                        DecodeColorInterleavedUInt16(memory, luminance, pixelCount, channels, cancellationToken);
                        break;

                    case SampleFormat.Float32:
                        DecodeColorInterleavedFloat32(memory, luminance, pixelCount, channels, cancellationToken);
                        break;

                    default:
                        DecodeColorGeneric(memory, luminance, pixelCount, channels, sampleFormat, planar: false, cancellationToken);
                        break;
                }
            }
        }

        var focalLengthMm = ResolveXisfFocalLengthMm(image);
        var pixelSizeUm = ResolveXisfPixelSizeUm(image);
        var exposureDateTime = ResolveXisfExposureDateTime(image);
        var exposureSeconds = ResolveXisfExposureSeconds(image);
        var filterName = ResolveXisfFilterName(image);
        var skyTemp = ResolveXisfSkyTemp(image);
        var imageType = ResolveXisfImageType(image);
        return (luminance, width, height, GetNormalizationMax(image.SampleFormat), focalLengthMm, pixelSizeUm, exposureDateTime, exposureSeconds, filterName, skyTemp, imageType);
    }

    private const int XisfDecodeChunkSize = 65536;

    private static void DecodeMonoUInt16(ReadOnlyMemory<byte> source, float[] destination, int pixelCount, CancellationToken cancellationToken)
    {
        var partitioner = Partitioner.Create(0, pixelCount, XisfDecodeChunkSize);
        Parallel.ForEach(partitioner, new ParallelOptions { CancellationToken = cancellationToken }, range =>
        {
            var src = MemoryMarshal.Cast<byte, ushort>(source.Span);
            for (var i = range.Item1; i < range.Item2; i++)
            {
                destination[i] = src[i];
            }
        });
    }

    private static void DecodeMonoFloat32(ReadOnlyMemory<byte> source, float[] destination, int pixelCount, CancellationToken cancellationToken)
    {
        var partitioner = Partitioner.Create(0, pixelCount, XisfDecodeChunkSize);
        Parallel.ForEach(partitioner, new ParallelOptions { CancellationToken = cancellationToken }, range =>
        {
            var src = MemoryMarshal.Cast<byte, float>(source.Span);
            src.Slice(range.Item1, range.Item2 - range.Item1).CopyTo(destination.AsSpan(range.Item1, range.Item2 - range.Item1));
        });
    }

    private static void DecodeMonoUInt8(ReadOnlyMemory<byte> source, float[] destination, int pixelCount, CancellationToken cancellationToken)
    {
        var partitioner = Partitioner.Create(0, pixelCount, XisfDecodeChunkSize);
        Parallel.ForEach(partitioner, new ParallelOptions { CancellationToken = cancellationToken }, range =>
        {
            var src = source.Span;
            for (var i = range.Item1; i < range.Item2; i++)
            {
                destination[i] = src[i];
            }
        });
    }

    private static void DecodeMonoGeneric(ReadOnlyMemory<byte> source, float[] destination, int pixelCount, SampleFormat sampleFormat, CancellationToken cancellationToken)
    {
        var partitioner = Partitioner.Create(0, pixelCount, XisfDecodeChunkSize);
        Parallel.ForEach(partitioner, new ParallelOptions { CancellationToken = cancellationToken }, range =>
        {
            var src = source.Span;
            for (var i = range.Item1; i < range.Item2; i++)
            {
                destination[i] = (float)ReadSample(src, i, sampleFormat);
            }
        });
    }

    private static void DecodeColorPlanarUInt16(ReadOnlyMemory<byte> source, float[] destination, int pixelCount, CancellationToken cancellationToken)
    {
        var partitioner = Partitioner.Create(0, pixelCount, XisfDecodeChunkSize);
        Parallel.ForEach(partitioner, new ParallelOptions { CancellationToken = cancellationToken }, range =>
        {
            var src = MemoryMarshal.Cast<byte, ushort>(source.Span);
            for (var i = range.Item1; i < range.Item2; i++)
            {
                var r = src[i];
                var g = src[i + pixelCount];
                var b = src[i + (2 * pixelCount)];
                destination[i] = (float)((0.2126 * r) + (0.7152 * g) + (0.0722 * b));
            }
        });
    }

    private static void DecodeColorPlanarFloat32(ReadOnlyMemory<byte> source, float[] destination, int pixelCount, CancellationToken cancellationToken)
    {
        var partitioner = Partitioner.Create(0, pixelCount, XisfDecodeChunkSize);
        Parallel.ForEach(partitioner, new ParallelOptions { CancellationToken = cancellationToken }, range =>
        {
            var src = MemoryMarshal.Cast<byte, float>(source.Span);
            for (var i = range.Item1; i < range.Item2; i++)
            {
                var r = src[i];
                var g = src[i + pixelCount];
                var b = src[i + (2 * pixelCount)];
                destination[i] = (float)((0.2126 * r) + (0.7152 * g) + (0.0722 * b));
            }
        });
    }

    private static void DecodeColorInterleavedUInt16(ReadOnlyMemory<byte> source, float[] destination, int pixelCount, int channels, CancellationToken cancellationToken)
    {
        var bChannelOffset = Math.Min(2, channels - 1);
        var partitioner = Partitioner.Create(0, pixelCount, XisfDecodeChunkSize);
        Parallel.ForEach(partitioner, new ParallelOptions { CancellationToken = cancellationToken }, range =>
        {
            var src = MemoryMarshal.Cast<byte, ushort>(source.Span);
            for (var i = range.Item1; i < range.Item2; i++)
            {
                var baseIdx = i * channels;
                var r = src[baseIdx];
                var g = src[baseIdx + 1];
                var b = src[baseIdx + bChannelOffset];
                destination[i] = (float)((0.2126 * r) + (0.7152 * g) + (0.0722 * b));
            }
        });
    }

    private static void DecodeColorInterleavedFloat32(ReadOnlyMemory<byte> source, float[] destination, int pixelCount, int channels, CancellationToken cancellationToken)
    {
        var bChannelOffset = Math.Min(2, channels - 1);
        var partitioner = Partitioner.Create(0, pixelCount, XisfDecodeChunkSize);
        Parallel.ForEach(partitioner, new ParallelOptions { CancellationToken = cancellationToken }, range =>
        {
            var src = MemoryMarshal.Cast<byte, float>(source.Span);
            for (var i = range.Item1; i < range.Item2; i++)
            {
                var baseIdx = i * channels;
                var r = src[baseIdx];
                var g = src[baseIdx + 1];
                var b = src[baseIdx + bChannelOffset];
                destination[i] = (float)((0.2126 * r) + (0.7152 * g) + (0.0722 * b));
            }
        });
    }

    private static void DecodeColorGeneric(ReadOnlyMemory<byte> source, float[] destination, int pixelCount, int channels, SampleFormat sampleFormat, bool planar, CancellationToken cancellationToken)
    {
        var partitioner = Partitioner.Create(0, pixelCount, XisfDecodeChunkSize);
        Parallel.ForEach(partitioner, new ParallelOptions { CancellationToken = cancellationToken }, range =>
        {
            var src = source.Span;
            for (var i = range.Item1; i < range.Item2; i++)
            {
                var r = planar ? ReadSample(src, i, sampleFormat) : ReadSample(src, i * channels, sampleFormat);
                var g = planar ? ReadSample(src, i + pixelCount, sampleFormat) : ReadSample(src, (i * channels) + 1, sampleFormat);
                var b = planar
                    ? ReadSample(src, i + (2 * pixelCount), sampleFormat)
                    : ReadSample(src, (i * channels) + Math.Min(2, channels - 1), sampleFormat);
                destination[i] = (float)((0.2126 * r) + (0.7152 * g) + (0.0722 * b));
            }
        });
    }

    private static double? ResolveXisfFocalLengthMm(XisfImage image)
    {
        foreach (var key in new[] { "FOCALLEN", "FOCAL", "FOCAL_LENGTH", "FOCLEN" })
        {
            if (TryReadXisfNumericMetadata(image, key, out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static double? ResolveXisfPixelSizeUm(XisfImage image)
    {
        foreach (var key in new[] { "XPIXSZ", "YPIXSZ", "PIXSIZE", "PIXELSZ", "PIXSZ", "PIXELSIZE" })
        {
            if (TryReadXisfNumericMetadata(image, key, out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    private static double? ResolveXisfSkyTemp(XisfImage image)
    {
        if (TryReadXisfNumericMetadata(image, "SKYTEMP", out var value))
        {
            return value;
        }

        return null;
    }

    private static DateTimeOffset? ResolveXisfExposureDateTime(XisfImage image)
    {
        if (!TryReadXisfStringMetadata(image, "DATE-OBS", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dto))
        {
            return dto;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            return new DateTimeOffset(dt);
        }

        return null;
    }

    private static double? ResolveXisfExposureSeconds(XisfImage image)
    {
        if (TryReadXisfNumericMetadata(image, "EXPOSURE", out var value) && value > 0)
        {
            return value;
        }

        return null;
    }

    private static string? ResolveXisfFilterName(XisfImage image)
    {
        if (TryReadXisfStringMetadata(image, "FILTER", out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }

    private static string? ResolveXisfImageType(XisfImage image)
    {
        if (TryReadXisfStringMetadata(image, "IMAGETYP", out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (TryReadXisfStringMetadata(image, "FRAME", out value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }

    private static bool TryReadXisfNumericMetadata(object source, string propertyName, out double value)
    {
        value = default;

        if (source is null)
        {
            return false;
        }

        if (TryReadNamedMetadataCollection(source, "Properties", propertyName, out value))
        {
            return true;
        }

        if (TryReadNamedMetadataCollection(source, "FITSKeywords", propertyName, out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadXisfStringMetadata(object source, string propertyName, out string? value)
    {
        value = null;

        if (source is null)
        {
            return false;
        }

        if (TryReadNamedMetadataCollection(source, "Properties", propertyName, out value))
        {
            return true;
        }

        if (TryReadNamedMetadataCollection(source, "FITSKeywords", propertyName, out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadNamedMetadataCollection(object source, string collectionPropertyName, string key, out double value)
    {
        value = default;

        var collectionProperty = source.GetType().GetProperty(collectionPropertyName, BindingFlags.Public | BindingFlags.Instance);
        if (collectionProperty?.GetValue(source) is not { } collection)
        {
            return false;
        }

        var collectionType = collection.GetType();
        var tryGetPropertyMethod = collectionType.GetMethod("TryGetProperty", BindingFlags.Public | BindingFlags.Instance, null, [typeof(string), typeof(object).MakeByRefType()], null);
        if (tryGetPropertyMethod is not null)
        {
            var args = new object?[] { key, null };
            if (tryGetPropertyMethod.Invoke(collection, args) is true && args[1] is not null && TryConvertMetadataValue(args[1], out value))
            {
                return true;
            }
        }

        var enumerable = collection as System.Collections.IEnumerable;
        if (enumerable is null)
        {
            return false;
        }

        foreach (var entry in enumerable)
        {
            if (entry is null)
            {
                continue;
            }

            if (!TryGetMetadataName(entry, out var name) || !string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryConvertMetadataValue(entry, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadNamedMetadataCollection(object source, string collectionPropertyName, string key, out string? value)
    {
        value = null;

        var collectionProperty = source.GetType().GetProperty(collectionPropertyName, BindingFlags.Public | BindingFlags.Instance);
        if (collectionProperty?.GetValue(source) is not { } collection)
        {
            return false;
        }

        var collectionType = collection.GetType();
        var tryGetPropertyMethod = collectionType.GetMethod("TryGetProperty", BindingFlags.Public | BindingFlags.Instance, null, [typeof(string), typeof(object).MakeByRefType()], null);
        if (tryGetPropertyMethod is not null)
        {
            var args = new object?[] { key, null };
            if (tryGetPropertyMethod.Invoke(collection, args) is true && args[1] is not null && TryConvertMetadataValue(args[1], out value))
            {
                return true;
            }
        }

        var enumerable = collection as System.Collections.IEnumerable;
        if (enumerable is null)
        {
            return false;
        }

        foreach (var entry in enumerable)
        {
            if (entry is null)
            {
                continue;
            }

            if (!TryGetMetadataName(entry, out var name) || !string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryConvertMetadataValue(entry, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetMetadataName(object metadata, out string? name)
    {
        name = null;
        foreach (var candidate in new[] { "Name", "Id", "Identifier", "Key" })
        {
            var property = metadata.GetType().GetProperty(candidate, BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(metadata) is string text && !string.IsNullOrWhiteSpace(text))
            {
                name = text;
                return true;
            }
        }

        return false;
    }

    private static bool TryConvertMetadataValue(object metadata, out string? value)
    {
        value = null;

        if (metadata is string text)
        {
            value = NormalizeXisfMetadataString(text);
            return !string.IsNullOrWhiteSpace(value);
        }

        foreach (var candidate in new[] { "Value", "StringValue", "Text", "ScalarValue" })
        {
            var property = metadata.GetType().GetProperty(candidate, BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(metadata) is { } inner && TryConvertMetadataValue(inner, out value))
            {
                return true;
            }
        }

        if (metadata is IFormattable formattable)
        {
            value = NormalizeXisfMetadataString(formattable.ToString(null, CultureInfo.InvariantCulture));
            return !string.IsNullOrWhiteSpace(value);
        }

        value = NormalizeXisfMetadataString(metadata.ToString());
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? NormalizeXisfMetadataString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();

        while (normalized.Length >= 2 &&
               ((normalized[0] == '\'' && normalized[^1] == '\'') ||
                (normalized[0] == '"' && normalized[^1] == '"')))
        {
            normalized = normalized[1..^1].Trim();
        }

        normalized = normalized.Trim('"', '\'').Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool TryConvertMetadataValue(object metadata, out double value)
    {
        value = default;

        if (metadata is double d)
        {
            value = d;
            return true;
        }

        if (metadata is float f)
        {
            value = f;
            return true;
        }

        if (metadata is IConvertible convertible)
        {
            try
            {
                value = convertible.ToDouble(CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
            }
        }

        foreach (var candidate in new[] { "ScalarValue", "Value", "NumericValue" })
        {
            var property = metadata.GetType().GetProperty(candidate, BindingFlags.Public | BindingFlags.Instance);
            if (property?.GetValue(metadata) is { } inner && TryConvertMetadataValue(inner, out value))
            {
                return true;
            }
        }

        return false;
    }

    private static LoadedFrame Rotate180(LoadedFrame frame)
    {
        var pixels = new float[frame.Pixels.Length];
        var source = frame.Pixels;
        for (var i = 0; i < source.Length; i++)
        {
            pixels[i] = source[source.Length - 1 - i];
        }

        return new LoadedFrame(pixels, frame.Width, frame.Height, frame.NormalizationMax, frame.FocalLengthMm, frame.PixelSizeUm, frame.ExposureDateTime, frame.ExposureSeconds, frame.FilterName, frame.Sqm, frame.SkyTemp, ImageType: frame.ImageType);
    }

    private static float[] CreateOrientationSample(float[] pixels, int width, int height, int sampleSize, bool rotate180)
    {
        var sample = new float[sampleSize * sampleSize];
        var widthDenom = Math.Max(1, sampleSize - 1);
        var heightDenom = Math.Max(1, sampleSize - 1);

        for (var y = 0; y < sampleSize; y++)
        {
            var sourceY = (int)Math.Round((y / (double)heightDenom) * (height - 1));
            for (var x = 0; x < sampleSize; x++)
            {
                var sourceX = (int)Math.Round((x / (double)widthDenom) * (width - 1));
                if (rotate180)
                {
                    sourceX = (width - 1) - sourceX;
                    sourceY = (height - 1) - sourceY;
                }

                sample[(y * sampleSize) + x] = pixels[(sourceY * width) + sourceX];
            }
        }

        return sample;
    }

    private static double ComputeCorrelation(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
        {
            return -1;
        }

        double sumA = 0;
        double sumB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sumA += a[i];
            sumB += b[i];
        }

        var meanA = sumA / a.Length;
        var meanB = sumB / b.Length;

        double dot = 0;
        double normA = 0;
        double normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            dot += da * db;
            normA += da * da;
            normB += db * db;
        }

        if (normA <= 1e-20 || normB <= 1e-20)
        {
            return -1;
        }

        return dot / Math.Sqrt(normA * normB);
    }

    private static double ComputeBestCorrelationWithOffsets(float[] reference, float[] candidate, int size, int maxOffset)
    {
        return ComputeBestCorrelationWithOffsetsAndShift(reference, candidate, size, maxOffset).Score;
    }

    private static (double Score, int Dx, int Dy) ComputeBestCorrelationWithOffsetsAndShift(float[] reference, float[] candidate, int size, int maxOffset)
    {
        var best = -1.0;
        var bestDx = 0;
        var bestDy = 0;
        for (var dy = -maxOffset; dy <= maxOffset; dy++)
        {
            for (var dx = -maxOffset; dx <= maxOffset; dx++)
            {
                var score = ComputeCorrelationWithOffset(reference, candidate, size, dx, dy);
                if (score > best)
                {
                    best = score;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }

        return (best, bestDx, bestDy);
    }

    private static double ComputeCorrelationWithOffset(float[] reference, float[] candidate, int size, int dx, int dy)
    {
        var xStart = Math.Max(0, dx);
        var yStart = Math.Max(0, dy);
        var xEnd = Math.Min(size, size + dx);
        var yEnd = Math.Min(size, size + dy);

        var overlapWidth = xEnd - xStart;
        var overlapHeight = yEnd - yStart;
        if (overlapWidth < size / 2 || overlapHeight < size / 2)
        {
            return -1;
        }

        double sumA = 0;
        double sumB = 0;
        var count = 0;

        for (var y = yStart; y < yEnd; y++)
        {
            var refRow = y * size;
            var candRow = (y - dy) * size;
            for (var x = xStart; x < xEnd; x++)
            {
                sumA += reference[refRow + x];
                sumB += candidate[candRow + (x - dx)];
                count++;
            }
        }

        if (count <= 0)
        {
            return -1;
        }

        var meanA = sumA / count;
        var meanB = sumB / count;
        double dot = 0;
        double normA = 0;
        double normB = 0;

        for (var y = yStart; y < yEnd; y++)
        {
            var refRow = y * size;
            var candRow = (y - dy) * size;
            for (var x = xStart; x < xEnd; x++)
            {
                var da = reference[refRow + x] - meanA;
                var db = candidate[candRow + (x - dx)] - meanB;
                dot += da * db;
                normA += da * da;
                normB += db * db;
            }
        }

        if (normA <= 1e-20 || normB <= 1e-20)
        {
            return -1;
        }

        return dot / Math.Sqrt(normA * normB);
    }

    private static IReadOnlyList<MeasuredStar> GetOrientationMeasuredStars(float[] pixels, int width, int height)
    {
        var statsSample = Sample(pixels);
        if (statsSample.Length == 0)
        {
            return Array.Empty<MeasuredStar>();
        }

        Array.Sort(statsSample);
        var background = PercentileFromSorted(statsSample, 0.5);
        var sigma = ComputeSigmaFromSample(statsSample, background);
        var (stars, _) = DetectStars(pixels, width, height, background, sigma);
        return stars
            .OrderByDescending(s => s.Peak)
            .Select(s => new MeasuredStar(s.X, s.Y, s.Fwhm, s.Hfr, s.Peak))
            .ToArray();
    }

    /// <summary>
    /// Picks a brightness-ranked, frame-INDEPENDENT subset of stars for triangle
    /// matching. The selection is deliberately simple so two different exposures of
    /// the same field converge on the SAME physical stars:
    ///   - keep stars away from the image edges (so rotated/non-rotated overlap)
    ///   - enforce a minimum pixel separation to drop neighbours / hot-pixel pairs
    ///   - order strictly by descending Peak and take the top <paramref name="maxStars"/>
    /// Critically, there is NO frame-dependent peak threshold and NO per-cell
    /// brightest-pick logic — both of which would let different filters / SNRs pick
    /// different stars on each image.
    /// </summary>
    private static List<MeasuredStar> SelectOrientationTriangulationStars(IReadOnlyList<MeasuredStar> stars, int width, int height, int maxStars)
    {
        const double edgeMarginNormalized = 0.10;
        var marginX = width * edgeMarginNormalized;
        var marginY = height * edgeMarginNormalized;
        // Minimum separation scaled to image size so close pairs (which produce
        // degenerate triangles and ambiguous correspondences) are rejected on both
        // frames identically.
        var minSeparation = Math.Max(16.0, Math.Min(width, height) * 0.01);
        var minSeparationSq = minSeparation * minSeparation;

        var ranked = new List<MeasuredStar>(stars.Count);
        for (var i = 0; i < stars.Count; i++)
        {
            var s = stars[i];
            if (s.Peak <= 0) continue;
            if (s.X < marginX || s.X > width - 1 - marginX) continue;
            if (s.Y < marginY || s.Y > height - 1 - marginY) continue;
            ranked.Add(s);
        }
        ranked.Sort((a, b) => b.Peak.CompareTo(a.Peak));

        var selected = new List<MeasuredStar>(maxStars);
        for (var i = 0; i < ranked.Count && selected.Count < maxStars; i++)
        {
            var candidate = ranked[i];
            var tooClose = false;
            for (var j = 0; j < selected.Count; j++)
            {
                var dx = candidate.X - selected[j].X;
                var dy = candidate.Y - selected[j].Y;
                if ((dx * dx) + (dy * dy) < minSeparationSq)
                {
                    tooClose = true;
                    break;
                }
            }
            if (!tooClose)
            {
                selected.Add(candidate);
            }
        }

        return selected;
    }

    private static List<MeasuredStar> SelectOrientationAnchorMeasuredStars(IReadOnlyList<MeasuredStar> stars, int width, int height, int maxStars, int gridSize)
    {
        const double edgeMarginNormalized = 0.16;
        var marginX = width * edgeMarginNormalized;
        var marginY = height * edgeMarginNormalized;

        var safeStars = stars
            .Where(s => s.X >= marginX && s.X <= width - 1 - marginX && s.Y >= marginY && s.Y <= height - 1 - marginY)
            .OrderByDescending(s => s.Peak)
            .ToList();

        if (safeStars.Count == 0)
        {
            return [];
        }

        var strongestPeak = safeStars[0].Peak;
        var robustStars = safeStars
            .Where(s => s.Peak >= strongestPeak * 0.22)
            .ToList();

        var selected = new List<MeasuredStar>(maxStars);
        TrySelectDistributedMeasured(robustStars.Count >= 5 ? robustStars : safeStars, selected, maxStars, marginX, marginY, width, height, gridSize, minSeparation: 24.0);

        if (selected.Count < 5)
        {
            TrySelectDistributedMeasured(safeStars, selected, maxStars, marginX, marginY, width, height, gridSize, minSeparation: 12.0);
        }

        if (selected.Count < 3)
        {
            foreach (var star in safeStars)
            {
                if (selected.Any(s => Math.Abs(s.X - star.X) < 0.5 && Math.Abs(s.Y - star.Y) < 0.5))
                {
                    continue;
                }

                selected.Add(star);
                if (selected.Count >= maxStars)
                {
                    break;
                }
            }
        }

        return selected;
    }

    private static void TrySelectDistributedMeasured(
        IReadOnlyList<MeasuredStar> sourceStars,
        List<MeasuredStar> selected,
        int maxStars,
        double marginX,
        double marginY,
        int width,
        int height,
        int gridSize,
        double minSeparation)
    {
        if (sourceStars.Count == 0 || selected.Count >= maxStars)
        {
            return;
        }

        var innerWidth = Math.Max(1.0, (width - (2 * marginX)) / gridSize);
        var innerHeight = Math.Max(1.0, (height - (2 * marginY)) / gridSize);

        for (var gy = 0; gy < gridSize; gy++)
        {
            for (var gx = 0; gx < gridSize; gx++)
            {
                if (selected.Count >= maxStars)
                {
                    return;
                }

                var x0 = marginX + (gx * innerWidth);
                var x1 = gx == gridSize - 1 ? width - marginX : x0 + innerWidth;
                var y0 = marginY + (gy * innerHeight);
                var y1 = gy == gridSize - 1 ? height - marginY : y0 + innerHeight;

                var bestInCell = sourceStars.FirstOrDefault(s =>
                    s.X >= x0 && s.X < x1 &&
                    s.Y >= y0 && s.Y < y1 &&
                    IsFarEnoughFromSelectedMeasured(s, selected, minSeparation));

                if (bestInCell is null || bestInCell.Peak <= 0)
                {
                    continue;
                }

                if (selected.All(s => Math.Abs(s.X - bestInCell.X) >= 0.5 || Math.Abs(s.Y - bestInCell.Y) >= 0.5))
                {
                    selected.Add(bestInCell);
                }
            }
        }

        foreach (var star in sourceStars)
        {
            if (selected.Count >= maxStars)
            {
                return;
            }

            if (!IsFarEnoughFromSelectedMeasured(star, selected, minSeparation))
            {
                continue;
            }

            if (selected.Any(s => Math.Abs(s.X - star.X) < 0.5 && Math.Abs(s.Y - star.Y) < 0.5))
            {
                continue;
            }

            selected.Add(star);
        }
    }

    private static bool IsFarEnoughFromSelectedMeasured(MeasuredStar candidate, IReadOnlyList<MeasuredStar> selected, double minSeparation)
    {
        var minSeparationSq = minSeparation * minSeparation;
        for (var i = 0; i < selected.Count; i++)
        {
            var dx = candidate.X - selected[i].X;
            var dy = candidate.Y - selected[i].Y;
            if ((dx * dx) + (dy * dy) < minSeparationSq)
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct TriangleSignature(int A, int B, int C, double Ratio1, double Ratio2, double AreaNorm);

    private OrientationReferenceCache GetOrCreateOrientationReferenceCache(LoadedFrame reference)
    {
        const int sampleSize = 512;
        var key = $"{reference.Width}x{reference.Height}:{reference.ExposureDateTime?.Ticks}:{reference.FilterName}:{reference.Pixels.Length}:{reference.Pixels[0]:G9}";
        if (_orientationReferenceCache is { } cache && string.Equals(cache.Key, key, StringComparison.Ordinal))
        {
            return cache;
        }

        var sample = CreateOrientationSample(reference.Pixels, reference.Width, reference.Height, sampleSize, rotate180: false);
        var stars = SelectOrientationAnchorMeasuredStars(GetOrientationMeasuredStars(sample, sampleSize, sampleSize), sampleSize, sampleSize, maxStars: 50, gridSize: 6);
        var sampleDenom = Math.Max(1, sampleSize - 1);
        var scaleX = (reference.Width - 1) / (double)sampleDenom;
        var scaleY = (reference.Height - 1) / (double)sampleDenom;
        var triangles = BuildTriangleSignatures(stars.Take(18).ToList());
        cache = new OrientationReferenceCache(key, stars, triangles, scaleX, scaleY);
        _orientationReferenceCache = cache;
        return cache;
    }

    private static (double Score, int Dx, int Dy, IReadOnlyList<int> ReferenceTriangle, IReadOnlyList<int> CandidateTriangle) ComputeTriangleAlignmentScoreWithShift(IReadOnlyList<MeasuredStar> referenceStars, IReadOnlyList<TriangleSignature> referenceTriangles, IReadOnlyList<MeasuredStar> candidateStars)
    {
        // Default inlier radius (8 px) is sized for the 512-sample-space path.
        return ComputeTriangleAlignmentScoreWithShift(referenceStars, referenceTriangles, candidateStars, inlierRadius: 8.0, ratioTolerance: 0.02, areaTolerance: 0.015);
    }

    private static (double Score, int Dx, int Dy, IReadOnlyList<int> ReferenceTriangle, IReadOnlyList<int> CandidateTriangle) ComputeTriangleAlignmentScoreWithShift(IReadOnlyList<MeasuredStar> referenceStars, IReadOnlyList<TriangleSignature> referenceTriangles, IReadOnlyList<MeasuredStar> candidateStars, double inlierRadius, double ratioTolerance, double areaTolerance)
    {
        if (referenceStars.Count < 3 || candidateStars.Count < 3)
        {
            return (-1, 0, 0, Array.Empty<int>(), Array.Empty<int>());
        }

        var refTriangles = referenceTriangles;
        var candTriangles = BuildTriangleSignatures(candidateStars);
        if (refTriangles.Count == 0 || candTriangles.Count == 0)
        {
            return (-1, 0, 0, Array.Empty<int>(), Array.Empty<int>());
        }

        var referenceMedianFwhm = MedianMeasuredStarFwhm(referenceStars);
        var candidateMedianFwhm = MedianMeasuredStarFwhm(candidateStars);
        var scaleRatio = candidateMedianFwhm > 0 && referenceMedianFwhm > 0
            ? Math.Clamp(referenceMedianFwhm / candidateMedianFwhm, 0.75, 1.25)
            : 1.0;

        // Each matched triangle pair produces 3 per-vertex shift votes (dx, dy).
        // For the correct orientation these votes cluster tightly around the true
        // image offset; for the wrong orientation the vertex pairings are geometric
        // nonsense and the votes scatter across the full image extent.
        var votes = new List<(double Dx, double Dy)>();
        TriangleSignature? bestReferenceTriangle = null;
        TriangleSignature? bestCandidateTriangle = null;
        var bestOverallError = double.MaxValue;

        foreach (var rt in refTriangles)
        {
            var best = default(TriangleSignature?);
            var bestError = double.MaxValue;
            foreach (var ct in candTriangles)
            {
                var ratioError = Math.Abs(rt.Ratio1 - ct.Ratio1) + Math.Abs(rt.Ratio2 - ct.Ratio2);
                var areaError = Math.Abs(rt.AreaNorm - ct.AreaNorm);
                if (Math.Abs(rt.Ratio1 - ct.Ratio1) > ratioTolerance ||
                    Math.Abs(rt.Ratio2 - ct.Ratio2) > ratioTolerance ||
                    areaError > areaTolerance)
                {
                    continue;
                }

                var totalError = ratioError + areaError;
                if (totalError < bestError)
                {
                    bestError = totalError;
                    best = ct;
                }
            }

            if (best is not TriangleSignature match)
            {
                continue;
            }

            if (bestError < bestOverallError)
            {
                bestOverallError = bestError;
                bestReferenceTriangle = rt;
                bestCandidateTriangle = match;
            }

            // A/B/C in each TriangleSignature are in canonical order
            // (opposite shortest/middle/longest side), so rt.A↔match.A etc. are
            // the correct geometric correspondences.
            votes.Add((referenceStars[rt.A].X - (candidateStars[match.A].X * scaleRatio),
                       referenceStars[rt.A].Y - (candidateStars[match.A].Y * scaleRatio)));
            votes.Add((referenceStars[rt.B].X - (candidateStars[match.B].X * scaleRatio),
                       referenceStars[rt.B].Y - (candidateStars[match.B].Y * scaleRatio)));
            votes.Add((referenceStars[rt.C].X - (candidateStars[match.C].X * scaleRatio),
                       referenceStars[rt.C].Y - (candidateStars[match.C].Y * scaleRatio)));
        }

        if (votes.Count == 0)
        {
            return (-1, 0, 0, Array.Empty<int>(), Array.Empty<int>());
        }

        var sortedDx = votes.Select(v => v.Dx).OrderBy(v => v).ToList();
        var sortedDy = votes.Select(v => v.Dy).OrderBy(v => v).ToList();
        var medianDx = sortedDx[sortedDx.Count / 2];
        var medianDy = sortedDy[sortedDy.Count / 2];

        // Score = fraction of votes within a tight radius of the median shift.
        // The radius is passed in by the caller so it can be scaled to the coordinate
        // space being used (sample-space ~8 px, full-resolution image-space larger).
        var inlierRadiusSq = inlierRadius * inlierRadius;
        var inliers = votes.Count(v =>
        {
            var ddx = v.Dx - medianDx;
            var ddy = v.Dy - medianDy;
            return (ddx * ddx) + (ddy * ddy) <= inlierRadiusSq;
        });
        var score = inliers / (double)votes.Count;

        var refTriangle = bestReferenceTriangle is TriangleSignature br ? new[] { br.A, br.B, br.C } : Array.Empty<int>();
        var candTriangle = bestCandidateTriangle is TriangleSignature bc ? new[] { bc.A, bc.B, bc.C } : Array.Empty<int>();
        return (score, (int)Math.Round(medianDx), (int)Math.Round(medianDy), refTriangle, candTriangle);
    }

    private static double MedianMeasuredStarFwhm(IReadOnlyList<MeasuredStar> stars)
    {
        if (stars.Count == 0)
        {
            return 0;
        }

        var values = stars.Where(s => s.Fwhm > 0).Select(s => s.Fwhm).OrderBy(v => v).ToArray();
        if (values.Length == 0)
        {
            return 0;
        }

        var mid = values.Length / 2;
        return (values.Length & 1) == 1 ? values[mid] : (values[mid - 1] + values[mid]) * 0.5;
    }

    private static List<TriangleSignature> BuildTriangleSignatures(IReadOnlyList<MeasuredStar> stars)
    {
        var result = new List<TriangleSignature>();
        for (var i = 0; i < stars.Count - 2; i++)
        {
            for (var j = i + 1; j < stars.Count - 1; j++)
            {
                for (var k = j + 1; k < stars.Count; k++)
                {
                    // Each edge is paired with the vertex opposite to it:
                    //   edge i-j (d1) is opposite vertex k
                    //   edge i-k (d2) is opposite vertex j
                    //   edge j-k (d3) is opposite vertex i
                    var d1 = Distance(stars[i], stars[j]);
                    var d2 = Distance(stars[i], stars[k]);
                    var d3 = Distance(stars[j], stars[k]);
                    // Sort edges while keeping track of the opposite vertex so that
                    // A/B/C in the stored signature correspond to the vertex opposite
                    // the shortest/middle/longest side respectively.  Two triangles
                    // that match on (Ratio1, Ratio2, AreaNorm) therefore have
                    // rt.A ↔ ct.A, rt.B ↔ ct.B, rt.C ↔ ct.C as their geometric
                    // vertex correspondences.
                    var e0 = (dist: d1, opp: k);
                    var e1 = (dist: d2, opp: j);
                    var e2 = (dist: d3, opp: i);
                    // Sort ascending by distance (simple 3-element sort)
                    if (e0.dist > e1.dist) { (e0, e1) = (e1, e0); }
                    if (e1.dist > e2.dist) { (e1, e2) = (e2, e1); }
                    if (e0.dist > e1.dist) { (e0, e1) = (e1, e0); }

                    if (e2.dist <= 1e-6)
                    {
                        continue;
                    }

                    var area2 = Math.Abs(((stars[j].X - stars[i].X) * (stars[k].Y - stars[i].Y)) - ((stars[j].Y - stars[i].Y) * (stars[k].X - stars[i].X)));
                    var areaNorm = area2 / (e2.dist * e2.dist);
                    // Reject near-degenerate (nearly-collinear) triangles. areaNorm is the
                    // twice-normalised area: for an equilateral triangle it is ~0.43, for a
                    // right isoceles ~0.50, for a very flat triangle <<0.05. Allowing tiny
                    // values produces pseudo-triangles that can false-match almost anything.
                    if (areaNorm < 0.05)
                    {
                        continue;
                    }

                    result.Add(new TriangleSignature(e0.opp, e1.opp, e2.opp, e0.dist / e2.dist, e1.dist / e2.dist, areaNorm));
                }
            }
        }

        return result;
    }

    private static double Distance(MeasuredStar a, MeasuredStar b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static void Flatten(Array array, List<double> output)
    {
        foreach (var item in array)
        {
            if (item is Array nested)
            {
                Flatten(nested, output);
                continue;
            }

            if (item is null)
            {
                continue;
            }

            output.Add(Convert.ToDouble(item, CultureInfo.InvariantCulture));
        }
    }

    private static int GetBytesPerSample(SampleFormat format)
    {
        return format switch
        {
            SampleFormat.UInt8 => 1,
            SampleFormat.UInt16 => 2,
            SampleFormat.UInt32 => 4,
            SampleFormat.UInt64 => 8,
            SampleFormat.Float32 => 4,
            SampleFormat.Float64 => 8,
            _ => throw new NotSupportedException($"Unsupported XISF sample format: {format}")
        };
    }

    private static double ReadSample(ReadOnlySpan<byte> bytes, int sampleIndex, SampleFormat format)
    {
        return format switch
        {
            SampleFormat.UInt8 => bytes[sampleIndex],
            SampleFormat.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes[(sampleIndex * 2)..((sampleIndex * 2) + 2)]),
            SampleFormat.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes[(sampleIndex * 4)..((sampleIndex * 4) + 4)]),
            SampleFormat.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(bytes[(sampleIndex * 8)..((sampleIndex * 8) + 8)]),
            SampleFormat.Float32 => BinaryPrimitives.ReadSingleLittleEndian(bytes[(sampleIndex * 4)..((sampleIndex * 4) + 4)]),
            SampleFormat.Float64 => BinaryPrimitives.ReadDoubleLittleEndian(bytes[(sampleIndex * 8)..((sampleIndex * 8) + 8)]),
            _ => 0
        };
    }

    private static double ReadSample(byte[] bytes, int sampleIndex, SampleFormat format)
        => ReadSample(new ReadOnlySpan<byte>(bytes), sampleIndex, format);

    // ------------------------------------------------------------------ //
    //  OSC / Bayer debayering                                              //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Bilinear Bayer demosaicing.  Returns [R[], G[], B[]] each of length width*height.
    /// Supported patterns: RGGB, BGGR, GRBG, GBRG (case-insensitive).
    /// </summary>
    private static float[][] DebayerBilinear(float[] raw, int width, int height, string pattern, int offsetX, int offsetY)
    {
        // Normalise pattern string
        var p = pattern.Trim().Trim('\'', '"').ToUpperInvariant();

        // Map the 2×2 Bayer cell: index [row%2][col%2] → channel 0=R,1=G,2=B
        // Each known pattern lists top-left, top-right, bottom-left, bottom-right
        var cellMap = p switch
        {
            "RGGB" => new[,] { { 0, 1 }, { 1, 2 } },
            "BGGR" => new[,] { { 2, 1 }, { 1, 0 } },
            "GRBG" => new[,] { { 1, 0 }, { 2, 1 } },
            "GBRG" => new[,] { { 1, 2 }, { 0, 1 } },
            _ => new[,] { { 0, 1 }, { 1, 2 } }   // default RGGB
        };

        var r = new float[width * height];
        var g = new float[width * height];
        var b = new float[width * height];

        // Boundary-safe read used by the 1-pixel border only.
        float GetClamped(int x, int y) => raw[Math.Clamp(y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)];

        // Flatten the 2D cellMap into 4 ints so the inner loop never indexes a 2D array.
        int ch00 = cellMap[0, 0], ch01 = cellMap[0, 1];
        int ch10 = cellMap[1, 0], ch11 = cellMap[1, 1];

        Parallel.For(0, height, y =>
        {
            // Pre-compute which row of the cell map applies to this y.
            var cellRow = ((y + offsetY) % 2 + 2) % 2;
            var rowChEven = cellRow == 0 ? ch00 : ch10; // channel at even x
            var rowChOdd = cellRow == 0 ? ch01 : ch11; // channel at odd  x

            // Decide whether the slow boundary path is needed for this row.
            var atTopOrBottom = y == 0 || y == height - 1;
            var rowOffset = y * width;
            var rowAbove = (y - 1) * width;
            var rowBelow = (y + 1) * width;

            for (var x = 0; x < width; x++)
            {
                var idx = rowOffset + x;
                var cellCol = ((x + offsetX) % 2 + 2) % 2;
                var channel = cellCol == 0 ? rowChEven : rowChOdd;

                // Border pixels use clamped reads to mirror the original behaviour.
                if (atTopOrBottom || x == 0 || x == width - 1)
                {
                    switch (channel)
                    {
                        case 0:
                            r[idx] = raw[idx];
                            g[idx] = (GetClamped(x - 1, y) + GetClamped(x + 1, y) + GetClamped(x, y - 1) + GetClamped(x, y + 1)) * 0.25f;
                            b[idx] = (GetClamped(x - 1, y - 1) + GetClamped(x + 1, y - 1) + GetClamped(x - 1, y + 1) + GetClamped(x + 1, y + 1)) * 0.25f;
                            break;

                        case 2:
                            b[idx] = raw[idx];
                            g[idx] = (GetClamped(x - 1, y) + GetClamped(x + 1, y) + GetClamped(x, y - 1) + GetClamped(x, y + 1)) * 0.25f;
                            r[idx] = (GetClamped(x - 1, y - 1) + GetClamped(x + 1, y - 1) + GetClamped(x - 1, y + 1) + GetClamped(x + 1, y + 1)) * 0.25f;
                            break;

                        default:
                            g[idx] = raw[idx];
                            if (cellRow == 0)
                            {
                                r[idx] = (GetClamped(x - 1, y) + GetClamped(x + 1, y)) * 0.5f;
                                b[idx] = (GetClamped(x, y - 1) + GetClamped(x, y + 1)) * 0.5f;
                            }
                            else
                            {
                                b[idx] = (GetClamped(x - 1, y) + GetClamped(x + 1, y)) * 0.5f;
                                r[idx] = (GetClamped(x, y - 1) + GetClamped(x, y + 1)) * 0.5f;
                            }
                            break;
                    }
                    continue;
                }

                // Fast interior path: no bounds checks, raw indexing only.
                switch (channel)
                {
                    case 0: // R pixel
                        r[idx] = raw[idx];
                        g[idx] = (raw[idx - 1] + raw[idx + 1] + raw[rowAbove + x] + raw[rowBelow + x]) * 0.25f;
                        b[idx] = (raw[rowAbove + x - 1] + raw[rowAbove + x + 1] + raw[rowBelow + x - 1] + raw[rowBelow + x + 1]) * 0.25f;
                        break;

                    case 2: // B pixel
                        b[idx] = raw[idx];
                        g[idx] = (raw[idx - 1] + raw[idx + 1] + raw[rowAbove + x] + raw[rowBelow + x]) * 0.25f;
                        r[idx] = (raw[rowAbove + x - 1] + raw[rowAbove + x + 1] + raw[rowBelow + x - 1] + raw[rowBelow + x + 1]) * 0.25f;
                        break;

                    default: // G pixel
                        g[idx] = raw[idx];
                        if (cellRow == 0)
                        {
                            r[idx] = (raw[idx - 1] + raw[idx + 1]) * 0.5f;
                            b[idx] = (raw[rowAbove + x] + raw[rowBelow + x]) * 0.5f;
                        }
                        else
                        {
                            b[idx] = (raw[idx - 1] + raw[idx + 1]) * 0.5f;
                            r[idx] = (raw[rowAbove + x] + raw[rowBelow + x]) * 0.5f;
                        }
                        break;
                }
            }
        });

        return [r, g, b];
    }

    // ------------------------------------------------------------------ //
    //  Per-channel (unlinked) auto-stretch for OSC frames                  //
    // ------------------------------------------------------------------ //

    /// <summary>Compute independent STF for each of R, G, B channels.</summary>
    public StfParameters[] ComputeAutoStretchOsc(LoadedFrame frame, double targetBackground = 0.25)
    {
        if (frame.ColorChannels is not { Length: 3 } cc)
        {
            var mono = ComputeAutoStretch(frame, targetBackground);
            return [mono, mono, mono];
        }

        return [
            ComputeAutoStretchForChannel(cc[0], frame.NormalizationMax, targetBackground),
            ComputeAutoStretchForChannel(cc[1], frame.NormalizationMax, targetBackground),
            ComputeAutoStretchForChannel(cc[2], frame.NormalizationMax, targetBackground)
        ];
    }

    private static StfParameters ComputeAutoStretchForChannel(float[] pixels, double normalizationMax, double targetBackground)
    {
        var sampled = Sample(pixels);
        if (sampled.Length == 0) return StfParameters.Default;
        Array.Sort(sampled);
        var dataMax = Math.Max(1.0, normalizationMax);
        var median = PercentileFromSorted(sampled, 0.5) / dataMax;
        var absDeviations = new float[sampled.Length];
        for (var i = 0; i < sampled.Length; i++)
        {
            absDeviations[i] = (float)Math.Abs((sampled[i] / dataMax) - median);
        }
        Array.Sort(absDeviations);
        var mad = PercentileFromSorted(absDeviations, 0.5);
        var sigma = 1.4826 * mad;
        const double shadowsClipping = -2.8;
        var c0 = Math.Clamp(median + (shadowsClipping * sigma), 0.0, 1.0);
        var medianNorm = Math.Clamp((median - c0) / Math.Max(1e-9, 1.0 - c0), 1e-9, 1.0 - 1e-9);
        var midtones = InverseMidtonesTransfer(targetBackground, medianNorm);
        if (double.IsNaN(midtones) || double.IsInfinity(midtones)) midtones = 0.25;
        midtones = Math.Clamp(midtones, 0.0, 1.0);
        return new StfParameters(c0, midtones, 1.0);
    }

    // ------------------------------------------------------------------ //
    //  Color (OSC) rendering helpers                                        //
    // ------------------------------------------------------------------ //

    private static byte StretchSample(float rawValue, double dataMax, StfParameters stf)
    {
        var normalised = rawValue / dataMax;
        var c0 = stf.Shadows;
        var m = stf.Midtones;
        var c1 = stf.Highlights;
        var stretchRange = Math.Max(1e-9, c1 - c0);
        var clipped = Math.Clamp((normalised - c0) / stretchRange, 0.0, 1.0);
        var stretched = Math.Clamp(MidtonesTransfer(clipped, m), 0.0, 1.0);
        return (byte)(stretched * 255.0 + 0.5);
    }

    /// <summary>Downsample R/G/B channels independently and produce an interleaved RGB24 byte array.</summary>
    private static byte[] DownsampleAndStretchColor(
        float[] rCh, float[] gCh, float[] bCh,
        int srcWidth, int srcHeight,
        int targetWidth, int targetHeight,
        StfParameters stfR, StfParameters stfG, StfParameters stfB,
        double normalizationMax)
    {
        var dataMax = Math.Max(1.0, normalizationMax);
        var data = new byte[targetWidth * targetHeight * 3];
        var useBilinear = srcWidth != targetWidth || srcHeight != targetHeight;

        Parallel.For(0, targetHeight, y =>
        {
            var sourceY = MapTargetToSourceCoordinate(y, srcHeight, targetHeight);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = MapTargetToSourceCoordinate(x, srcWidth, targetWidth);
                float rv, gv, bv;
                if (useBilinear)
                {
                    rv = (float)SampleBilinear(rCh, srcWidth, srcHeight, sourceX, sourceY);
                    gv = (float)SampleBilinear(gCh, srcWidth, srcHeight, sourceX, sourceY);
                    bv = (float)SampleBilinear(bCh, srcWidth, srcHeight, sourceX, sourceY);
                }
                else
                {
                    var si = ((int)sourceY * srcWidth) + (int)sourceX;
                    rv = rCh[si]; gv = gCh[si]; bv = bCh[si];
                }
                var idx = ((y * targetWidth) + x) * 3;
                data[idx] = StretchSample(rv, dataMax, stfR);
                data[idx + 1] = StretchSample(gv, dataMax, stfG);
                data[idx + 2] = StretchSample(bv, dataMax, stfB);
            }
        });

        return data;
    }

    private static BitmapSource CreateThumbnailBitmapColor(
        float[] rCh, float[] gCh, float[] bCh,
        int width, int height, int maxWidth, int maxHeight,
        StfParameters stfR, StfParameters stfG, StfParameters stfB,
        AstroMetrics? metrics, double normalizationMax)
    {
        var scale = Math.Min(maxWidth / (double)Math.Max(1, width), maxHeight / (double)Math.Max(1, height));
        scale = Math.Min(1.0, scale <= 0 ? 1.0 : scale);
        var contentWidth = Math.Max(1, (int)Math.Round(width * scale));
        var contentHeight = Math.Max(1, (int)Math.Round(height * scale));
        var sample = DownsampleAndStretchColor(rCh, gCh, bCh, width, height, contentWidth, contentHeight, stfR, stfG, stfB, normalizationMax);
        if (metrics is { SatelliteTrailConfidence: > 0, TrailX1: not null, TrailY1: not null, TrailX2: not null, TrailY2: not null })
        {
            DrawTrailOverlay(sample, contentWidth, contentHeight, metrics);
        }
        if (contentWidth != maxWidth || contentHeight != maxHeight)
        {
            sample = PadRgb(sample, contentWidth, contentHeight, maxWidth, maxHeight);
        }
        var bitmap = BitmapSource.Create(maxWidth, maxHeight, 96, 96, PixelFormats.Rgb24, null, sample, maxWidth * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateRoiBitmapColor(
        float[] rCh, float[] gCh, float[] bCh,
        int width, int height, int roiSize,
        StfParameters stfR, StfParameters stfG, StfParameters stfB,
        (double Left, double Top, double Width, double Height)? roiNormalizedRect,
        double normalizationMax)
    {
        int startX, startY, cropW, cropH;
        if (roiNormalizedRect is { } rect)
        {
            startX = (int)Math.Round(Math.Clamp(rect.Left, 0, 1) * (width - 1));
            startY = (int)Math.Round(Math.Clamp(rect.Top, 0, 1) * (height - 1));
            cropW = Math.Max(1, (int)Math.Round(Math.Clamp(rect.Width, 0, 1) * width));
            cropH = Math.Max(1, (int)Math.Round(Math.Clamp(rect.Height, 0, 1) * height));
            startX = Math.Clamp(startX, 0, Math.Max(0, width - 1));
            startY = Math.Clamp(startY, 0, Math.Max(0, height - 1));
            cropW = Math.Min(cropW, width - startX);
            cropH = Math.Min(cropH, height - startY);
            var cropSide = Math.Min(cropW, cropH);
            if (cropW != cropSide) { startX += (cropW - cropSide) / 2; cropW = cropSide; }
            if (cropH != cropSide) { startY += (cropH - cropSide) / 2; cropH = cropSide; }
        }
        else
        {
            // Use luminance channel to detect center
            var lum = new float[rCh.Length];
            for (var i = 0; i < lum.Length; i++)
                lum[i] = 0.2126f * rCh[i] + 0.7152f * gCh[i] + 0.0722f * bCh[i];
            var (cx, cy) = DetectRoiCenter(lum, width, height);
            var half = roiSize / 2;
            startX = Math.Clamp(cx - half, 0, Math.Max(0, width - roiSize));
            startY = Math.Clamp(cy - half, 0, Math.Max(0, height - roiSize));
            cropW = Math.Min(roiSize, width);
            cropH = Math.Min(roiSize, height);
        }

        float[] CropChannel(float[] ch)
        {
            var crop = new float[cropW * cropH];
            for (var y = 0; y < cropH; y++)
                Array.Copy(ch, ((startY + y) * width) + startX, crop, y * cropW, cropW);
            return crop;
        }

        var rCrop = CropChannel(rCh);
        var gCrop = CropChannel(gCh);
        var bCrop = CropChannel(bCh);

        var sample = DownsampleAndStretchColor(rCrop, gCrop, bCrop, cropW, cropH, roiSize, roiSize, stfR, stfG, stfB, normalizationMax);
        var bitmap = BitmapSource.Create(roiSize, roiSize, 96, 96, PixelFormats.Rgb24, null, sample, roiSize * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateFullFrameBitmapColor(
        float[] rCh, float[] gCh, float[] bCh,
        int width, int height,
        StfParameters stfR, StfParameters stfG, StfParameters stfB,
        double normalizationMax)
    {
        var sample = DownsampleAndStretchColor(rCh, gCh, bCh, width, height, width, height, stfR, stfG, stfB, normalizationMax);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, sample, width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateScaledFrameBitmapColor(
        float[] rCh, float[] gCh, float[] bCh,
        int width, int height, int targetWidth, int targetHeight,
        StfParameters stfR, StfParameters stfG, StfParameters stfB,
        double normalizationMax)
    {
        var tw = Math.Max(1, Math.Min(width, targetWidth));
        var th = Math.Max(1, Math.Min(height, targetHeight));
        var sample = DownsampleAndStretchColor(rCh, gCh, bCh, width, height, tw, th, stfR, stfG, stfB, normalizationMax);
        var bitmap = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Rgb24, null, sample, tw * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateThumbnailBitmap(float[] pixels, int width, int height, int maxWidth, int maxHeight, StfParameters stf, AstroMetrics? metrics, double normalizationMax)
    {
        var scale = Math.Min(maxWidth / (double)Math.Max(1, width), maxHeight / (double)Math.Max(1, height));
        scale = Math.Min(1.0, scale <= 0 ? 1.0 : scale);
        var contentWidth = Math.Max(1, (int)Math.Round(width * scale));
        var contentHeight = Math.Max(1, (int)Math.Round(height * scale));

        var sample = DownsampleAndStretch(pixels, width, height, contentWidth, contentHeight, stf, normalizationMax);
        if (metrics is { SatelliteTrailConfidence: > 0, TrailX1: not null, TrailY1: not null, TrailX2: not null, TrailY2: not null })
        {
            DrawTrailOverlay(sample, contentWidth, contentHeight, metrics);
        }

        if (contentWidth != maxWidth || contentHeight != maxHeight)
        {
            sample = PadRgb(sample, contentWidth, contentHeight, maxWidth, maxHeight);
        }

        var stride = maxWidth * 3;

        var bitmap = BitmapSource.Create(maxWidth, maxHeight, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] PadRgb(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var padded = new byte[targetWidth * targetHeight * 3];
        var offsetX = Math.Max(0, (targetWidth - sourceWidth) / 2);
        var offsetY = Math.Max(0, (targetHeight - sourceHeight) / 2);

        for (var y = 0; y < sourceHeight; y++)
        {
            var sourceOffset = y * sourceWidth * 3;
            var targetOffset = (((y + offsetY) * targetWidth) + offsetX) * 3;
            Buffer.BlockCopy(source, sourceOffset, padded, targetOffset, sourceWidth * 3);
        }

        return padded;
    }

    private static void DrawTrailOverlay(byte[] rgb, int width, int height, AstroMetrics metrics)
    {
        var x1 = (int)Math.Round(Math.Clamp(metrics.TrailX1 ?? 0, 0.0, 1.0) * (width - 1));
        var y1 = (int)Math.Round(Math.Clamp(metrics.TrailY1 ?? 0, 0.0, 1.0) * (height - 1));
        var x2 = (int)Math.Round(Math.Clamp(metrics.TrailX2 ?? 0, 0.0, 1.0) * (width - 1));
        var y2 = (int)Math.Round(Math.Clamp(metrics.TrailY2 ?? 0, 0.0, 1.0) * (height - 1));

        var dx = Math.Abs(x2 - x1);
        var dy = Math.Abs(y2 - y1);
        var sx = x1 < x2 ? 1 : -1;
        var sy = y1 < y2 ? 1 : -1;
        var err = dx - dy;
        var x = x1;
        var y = y1;

        while (true)
        {
            DrawGreenDot(rgb, width, height, x, y, 1);
            if (x == x2 && y == y2)
            {
                break;
            }

            var e2 = 2 * err;
            if (e2 > -dy)
            {
                err -= dy;
                x += sx;
            }

            if (e2 < dx)
            {
                err += dx;
                y += sy;
            }
        }
    }

    private static void DrawGreenDot(byte[] rgb, int width, int height, int cx, int cy, int radius)
    {
        for (var oy = -radius; oy <= radius; oy++)
        {
            var y = cy + oy;
            if ((uint)y >= (uint)height)
            {
                continue;
            }

            for (var ox = -radius; ox <= radius; ox++)
            {
                var x = cx + ox;
                if ((uint)x >= (uint)width)
                {
                    continue;
                }

                var idx = ((y * width) + x) * 3;
                rgb[idx] = 0;
                rgb[idx + 1] = 255;
                rgb[idx + 2] = 0;
            }
        }
    }

    private static BitmapSource CreateRoiBitmap(float[] pixels, int width, int height, int roiSize, StfParameters stf, (double Left, double Top, double Width, double Height)? roiNormalizedRect, double normalizationMax)
    {
        int startX, startY, cropW, cropH;
        if (roiNormalizedRect is { } rect)
        {
            // Convert normalized rect to pixel coordinates
            startX = (int)Math.Round(Math.Clamp(rect.Left, 0, 1) * (width - 1));
            startY = (int)Math.Round(Math.Clamp(rect.Top, 0, 1) * (height - 1));
            cropW = Math.Max(1, (int)Math.Round(Math.Clamp(rect.Width, 0, 1) * width));
            cropH = Math.Max(1, (int)Math.Round(Math.Clamp(rect.Height, 0, 1) * height));
            // Clamp to image bounds
            startX = Math.Clamp(startX, 0, Math.Max(0, width - 1));
            startY = Math.Clamp(startY, 0, Math.Max(0, height - 1));
            cropW = Math.Min(cropW, width - startX);
            cropH = Math.Min(cropH, height - startY);

            // Force a square crop so the ROI bitmap is not distorted.
            // Use the smaller axis and re-center on the other axis.
            var cropSide = Math.Min(cropW, cropH);
            if (cropW != cropSide)
            {
                startX += (cropW - cropSide) / 2;
                cropW = cropSide;
            }
            if (cropH != cropSide)
            {
                startY += (cropH - cropSide) / 2;
                cropH = cropSide;
            }
        }
        else
        {
            var (cx, cy) = DetectRoiCenter(pixels, width, height);
            var half = roiSize / 2;
            startX = Math.Clamp(cx - half, 0, Math.Max(0, width - roiSize));
            startY = Math.Clamp(cy - half, 0, Math.Max(0, height - roiSize));
            cropW = Math.Min(roiSize, width);
            cropH = Math.Min(roiSize, height);
        }

        var crop = new float[cropW * cropH];
        for (var y = 0; y < cropH; y++)
        {
            var sourceOffset = ((startY + y) * width) + startX;
            var targetOffset = y * cropW;
            Array.Copy(pixels, sourceOffset, crop, targetOffset, cropW);
        }

        var sample = DownsampleAndStretch(crop, cropW, cropH, roiSize, roiSize, stf, normalizationMax);
        var stride = roiSize * 3;
        var bitmap = BitmapSource.Create(roiSize, roiSize, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateFullFrameBitmap(float[] pixels, int width, int height, StfParameters stf, double normalizationMax)
    {
        var sample = DownsampleAndStretch(pixels, width, height, width, height, stf, normalizationMax);
        var stride = width * 3;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateScaledFrameBitmap(float[] pixels, int width, int height, int targetWidth, int targetHeight, StfParameters stf, double normalizationMax)
    {
        var safeTargetWidth = Math.Max(1, Math.Min(width, targetWidth));
        var safeTargetHeight = Math.Max(1, Math.Min(height, targetHeight));
        var sample = DownsampleAndStretch(pixels, width, height, safeTargetWidth, safeTargetHeight, stf, normalizationMax);
        var stride = safeTargetWidth * 3;
        var bitmap = BitmapSource.Create(safeTargetWidth, safeTargetHeight, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        bitmap.Freeze();

        if (safeTargetWidth == width && safeTargetHeight == height)
        {
            return bitmap;
        }

        var scaledBitmap = new TransformedBitmap(bitmap, new ScaleTransform(width / (double)safeTargetWidth, height / (double)safeTargetHeight));
        scaledBitmap.Freeze();
        return scaledBitmap;
    }

    private static (int X, int Y) DetectRoiCenter(float[] pixels, int width, int height)
    {
        // Downsample to a small working image so blurring is cheap and covers large spatial scales.
        var longest = Math.Max(width, height);
        var scale = longest > 192 ? 192.0 / longest : 1.0;
        var sw = Math.Max(32, (int)Math.Round(width * scale));
        var sh = Math.Max(32, (int)Math.Round(height * scale));

        var small = ResampleNearest(pixels, width, height, sw, sh);

        // Background subtract using the median, then clip very bright pixels at a high
        // percentile so a single saturated star can't dominate the scoring.
        var sampled = Sample(small);
        Array.Sort(sampled);
        var bg = (float)PercentileFromSorted(sampled, 0.5);
        var hi = (float)PercentileFromSorted(sampled, 0.98);
        var clipCeiling = Math.Max(bg + 1e-6f, hi);
        for (var i = 0; i < small.Length; i++)
        {
            var v = small[i] - bg;
            if (v < 0f) v = 0f;
            // Soft clip so bright stars saturate and don't outweigh extended structure.
            if (v > clipCeiling) v = clipCeiling;
            small[i] = v;
        }

        // Two parallel blurs:
        //  - "structure" blur (large radius) detects extended sources like galaxies / nebulae.
        //  - "detail" blur (small radius) preserves local texture for the contrast metric.
        var structureRadius = Math.Max(3, (int)Math.Round(Math.Min(sw, sh) * 0.06));
        var detailRadius = Math.Max(1, (int)Math.Round(Math.Min(sw, sh) * 0.015));
        var structure = small;
        for (var pass = 0; pass < 2; pass++)
        {
            structure = BoxBlur(structure, sw, sh, structureRadius);
        }
        var detail = BoxBlur(small, sw, sh, detailRadius);

        // Local contrast: variance of `detail` over a window roughly the size of the
        // structure blur. High contrast = interesting texture (dust lanes, cluster cores,
        // nebula edges). Plain background has near-zero contrast.
        var contrast = LocalVariance(detail, sw, sh, structureRadius);

        // Center-bias Gaussian weight: peaks at image center, drops off but never to zero.
        var cxImg = (sw - 1) * 0.5;
        var cyImg = (sh - 1) * 0.5;
        var sigma = Math.Min(sw, sh) * 0.35;
        var twoSigmaSq = 2.0 * sigma * sigma;

        // Normalize structure and contrast to comparable ranges so neither dominates.
        var maxStructure = 0f;
        var maxContrast = 0f;
        for (var i = 0; i < structure.Length; i++)
        {
            if (structure[i] > maxStructure) maxStructure = structure[i];
            if (contrast[i] > maxContrast) maxContrast = contrast[i];
        }
        var invStructure = maxStructure > 0 ? 1f / maxStructure : 0f;
        var invContrast = maxContrast > 0 ? 1f / maxContrast : 0f;

        // Restrict to the central 90 % of the frame to avoid picking up edge artefacts.
        var marginX = (int)Math.Round(sw * 0.05);
        var marginY = (int)Math.Round(sh * 0.05);

        var bestScore = double.NegativeInfinity;
        var bestPx = sw / 2;
        var bestPy = sh / 2;

        for (var y = marginY; y < sh - marginY; y++)
        {
            var row = y * sw;
            var dy = y - cyImg;
            for (var x = marginX; x < sw - marginX; x++)
            {
                var dx = x - cxImg;
                var centerWeight = Math.Exp(-(dx * dx + dy * dy) / twoSigmaSq);

                var s = structure[row + x] * invStructure;
                var c = contrast[row + x] * invContrast;

                // Combined score: extended brightness AND local contrast, both required.
                // Multiplying ensures plain background (s>0, c~0) and isolated stars
                // (s small after clipping, c moderate but spatially tiny after blur) score low,
                // while galaxies/clusters/nebulae (s and c both elevated over a wide area) win.
                var score = (0.6 * s + 0.4 * c) * (0.3 + 0.7 * s) * (0.3 + 0.7 * c) * centerWeight;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPx = x;
                    bestPy = y;
                }
            }
        }

        // Map the small-image peak back to full-resolution pixel coordinates.
        var fullX = (int)Math.Round((bestPx / (double)Math.Max(1, sw - 1)) * (width - 1));
        var fullY = (int)Math.Round((bestPy / (double)Math.Max(1, sh - 1)) * (height - 1));
        return (Math.Clamp(fullX, 0, width - 1), Math.Clamp(fullY, 0, height - 1));
    }

    private static float[] LocalVariance(float[] input, int width, int height, int radius)
    {
        // Compute local variance using box-blurred mean and box-blurred mean-of-squares.
        var squared = new float[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            squared[i] = input[i] * input[i];
        }
        var mean = BoxBlur(input, width, height, radius);
        var meanSq = BoxBlur(squared, width, height, radius);
        var result = new float[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var v = meanSq[i] - (mean[i] * mean[i]);
            result[i] = v > 0f ? v : 0f;
        }
        return result;
    }

    private static float[] ResampleNearest(float[] pixels, int width, int height, int targetWidth, int targetHeight)
    {
        var result = new float[targetWidth * targetHeight];
        var targetHeightM1 = Math.Max(1, targetHeight - 1);
        var targetWidthM1 = Math.Max(1, targetWidth - 1);
        Parallel.For(0, targetHeight, y =>
        {
            var sy = Math.Min(height - 1, (int)Math.Round((y / (double)targetHeightM1) * (height - 1)));
            var srcRow = sy * width;
            var dstRow = y * targetWidth;
            for (var x = 0; x < targetWidth; x++)
            {
                var sx = Math.Min(width - 1, (int)Math.Round((x / (double)targetWidthM1) * (width - 1)));
                result[dstRow + x] = pixels[srcRow + sx];
            }
        });
        return result;
    }

    private static float[] BoxBlur(float[] input, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            return input;
        }

        var output = new float[input.Length];
        var area = (2 * radius + 1) * (2 * radius + 1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double sum = 0;
                for (var oy = -radius; oy <= radius; oy++)
                {
                    var sy = Math.Clamp(y + oy, 0, height - 1);
                    var row = sy * width;
                    for (var ox = -radius; ox <= radius; ox++)
                    {
                        var sx = Math.Clamp(x + ox, 0, width - 1);
                        sum += input[row + sx];
                    }
                }

                output[(y * width) + x] = (float)(sum / area);
            }
        }

        return output;
    }

    private static byte[] DownsampleAndStretch(float[] pixels, int width, int height, int targetWidth, int targetHeight, StfParameters stf, double normalizationMax)
    {
        var dataMax = Math.Max(1.0, normalizationMax);

        // STF clipping bounds in [0,1]
        var c0 = stf.Shadows;    // shadow clipping
        var m = stf.Midtones;   // midtones balance
        var c1 = stf.Highlights; // highlights clipping (typically 1.0)
        var stretchRange = Math.Max(1e-9, c1 - c0);

        var data = new byte[targetWidth * targetHeight * 3];
        var useBilinear = width != targetWidth || height != targetHeight;

        Parallel.For(0, targetHeight, y =>
        {
            var sourceY = MapTargetToSourceCoordinate(y, height, targetHeight);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = MapTargetToSourceCoordinate(x, width, targetWidth);
                var rawValue = useBilinear
                    ? SampleBilinear(pixels, width, height, sourceX, sourceY)
                    : pixels[((int)sourceY * width) + (int)sourceX];

                // Normalise to [0,1]
                var normalised = rawValue / dataMax;

                // Apply STF: shadows/highlights clip then midtones transfer
                var clipped = Math.Clamp((normalised - c0) / stretchRange, 0.0, 1.0);
                var stretched = Math.Clamp(MidtonesTransfer(clipped, m), 0.0, 1.0);
                var b = (byte)(stretched * 255.0 + 0.5);

                var index = ((y * targetWidth) + x) * 3;
                data[index] = b;
                data[index + 1] = b;
                data[index + 2] = b;
            }
        });

        return data;
    }

    private static double ComputeFitsNormalizationMax(FitsHeaderInfo header)
    {
        var absBitPix = Math.Abs(header.BitPix);
        if (absBitPix is 8 or 16 or 32)
        {
            return Math.Pow(2.0, absBitPix) - 1.0;
        }

        return 1.0;
    }

    private static double GetNormalizationMax(SampleFormat format)
    {
        return format switch
        {
            SampleFormat.UInt8 => byte.MaxValue,
            SampleFormat.UInt16 => ushort.MaxValue,
            SampleFormat.UInt32 => uint.MaxValue,
            SampleFormat.UInt64 => ulong.MaxValue,
            _ => 1.0
        };
    }

    private static double MapTargetToSourceCoordinate(int targetIndex, int sourceSize, int targetSize)
    {
        if (sourceSize <= 1 || targetSize <= 1)
        {
            return 0;
        }

        return Math.Clamp((((targetIndex + 0.5) * sourceSize) / (double)targetSize) - 0.5, 0.0, sourceSize - 1.0);
    }

    private static float SampleBilinear(float[] pixels, int width, int height, double x, double y)
    {
        var x0 = Math.Clamp((int)Math.Floor(x), 0, width - 1);
        var y0 = Math.Clamp((int)Math.Floor(y), 0, height - 1);
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var tx = x - x0;
        var ty = y - y0;

        var topLeft = pixels[(y0 * width) + x0];
        var topRight = pixels[(y0 * width) + x1];
        var bottomLeft = pixels[(y1 * width) + x0];
        var bottomRight = pixels[(y1 * width) + x1];

        var top = (topLeft * (1.0 - tx)) + (topRight * tx);
        var bottom = (bottomLeft * (1.0 - tx)) + (bottomRight * tx);
        return (float)((top * (1.0 - ty)) + (bottom * ty));
    }

    private static void NormalizeRenderedBackground(byte[] rgb, double targetBackground)
    {
        if (rgb.Length < 3)
        {
            return;
        }

        var pixelCount = rgb.Length / 3;
        var sampleCount = Math.Min(pixelCount, 200_000);
        var step = Math.Max(1, pixelCount / sampleCount);
        var sample = new byte[(pixelCount + step - 1) / step];
        var sampleIndex = 0;

        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex += step)
        {
            sample[sampleIndex++] = rgb[pixelIndex * 3];
        }

        Array.Sort(sample, 0, sampleIndex);
        var currentBackground = sample[Math.Clamp((int)Math.Round((sampleIndex - 1) * 0.35), 0, sampleIndex - 1)];
        var target = Math.Clamp((int)Math.Round(targetBackground * 255.0), 0, 255);
        var delta = target - currentBackground;

        if (delta == 0)
        {
            return;
        }

        for (var i = 0; i < rgb.Length; i += 3)
        {
            var adjusted = Math.Clamp(rgb[i] + delta, 0, 255);
            var b = (byte)adjusted;
            rgb[i] = b;
            rgb[i + 1] = b;
            rgb[i + 2] = b;
        }
    }

    private static float[] CreateAnalysisPixels(float[] pixels, int width, int height, int maxDimension, out int analysisWidth, out int analysisHeight, out double xScale, out double yScale)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxDimension)
        {
            analysisWidth = width;
            analysisHeight = height;
            xScale = 1.0;
            yScale = 1.0;
            return pixels;
        }

        var scale = maxDimension / (double)longest;
        analysisWidth = Math.Max(256, (int)Math.Round(width * scale));
        analysisHeight = Math.Max(256, (int)Math.Round(height * scale));
        xScale = width / (double)analysisWidth;
        yScale = height / (double)analysisHeight;
        return ResampleNearest(pixels, width, height, analysisWidth, analysisHeight);
    }

    private static AstroMetrics ComputeMetrics(LoadedFrame frame)
    {
        var pixels = frame.Pixels;
        var width = frame.Width;
        var height = frame.Height;
        var statsSample = Sample(pixels);
        Array.Sort(statsSample);
        var median = PercentileFromSorted(statsSample, 0.5);
        var mad = MedianAbsoluteDeviation(statsSample, median, alreadySorted: true);
        var (minValue, minCount, maxValue, maxCount) = ComputeExtremaWithCounts(pixels);
        var background = median;
        var sigma = ComputeSigmaFromSample(statsSample, background);
        var analysisPixels = CreateAnalysisPixels(pixels, width, height, 1536, out var analysisWidth, out var analysisHeight, out _, out _);

        // Derive 768-px trail buffer from the already-computed 1536-px analysis pixels
        // instead of resampling from the full-resolution source again.
        float[] trailPixels;
        int trailWidth, trailHeight;
        if (analysisPixels == pixels)
        {
            trailPixels = CreateAnalysisPixels(pixels, width, height, 768, out trailWidth, out trailHeight, out _, out _);
        }
        else
        {
            trailPixels = CreateAnalysisPixels(analysisPixels, analysisWidth, analysisHeight, 768, out trailWidth, out trailHeight, out _, out _);
        }

        var (stars, totalStarCount) = DetectStars(pixels, width, height, background, sigma);
        var orderedStars = stars.OrderByDescending(s => s.Peak).ToList();

        // Reject saturated stars (their FWHM is artificially wide because the core
        // is clipped) and excessively eccentric ones (trailed/blended detections).
        // We also drop the bottom ~20% of detections to avoid noise-driven outliers.
        // Using a relative saturation threshold against the per-frame max keeps the
        // filter robust to scaling differences between FITS/XISF sources.
        var saturationLevel = maxValue > 0 ? maxValue * 0.85 : double.PositiveInfinity;
        var filtered = orderedStars
            .Where(s => s.Peak < saturationLevel && s.Eccentricity < 0.7 && s.Fwhm > 0 && s.Fwhm < 12.0)
            .ToList();

        var fwhmSource = filtered.Count >= 10 ? filtered : orderedStars;

        // Center-weighted aggregation. Field curvature/coma at the periphery
        // inflates per-star FWHM and eccentricity; CCDInspector handles this
        // implicitly by sampling thousands of stars, so the well-corrected
        // central majority dominates the median. We instead weight each star
        // by a smooth radial falloff from the image center (cos^2 of the
        // normalized radius), which yields the same "average representative
        // FWHM of the central field" without requiring a much larger sample.
        var centerX = width * 0.5;
        var centerY = height * 0.5;
        var halfDiag = Math.Sqrt((centerX * centerX) + (centerY * centerY));
        var fwhm = WeightedMedian(fwhmSource, s => s.Fwhm, s => RadialCenterWeight(s.X, s.Y, centerX, centerY, halfDiag));
        var hfr = WeightedMedian(fwhmSource, s => s.Hfr, s => RadialCenterWeight(s.X, s.Y, centerX, centerY, halfDiag));
        var eccentricity = WeightedMedian(fwhmSource, s => s.Eccentricity, s => RadialCenterWeight(s.X, s.Y, centerX, centerY, halfDiag));
        var trail = DetectTrail(trailPixels, trailWidth, trailHeight);
        var starCount = totalStarCount;

        var measuredStars = new MeasuredStar[fwhmSource.Count];
        for (var i = 0; i < fwhmSource.Count; i++)
        {
            var s = fwhmSource[i];
            measuredStars[i] = new MeasuredStar(s.X, s.Y, s.Fwhm, s.Hfr, s.Peak);
        }

        double? fwhmArcsec = null;
        if (frame.FocalLengthMm is > 0 && frame.PixelSizeUm is > 0 && fwhm > 0)
        {
            var arcsecPerPixel = 206.265 * (frame.PixelSizeUm.Value / frame.FocalLengthMm.Value);
            fwhmArcsec = fwhm * arcsecPerPixel;
        }

        return new AstroMetrics
        {
            Fwhm = fwhm,
            FwhmArcsec = fwhmArcsec,
            Sqm = frame.Sqm,
            SkyTemp = frame.SkyTemp,
            Hfr = hfr,
            StarCount = starCount,
            Eccentricity = eccentricity,
            MeanBackground = background,
            Median = median,
            Mad = mad,
            Min = minValue,
            MinCount = minCount,
            Max = maxValue,
            MaxCount = maxCount,
            FocalLengthMm = frame.FocalLengthMm,
            PixelSizeUm = frame.PixelSizeUm,
            SatelliteTrailConfidence = trail.Confidence,
            TrailX1 = trail.Confidence > 0 ? trail.X1 : null,
            TrailY1 = trail.Confidence > 0 ? trail.Y1 : null,
            TrailX2 = trail.Confidence > 0 ? trail.X2 : null,
            TrailY2 = trail.Confidence > 0 ? trail.Y2 : null,
            Stars = measuredStars
        };
    }

    private static double TrimmedMedian(IEnumerable<double> values)
    {
        var arr = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).ToArray();
        if (arr.Length == 0) return 0;
        if (arr.Length < 10) return Median(arr);
        Array.Sort(arr);
        var trim = arr.Length / 10; // drop 10% from each end
        var span = arr.AsSpan(trim, arr.Length - (2 * trim));
        var mid = span.Length / 2;
        return (span.Length & 1) == 1 ? span[mid] : (span[mid - 1] + span[mid]) * 0.5;
    }

    /// <summary>
    /// Radial weight: 1.0 at the image center, falling sharply with radius and
    /// going to zero outside the inner 75% of the half-diagonal. The hard outer
    /// cut excludes the periphery (where coma/field curvature inflates FWHM and
    /// eccentricity) entirely, and the cos^8 falloff biases the surviving
    /// weighted median strongly toward the well-corrected inner field. This
    /// mirrors how CCDInspector reports a "representative" FWHM that is not
    /// skewed by edge curvature.
    /// </summary>
    private static double RadialCenterWeight(double x, double y, double centerX, double centerY, double halfDiag)
    {
        if (halfDiag <= 0)
        {
            return 1.0;
        }

        var dx = x - centerX;
        var dy = y - centerY;
        var r = Math.Sqrt((dx * dx) + (dy * dy)) / halfDiag; // 0 at center, 1 at corner

        const double cutoff = 0.75;
        if (r >= cutoff)
        {
            return 0;
        }

        // Remap r into [0, 1] over the inner field, then cos^4 falloff.
        var rn = r / cutoff;
        var c = Math.Cos(rn * Math.PI * 0.5);
        var c2 = c * c;
        return c2 * c2;
    }

    private static double WeightedMedian<T>(IReadOnlyList<T> items, Func<T, double> valueSelector, Func<T, double> weightSelector)
    {
        if (items.Count == 0)
        {
            return 0;
        }

        var pairs = new (double Value, double Weight)[items.Count];
        double totalWeight = 0;
        var n = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var v = valueSelector(items[i]);
            var w = weightSelector(items[i]);
            if (double.IsNaN(v) || double.IsInfinity(v) || w <= 0)
            {
                continue;
            }
            pairs[n++] = (v, w);
            totalWeight += w;
        }

        if (n == 0 || totalWeight <= 0)
        {
            return 0;
        }

        Array.Sort(pairs, 0, n, Comparer<(double Value, double Weight)>.Create((a, b) => a.Value.CompareTo(b.Value)));

        var half = totalWeight * 0.5;
        double cumulative = 0;
        for (var i = 0; i < n; i++)
        {
            cumulative += pairs[i].Weight;
            if (cumulative >= half)
            {
                return pairs[i].Value;
            }
        }

        return pairs[n - 1].Value;
    }

    private static (double Min, int MinCount, double Max, int MaxCount) ComputeExtremaWithCounts(float[] values)
    {
        var initialized = false;
        float min = 0;
        float max = 0;
        var minCount = 0;
        var maxCount = 0;

        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                continue;
            }

            if (!initialized)
            {
                min = value;
                max = value;
                minCount = 1;
                maxCount = 1;
                initialized = true;
                continue;
            }

            if (value < min)
            {
                min = value;
                minCount = 1;
            }
            else if (value == min)
            {
                minCount++;
            }

            if (value > max)
            {
                max = value;
                maxCount = 1;
            }
            else if (value == max)
            {
                maxCount++;
            }
        }

        return initialized ? (min, minCount, max, maxCount) : (0, 0, 0, 0);
    }

    private static (List<(double Peak, double Fwhm, double Hfr, double Eccentricity, double X, double Y)> Measurements, int TotalCount) DetectStars(float[] pixels, int width, int height, double background, double sigma)
    {
        // Detect on full-resolution image. Use a permissive 3-sigma threshold
        // similar to PixInsight / Hocus Focus defaults, then suppress duplicates
        // via a coarse spatial hash so we don't run an O(N^2) scan.
        const int maxMeasuredStars = 2000;
        const double suppressionRadius = 4.0;
        const double suppressionRadiusSq = suppressionRadius * suppressionRadius;
        const int cellSize = 8; // > 2 * suppressionRadius

        var threshold = background + (3.0 * sigma);
        var minNeighborLevel = background + (1.5 * sigma);

        if (width < 5 || height < 5)
        {
            return (new List<(double, double, double, double, double, double)>(), 0);
        }

        // Parallel local-max scan over horizontal stripes.
        const int stripeHeight = 64;
        var stripeCount = ((height - 2) + stripeHeight - 1) / stripeHeight;
        var stripeLists = new List<(float Peak, int X, int Y)>[stripeCount];

        Parallel.For(0, stripeCount, stripe =>
        {
            var yStart = 1 + (stripe * stripeHeight);
            var yEnd = Math.Min(yStart + stripeHeight, height - 1);
            var local = new List<(float, int, int)>(1024);

            for (var y = yStart; y < yEnd; y++)
            {
                var row = y * width;
                var rowUp = row - width;
                var rowDn = row + width;
                for (var x = 1; x < width - 1; x++)
                {
                    var center = pixels[row + x];
                    if (center < threshold)
                    {
                        continue;
                    }

                    // Strict 3x3 local maximum.
                    if (center < pixels[row + x - 1] ||
                        center <= pixels[row + x + 1] ||
                        center < pixels[rowUp + x] ||
                        center <= pixels[rowDn + x] ||
                        center < pixels[rowUp + x - 1] ||
                        center <= pixels[rowUp + x + 1] ||
                        center < pixels[rowDn + x - 1] ||
                        center <= pixels[rowDn + x + 1])
                    {
                        continue;
                    }

                    // Require at least 3 bright neighbours so single hot pixels
                    // (with a dark 8-neighbourhood) are rejected.
                    var support = 0;
                    if (pixels[row + x - 1] >= minNeighborLevel) support++;
                    if (pixels[row + x + 1] >= minNeighborLevel) support++;
                    if (pixels[rowUp + x] >= minNeighborLevel) support++;
                    if (pixels[rowDn + x] >= minNeighborLevel) support++;
                    if (pixels[rowUp + x - 1] >= minNeighborLevel) support++;
                    if (pixels[rowUp + x + 1] >= minNeighborLevel) support++;
                    if (pixels[rowDn + x - 1] >= minNeighborLevel) support++;
                    if (pixels[rowDn + x + 1] >= minNeighborLevel) support++;

                    if (support < 3)
                    {
                        continue;
                    }

                    local.Add((center, x, y));
                }
            }

            stripeLists[stripe] = local;
        });

        var totalCandidates = 0;
        for (var i = 0; i < stripeLists.Length; i++)
        {
            totalCandidates += stripeLists[i]?.Count ?? 0;
        }

        var result = new List<(double Peak, double Fwhm, double Hfr, double Eccentricity, double X, double Y)>(Math.Min(maxMeasuredStars, totalCandidates));
        if (totalCandidates == 0)
        {
            return (result, 0);
        }

        var candidates = new (float Peak, int X, int Y)[totalCandidates];
        var offset = 0;
        for (var i = 0; i < stripeLists.Length; i++)
        {
            var list = stripeLists[i];
            if (list == null) continue;
            for (var j = 0; j < list.Count; j++)
            {
                candidates[offset++] = list[j];
            }
        }

        // Sort brightest-first.
        Array.Sort(candidates, (a, b) => b.Peak.CompareTo(a.Peak));

        // Spatial-hash suppression: keep brightest, reject anything within radius.
        var gridW = (width / cellSize) + 1;
        var gridH = (height / cellSize) + 1;
        var grid = new List<int>[gridW * gridH];
        var keptX = new int[candidates.Length];
        var keptY = new int[candidates.Length];
        var keptPeak = new float[candidates.Length];
        var keptCount = 0;

        for (var i = 0; i < candidates.Length; i++)
        {
            var (peak, x, y) = candidates[i];
            var gx = x / cellSize;
            var gy = y / cellSize;
            var tooClose = false;
            for (var ay = Math.Max(0, gy - 1); ay <= Math.Min(gridH - 1, gy + 1) && !tooClose; ay++)
            {
                for (var ax = Math.Max(0, gx - 1); ax <= Math.Min(gridW - 1, gx + 1); ax++)
                {
                    var bucket = grid[(ay * gridW) + ax];
                    if (bucket == null) continue;
                    for (var k = 0; k < bucket.Count; k++)
                    {
                        var idx = bucket[k];
                        var dx = keptX[idx] - x;
                        var dy = keptY[idx] - y;
                        if ((dx * dx) + (dy * dy) <= suppressionRadiusSq)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                    if (tooClose) break;
                }
            }

            if (tooClose) continue;

            keptX[keptCount] = x;
            keptY[keptCount] = y;
            keptPeak[keptCount] = peak;
            var cellIdx = (gy * gridW) + gx;
            (grid[cellIdx] ??= new List<int>(4)).Add(keptCount);
            keptCount++;
        }

        // Measure the brightest subset only (perf); the total kept count is the star count.
        var measureCount = Math.Min(keptCount, maxMeasuredStars);
        var measurements = new (double Peak, double Fwhm, double Hfr, double Eccentricity, double X, double Y)[measureCount];
        Parallel.For(0, measureCount, i =>
        {
            var cx = Math.Clamp(keptX[i], 3, width - 4);
            var cy = Math.Clamp(keptY[i], 3, height - 4);
            var m = MeasureStar(pixels, width, height, cx, cy, background);
            measurements[i] = (keptPeak[i], m.Fwhm, m.Hfr, m.Eccentricity, m.X, m.Y);
        });

        for (var i = 0; i < measureCount; i++)
        {
            var m = measurements[i];
            if (m.Fwhm > 0 && m.Hfr > 0)
            {
                result.Add(m);
            }
        }

        return (result, keptCount);
    }

    private static float[] MedianFilter3x3(float[] pixels, int width, int height)
    {
        if (width < 3 || height < 3)
        {
            return pixels;
        }

        var filtered = new float[pixels.Length];
        Array.Copy(pixels, filtered, pixels.Length);

        var window = new float[9];
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var k = 0;
                for (var oy = -1; oy <= 1; oy++)
                {
                    var row = (y + oy) * width;
                    for (var ox = -1; ox <= 1; ox++)
                    {
                        window[k++] = pixels[row + x + ox];
                    }
                }

                Array.Sort(window);
                filtered[(y * width) + x] = window[4];
            }
        }

        return filtered;
    }

    private static (double Fwhm, double Hfr, double Eccentricity, double X, double Y) MeasureStar(float[] pixels, int width, int height, int cx, int cy, double background)
    {
        const int radius = 7;
        const int annulusInner = 9;
        const int annulusOuter = 13;
        var points = new List<(double X, double Y, double R, double Flux)>((radius * 2 + 1) * (radius * 2 + 1));
        Span<float> annulus = stackalloc float[(annulusOuter * 2 + 1) * (annulusOuter * 2 + 1)];
        var annulusCount = 0;
        double fluxSum = 0;
        double xSum = 0;
        double ySum = 0;

        for (var y = cy - annulusOuter; y <= cy + annulusOuter; y++)
        {
            if (y < 0 || y >= height)
            {
                continue;
            }

            for (var x = cx - annulusOuter; x <= cx + annulusOuter; x++)
            {
                if (x < 0 || x >= width)
                {
                    continue;
                }

                var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                if (r < annulusInner || r > annulusOuter)
                {
                    continue;
                }

                annulus[annulusCount++] = pixels[(y * width) + x];
            }
        }

        var localBackground = background;
        if (annulusCount >= 16)
        {
            var annulusValues = annulus[..annulusCount];
            annulusValues.Sort();
            var mid = annulusCount / 2;
            localBackground = annulusCount % 2 == 0
                ? (annulusValues[mid - 1] + annulusValues[mid]) * 0.5
                : annulusValues[mid];
        }

        for (var y = cy - radius; y <= cy + radius; y++)
        {
            if (y < 0 || y >= height)
            {
                continue;
            }

            for (var x = cx - radius; x <= cx + radius; x++)
            {
                if (x < 0 || x >= width)
                {
                    continue;
                }

                var signal = Math.Max(0, pixels[(y * width) + x] - localBackground);
                if (signal <= 0)
                {
                    continue;
                }

                fluxSum += signal;
                xSum += signal * x;
                ySum += signal * y;

                var r = Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));
                points.Add((x, y, r, signal));
            }
        }

        if (fluxSum <= 0 || points.Count < 6)
        {
            return (0, 0, 0, cx, cy);
        }

        var mx = xSum / fluxSum;
        var my = ySum / fluxSum;

        // Re-center radial distances on the intensity-weighted centroid. For
        // undersampled stars the integer local-max pixel can be offset by up
        // to 0.5 px from the true centroid, which significantly broadens the
        // measured FWHM. Recomputing radii here keeps the radial profile sharp.
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var px = p.X - mx;
            var py = p.Y - my;
            var r = Math.Sqrt((px * px) + (py * py));
            points[i] = (p.X, p.Y, r, p.Flux);
        }

        double mxx = 0;
        double myy = 0;
        double mxy = 0;
        foreach (var p in points)
        {
            var px = p.X - mx;
            var py = p.Y - my;
            mxx += p.Flux * px * px;
            myy += p.Flux * py * py;
            mxy += p.Flux * px * py;
        }

        mxx /= fluxSum;
        myy /= fluxSum;
        mxy /= fluxSum;

        var trace = mxx + myy;
        var det = (mxx * myy) - (mxy * mxy);
        var disc = Math.Max(0, (trace * trace) - (4 * det));
        var lambda1 = Math.Max(1e-6, (trace + Math.Sqrt(disc)) / 2.0);
        var lambda2 = Math.Max(1e-6, (trace - Math.Sqrt(disc)) / 2.0);

        // Use the Gaussian-core fit as the sole FWHM estimator. If it rejects
        // the candidate (low R², unreasonable sigma, non-peaked profile) we
        // return zero so the caller drops this detection entirely. The previous
        // half-max / second-moment fallbacks happily measured non-stellar
        // sources such as galaxy knots or nebulosity, which polluted the
        // per-frame FWHM median.
        var fwhm = EstimateFwhmGaussianFit(points);
        if (fwhm <= 0)
        {
            return (0, 0, 0, mx, my);
        }
        var hfr = ComputeHfr(points, fluxSum);
        var eccentricity = Math.Sqrt(Math.Max(0, 1.0 - (lambda2 / lambda1)));

        return (fwhm, hfr, eccentricity, mx, my);
    }

    private static double ComputeHfr(List<(double X, double Y, double R, double Flux)> points, double totalFlux)
    {
        points.Sort(static (a, b) => a.R.CompareTo(b.R));
        var half = totalFlux * 0.5;
        double accum = 0;
        foreach (var p in points)
        {
            accum += p.Flux;
            if (accum >= half)
            {
                return p.R;
            }
        }

        return points.Count == 0 ? 0 : points[^1].R;
    }

    /// <summary>
    /// Estimates FWHM by fitting a 2D Gaussian to the inner core of the star.
    /// For a Gaussian profile, ln(F(r)) is linear in r^2 with slope -1/(2 sigma^2),
    /// so a flux-weighted linear regression of ln(F) vs r^2 over the brightest
    /// inner samples recovers sigma directly. This matches the way PSF fitters
    /// such as PixInsight derive FWHM and is much more accurate than binned
    /// half-maximum interpolation on noisy, square-pixel-sampled data.
    /// </summary>
    private static double EstimateFwhmGaussianFit(List<(double X, double Y, double R, double Flux)> points)
    {
        if (points.Count < 6)
        {
            return 0;
        }

        // Robust peak: median of the 3 innermost samples.
        points.Sort(static (a, b) => a.R.CompareTo(b.R));
        var innerCount = Math.Min(points.Count, 3);
        Span<double> inner = stackalloc double[innerCount];
        for (var i = 0; i < innerCount; i++)
        {
            inner[i] = points[i].Flux;
        }
        inner.Sort();
        var peak = inner[innerCount / 2];
        if (peak <= 0)
        {
            return 0;
        }

        // Fit only the FWHM core (samples above ~half-peak). Real PSFs are
        // Moffat-like with heavier wings than a Gaussian, so including the
        // wings biases sigma upward. Restricting to the core also makes the
        // fit insensitive to small residuals in the local-background estimate,
        // which would otherwise lift ln(F) at large radii and flatten the slope.
        var lowThreshold = peak * 0.4;
        var highThreshold = peak * 1.05;

        double sw = 0;     // sum of weights
        double swx = 0;    // sum w * x   (x = r^2)
        double swy = 0;    // sum w * y   (y = ln F)
        double swxx = 0;
        double swxy = 0;
        double swyy = 0;   // sum w * y^2  (needed for goodness-of-fit / R^2)
        var samples = 0;

        foreach (var p in points)
        {
            if (p.Flux <= lowThreshold || p.Flux >= highThreshold)
            {
                continue;
            }

            var x = p.R * p.R;
            var y = Math.Log(p.Flux);
            // Weight by flux: bright core pixels have higher SNR and should
            // dominate the fit (Poisson-weighted regression of ln(F)).
            var w = p.Flux;
            sw += w;
            swx += w * x;
            swy += w * y;
            swxx += w * x * x;
            swxy += w * x * y;
            swyy += w * y * y;
            samples++;
        }

        if (samples < 5 || sw <= 0)
        {
            return 0;
        }

        var denom = (sw * swxx) - (swx * swx);
        if (Math.Abs(denom) < 1e-12)
        {
            return 0;
        }

        var slope = ((sw * swxy) - (swx * swy)) / denom;
        if (slope >= 0)
        {
            return 0; // not a peaked profile -> fall back
        }

        // Goodness-of-fit (weighted R^2). For a real star, ln(F) is essentially
        // linear in r^2, so R^2 is very close to 1. Extended objects (galaxy
        // cores, nebulosity knots, hot-pixel clusters) produce a much weaker
        // linear relationship and a noticeably lower R^2. Rejecting low-R^2
        // detections here keeps non-stellar sources out of the FWHM median.
        var meanY = swy / sw;
        var sse = swyy - (slope * swxy) - ((meanY - (slope * (swx / sw))) * swy);
        var sst = swyy - (meanY * swy);
        if (sst <= 0)
        {
            return 0;
        }

        var rSquared = 1.0 - (sse / sst);
        if (rSquared < 0.75)
        {
            return 0;
        }

        var sigmaSquared = -1.0 / (2.0 * slope);
        if (sigmaSquared <= 0 || double.IsNaN(sigmaSquared) || double.IsInfinity(sigmaSquared))
        {
            return 0;
        }

        var sigma = Math.Sqrt(sigmaSquared);
        // Reasonable sigma range for a stellar PSF: 0.4–6 px (FWHM ~1–14 px).
        // Beyond that we're almost certainly fitting an extended source.
        if (sigma < 0.4 || sigma > 6.0)
        {
            return 0;
        }

        return 2.3548200450309493 * sigma;
    }

    private static double EstimateFwhmHalfMaximum(List<(double X, double Y, double R, double Flux)> points)
    {
        if (points.Count < 6)
        {
            return 0;
        }

        // Robust peak: average the inner core (smallest radii) rather than taking
        // the single brightest pixel. The single-pixel max is biased upward by
        // shot noise, which pulls the half-maximum threshold up and yields a
        // *narrower* (and saturated stars yield a *wider*) profile than truth.
        points.Sort(static (a, b) => a.R.CompareTo(b.R));
        var coreCount = Math.Min(points.Count, 4);
        double coreSum = 0;
        for (var i = 0; i < coreCount; i++)
        {
            coreSum += points[i].Flux;
        }
        var peak = coreSum / coreCount;
        if (peak <= 0)
        {
            return 0;
        }

        var half = peak * 0.5;
        // Finer radial bins so sub-arcsecond, well-sampled stars resolve correctly.
        const double binSize = 0.3;
        var maxRadius = points[^1].R;
        var binCount = Math.Max(3, (int)Math.Ceiling(maxRadius / binSize) + 1);
        var sum = new double[binCount];
        var count = new int[binCount];

        foreach (var p in points)
        {
            var bin = Math.Clamp((int)Math.Floor(p.R / binSize), 0, binCount - 1);
            sum[bin] += p.Flux;
            count[bin] += 1;
        }

        var previousRadius = 0.0;
        var previousValue = peak;
        for (var i = 0; i < binCount; i++)
        {
            if (count[i] == 0)
            {
                continue;
            }

            var radius = (i + 0.5) * binSize;
            var value = sum[i] / count[i];
            if (value <= half)
            {
                if (previousValue <= half)
                {
                    return 2.0 * radius;
                }

                var t = (half - previousValue) / Math.Max(1e-9, value - previousValue);
                var halfRadius = previousRadius + ((radius - previousRadius) * t);
                return 2.0 * Math.Max(0, halfRadius);
            }

            previousRadius = radius;
            previousValue = value;
        }

        return 0;
    }

    private static TrailDetectionResult DetectTrail(float[] pixels, int width, int height)
    {
        if (width < 16 || height < 16)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        // ── Background subtraction ────────────────────────────────────────────
        // BoxBlur radius 2 removes the DC background; what remains is high-frequency
        // residuals. A satellite trail is a bright stripe in this residual image.
        var blurred = BoxBlur(pixels, width, height, 2);
        var enhanced = new float[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
            enhanced[i] = Math.Max(0f, pixels[i] - blurred[i]);

        var sample = Sample(enhanced);
        if (sample.Length == 0)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        Array.Sort(sample);

        // Single strict threshold: only the very top of the residual histogram.
        // Stars appear here as isolated blobs; a trail appears as a connected stripe.
        var threshold = Math.Max(1e-6, PercentileFromSorted(sample, 0.997));

        // ── Directional candidate collection ─────────────────────────────────
        // A pixel is a candidate only if it has STRONG directional support in
        // exactly one axis (bestSupport > 2.5 × secondSupport).
        // Stars are nearly isotropic → they fail this test convincingly.
        var points = new List<(int X, int Y, double Signal)>(1024);
        var cx = (width - 1) * 0.5;
        var cy = (height - 1) * 0.5;

        for (var y = 5; y < height - 5; y++)
        {
            var row = y * width;
            for (var x = 5; x < width - 5; x++)
            {
                var center = enhanced[row + x];
                if (center < threshold)
                    continue;

                var h = ComputeDirectionalTrailSupport(enhanced, width, x, y, 1, 0);
                var v = ComputeDirectionalTrailSupport(enhanced, width, x, y, 0, 1);
                var d1 = ComputeDirectionalTrailSupport(enhanced, width, x, y, 1, 1);
                var d2 = ComputeDirectionalTrailSupport(enhanced, width, x, y, 1, -1);

                var best = h;
                var second = 0.0;
                UpdateTopTwo(v, ref best, ref second);
                UpdateTopTwo(d1, ref best, ref second);
                UpdateTopTwo(d2, ref best, ref second);

                // Hard dominance gate — stars have similar support in all 4 directions.
                if (best <= 2.0 * Math.Max(0.0, second))
                    continue;

                // Signal must also clear the threshold in absolute terms.
                var signal = best - second;
                if (signal < threshold * 2.0)
                    continue;

                points.Add((x, y, signal));
            }
        }

        // Hard gate: need a meaningful number of strictly trail-like pixels.
        if (points.Count < 12)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        const int maxCandidates = 2000;
        if (points.Count > maxCandidates)
            points = points.OrderByDescending(p => p.Signal).Take(maxCandidates).ToList();

        // ── Hough accumulator (1° resolution) ────────────────────────────────
        const int angleBins = 180;
        var maxRho = Math.Sqrt((cx * cx) + (cy * cy));
        var rhoBinSize = Math.Max(2.0, Math.Min(5.0, Math.Min(width, height) / 180.0));
        var rhoBins = Math.Max(180, (int)Math.Ceiling((2.0 * maxRho) / rhoBinSize) + 1);
        var accumulator = new int[angleBins * rhoBins];
        var cosTable = new double[angleBins];
        var sinTable = new double[angleBins];

        for (var a = 0; a < angleBins; a++)
        {
            var theta = (a / (double)angleBins) * Math.PI;
            cosTable[a] = Math.Cos(theta);
            sinTable[a] = Math.Sin(theta);
        }

        foreach (var p in points)
        {
            var dx = p.X - cx;
            var dy = p.Y - cy;
            var weight = 1 + Math.Clamp((int)Math.Round(p.Signal / Math.Max(1e-6, threshold * 3.0)), 0, 3);

            for (var a = 0; a < angleBins; a++)
            {
                var rho = (dx * cosTable[a]) + (dy * sinTable[a]);
                var rhoIndex = (int)Math.Round((rho + maxRho) / rhoBinSize);
                if ((uint)rhoIndex < (uint)rhoBins)
                    accumulator[(a * rhoBins) + rhoIndex] += weight;
            }
        }

        var bestAngleBin = -1;
        var bestRhoBin = -1;
        var bestVotes = 0;
        for (var a = 0; a < angleBins; a++)
        {
            var rowOff = a * rhoBins;
            for (var r = 0; r < rhoBins; r++)
            {
                var v = accumulator[rowOff + r];
                if (v > bestVotes) { bestVotes = v; bestAngleBin = a; bestRhoBin = r; }
            }
        }

        // Hard gate: the winning bin must hold a large fraction of all votes.
        // For noise/stars the votes are spread uniformly across all bins.
        // For a real trail they concentrate strongly in one (angle, rho) cell.
        if (bestAngleBin < 0 || bestVotes < Math.Max(20, points.Count / 20))
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        var normalX = cosTable[bestAngleBin];
        var normalY = sinTable[bestAngleBin];
        var rhoCenter = (bestRhoBin * rhoBinSize) - maxRho;

        // ── Pass 1: coarse inliers → PCA ─────────────────────────────────────
        var pass1Dist = Math.Max(2.5, rhoBinSize * 1.2);
        var inlierPositions = new List<(double DX, double DY)>(points.Count);

        foreach (var p in points)
        {
            var dx = p.X - cx;
            var dy = p.Y - cy;
            if (Math.Abs((dx * normalX) + (dy * normalY) - rhoCenter) <= pass1Dist)
                inlierPositions.Add((dx, dy));
        }

        if (inlierPositions.Count < 10)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        // PCA → sub-degree trail direction + elongation ratio.
        double sumDx = 0, sumDy = 0;
        foreach (var (dx, dy) in inlierPositions) { sumDx += dx; sumDy += dy; }
        var centDx = sumDx / inlierPositions.Count;
        var centDy = sumDy / inlierPositions.Count;

        double cxx = 0, cxy = 0, cyy = 0;
        foreach (var (dx, dy) in inlierPositions)
        {
            var ex = dx - centDx; var ey = dy - centDy;
            cxx += ex * ex; cxy += ex * ey; cyy += ey * ey;
        }
        cxx /= inlierPositions.Count;
        cxy /= inlierPositions.Count;
        cyy /= inlierPositions.Count;

        var trace = cxx + cyy;
        var disc = Math.Sqrt(Math.Max(0.0, (trace * trace / 4.0) - (cxx * cyy - cxy * cxy)));
        var lambda1 = (trace / 2.0) + disc;
        var lambda2 = (trace / 2.0) - disc;

        // Hard gate: strong elongation required (ratio ≥ 7).
        // Random noise can score 2–6; a real trail scores 20–1000+.
        if (lambda2 < 1e-9 || lambda1 / lambda2 < 7.0)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        double evX, evY;
        if (Math.Abs(cxy) > 1e-10) { evX = lambda1 - cyy; evY = cxy; }
        else { evX = cxx >= cyy ? 1.0 : 0.0; evY = cxx >= cyy ? 0.0 : 1.0; }

        var evLen = Math.Sqrt((evX * evX) + (evY * evY));
        if (evLen < 1e-10) return new TrailDetectionResult(0, 0, 0, 0, 0);

        evX /= evLen; evY /= evLen;
        var refinedNormalX = -evY;
        var refinedNormalY = evX;
        var refinedRho = (centDx * refinedNormalX) + (centDy * refinedNormalY);

        // ── Pass 2: tight inliers ─────────────────────────────────────────────
        // 2.0 px band — real trail pixels are very tightly collinear.
        var maxDistPass2 = Math.Max(2.0, rhoBinSize * 0.7);
        var dirX = evX;
        var dirY = evY;

        double minT = double.PositiveInfinity;
        double maxT = double.NegativeInfinity;
        var inlierCount = 0;
        double rmsSum = 0;
        var spanBinSize = Math.Max(4.0, Math.Min(width, height) / 90.0);
        HashSet<int> occupiedSpanBins = [];
        var inlierTs = new List<double>(inlierPositions.Count);

        foreach (var p in points)
        {
            var dx = p.X - cx;
            var dy = p.Y - cy;
            var dist = Math.Abs((dx * refinedNormalX) + (dy * refinedNormalY) - refinedRho);
            if (dist > maxDistPass2) continue;

            var t = (dx * dirX) + (dy * dirY);
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
            occupiedSpanBins.Add((int)Math.Floor(t / spanBinSize));
            inlierTs.Add(t);
            rmsSum += dist * dist;
            inlierCount++;
        }

        if (inlierCount < 10)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        var span = maxT - minT;
        var imageDim = Math.Min(width, height);

        // ── Hard rejection gates ──────────────────────────────────────────────
        // These fire for noise/star-cluster patterns that passed earlier tests.

        // 1. Span: trail must cross at least 20 % of the shorter image dimension.
        if (span < 0.20 * imageDim)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        // 2. Coverage: inliers must occupy at least 20 % of the span bins
        //    (no very gappy, discontinuous patterns).
        var spanBinCount = Math.Max(1, (int)Math.Ceiling(span / spanBinSize));
        var coverage = occupiedSpanBins.Count / (double)spanBinCount;
        if (coverage < 0.20)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        // 3. Maximum single gap: must not exceed 35 % of span.
        inlierTs.Sort();
        var maxGap = 0.0;
        for (var i = 1; i < inlierTs.Count; i++)
        {
            var gap = inlierTs[i] - inlierTs[i - 1];
            if (gap > maxGap) maxGap = gap;
        }
        if (maxGap > 0.35 * span)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        // 4. RMS perpendicular residual: must be ≤ 2.0 px.
        var rms = Math.Sqrt(rmsSum / inlierCount);
        if (rms > 2.0)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        // 5. Density: at least 1 inlier per 2 span-bins of length.
        if (inlierCount < span / (spanBinSize * 2.0))
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        // ── Confidence score (1–100) — only reached after all hard gates pass ─
        // Scores how strong/clear the trail is, not whether it exists.
        // Users can tune the rejection threshold slider to taste.

        var elongRatio = lambda1 / lambda2;
        var sElongation = Math.Clamp((elongRatio - 7.0) / 93.0, 0.0, 1.0);    // 7→0 … 100→1

        var spanFrac = span / imageDim;
        var sSpan = Math.Clamp((spanFrac - 0.20) / 0.70, 0.0, 1.0);     // 20%→0 … 90%→1

        var sCoverage = Math.Clamp((coverage - 0.20) / 0.70, 0.0, 1.0);     // 20%→0 … 90%→1

        var gapFrac = span > 0 ? maxGap / span : 1.0;
        var sGap = Math.Clamp(1.0 - gapFrac / 0.35, 0.0, 1.0);        // 0→1 … 35%→0

        var sRms = Math.Clamp(1.0 - rms / 2.0, 0.0, 1.0);             // 0→1 … 2.0px→0

        var rawScore = (0.25 * sElongation)
                     + (0.25 * sSpan)
                     + (0.20 * sCoverage)
                     + (0.15 * sGap)
                     + (0.15 * sRms);

        // Minimum score is 1 (passed all gates), maximum is 100 (perfect trail).
        var confidence = Math.Max(1, (int)Math.Round(rawScore * 100.0));

        // ── Result coordinates ────────────────────────────────────────────────
        var baseX = cx + (refinedRho * refinedNormalX);
        var baseY = cy + (refinedRho * refinedNormalY);
        var x1 = baseX + (minT * dirX);
        var y1 = baseY + (minT * dirY);
        var x2 = baseX + (maxT * dirX);
        var y2 = baseY + (maxT * dirY);

        return new TrailDetectionResult(
            confidence,
            width <= 1 ? 0.5 : Math.Clamp(x1 / (width - 1), 0.0, 1.0),
            height <= 1 ? 0.5 : Math.Clamp(y1 / (height - 1), 0.0, 1.0),
            width <= 1 ? 0.5 : Math.Clamp(x2 / (width - 1), 0.0, 1.0),
            height <= 1 ? 0.5 : Math.Clamp(y2 / (height - 1), 0.0, 1.0));
    }

    private static double ComputeDirectionalTrailSupport(float[] pixels, int width, int x, int y, int dx, int dy)
    {
        var center = pixels[(y * width) + x] * 1.25;
        var sum = center;
        var perpX = -dy;
        var perpY = dx;

        // Sample 5 steps along the trail direction (longer reach catches faint trails).
        double[] alongWeights = [1.0, 0.85, 0.70, 0.55, 0.40];
        for (var step = 1; step <= 5; step++)
        {
            var nx = x + (dx * step);
            var ny = y + (dy * step);
            if ((uint)nx >= (uint)width || (uint)ny >= (uint)(pixels.Length / width)) break;
            sum += alongWeights[step - 1] * pixels[(ny * width) + nx];

            nx = x - (dx * step);
            ny = y - (dy * step);
            if ((uint)nx >= (uint)width || (uint)ny >= (uint)(pixels.Length / width)) break;
            sum += alongWeights[step - 1] * pixels[(ny * width) + nx];
        }

        // Subtract perpendicular neighbours — suppresses point sources and blobs.
        double[] perpWeights = [1.0, 0.70, 0.45];
        for (var step = 1; step <= 3; step++)
        {
            var nx = x + (perpX * step);
            var ny = y + (perpY * step);
            if ((uint)nx < (uint)width && (uint)ny < (uint)(pixels.Length / width))
                sum -= perpWeights[step - 1] * pixels[(ny * width) + nx];

            nx = x - (perpX * step);
            ny = y - (perpY * step);
            if ((uint)nx < (uint)width && (uint)ny < (uint)(pixels.Length / width))
                sum -= perpWeights[step - 1] * pixels[(ny * width) + nx];
        }

        return sum;
    }

    private static void UpdateTopTwo(double candidate, ref double best, ref double second)
    {
        if (candidate >= best)
        {
            second = best;
            best = candidate;
            return;
        }

        if (candidate > second)
        {
            second = candidate;
        }
    }

    private static double Median9(float[] pixels, int width, int x, int y)
    {
        Span<float> w = stackalloc float[9];
        var k = 0;
        for (var oy = -1; oy <= 1; oy++)
        {
            var row = (y + oy) * width;
            for (var ox = -1; ox <= 1; ox++)
            {
                w[k++] = pixels[row + x + ox];
            }
        }

        w.Sort();
        return w[4];
    }

    private static double ComputeSigma(float[] values, double mean)
    {
        return ComputeSigmaFromSample(Sample(values), mean);
    }

    private static double ComputeSigmaFromSample(float[] sample, double mean)
    {
        if (sample.Length == 0)
        {
            return 1;
        }

        double sum = 0;
        foreach (var v in sample)
        {
            var d = v - mean;
            sum += d * d;
        }

        return Math.Sqrt(sum / sample.Length);
    }

    private static double Percentile(float[] values, double percentile)
    {
        var sample = Sample(values);
        if (sample.Length == 0)
        {
            return 0;
        }

        Array.Sort(sample);
        var idx = (int)Math.Clamp(Math.Round(percentile * (sample.Length - 1)), 0, sample.Length - 1);
        return sample[idx];
    }

    private static double PercentileFromSorted(float[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0)
        {
            return 0;
        }

        var idx = (int)Math.Clamp(Math.Round(percentile * (sortedValues.Length - 1)), 0, sortedValues.Length - 1);
        return sortedValues[idx];
    }

    private static double MedianAbsoluteDeviation(float[] values, double median, bool alreadySorted = false)
    {
        var source = alreadySorted ? values : values.OrderBy(v => v).ToArray();
        var abs = new float[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            abs[i] = (float)Math.Abs(source[i] - median);
        }

        Array.Sort(abs);
        return PercentileFromSorted(abs, 0.5);
    }

    private static double MidtonesTransfer(double x, double m)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        m = Math.Clamp(m, 1e-6, 1.0 - 1e-6);

        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        var denominator = (((2.0 * m) - 1.0) * x) - m;
        if (Math.Abs(denominator) < 1e-9)
        {
            return x;
        }

        return ((m - 1.0) * x) / denominator;
    }

    private static double InverseMidtonesTransfer(double y, double x)
    {
        y = Math.Clamp(y, 0.0, 1.0);
        x = Math.Clamp(x, 0.0, 1.0);

        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        var denominator = x + y - (2.0 * x * y);
        if (Math.Abs(denominator) < 1e-9)
        {
            return 0.5;
        }

        return (x * (1.0 - y)) / denominator;
    }

    private static double Median(IEnumerable<double> values)
    {
        var arr = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0).ToArray();
        if (arr.Length == 0)
        {
            return 0;
        }

        Array.Sort(arr);
        var mid = arr.Length / 2;
        return arr.Length % 2 == 0 ? (arr[mid - 1] + arr[mid]) / 2.0 : arr[mid];
    }

    private static float[] Sample(float[] values)
    {
        if (values.Length <= 200_000)
        {
            return values.ToArray();
        }

        var result = new float[200_000];
        var stride = values.Length / (double)result.Length;
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = values[(int)(i * stride)];
        }

        return result;
    }
}