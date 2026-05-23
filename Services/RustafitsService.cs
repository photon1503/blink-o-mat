using System.Buffers.Binary;
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
        float[][]? ColorChannels = null)
    {
        /// <summary>True when this frame was debayered from a single-channel OSC sensor.</summary>
        public bool IsOsc => ColorChannels is { Length: 3 };
    }

    public async Task<FrameItem> ProcessFrameAsync(string filePath, string thumbnailDirectory, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            var frame = await LoadFrameAsync(filePath, cancellationToken);

            var loadedFrame = new LoadedFrame(frame.Pixels, frame.Width, frame.Height, frame.NormalizationMax, frame.FocalLengthMm, frame.PixelSizeUm, frame.ExposureDateTime, frame.ExposureSeconds, frame.FilterName, ParseSqmFromFileName(filePath), frame.SkyTemp, frame.ColorChannels);
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
            return new LoadedFrame(frame.Pixels, frame.Width, frame.Height, frame.NormalizationMax, frame.FocalLengthMm, frame.PixelSizeUm, frame.ExposureDateTime, frame.ExposureSeconds, frame.FilterName, ParseSqmFromFileName(filePath), frame.SkyTemp, frame.ColorChannels);
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
                var roi  = CreateRoiBitmapColor(cc[0], cc[1], cc[2], frame.Width, frame.Height, 160, oscStf[0], oscStf[1], oscStf[2], roiNormalizedRect, frame.NormalizationMax);
                return (full, roi);
            }

            var monoFull = CreateThumbnailBitmap(frame.Pixels, frame.Width, frame.Height, 160, 160, stf, metrics, frame.NormalizationMax);
            var monoRoi  = CreateRoiBitmap(frame.Pixels, frame.Width, frame.Height, 160, stf, roiNormalizedRect, frame.NormalizationMax);
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
        const int sampleSize = 256;
        const int maxOffset = 48;
        const double minImprovement = 0.04;

        var referenceSample = CreateOrientationSample(reference.Pixels, reference.Width, reference.Height, sampleSize, rotate180: false);
        var originalSample = CreateOrientationSample(frame.Pixels, frame.Width, frame.Height, sampleSize, rotate180: false);
        var rotatedSample = CreateOrientationSample(frame.Pixels, frame.Width, frame.Height, sampleSize, rotate180: true);

        var referenceStars = DetectOrientationStars(referenceSample, sampleSize, sampleSize);
        var originalStars = DetectOrientationStars(originalSample, sampleSize, sampleSize);
        var rotatedStars = DetectOrientationStars(rotatedSample, sampleSize, sampleSize);

        double originalScore;
        double rotatedScore;
        if (referenceStars.Count >= 10 && originalStars.Count >= 10 && rotatedStars.Count >= 10)
        {
            originalScore = ComputeStarAlignmentScore(referenceStars, originalStars, sampleSize, sampleSize);
            rotatedScore = ComputeStarAlignmentScore(referenceStars, rotatedStars, sampleSize, sampleSize);
        }
        else
        {
            originalScore = ComputeBestCorrelationWithOffsets(referenceSample, originalSample, sampleSize, maxOffset);
            rotatedScore = ComputeBestCorrelationWithOffsets(referenceSample, rotatedSample, sampleSize, maxOffset);
        }

        return rotatedScore > originalScore + minImprovement;
    }

    public LoadedFrame ApplyOrientation(LoadedFrame frame, bool rotate180)
    {
        return rotate180 ? Rotate180(frame) : frame;
    }

    private static async Task<(float[] Pixels, int Width, int Height, double NormalizationMax, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName, double? SkyTemp, float[][]? ColorChannels)> LoadFrameAsync(string filePath, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".fits", StringComparison.OrdinalIgnoreCase) || ext.Equals(".fit", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFits(filePath);
        }

        if (ext.Equals(".xisf", StringComparison.OrdinalIgnoreCase))
        {
            var r = await LoadXisfAsync(filePath, cancellationToken);
            return (r.Pixels, r.Width, r.Height, r.NormalizationMax, r.FocalLengthMm, r.PixelSizeUm, r.ExposureDateTime, r.ExposureSeconds, r.FilterName, r.SkyTemp, null);
        }

        throw new NotSupportedException($"Unsupported file type: {ext}");
    }

    private static (float[] Pixels, int Width, int Height, double NormalizationMax, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName, double? SkyTemp, float[][]? ColorChannels) LoadFits(string filePath)
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

    private static (float[] Pixels, int Width, int Height, double NormalizationMax, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName, double? SkyTemp, float[][]? ColorChannels)? TryDecodeFitsImage(Stream stream, FitsHeaderInfo header)
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
        var bZero  = header.BZero;

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

        return (result, width, height, ComputeFitsNormalizationMax(header), header.FocalLengthMm, header.PixelSizeUm, header.ExposureDateTime, header.ExposureSeconds, header.FilterName, header.SkyTemp, colorChannels);
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
                    return new FitsHeaderInfo(bitPix, axisCount, axes, bScale, bZero, focalLengthMm, pixelSizeUm, exposureDateTime, exposureSeconds, filterName, skyTemp, bayerPattern, bayerOffsetX, bayerOffsetY);
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
        int BayerOffsetY = 0);

    private static async Task<(float[] Pixels, int Width, int Height, double NormalizationMax, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName, double? SkyTemp)> LoadXisfAsync(string filePath, CancellationToken cancellationToken)
    {
        var image = await XisfImage.LoadAsync(filePath, cancellationToken);
        // Copy to a regular byte array so it can be captured in Parallel.For lambdas (Span<T> is ref struct)
        var bytesSpan = image.Data.Span;
        var rawData = bytesSpan.ToArray();
        var width = image.Width;
        var height = image.Height;
        var channels = Math.Max(1, image.Channels);

        var pixelCount = width * height;
        var bytesPerSample = GetBytesPerSample(image.SampleFormat);
        var sampleCount = pixelCount * channels;
        if (rawData.Length < sampleCount * bytesPerSample)
        {
            throw new InvalidOperationException("XISF data size mismatch.");
        }

        var luminance = new float[pixelCount];
        var planar = image.PixelStorage == PixelStorage.Planar;
        var sampleFormat = image.SampleFormat;
        if (channels == 1)
        {
            Parallel.For(0, pixelCount, i =>
            {
                luminance[i] = (float)ReadSample(rawData, i, sampleFormat);
            });
        }
        else
        {
            Parallel.For(0, pixelCount, i =>
            {
                var r = planar ? ReadSample(rawData, i, sampleFormat) : ReadSample(rawData, (i * channels), sampleFormat);
                var g = planar ? ReadSample(rawData, i + pixelCount, sampleFormat) : ReadSample(rawData, (i * channels) + 1, sampleFormat);
                var b = planar
                    ? ReadSample(rawData, i + (2 * pixelCount), sampleFormat)
                    : ReadSample(rawData, (i * channels) + Math.Min(2, channels - 1), sampleFormat);

                luminance[i] = (float)((0.2126 * r) + (0.7152 * g) + (0.0722 * b));
            });
        }

        var focalLengthMm = ResolveXisfFocalLengthMm(image);
        var pixelSizeUm = ResolveXisfPixelSizeUm(image);
        var exposureDateTime = ResolveXisfExposureDateTime(image);
        var exposureSeconds = ResolveXisfExposureSeconds(image);
        var filterName = ResolveXisfFilterName(image);
        var skyTemp = ResolveXisfSkyTemp(image);
        return (luminance, width, height, GetNormalizationMax(image.SampleFormat), focalLengthMm, pixelSizeUm, exposureDateTime, exposureSeconds, filterName, skyTemp);
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

        return new LoadedFrame(pixels, frame.Width, frame.Height, frame.NormalizationMax, frame.FocalLengthMm, frame.PixelSizeUm, frame.ExposureDateTime, frame.ExposureSeconds, frame.FilterName, frame.Sqm, frame.SkyTemp);
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
        var best = -1.0;
        for (var dy = -maxOffset; dy <= maxOffset; dy++)
        {
            for (var dx = -maxOffset; dx <= maxOffset; dx++)
            {
                var score = ComputeCorrelationWithOffset(reference, candidate, size, dx, dy);
                if (score > best)
                {
                    best = score;
                }
            }
        }

        return best;
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

    private static List<StarPoint> DetectOrientationStars(float[] pixels, int width, int height)
    {
        var sample = Sample(pixels);
        if (sample.Length == 0)
        {
            return [];
        }

        Array.Sort(sample);
        var background = PercentileFromSorted(sample, 0.5);
        var mad = MedianAbsoluteDeviation(sample, background, alreadySorted: true);
        var sigma = Math.Max(1e-6, 1.4826 * mad);
        var p98 = PercentileFromSorted(sample, 0.98);
        var threshold = Math.Max(background + (4.0 * sigma), background + ((p98 - background) * 0.35));
        var stars = new List<StarPoint>(256);
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var center = pixels[row + x];
                if (center < threshold)
                {
                    continue;
                }

                if (center < pixels[row + x - 1] ||
                    center < pixels[row + x + 1] ||
                    center < pixels[row - width + x] ||
                    center < pixels[row + width + x] ||
                    center < pixels[row - width + x - 1] ||
                    center < pixels[row - width + x + 1] ||
                    center < pixels[row + width + x - 1] ||
                    center < pixels[row + width + x + 1])
                {
                    continue;
                }

                var signal = (float)Math.Max(0, center - background);
                stars.Add(new StarPoint(x, y, signal));
            }
        }

        return stars
            .OrderByDescending(s => s.Signal)
            .Take(160)
            .ToList();
    }

    private static double ComputeStarAlignmentScore(IReadOnlyList<StarPoint> referenceStars, IReadOnlyList<StarPoint> candidateStars, int width, int height)
    {
        if (referenceStars.Count < 3 || candidateStars.Count < 3)
        {
            return -1;
        }

        const int maxStarsForShift = 18;
        const double tolerance = 2.0;
        var refTop = referenceStars.Take(maxStarsForShift).ToList();
        var candTop = candidateStars.Take(maxStarsForShift).ToList();

        var occupancy = BuildStarOccupancy(referenceStars, width, height);
        var best = 0.0;
        foreach (var r in refTop)
        {
            foreach (var c in candTop)
            {
                var dx = (int)Math.Round(r.X - c.X);
                var dy = (int)Math.Round(r.Y - c.Y);
                var score = ScoreShift(occupancy, candidateStars, dx, dy, tolerance, width, height, referenceStars.Count);
                if (score > best)
                {
                    best = score;
                }
            }
        }

        return best;
    }

    private static bool[] BuildStarOccupancy(IReadOnlyList<StarPoint> stars, int width, int height)
    {
        var occupancy = new bool[width * height];
        foreach (var s in stars)
        {
            var x = Math.Clamp((int)Math.Round(s.X), 0, width - 1);
            var y = Math.Clamp((int)Math.Round(s.Y), 0, height - 1);
            occupancy[(y * width) + x] = true;
        }

        return occupancy;
    }

    private static double ScoreShift(bool[] referenceOccupancy, IReadOnlyList<StarPoint> candidateStars, int dx, int dy, double tolerance, int width, int height, int referenceCount)
    {
        var toleranceInt = (int)Math.Ceiling(tolerance);
        var toleranceSq = tolerance * tolerance;
        var matched = 0;

        foreach (var c in candidateStars)
        {
            var tx = (int)Math.Round(c.X + dx);
            var ty = (int)Math.Round(c.Y + dy);
            var found = false;

            for (var oy = -toleranceInt; oy <= toleranceInt && !found; oy++)
            {
                var y = ty + oy;
                if ((uint)y >= (uint)height)
                {
                    continue;
                }

                for (var ox = -toleranceInt; ox <= toleranceInt; ox++)
                {
                    var x = tx + ox;
                    if ((uint)x >= (uint)width)
                    {
                        continue;
                    }

                    if ((ox * ox) + (oy * oy) > toleranceSq)
                    {
                        continue;
                    }

                    if (referenceOccupancy[(y * width) + x])
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (found)
            {
                matched++;
            }
        }

        var denominator = Math.Max(1, Math.Min(referenceCount, candidateStars.Count));
        return matched / (double)denominator;
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
            _      => new[,] { { 0, 1 }, { 1, 2 } }   // default RGGB
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
            var rowChOdd  = cellRow == 0 ? ch01 : ch11; // channel at odd  x

            // Decide whether the slow boundary path is needed for this row.
            var atTopOrBottom = y == 0 || y == height - 1;
            var rowOffset = y * width;
            var rowAbove  = (y - 1) * width;
            var rowBelow  = (y + 1) * width;

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
        var m  = stf.Midtones;
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
                data[idx]     = StretchSample(rv, dataMax, stfR);
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
        var contentWidth  = Math.Max(1, (int)Math.Round(width  * scale));
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
        var targetWidthM1  = Math.Max(1, targetWidth  - 1);
        Parallel.For(0, targetHeight, y =>
        {
            var sy     = Math.Min(height - 1, (int)Math.Round((y / (double)targetHeightM1) * (height - 1)));
            var srcRow = sy * width;
            var dstRow = y  * targetWidth;
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
        var m  = stf.Midtones;   // midtones balance
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
        var analysisPixels = CreateAnalysisPixels(pixels, width, height, 1536, out var analysisWidth, out var analysisHeight, out var xScale, out var yScale);
        double analysisBackground;
        double analysisSigma;
        if (analysisPixels == pixels)
        {
            analysisBackground = background;
            analysisSigma = sigma;
        }
        else
        {
            var analysisSample = Sample(analysisPixels);
            Array.Sort(analysisSample);
            analysisBackground = PercentileFromSorted(analysisSample, 0.5);
            analysisSigma = ComputeSigmaFromSample(analysisSample, analysisBackground);
        }

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

        var stars = DetectStars(pixels, width, height, analysisPixels, analysisWidth, analysisHeight, analysisBackground, analysisSigma, xScale, yScale);
        var orderedStars = stars.OrderByDescending(s => s.Peak).Take(300).ToList();

        var fwhm = Median(orderedStars.Select(s => s.Fwhm));
        var hfr = Median(orderedStars.Select(s => s.Hfr));
        var eccentricity = Median(orderedStars.Select(s => s.Eccentricity));
        var trail = DetectTrail(trailPixels, trailWidth, trailHeight);
        var starCount = orderedStars.Count;

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
            TrailY2 = trail.Confidence > 0 ? trail.Y2 : null
        };
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

    private static List<(double Peak, double Fwhm, double Hfr, double Eccentricity)> DetectStars(float[] pixels, int width, int height, float[] analysisPixels, int analysisWidth, int analysisHeight, double background, double sigma, double xScale, double yScale)
    {
        const int maxCandidates = 768;
        const int maxMeasuredStars = 300;

        var threshold = background + (5.0 * sigma);
        var minNeighborLevel = background + (2.0 * sigma);
        var candidateBag = new System.Collections.Concurrent.ConcurrentBag<(double Peak, int X, int Y)>();

        Parallel.For(1, analysisHeight - 1, y =>
        {
            var row = y * analysisWidth;
            for (var x = 1; x < analysisWidth - 1; x++)
            {
                var center = analysisPixels[row + x];
                if (center < threshold)
                {
                    continue;
                }

                if (center < analysisPixels[row + x - 1] ||
                    center < analysisPixels[row + x + 1] ||
                    center < analysisPixels[row - analysisWidth + x] ||
                    center < analysisPixels[row + analysisWidth + x] ||
                    center < analysisPixels[row - analysisWidth + x - 1] ||
                    center < analysisPixels[row - analysisWidth + x + 1] ||
                    center < analysisPixels[row + analysisWidth + x - 1] ||
                    center < analysisPixels[row + analysisWidth + x + 1])
                {
                    continue;
                }

                var supportNeighbors = 0;
                for (var ny = -1; ny <= 1; ny++)
                {
                    for (var nx = -1; nx <= 1; nx++)
                    {
                        if (nx == 0 && ny == 0)
                        {
                            continue;
                        }

                        var neighbor = analysisPixels[((y + ny) * analysisWidth) + (x + nx)];
                        if (neighbor >= minNeighborLevel)
                        {
                            supportNeighbors++;
                        }
                    }
                }

                if (supportNeighbors < 2)
                {
                    continue;
                }

                var sourceX = Math.Clamp((int)Math.Round(((x + 0.5) * xScale) - 0.5), 3, width - 4);
                var sourceY = Math.Clamp((int)Math.Round(((y + 0.5) * yScale) - 0.5), 3, height - 4);
                candidateBag.Add((center, sourceX, sourceY));
            }
        });

        var candidates = candidateBag;
        var result = new List<(double Peak, double Fwhm, double Hfr, double Eccentricity)>(Math.Min(maxMeasuredStars, candidates.Count));
        if (candidates.Count == 0)
        {
            return result;
        }

        var suppressionRadius = Math.Max(6.0, 4.0 * Math.Max(xScale, yScale));
        var suppressionRadiusSq = suppressionRadius * suppressionRadius;
        var selected = new List<(double Peak, int X, int Y)>(maxMeasuredStars);

        foreach (var candidate in candidates.OrderByDescending(c => c.Peak).Take(maxCandidates))
        {
            var tooClose = false;
            foreach (var existing in selected)
            {
                var dx = existing.X - candidate.X;
                var dy = existing.Y - candidate.Y;
                if ((dx * dx) + (dy * dy) <= suppressionRadiusSq)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
            {
                selected.Add((candidate.Peak, candidate.X, candidate.Y));
                if (selected.Count >= maxMeasuredStars)
                {
                    break;
                }
            }
        }

        var measurements = new (double Peak, double Fwhm, double Hfr, double Eccentricity)[selected.Count];
        Parallel.For(0, selected.Count, i =>
        {
            var (peak, cx, cy) = selected[i];
            var m = MeasureStar(pixels, width, height, cx, cy, background);
            measurements[i] = (peak, m.Fwhm, m.Hfr, m.Eccentricity);
        });

        foreach (var m in measurements)
        {
            if (m.Fwhm > 0 && m.Hfr > 0)
            {
                result.Add(m);
            }
        }

        return result;
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

    private static (double Fwhm, double Hfr, double Eccentricity) MeasureStar(float[] pixels, int width, int height, int cx, int cy, double background)
    {
        const int radius = 7;
        const int annulusInner = 8;
        const int annulusOuter = 12;
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
            return (0, 0, 0);
        }

        var mx = xSum / fluxSum;
        var my = ySum / fluxSum;

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

        var fwhm = EstimateFwhmHalfMaximum(points);
        if (fwhm <= 0)
        {
            var sigma = Math.Sqrt((lambda1 + lambda2) / 2.0);
            fwhm = 2.3548 * sigma;
        }
        var hfr = ComputeHfr(points, fluxSum);
        var eccentricity = Math.Sqrt(Math.Max(0, 1.0 - (lambda2 / lambda1)));

        return (fwhm, hfr, eccentricity);
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

    private static double EstimateFwhmHalfMaximum(List<(double X, double Y, double R, double Flux)> points)
    {
        if (points.Count < 6)
        {
            return 0;
        }

        var peak = points.Max(p => p.Flux);
        if (peak <= 0)
        {
            return 0;
        }

        var half = peak * 0.5;
        const double binSize = 0.5;
        var maxRadius = points.Max(p => p.R);
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
        var cx = (width  - 1) * 0.5;
        var cy = (height - 1) * 0.5;

        for (var y = 5; y < height - 5; y++)
        {
            var row = y * width;
            for (var x = 5; x < width - 5; x++)
            {
                var center = enhanced[row + x];
                if (center < threshold)
                    continue;

                var h  = ComputeDirectionalTrailSupport(enhanced, width, x, y, 1,  0);
                var v  = ComputeDirectionalTrailSupport(enhanced, width, x, y, 0,  1);
                var d1 = ComputeDirectionalTrailSupport(enhanced, width, x, y, 1,  1);
                var d2 = ComputeDirectionalTrailSupport(enhanced, width, x, y, 1, -1);

                var best   = h;
                var second = 0.0;
                UpdateTopTwo(v,  ref best, ref second);
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
        var maxRho     = Math.Sqrt((cx * cx) + (cy * cy));
        var rhoBinSize = Math.Max(2.0, Math.Min(5.0, Math.Min(width, height) / 180.0));
        var rhoBins    = Math.Max(180, (int)Math.Ceiling((2.0 * maxRho) / rhoBinSize) + 1);
        var accumulator = new int[angleBins * rhoBins];
        var cosTable    = new double[angleBins];
        var sinTable    = new double[angleBins];

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
                var rho      = (dx * cosTable[a]) + (dy * sinTable[a]);
                var rhoIndex = (int)Math.Round((rho + maxRho) / rhoBinSize);
                if ((uint)rhoIndex < (uint)rhoBins)
                    accumulator[(a * rhoBins) + rhoIndex] += weight;
            }
        }

        var bestAngleBin = -1;
        var bestRhoBin   = -1;
        var bestVotes    = 0;
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

        var normalX   = cosTable[bestAngleBin];
        var normalY   = sinTable[bestAngleBin];
        var rhoCenter = (bestRhoBin * rhoBinSize) - maxRho;

        // ── Pass 1: coarse inliers → PCA ─────────────────────────────────────
        var pass1Dist      = Math.Max(2.5, rhoBinSize * 1.2);
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

        var trace   = cxx + cyy;
        var disc    = Math.Sqrt(Math.Max(0.0, (trace * trace / 4.0) - (cxx * cyy - cxy * cxy)));
        var lambda1 = (trace / 2.0) + disc;
        var lambda2 = (trace / 2.0) - disc;

        // Hard gate: strong elongation required (ratio ≥ 7).
        // Random noise can score 2–6; a real trail scores 20–1000+.
        if (lambda2 < 1e-9 || lambda1 / lambda2 < 7.0)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        double evX, evY;
        if (Math.Abs(cxy) > 1e-10) { evX = lambda1 - cyy; evY = cxy; }
        else                        { evX = cxx >= cyy ? 1.0 : 0.0; evY = cxx >= cyy ? 0.0 : 1.0; }

        var evLen = Math.Sqrt((evX * evX) + (evY * evY));
        if (evLen < 1e-10) return new TrailDetectionResult(0, 0, 0, 0, 0);

        evX /= evLen; evY /= evLen;
        var refinedNormalX = -evY;
        var refinedNormalY =  evX;
        var refinedRho     = (centDx * refinedNormalX) + (centDy * refinedNormalY);

        // ── Pass 2: tight inliers ─────────────────────────────────────────────
        // 2.0 px band — real trail pixels are very tightly collinear.
        var maxDistPass2 = Math.Max(2.0, rhoBinSize * 0.7);
        var dirX = evX;
        var dirY = evY;

        double minT = double.PositiveInfinity;
        double maxT = double.NegativeInfinity;
        var inlierCount = 0;
        double rmsSum   = 0;
        var spanBinSize = Math.Max(4.0, Math.Min(width, height) / 90.0);
        HashSet<int> occupiedSpanBins = [];
        var inlierTs = new List<double>(inlierPositions.Count);

        foreach (var p in points)
        {
            var dx   = p.X - cx;
            var dy   = p.Y - cy;
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

        var span     = maxT - minT;
        var imageDim = Math.Min(width, height);

        // ── Hard rejection gates ──────────────────────────────────────────────
        // These fire for noise/star-cluster patterns that passed earlier tests.

        // 1. Span: trail must cross at least 20 % of the shorter image dimension.
        if (span < 0.20 * imageDim)
            return new TrailDetectionResult(0, 0, 0, 0, 0);

        // 2. Coverage: inliers must occupy at least 20 % of the span bins
        //    (no very gappy, discontinuous patterns).
        var spanBinCount = Math.Max(1, (int)Math.Ceiling(span / spanBinSize));
        var coverage     = occupiedSpanBins.Count / (double)spanBinCount;
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

        var elongRatio  = lambda1 / lambda2;
        var sElongation = Math.Clamp((elongRatio - 7.0) / 93.0, 0.0, 1.0);    // 7→0 … 100→1

        var spanFrac    = span / imageDim;
        var sSpan       = Math.Clamp((spanFrac - 0.20) / 0.70, 0.0, 1.0);     // 20%→0 … 90%→1

        var sCoverage   = Math.Clamp((coverage - 0.20) / 0.70, 0.0, 1.0);     // 20%→0 … 90%→1

        var gapFrac     = span > 0 ? maxGap / span : 1.0;
        var sGap        = Math.Clamp(1.0 - gapFrac / 0.35, 0.0, 1.0);        // 0→1 … 35%→0

        var sRms        = Math.Clamp(1.0 - rms / 2.0, 0.0, 1.0);             // 0→1 … 2.0px→0

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
            width  <= 1 ? 0.5 : Math.Clamp(x1 / (width  - 1), 0.0, 1.0),
            height <= 1 ? 0.5 : Math.Clamp(y1 / (height - 1), 0.0, 1.0),
            width  <= 1 ? 0.5 : Math.Clamp(x2 / (width  - 1), 0.0, 1.0),
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