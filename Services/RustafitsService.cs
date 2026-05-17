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

    public sealed record LoadedFrame(float[] Pixels, int Width, int Height, double? FocalLengthMm = null, double? PixelSizeUm = null);

    public async Task<FrameItem> ProcessFrameAsync(string filePath, string thumbnailDirectory, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            var frame = await LoadFrameAsync(filePath, cancellationToken);

            var loadedFrame = new LoadedFrame(frame.Pixels, frame.Width, frame.Height, frame.FocalLengthMm, frame.PixelSizeUm);
            var previews = await RenderPreviewBitmapsAsync(loadedFrame, 1.0, null, cancellationToken);

            var metrics = ComputeMetrics(loadedFrame);

            return new FrameItem
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
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
            return new LoadedFrame(frame.Pixels, frame.Width, frame.Height, frame.FocalLengthMm, frame.PixelSizeUm);
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

    public Task<(BitmapSource Full, BitmapSource Roi)> RenderPreviewBitmapsAsync(LoadedFrame frame, double stretchStrength, (double X, double Y)? roiNormalizedCenter, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var full = CreateThumbnailBitmap(frame.Pixels, frame.Width, frame.Height, 160, 160, stretchStrength);
            var roi = CreateRoiBitmap(frame.Pixels, frame.Width, frame.Height, 160, stretchStrength, roiNormalizedCenter);
            return (full, roi);
        }, cancellationToken);
    }

    public Task<BitmapSource> RenderFullBitmapAsync(LoadedFrame frame, double stretchStrength, CancellationToken cancellationToken)
    {
        return Task.Run(() => CreateFullFrameBitmap(frame.Pixels, frame.Width, frame.Height, stretchStrength), cancellationToken);
    }

    public (double X, double Y) DetectRoiNormalizedCenter(LoadedFrame frame)
    {
        var (x, y) = DetectRoiCenter(frame.Pixels, frame.Width, frame.Height);
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

    private static async Task<(float[] Pixels, int Width, int Height, double? FocalLengthMm, double? PixelSizeUm)> LoadFrameAsync(string filePath, CancellationToken cancellationToken)
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

    private static (float[] Pixels, int Width, int Height, double? FocalLengthMm, double? PixelSizeUm) LoadFits(string filePath)
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

    private static (float[] Pixels, int Width, int Height, double? FocalLengthMm, double? PixelSizeUm)? TryDecodeFitsImage(Stream stream, FitsHeaderInfo header)
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

        return (result, width, height, header.FocalLengthMm, header.PixelSizeUm);
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
                    return new FitsHeaderInfo(bitPix, axisCount, axes, bScale, bZero, focalLengthMm, pixelSizeUm);
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

    private readonly record struct FitsHeaderInfo(int BitPix, int AxisCount, long[] Axes, double BScale, double BZero, double? FocalLengthMm, double? PixelSizeUm);

    private static async Task<(float[] Pixels, int Width, int Height, double? FocalLengthMm, double? PixelSizeUm)> LoadXisfAsync(string filePath, CancellationToken cancellationToken)
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

        return (luminance, width, height, null, null);
    }

    private static LoadedFrame Rotate180(LoadedFrame frame)
    {
        var pixels = new float[frame.Pixels.Length];
        var source = frame.Pixels;
        for (var i = 0; i < source.Length; i++)
        {
            pixels[i] = source[source.Length - 1 - i];
        }

        return new LoadedFrame(pixels, frame.Width, frame.Height, frame.FocalLengthMm, frame.PixelSizeUm);
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

    private static BitmapSource CreateThumbnailBitmap(float[] pixels, int width, int height, int maxWidth, int maxHeight, double stretchStrength)
    {
        var scale = Math.Min(maxWidth / (double)Math.Max(1, width), maxHeight / (double)Math.Max(1, height));
        scale = Math.Min(1.0, scale <= 0 ? 1.0 : scale);
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

        var sample = DownsampleAndStretch(pixels, width, height, targetWidth, targetHeight, stretchStrength);
        Debug.WriteLine($"CreateThumbnailBitmap stretch={stretchStrength:F2} min={sample.Min()} max={sample.Max()} avg={sample.Select(v => (int)v).Average():F2}");
        var stride = targetWidth * 3;

        var bitmap = BitmapSource.Create(targetWidth, targetHeight, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateRoiBitmap(float[] pixels, int width, int height, int roiSize, double stretchStrength, (double X, double Y)? roiNormalizedCenter)
    {
        var (cx, cy) = roiNormalizedCenter is { } roi
            ? ((int)Math.Round(Math.Clamp(roi.X, 0, 1) * (width - 1)), (int)Math.Round(Math.Clamp(roi.Y, 0, 1) * (height - 1)))
            : DetectRoiCenter(pixels, width, height);

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

        var sample = DownsampleAndStretch(crop, actualWidth, actualHeight, roiSize, roiSize, stretchStrength);
        Debug.WriteLine($"CreateRoiBitmap stretch={stretchStrength:F2} min={sample.Min()} max={sample.Max()} avg={sample.Select(v => (int)v).Average():F2}");
        var stride = roiSize * 3;
        var bitmap = BitmapSource.Create(roiSize, roiSize, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource CreateFullFrameBitmap(float[] pixels, int width, int height, double stretchStrength)
    {
        var sample = DownsampleAndStretch(pixels, width, height, width, height, stretchStrength);
        var stride = width * 3;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static (int X, int Y) DetectRoiCenter(float[] pixels, int width, int height)
    {
        const int grid = 48;
        var tileW = Math.Max(8, width / grid);
        var tileH = Math.Max(8, height / grid);
        var nx = Math.Max(1, width / tileW);
        var ny = Math.Max(1, height / tileH);

        var sampled = Sample(pixels);
        Array.Sort(sampled);
        var bg = PercentileFromSorted(sampled, 0.5);
        var hi = PercentileFromSorted(sampled, 0.995);
        var floor = Math.Max(bg, hi * 0.25);

        double bestScore = double.NegativeInfinity;
        int bestX = width / 2;
        int bestY = height / 2;

        for (var ty = 0; ty < ny; ty++)
        {
            var y0 = ty * tileH;
            var y1 = Math.Min(height, y0 + tileH);

            for (var tx = 0; tx < nx; tx++)
            {
                var x0 = tx * tileW;
                var x1 = Math.Min(width, x0 + tileW);

                double sum = 0;
                var count = 0;
                double w = 0;
                double sx = 0;
                double sy = 0;
                double sxx = 0;
                double syy = 0;
                double sxy = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = y * width;
                    for (var x = x0; x < x1; x++)
                    {
                        var v = pixels[row + x];
                        if (v <= floor)
                        {
                            continue;
                        }

                        var signal = v - floor;
                        sum += signal;
                        count++;

                        w += signal;
                        sx += signal * x;
                        sy += signal * y;
                        sxx += signal * x * x;
                        syy += signal * y * y;
                        sxy += signal * x * y;
                    }
                }

                var isotropy = 0.0;
                if (w > 0)
                {
                    var mx = sx / w;
                    var my = sy / w;
                    var cxx = (sxx / w) - (mx * mx);
                    var cyy = (syy / w) - (my * my);
                    var cxy = (sxy / w) - (mx * my);

                    var trace = cxx + cyy;
                    var det = (cxx * cyy) - (cxy * cxy);
                    var disc = Math.Max(0.0, (trace * trace) - (4.0 * det));
                    var major = Math.Max(1e-12, (trace + Math.Sqrt(disc)) / 2.0);
                    var minor = Math.Max(1e-12, (trace - Math.Sqrt(disc)) / 2.0);
                    isotropy = Math.Clamp(minor / major, 0.0, 1.0);
                }

                var centerBiasX = ((x0 + x1) * 0.5 - (width * 0.5)) / width;
                var centerBiasY = ((y0 + y1) * 0.5 - (height * 0.5)) / height;
                var centerPenalty = (centerBiasX * centerBiasX) + (centerBiasY * centerBiasY);
                var shapeWeight = 0.25 + (0.75 * isotropy);
                var score = ((count > 0 ? sum / Math.Sqrt(count) : 0) * shapeWeight) - (0.10 * centerPenalty);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = (x0 + x1) / 2;
                    bestY = (y0 + y1) / 2;
                }
            }
        }

        return (Math.Clamp(bestX, 0, width - 1), Math.Clamp(bestY, 0, height - 1));
    }

    private static byte[] DownsampleAndStretch(float[] pixels, int width, int height, int targetWidth, int targetHeight, double stretchStrength)
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
        var targetBackground = Math.Clamp(0.22 - (0.06 * (normalizedStrength - 1.0)), 0.08, 0.30);
        var midtones = InverseMidtonesTransfer(targetBackground, medianPostClip);
        if (double.IsNaN(midtones) || double.IsInfinity(midtones))
        {
            midtones = 0.25;
        }
        midtones = Math.Clamp(midtones, 0.02, 0.98);
        Debug.WriteLine($"StretchStats low={low:F6} high={high:F6} median={median:F6} mad={mad:F6} c0={c0:F6} m={midtones:F6}");

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

        var stars = DetectStars(pixels, width, height, threshold, background);
        var orderedStars = stars.OrderByDescending(s => s.Peak).Take(300).ToList();

        var fwhm = Median(orderedStars.Select(s => s.Fwhm));
        var hfr = Median(orderedStars.Select(s => s.Hfr));
        var eccentricity = Median(orderedStars.Select(s => s.Eccentricity));
        var possibleTrail = DetectTrail(pixels, width, height, background, sigma);
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
            PossibleSatelliteTrail = possibleTrail
        };
    }

    private static List<(double Peak, double Fwhm, double Hfr, double Eccentricity)> DetectStars(float[] pixels, int width, int height, double threshold, double background)
    {
        var result = new List<(double Peak, double Fwhm, double Hfr, double Eccentricity)>();
        for (var y = 3; y < height - 3; y++)
        {
            for (var x = 3; x < width - 3; x++)
            {
                var center = pixels[(y * width) + x];
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

                        if (pixels[((y + ny) * width) + (x + nx)] > center)
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

                var measurement = MeasureStar(pixels, width, height, x, y, background);
                if (measurement.Fwhm > 0 && measurement.Hfr > 0)
                {
                    result.Add((center, measurement.Fwhm, measurement.Hfr, measurement.Eccentricity));
                }
            }
        }

        return result;
    }

    private static (double Fwhm, double Hfr, double Eccentricity) MeasureStar(float[] pixels, int width, int height, int cx, int cy, double background)
    {
        const int radius = 4;
        var points = new List<(double X, double Y, double R, double Flux)>();
        double fluxSum = 0;
        double xSum = 0;
        double ySum = 0;

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

                var signal = Math.Max(0, pixels[(y * width) + x] - background);
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

        var sigma = Math.Sqrt((lambda1 + lambda2) / 2.0);
        var fwhm = 2.3548 * sigma;
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

    private static bool DetectTrail(float[] pixels, int width, int height, double background, double sigma)
    {
        var threshold = background + (6 * sigma);
        double w = 0;
        double sx = 0;
        double sy = 0;
        double sxx = 0;
        double syy = 0;
        double sxy = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = pixels[(y * width) + x];
                if (v < threshold)
                {
                    continue;
                }

                var weight = v - background;
                w += weight;
                sx += weight * x;
                sy += weight * y;
                sxx += weight * x * x;
                syy += weight * y * y;
                sxy += weight * x * y;
            }
        }

        if (w <= 0)
        {
            return false;
        }

        var mx = sx / w;
        var my = sy / w;
        var cxx = (sxx / w) - (mx * mx);
        var cyy = (syy / w) - (my * my);
        var cxy = (sxy / w) - (mx * my);

        var trace = cxx + cyy;
        var det = (cxx * cyy) - (cxy * cxy);
        var disc = Math.Max(0, (trace * trace) - (4 * det));
        var major = Math.Max(1e-6, (trace + Math.Sqrt(disc)) / 2.0);
        var minor = Math.Max(1e-6, (trace - Math.Sqrt(disc)) / 2.0);
        var elongation = major / minor;

        return elongation > 25;
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
