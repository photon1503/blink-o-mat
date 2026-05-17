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
    public sealed record LoadedFrame(float[] Pixels, int Width, int Height);

    public async Task<FrameItem> ProcessFrameAsync(string filePath, string thumbnailDirectory, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            var frame = await LoadFrameAsync(filePath, cancellationToken);

            Directory.CreateDirectory(thumbnailDirectory);
            var thumbnailPath = Path.Combine(thumbnailDirectory, Path.GetFileNameWithoutExtension(filePath) + ".jpg");
            var roiThumbnailPath = Path.Combine(thumbnailDirectory, Path.GetFileNameWithoutExtension(filePath) + "_roi.jpg");
            SaveThumbnail(frame.Pixels, frame.Width, frame.Height, thumbnailPath, stretchStrength: 1.0);
            SaveRoiThumbnail(frame.Pixels, frame.Width, frame.Height, roiThumbnailPath, stretchStrength: 1.0, roiNormalizedCenter: null);

            var metrics = ComputeMetrics(frame.Pixels, frame.Width, frame.Height);

            return new FrameItem
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                ThumbnailPath = thumbnailPath,
                RoiThumbnailPath = roiThumbnailPath,
                FullPreviewPath = thumbnailPath,
                Metrics = metrics
            };
        }, cancellationToken);
    }

    public Task<LoadedFrame> LoadRawFrameAsync(string filePath, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var frame = await LoadFrameAsync(filePath, cancellationToken);
            NormalizeInPlace(frame.Pixels);
            return new LoadedFrame(frame.Pixels, frame.Width, frame.Height);
        }, cancellationToken);
    }

    public Task RenderThumbnailsAsync(LoadedFrame frame, string thumbnailPath, string roiThumbnailPath, double stretchStrength, (double X, double Y)? roiNormalizedCenter, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            SaveThumbnail(frame.Pixels, frame.Width, frame.Height, thumbnailPath, stretchStrength);
            SaveRoiThumbnail(frame.Pixels, frame.Width, frame.Height, roiThumbnailPath, stretchStrength, roiNormalizedCenter);
        }, cancellationToken);
    }

    public Task RenderFullFrameAsync(LoadedFrame frame, string outputPath, double stretchStrength, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            SaveFullFrame(frame.Pixels, frame.Width, frame.Height, outputPath, stretchStrength);
        }, cancellationToken);
    }

    public (double X, double Y) DetectRoiNormalizedCenter(LoadedFrame frame)
    {
        var (x, y) = DetectRoiCenter(frame.Pixels, frame.Width, frame.Height);
        return (frame.Width <= 1 ? 0.5 : x / (double)(frame.Width - 1), frame.Height <= 1 ? 0.5 : y / (double)(frame.Height - 1));
    }

    public AstroMetrics AnalyzeFrame(LoadedFrame frame)
    {
        return ComputeMetrics(frame.Pixels, frame.Width, frame.Height);
    }

    private static async Task<(float[] Pixels, int Width, int Height)> LoadFrameAsync(string filePath, CancellationToken cancellationToken)
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

    private static (float[] Pixels, int Width, int Height) LoadFits(string filePath)
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

    private static (float[] Pixels, int Width, int Height)? TryDecodeFitsImage(Stream stream, FitsHeaderInfo header)
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

        return (result, width, height);
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
                    return new FitsHeaderInfo(bitPix, axisCount, axes, bScale, bZero);
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

    private readonly record struct FitsHeaderInfo(int BitPix, int AxisCount, long[] Axes, double BScale, double BZero);

    private static void NormalizeInPlace(float[] pixels)
    {
        if (pixels.Length == 0)
        {
            return;
        }

        var min = float.PositiveInfinity;
        var max = float.NegativeInfinity;
        for (var i = 0; i < pixels.Length; i++)
        {
            var v = pixels[i];
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                continue;
            }

            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (!float.IsFinite(min) || !float.IsFinite(max) || max - min < 1e-20f)
        {
            Array.Fill(pixels, 0f);
            return;
        }

        var scale = 1.0f / (max - min);
        for (var i = 0; i < pixels.Length; i++)
        {
            var v = pixels[i];
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                pixels[i] = 0;
                continue;
            }

            pixels[i] = (v - min) * scale;
        }
    }

    private static async Task<(float[] Pixels, int Width, int Height)> LoadXisfAsync(string filePath, CancellationToken cancellationToken)
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

        return (luminance, width, height);
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

    private static void SaveThumbnail(float[] pixels, int width, int height, string outputPath, double stretchStrength)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        const int targetMax = 320;
        var scale = Math.Max(width, height) > targetMax ? targetMax / (double)Math.Max(width, height) : 1.0;
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

        var sample = DownsampleAndStretch(pixels, width, height, targetWidth, targetHeight, stretchStrength);
        Debug.WriteLine($"SaveThumbnail {Path.GetFileName(outputPath)} stretch={stretchStrength:F2} min={sample.Min()} max={sample.Max()} avg={sample.Select(v => (int)v).Average():F2}");
        var stride = targetWidth * 3;

        var bitmap = BitmapSource.Create(targetWidth, targetHeight, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }

    private static void SaveRoiThumbnail(float[] pixels, int width, int height, string outputPath, double stretchStrength, (double X, double Y)? roiNormalizedCenter)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        const int roiSize = 220;
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
        Debug.WriteLine($"SaveRoiThumbnail {Path.GetFileName(outputPath)} stretch={stretchStrength:F2} min={sample.Min()} max={sample.Max()} avg={sample.Select(v => (int)v).Average():F2}");
        var stride = roiSize * 3;
        var bitmap = BitmapSource.Create(roiSize, roiSize, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }

    private static void SaveFullFrame(float[] pixels, int width, int height, string outputPath, double stretchStrength)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sample = DownsampleAndStretch(pixels, width, height, width, height, stretchStrength);
        var stride = width * 3;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var fs = File.Create(outputPath);
        encoder.Save(fs);
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

        var normalizedStrength = Math.Clamp(stretchStrength, 0.25, 3.0);
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

    private static AstroMetrics ComputeMetrics(float[] pixels, int width, int height)
    {
        var background = Percentile(pixels, 0.5);
        var sigma = ComputeSigma(pixels, background);
        var threshold = background + (5 * sigma);

        var stars = DetectStars(pixels, width, height, threshold, background);
        var orderedStars = stars.OrderByDescending(s => s.Peak).Take(300).ToList();

        var fwhm = Median(orderedStars.Select(s => s.Fwhm));
        var hfr = Median(orderedStars.Select(s => s.Hfr));
        var eccentricity = Median(orderedStars.Select(s => s.Eccentricity));
        var possibleTrail = DetectTrail(pixels, width, height, background, sigma);

        return new AstroMetrics
        {
            Fwhm = fwhm,
            Hfr = hfr,
            Eccentricity = eccentricity,
            MeanBackground = background,
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
