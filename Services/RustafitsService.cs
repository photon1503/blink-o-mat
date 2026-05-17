using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using blink_o_mat.Models;
using nom.tam.fits;
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
            SaveRoiThumbnail(frame.Pixels, frame.Width, frame.Height, roiThumbnailPath, stretchStrength: 1.0);

            var metrics = ComputeMetrics(frame.Pixels, frame.Width, frame.Height);

            return new FrameItem
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                ThumbnailPath = thumbnailPath,
                RoiThumbnailPath = roiThumbnailPath,
                Metrics = metrics
            };
        }, cancellationToken);
    }

    public Task<LoadedFrame> LoadRawFrameAsync(string filePath, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var frame = await LoadFrameAsync(filePath, cancellationToken);
            return new LoadedFrame(frame.Pixels, frame.Width, frame.Height);
        }, cancellationToken);
    }

    public Task RenderThumbnailsAsync(LoadedFrame frame, string thumbnailPath, string roiThumbnailPath, double stretchStrength, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            SaveThumbnail(frame.Pixels, frame.Width, frame.Height, thumbnailPath, stretchStrength);
            SaveRoiThumbnail(frame.Pixels, frame.Width, frame.Height, roiThumbnailPath, stretchStrength);
        }, cancellationToken);
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
        var fits = new Fits(filePath);
        try
        {
            BasicHDU? selectedHdu = null;
            Array? kernel = null;

            BasicHDU? hdu;
            while ((hdu = fits.ReadHDU()) is not null)
            {
                if (hdu.Axes is null || hdu.Axes.Length < 2)
                {
                    continue;
                }

                var candidateKernel = hdu.Kernel as Array ?? hdu.Data?.DataArray as Array;
                if (candidateKernel is null)
                {
                    continue;
                }

                selectedHdu = hdu;
                kernel = candidateKernel;
                break;
            }

            if (selectedHdu is null || selectedHdu.Axes is null || kernel is null)
            {
                throw new InvalidOperationException("FITS image data not found.");
            }

            var width = selectedHdu.Axes[0];
            var height = selectedHdu.Axes[1];
            var channels = selectedHdu.Axes.Length > 2 ? Math.Max(1, selectedHdu.Axes[2]) : 1;
            var bScale = Math.Abs(selectedHdu.BScale) < double.Epsilon ? 1.0 : selectedHdu.BScale;
            var bZero = selectedHdu.BZero;

            var samples = new List<double>();
            Flatten(kernel, samples);

            var pixelCount = width * height;
            if (samples.Count < pixelCount)
            {
                throw new InvalidOperationException("FITS data size mismatch.");
            }

            var luminance = new float[pixelCount];
            if (channels <= 1 || samples.Count < pixelCount * channels)
            {
                for (var i = 0; i < pixelCount; i++)
                {
                    luminance[i] = (float)((samples[i] * bScale) + bZero);
                }
            }
            else
            {
                for (var i = 0; i < pixelCount; i++)
                {
                    var r = (samples[i] * bScale) + bZero;
                    var g = (samples[i + pixelCount] * bScale) + bZero;
                    var b = (samples[i + (2 * pixelCount)] * bScale) + bZero;
                    luminance[i] = (float)((0.2126 * r) + (0.7152 * g) + (0.0722 * b));
                }
            }

            return (luminance, width, height);
        }
        finally
        {
            fits.Close();
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
        const int targetMax = 320;
        var scale = Math.Max(width, height) > targetMax ? targetMax / (double)Math.Max(width, height) : 1.0;
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

        var sample = DownsampleAndStretch(pixels, width, height, targetWidth, targetHeight, stretchStrength);
        var stride = targetWidth * 3;

        var bitmap = BitmapSource.Create(targetWidth, targetHeight, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }

    private static void SaveRoiThumbnail(float[] pixels, int width, int height, string outputPath, double stretchStrength)
    {
        const int roiSize = 220;
        var (cx, cy) = DetectRoiCenter(pixels, width, height);

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
        var stride = roiSize * 3;
        var bitmap = BitmapSource.Create(roiSize, roiSize, 96, 96, PixelFormats.Rgb24, null, sample, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }

    private static (int X, int Y) DetectRoiCenter(float[] pixels, int width, int height)
    {
        var sampled = Sample(pixels);
        Array.Sort(sampled);
        var threshold = PercentileFromSorted(sampled, 0.999);

        double w = 0;
        double sx = 0;
        double sy = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var v = pixels[(y * width) + x];
                if (v < threshold)
                {
                    continue;
                }

                var weight = v - threshold;
                w += weight;
                sx += weight * x;
                sy += weight * y;
            }
        }

        if (w <= 0)
        {
            return (width / 2, height / 2);
        }

        return ((int)Math.Clamp(Math.Round(sx / w), 0, width - 1), (int)Math.Clamp(Math.Round(sy / w), 0, height - 1));
    }

    private static byte[] DownsampleAndStretch(float[] pixels, int width, int height, int targetWidth, int targetHeight, double stretchStrength)
    {
        var sampled = Sample(pixels);
        if (sampled.Length == 0)
        {
            return new byte[targetWidth * targetHeight * 3];
        }

        Array.Sort(sampled);
        var median = PercentileFromSorted(sampled, 0.5);
        var mad = MedianAbsoluteDeviation(sampled, median, alreadySorted: true);
        var sigma = Math.Max(1e-6, mad * 1.4826);

        var normalizedStrength = Math.Clamp(stretchStrength, 0.25, 3.0);
        var low = median - ((1.4 + (1.4 * normalizedStrength)) * sigma);
        var high = PercentileFromSorted(sampled, 0.9995);
        if (high <= low)
        {
            low = PercentileFromSorted(sampled, 0.01);
            high = PercentileFromSorted(sampled, 0.999);
            if (high <= low)
            {
                high = low + 1;
            }
        }

        var clippedMedian = Math.Clamp((median - low) / (high - low), 0.0001, 0.9999);
        var midtones = Math.Clamp((0.25 * (1.0 - clippedMedian)) / normalizedStrength, 0.02, 0.40);

        var data = new byte[targetWidth * targetHeight * 3];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Min(height - 1, (int)((y / (double)Math.Max(1, targetHeight - 1)) * (height - 1)));
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Min(width - 1, (int)((x / (double)Math.Max(1, targetWidth - 1)) * (width - 1)));
                var value = pixels[(sourceY * width) + sourceX];
                var normalized = Math.Clamp((value - low) / (high - low), 0.0, 1.0);
                var stretched = MidtonesTransfer(normalized, midtones);
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
        return ((m - 1.0) * x) / (((2.0 * m) - 1.0) * x - m);
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
