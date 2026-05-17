using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using blink_o_mat.Models;
using XisfSharp;

namespace blink_o_mat.Services;

public sealed class RustafitsService
{
    private readonly record struct StarPoint(float X, float Y, float Signal);
    private readonly record struct TrailDetectionResult(bool Detected, double X1, double Y1, double X2, double Y2);

    public sealed record LoadedFrame(
        float[] Pixels,
        int Width,
        int Height,
        double? FocalLengthMm = null,
        double? PixelSizeUm = null,
        DateTimeOffset? ExposureDateTime = null,
        double? ExposureSeconds = null,
        string? FilterName = null);

    public async Task<FrameItem> ProcessFrameAsync(string filePath, string thumbnailDirectory, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            var frame = await LoadFrameAsync(filePath, cancellationToken);

            var loadedFrame = new LoadedFrame(frame.Pixels, frame.Width, frame.Height, frame.FocalLengthMm, frame.PixelSizeUm, frame.ExposureDateTime, frame.ExposureSeconds, frame.FilterName);
            var metrics = ComputeMetrics(loadedFrame);
            var previews = await RenderPreviewBitmapsAsync(loadedFrame, 1.0, StretchMode.Default, null, metrics, cancellationToken);

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
            return new LoadedFrame(frame.Pixels, frame.Width, frame.Height, frame.FocalLengthMm, frame.PixelSizeUm, frame.ExposureDateTime, frame.ExposureSeconds, frame.FilterName);
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

    public Task<(BitmapSource Full, BitmapSource Roi)> RenderPreviewBitmapsAsync(LoadedFrame frame, double stretchStrength, StretchMode stretchMode, (double X, double Y)? roiNormalizedCenter, AstroMetrics? metrics, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var full = CreateThumbnailBitmap(frame.Pixels, frame.Width, frame.Height, 160, 160, stretchStrength, stretchMode, metrics);
            var roi = CreateRoiBitmap(frame.Pixels, frame.Width, frame.Height, 160, stretchStrength, stretchMode, roiNormalizedCenter);
            return (full, roi);
        }, cancellationToken);
    }

    public Task<BitmapSource> RenderFullBitmapAsync(LoadedFrame frame, double stretchStrength, StretchMode stretchMode, CancellationToken cancellationToken)
    {
        return Task.Run(() => CreateFullFrameBitmap(frame.Pixels, frame.Width, frame.Height, stretchStrength, stretchMode), cancellationToken);
    }

    public (double X, double Y) DetectRoiNormalizedCenter(LoadedFrame frame, RoiBias bias)
    {
        var (x, y) = DetectRoiCenter(frame.Pixels, frame.Width, frame.Height, bias);
        return (frame.Width <= 1 ? 0.5 : x / (double)(frame.Width - 1), frame.Height <= 1 ? 0.5 : y / (double)(frame.Height - 1));
    }

    public AstroMetrics AnalyzeFrame(LoadedFrame frame)
    {
        return ComputeMetrics(frame);
    }

    public LoadedFrame NormalizeOrientation(LoadedFrame frame, LoadedFrame reference)
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

        if (rotatedScore > originalScore + minImprovement)
        {
            return Rotate180(frame);
        }

        return frame;
    }

    private static async Task<(float[] Pixels, int Width, int Height, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName)> LoadFrameAsync(string filePath, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(filePath);
        if (ext.Equals(".fits", StringComparison.OrdinalIgnoreCase) || ext.Equals(".fit", StringComparison.OrdinalIgnoreCase))
        {
            return LoadFits(filePath);
        }

        if (ext.Equals(".xisf", StringComparison.OrdinalIgnoreCase))
        {
            return await LoadXisfAsync(filePath, cancellationToken);
        }

        throw new NotSupportedException($"Unsupported file type: {ext}");
    }

    private static (float[] Pixels, int Width, int Height, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName) LoadFits(string filePath)
    {
        using var stream = File.OpenRead(filePath);
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

    private static (float[] Pixels, int Width, int Height, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName)? TryDecodeFitsImage(Stream stream, FitsHeaderInfo header)
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

        long consumed = 0;
        for (var c = 0; c < channels; c++)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var value = ReadFitsSample(stream, header.BitPix);
                    consumed++;

                    if (channels == 1)
                    {
                        result[(y * width) + x] = (float)((value * header.BScale) + header.BZero);
                        continue;
                    }

                    if (c < 3)
                    {
                        var scaled = (value * header.BScale) + header.BZero;
                        result[(y * width) + x] += (float)(scaled * weights[c]);
                    }
                }
            }
        }

        var totalSamples = Math.Max(1L, axis3) * pixelCount * extraFrames;
        var remainingSamples = totalSamples - consumed;
        if (remainingSamples > 0)
        {
            stream.Seek(remainingSamples * bytesPerSample, SeekOrigin.Current);
        }

        var dataBytes = totalSamples * bytesPerSample;
        var paddingBytes = (2880L - (dataBytes % 2880L)) % 2880L;
        if (paddingBytes > 0)
        {
            stream.Seek(paddingBytes, SeekOrigin.Current);
        }

        return (result, width, height, header.FocalLengthMm, header.PixelSizeUm, header.ExposureDateTime, header.ExposureSeconds, header.FilterName);
    }

    private static FitsHeaderInfo ReadFitsHeader(Stream stream)
    {
        var cards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var block = new byte[2880];
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
                    return new FitsHeaderInfo(bitPix, axisCount, axes, bScale, bZero, focalLengthMm, pixelSizeUm, exposureDateTime, exposureSeconds, filterName);
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

        var normalized = raw.Replace('D', 'E').Replace('d', 'E');
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

        var normalized = raw.Replace('D', 'E').Replace('d', 'E');
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
        string? FilterName);

    private static async Task<(float[] Pixels, int Width, int Height, double? FocalLengthMm, double? PixelSizeUm, DateTimeOffset? ExposureDateTime, double? ExposureSeconds, string? FilterName)> LoadXisfAsync(string filePath, CancellationToken cancellationToken)
    {
        var image = await XisfImage.LoadAsync(filePath, cancellationToken);
        var bytes = image.Data.Span;
        var width = image.Width;
        var height = image.Height;
        var channels = Math.Max(1, image.Channels);

        var pixelCount = width * height;
        var bytesPerSample = GetBytesPerSample(image.SampleFormat);
        var sampleCount = pixelCount * channels;
        if (bytes.Length < sampleCount * bytesPerSample)
        {
            throw new InvalidOperationException("XISF data size mismatch.");
        }

        var luminance = new float[pixelCount];
        var planar = image.PixelStorage == PixelStorage.Planar;
        for (var i = 0; i < pixelCount; i++)
        {
            if (channels == 1)
            {
                luminance[i] = (float)ReadSample(bytes, i, image.SampleFormat);
                continue;
            }

            var r = planar ? ReadSample(bytes, i, image.SampleFormat) : ReadSample(bytes, (i * channels), image.SampleFormat);
            var g = planar ? ReadSample(bytes, i + pixelCount, image.SampleFormat) : ReadSample(bytes, (i * channels) + 1, image.SampleFormat);
            var b = planar
                ? ReadSample(bytes, i + (2 * pixelCount), image.SampleFormat)
                : ReadSample(bytes, (i * channels) + Math.Min(2, channels - 1), image.SampleFormat);

            luminance[i] = (float)((0.2126 * r) + (0.7152 * g) + (0.0722 * b));
        }

        return (luminance, width, height, null, null, null, null, null);
    }

    private static LoadedFrame Rotate180(LoadedFrame frame)
    {
        var pixels = new float[frame.Pixels.Length];
        var source = frame.Pixels;
        for (var i = 0; i < source.Length; i++)
        {
            pixels[i] = source[source.Length - 1 - i];
        }

        return new LoadedFrame(pixels, frame.Width, frame.Height, frame.FocalLengthMm, frame.PixelSizeUm, frame.ExposureDateTime, frame.ExposureSeconds, frame.FilterName);
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

    private static BitmapSource CreateThumbnailBitmap(float[] pixels, int width, int height, int maxWidth, int maxHeight, double stretchStrength, StretchMode stretchMode, AstroMetrics? metrics)
    {
        var scale = Math.Min(maxWidth / (double)Math.Max(1, width), maxHeight / (double)Math.Max(1, height));
        scale = Math.Min(1.0, scale <= 0 ? 1.0 : scale);
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

        var sample = DownsampleAndStretch(pixels, width, height, targetWidth, targetHeight, stretchStrength, stretchMode);
        if (metrics is { PossibleSatelliteTrail: true, TrailX1: not null, TrailY1: not null, TrailX2: not null, TrailY2: not null })
        {
            DrawTrailOverlay(sample, targetWidth, targetHeight, metrics);
        }
        var stride = targetWidth * 3;

        var bitmap = BitmapSource.Create(targetWidth, targetHeight, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        bitmap.Freeze();
        return bitmap;
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

    private static BitmapSource CreateRoiBitmap(float[] pixels, int width, int height, int roiSize, double stretchStrength, StretchMode stretchMode, (double X, double Y)? roiNormalizedCenter)
    {
        var (cx, cy) = roiNormalizedCenter is { } roi
            ? ((int)Math.Round(Math.Clamp(roi.X, 0, 1) * (width - 1)), (int)Math.Round(Math.Clamp(roi.Y, 0, 1) * (height - 1)))
            : DetectRoiCenter(pixels, width, height, RoiBias.Galaxy);

        var half = roiSize / 2;
        var startX = Math.Clamp(cx - half, 0, Math.Max(0, width - roiSize));
        var startY = Math.Clamp(cy - half, 0, Math.Max(0, height - roiSize));
        var actualWidth = Math.Min(roiSize, width);
        var actualHeight = Math.Min(roiSize, height);

        var crop = new float[actualWidth * actualHeight];
        for (var y = 0; y < actualHeight; y++)
        {
            var sourceOffset = ((startY + y) * width) + startX;
            var targetOffset = y * actualWidth;
            Array.Copy(pixels, sourceOffset, crop, targetOffset, actualWidth);
        }

        var sample = DownsampleAndStretch(crop, actualWidth, actualHeight, roiSize, roiSize, stretchStrength, stretchMode);
        var stride = roiSize * 3;
        var bitmap = BitmapSource.Create(roiSize, roiSize, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateFullFrameBitmap(float[] pixels, int width, int height, double stretchStrength, StretchMode stretchMode)
    {
        var sample = DownsampleAndStretch(pixels, width, height, width, height, stretchStrength, stretchMode);
        var stride = width * 3;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static (int X, int Y) DetectRoiCenter(float[] pixels, int width, int height, RoiBias bias)
    {
        var longest = Math.Max(width, height);
        var scale = longest > 256 ? 256.0 / longest : 1.0;
        var sw = Math.Max(64, (int)Math.Round(width * scale));
        var sh = Math.Max(64, (int)Math.Round(height * scale));

        var small = ResampleNearest(pixels, width, height, sw, sh);
        small = BoxBlur(small, sw, sh, 2);
        small = BoxBlur(small, sw, sh, 2);

        var sampled = Sample(small);
        Array.Sort(sampled);
        var bg = PercentileFromSorted(sampled, 0.5);
        var hi = PercentileFromSorted(sampled, 0.995);
        var threshold = bg + ((hi - bg) * 0.16);

        var visited = new bool[sw * sh];
        var queue = new Queue<int>();
        double bestScore = double.NegativeInfinity;
        double bestCx = sw * 0.5;
        double bestCy = sh * 0.5;

        for (var y = 1; y < sh - 1; y++)
        {
            for (var x = 1; x < sw - 1; x++)
            {
                var idx = (y * sw) + x;
                if (visited[idx] || small[idx] <= threshold)
                {
                    continue;
                }

                visited[idx] = true;
                queue.Clear();
                queue.Enqueue(idx);

                var count = 0;
                double signalSum = 0;
                double sx = 0;
                double sy = 0;
                double peak = 0;

                while (queue.Count > 0)
                {
                    var cur = queue.Dequeue();
                    var cy = cur / sw;
                    var cx = cur - (cy * sw);
                    var v = small[cur];
                    var signal = Math.Max(0.0, v - threshold);

                    count++;
                    signalSum += signal;
                    sx += signal * cx;
                    sy += signal * cy;
                    if (signal > peak)
                    {
                        peak = signal;
                    }

                    for (var ny = Math.Max(0, cy - 1); ny <= Math.Min(sh - 1, cy + 1); ny++)
                    {
                        var row = ny * sw;
                        for (var nx = Math.Max(0, cx - 1); nx <= Math.Min(sw - 1, cx + 1); nx++)
                        {
                            var nidx = row + nx;
                            if (visited[nidx] || small[nidx] <= threshold)
                            {
                                continue;
                            }

                            visited[nidx] = true;
                            queue.Enqueue(nidx);
                        }
                    }
                }

                if (count < 6 || signalSum <= 0)
                {
                    continue;
                }

                var cxW = sx / signalSum;
                var cyW = sy / signalSum;
                var areaWeight = Math.Sqrt(count);
                var compactSignal = signalSum / Math.Max(1.0, areaWeight * 0.8);
                var peakPenalty = Math.Max(0.0, (peak / Math.Max(1e-9, signalSum / count)) - 10.0);

                var dx = (cxW - (sw * 0.5)) / sw;
                var dy = (cyW - (sh * 0.5)) / sh;
                var centerPenalty = (dx * dx) + (dy * dy);

                var (areaMul, peakMul, centerMul) = bias switch
                {
                    RoiBias.Core => (0.70, 0.10, 0.40),
                    RoiBias.Starfield => (0.45, -0.20, 0.10),
                    _ => (1.00, 0.35, 0.15)
                };

                var score = (compactSignal * (1.0 + (areaMul * areaWeight)))
                            - (peakMul * peakPenalty)
                            - (centerMul * centerPenalty * compactSignal);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCx = cxW;
                    bestCy = cyW;
                }
            }
        }

        var fullX = (int)Math.Round((bestCx / Math.Max(1, sw - 1)) * (width - 1));
        var fullY = (int)Math.Round((bestCy / Math.Max(1, sh - 1)) * (height - 1));
        return (Math.Clamp(fullX, 0, width - 1), Math.Clamp(fullY, 0, height - 1));
    }

    private static float[] ResampleNearest(float[] pixels, int width, int height, int targetWidth, int targetHeight)
    {
        var result = new float[targetWidth * targetHeight];
        for (var y = 0; y < targetHeight; y++)
        {
            var sy = Math.Min(height - 1, (int)Math.Round((y / (double)Math.Max(1, targetHeight - 1)) * (height - 1)));
            var srcRow = sy * width;
            var dstRow = y * targetWidth;
            for (var x = 0; x < targetWidth; x++)
            {
                var sx = Math.Min(width - 1, (int)Math.Round((x / (double)Math.Max(1, targetWidth - 1)) * (width - 1)));
                result[dstRow + x] = pixels[srcRow + sx];
            }
        }

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

    private static byte[] DownsampleAndStretch(float[] pixels, int width, int height, int targetWidth, int targetHeight, double stretchStrength, StretchMode stretchMode)
    {
        var sampled = Sample(pixels);
        if (sampled.Length == 0)
        {
            return new byte[targetWidth * targetHeight * 3];
        }

        Array.Sort(sampled);
        double low = sampled[0];
        double high = sampled[^1];
        if (high - low < 1e-12)
        {
            low = PercentileFromSorted(sampled, 0.01);
            high = PercentileFromSorted(sampled, 0.999);
            if (high - low < 1e-12)
            {
                high = low + 1;
            }
        }

        var range = high - low;
        var median = PercentileFromSorted(sampled, 0.5);
        var mad = MedianAbsoluteDeviation(sampled, median, alreadySorted: true);

        var medianN = Math.Clamp((median - low) / range, 0.0, 1.0);
        var madN = mad / range;

        var normalizedStrength = Math.Clamp(stretchStrength, 0.25, 5.0);
        var shadowsClipping = Math.Clamp(-2.8 * normalizedStrength, -8.0, -0.5);
        var c0 = Math.Clamp(medianN + (shadowsClipping * 1.4826 * madN), 0.0, 0.99);

        var medianPostClip = Math.Clamp((medianN - c0) / (1.0 - c0), 0.0, 1.0);
        var targetBackground = stretchMode == StretchMode.NinaStyle
            ? Math.Clamp(0.36 - (0.05 * (normalizedStrength - 1.0)), 0.20, 0.50)
            : Math.Clamp(0.22 - (0.06 * (normalizedStrength - 1.0)), 0.08, 0.30);
        var midtones = InverseMidtonesTransfer(targetBackground, medianPostClip);
        if (double.IsNaN(midtones) || double.IsInfinity(midtones))
        {
            midtones = 0.25;
        }
        midtones = Math.Clamp(midtones, 0.02, 0.98);
        var data = new byte[targetWidth * targetHeight * 3];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Min(height - 1, (int)((y / (double)Math.Max(1, targetHeight - 1)) * (height - 1)));
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Min(width - 1, (int)((x / (double)Math.Max(1, targetWidth - 1)) * (width - 1)));
                var value = pixels[(sourceY * width) + sourceX];
                var normalized = Math.Clamp((value - low) / range, 0.0, 1.0);
                normalized = Math.Clamp((normalized - c0) / (1.0 - c0), 0.0, 1.0);
                var stretched = Math.Clamp(MidtonesTransfer(normalized, midtones), 0.0, 1.0);
                var b = (byte)Math.Clamp((int)Math.Round(stretched * 255.0), 0, 255);

                var index = ((y * targetWidth) + x) * 3;
                data[index] = b;
                data[index + 1] = b;
                data[index + 2] = b;
            }
        }

        return data;
    }

    private static AstroMetrics ComputeMetrics(LoadedFrame frame)
    {
        var pixels = frame.Pixels;
        var width = frame.Width;
        var height = frame.Height;
        var background = Percentile(pixels, 0.5);
        var sigma = ComputeSigma(pixels, background);
        var threshold = background + (5 * sigma);

        var stars = DetectStars(pixels, width, height, threshold, background, sigma);
        var orderedStars = stars.OrderByDescending(s => s.Peak).Take(300).ToList();

        var fwhm = Median(orderedStars.Select(s => s.Fwhm));
        var hfr = Median(orderedStars.Select(s => s.Hfr));
        var eccentricity = Median(orderedStars.Select(s => s.Eccentricity));
        var trail = DetectTrail(pixels, width, height, background, sigma);
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
            Hfr = hfr,
            StarCount = starCount,
            Eccentricity = eccentricity,
            MeanBackground = background,
            FocalLengthMm = frame.FocalLengthMm,
            PixelSizeUm = frame.PixelSizeUm,
            PossibleSatelliteTrail = trail.Detected,
            TrailX1 = trail.Detected ? trail.X1 : null,
            TrailY1 = trail.Detected ? trail.Y1 : null,
            TrailX2 = trail.Detected ? trail.X2 : null,
            TrailY2 = trail.Detected ? trail.Y2 : null
        };
    }

    private static List<(double Peak, double Fwhm, double Hfr, double Eccentricity)> DetectStars(float[] pixels, int width, int height, double threshold, double background, double sigma)
    {
        var result = new List<(double Peak, double Fwhm, double Hfr, double Eccentricity)>();
        var filtered = MedianFilter3x3(pixels, width, height);
        var minNeighborLevel = background + (2.0 * sigma);
        for (var y = 3; y < height - 3; y++)
        {
            for (var x = 3; x < width - 3; x++)
            {
                var center = filtered[(y * width) + x];
                if (center < threshold)
                {
                    continue;
                }

                var isPeak = true;
                for (var ny = -1; ny <= 1 && isPeak; ny++)
                {
                    for (var nx = -1; nx <= 1; nx++)
                    {
                        if (nx == 0 && ny == 0)
                        {
                            continue;
                        }

                        if (filtered[((y + ny) * width) + (x + nx)] > center)
                        {
                            isPeak = false;
                            break;
                        }
                    }
                }

                if (!isPeak)
                {
                    continue;
                }

                // Reject isolated hot pixels: require supporting signal in immediate neighborhood.
                var supportNeighbors = 0;
                for (var ny = -1; ny <= 1; ny++)
                {
                    for (var nx = -1; nx <= 1; nx++)
                    {
                        if (nx == 0 && ny == 0)
                        {
                            continue;
                        }

                        var neighbor = filtered[((y + ny) * width) + (x + nx)];
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

                var measurement = MeasureStar(pixels, width, height, x, y, background);
                if (measurement.Fwhm > 0 && measurement.Hfr > 0)
                {
                    result.Add((center, measurement.Fwhm, measurement.Hfr, measurement.Eccentricity));
                }
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
        var points = new List<(double X, double Y, double R, double Flux)>();
        var annulus = new List<float>();
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

                annulus.Add(pixels[(y * width) + x]);
            }
        }

        var localBackground = background;
        if (annulus.Count >= 16)
        {
            annulus.Sort();
            var mid = annulus.Count / 2;
            localBackground = annulus.Count % 2 == 0
                ? (annulus[mid - 1] + annulus[mid]) * 0.5
                : annulus[mid];
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
        var sorted = points.OrderBy(p => p.R).ToList();
        var half = totalFlux * 0.5;
        double accum = 0;
        foreach (var p in sorted)
        {
            accum += p.Flux;
            if (accum >= half)
            {
                return p.R;
            }
        }

        return sorted.Count == 0 ? 0 : sorted[^1].R;
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

    private static TrailDetectionResult DetectTrail(float[] pixels, int width, int height, double background, double sigma)
    {
        var threshold = background + (4.0 * sigma);
        if (threshold <= background)
        {
            threshold = background + 1e-6;
        }

        const int binCount = 180;
        var hist = new int[binCount];
        var points = new List<(int X, int Y)>(Math.Min(50000, width * height / 8));
        var cx = (width - 1) * 0.5;
        var cy = (height - 1) * 0.5;

        for (var y = 1; y < height - 1; y++)
        {
            var row = y * width;
            for (var x = 1; x < width - 1; x++)
            {
                var v = pixels[row + x];
                if (v <= threshold)
                {
                    continue;
                }

                var localMedian = Median9(pixels, width, x, y);
                if (v - localMedian < (2.5 * sigma))
                {
                    continue;
                }

                points.Add((x, y));

                var angle = Math.Atan2(y - cy, x - cx);
                if (angle < 0) angle += Math.PI;
                var bin = Math.Clamp((int)Math.Round((angle / Math.PI) * (binCount - 1)), 0, binCount - 1);
                hist[bin]++;
            }
        }

        if (points.Count < 60)
        {
            return new TrailDetectionResult(false, 0, 0, 0, 0);
        }

        var peakBin = 0;
        var peakVotes = 0;
        var totalVotes = 0;
        for (var i = 0; i < hist.Length; i++)
        {
            var v = hist[i];
            totalVotes += v;
            if (v > peakVotes)
            {
                peakVotes = v;
                peakBin = i;
            }
        }

        if (totalVotes <= 0 || peakVotes < 40 || peakVotes < (int)(totalVotes * 0.22))
        {
            return new TrailDetectionResult(false, 0, 0, 0, 0);
        }

        var theta = (peakBin / (double)(binCount - 1)) * Math.PI;
        var dirX = Math.Cos(theta);
        var dirY = Math.Sin(theta);
        var perpX = -dirY;
        var perpY = dirX;

        const double maxDistance = 4.0;
        double minT = double.PositiveInfinity;
        double maxT = double.NegativeInfinity;
        var inlierCount = 0;

        foreach (var p in points)
        {
            var dx = p.X - cx;
            var dy = p.Y - cy;
            var dist = Math.Abs((dx * perpX) + (dy * perpY));
            if (dist > maxDistance)
            {
                continue;
            }

            var t = (dx * dirX) + (dy * dirY);
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
            inlierCount++;
        }

        var span = maxT - minT;
        var minRequiredSpan = 0.35 * Math.Min(width, height);
        if (inlierCount < 50 || span < minRequiredSpan)
        {
            return new TrailDetectionResult(false, 0, 0, 0, 0);
        }

        var x1 = cx + (minT * dirX);
        var y1 = cy + (minT * dirY);
        var x2 = cx + (maxT * dirX);
        var y2 = cy + (maxT * dirY);

        return new TrailDetectionResult(
            true,
            width <= 1 ? 0.5 : Math.Clamp(x1 / (width - 1), 0.0, 1.0),
            height <= 1 ? 0.5 : Math.Clamp(y1 / (height - 1), 0.0, 1.0),
            width <= 1 ? 0.5 : Math.Clamp(x2 / (width - 1), 0.0, 1.0),
            height <= 1 ? 0.5 : Math.Clamp(y2 / (height - 1), 0.0, 1.0));
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
        var sample = Sample(values);
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
