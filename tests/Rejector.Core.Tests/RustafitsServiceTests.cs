using System.Buffers.Binary;
using System.Text;
using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public sealed class RustafitsServiceTests
{
    [Fact]
    public async Task ProcessFrameAsync_LoadsFitsAndComputesMetrics()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var filePath = Path.Combine(tempRoot, "synthetic.fit");
            WriteFitsFile(filePath, 32, 32, BuildSyntheticPixels(32, 32));

            var service = new RustafitsService();
            var result = await service.ProcessFrameAsync(filePath, CancellationToken.None);

            Assert.Equal("synthetic.fit", result.FileName);
            Assert.Equal(filePath, result.FilePath);
            Assert.True(result.Metrics.StarCount >= 1);
            Assert.True(result.Metrics.Fwhm > 0);
            Assert.True(result.Metrics.Hfr > 0);
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task RenderScaledPreviewImageAsync_PreservesAspectRatio()
    {
        const int width = 1200;
        const int height = 600;
        var frame = new RustafitsService.LoadedFrame(new float[width * height], width, height);
        var service = new RustafitsService();

        var preview = await service.RenderScaledPreviewImageAsync(
            frame,
            720,
            720,
            new Rejector.Core.Models.StfParameters(0, 0.5, 1),
            CancellationToken.None);

        Assert.Equal(720, preview.Width);
        Assert.Equal(360, preview.Height);
        Assert.Equal(width / (double)height, preview.Width / (double)preview.Height, precision: 6);
    }

    [Fact]
    public async Task RenderFullPreviewImageAsync_KeepsOriginalResolution()
    {
        const int width = 1234;
        const int height = 777;
        var frame = new RustafitsService.LoadedFrame(new float[width * height], width, height);
        var service = new RustafitsService();

        var preview = await service.RenderFullPreviewImageAsync(
            frame,
            new Rejector.Core.Models.StfParameters(0, 0.5, 1),
            CancellationToken.None);

        Assert.Equal(width, preview.Width);
        Assert.Equal(height, preview.Height);
    }

    private static short[] BuildSyntheticPixels(int width, int height)
    {
        var pixels = new short[width * height];
        const double background = 100;
        const double peak = 4000;
        const double sigma = 1.2;
        var cx = (width - 1) / 2.0;
        var cy = (height - 1) / 2.0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var intensity = background + (peak * Math.Exp(-((dx * dx) + (dy * dy)) / (2 * sigma * sigma)));
                pixels[(y * width) + x] = (short)Math.Clamp((int)Math.Round(intensity), short.MinValue, short.MaxValue);
            }
        }

        return pixels;
    }

    [Fact]
    public void AnalyzeFrame_PureNoiseFrame_YieldsNoStars()
    {
        const int width = 256;
        const int height = 256;
        var rng = new Random(42);
        var pixels = new float[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = 500f + (float)NextGaussian(rng, 0, 20);
        }
        var frame = new RustafitsService.LoadedFrame(pixels, width, height);

        var metrics = new RustafitsService().AnalyzeFrame(frame);

        Assert.True(metrics.StarCount <= 2, $"Expected ≤2 stars on pure noise, got {metrics.StarCount}");
    }

    [Fact]
    public void AnalyzeFrame_FrameWithGaussianStars_DetectsThem()
    {
        const int width = 256;
        const int height = 256;
        var pixels = new float[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = 500f;
        }
        AddGaussianStar(pixels, width, height, 60, 80, 4000, 1.4);
        AddGaussianStar(pixels, width, height, 180, 120, 3000, 1.6);
        AddGaussianStar(pixels, width, height, 100, 200, 2500, 1.3);
        var frame = new RustafitsService.LoadedFrame(pixels, width, height);

        var metrics = new RustafitsService().AnalyzeFrame(frame);

        Assert.Equal(3, metrics.StarCount);
    }

    [Fact]
    public void AnalyzeFrame_BrightAndDimCopies_ShouldHaveComparableUsableStarCounts()
    {
        const int width = 256;
        const int height = 256;
        var baseline = new float[width * height];
        for (var i = 0; i < baseline.Length; i++)
        {
            baseline[i] = 500f;
        }

        AddGaussianStar(baseline, width, height, 60, 80, 4000, 1.3);
        AddGaussianStar(baseline, width, height, 180, 120, 3200, 1.5);
        AddGaussianStar(baseline, width, height, 100, 200, 2800, 1.2);
        AddGaussianStar(baseline, width, height, 220, 60, 2600, 1.4);

        var dimPixels = new float[baseline.Length];
        var brightPixels = new float[baseline.Length];
        for (var i = 0; i < baseline.Length; i++)
        {
            dimPixels[i] = baseline[i] * 0.35f + 30f;
            brightPixels[i] = baseline[i] * 2.5f + 150f;
        }

        var service = new RustafitsService();
        var dimMetrics = service.AnalyzeFrame(new RustafitsService.LoadedFrame(dimPixels, width, height));
        var brightMetrics = service.AnalyzeFrame(new RustafitsService.LoadedFrame(brightPixels, width, height));

        var delta = Math.Abs(dimMetrics.StarCount - brightMetrics.StarCount);
        Assert.True(delta <= 1, $"Expected similar usable star counts across brightness changes, got dim={dimMetrics.StarCount}, bright={brightMetrics.StarCount}, delta={delta}");
    }

    [Fact]
    public void AnalyzeFrame_TwoToOneDiagonalTrail_IsDetected()
    {
        const int width = 320;
        const int height = 240;
        var pixels = new float[width * height];
        Array.Fill(pixels, 500f);

        for (var x = 24; x < 145; x++)
        {
            var y = 30 + (2 * (x - 24));
            if (y >= height - 2)
            {
                break;
            }

            for (var offset = -1; offset <= 1; offset++)
            {
                pixels[(y * width) + x] = 1800f;
                if (y + offset >= 0 && y + offset < height)
                {
                    pixels[((y + offset) * width) + x] = 1800f;
                }
            }
        }

        var metrics = new RustafitsService().AnalyzeFrame(new RustafitsService.LoadedFrame(pixels, width, height));

        Assert.True(metrics.SatelliteTrailConfidence > 0);
        Assert.NotNull(metrics.TrailX1);
        Assert.NotNull(metrics.TrailY1);
        Assert.NotNull(metrics.TrailX2);
        Assert.NotNull(metrics.TrailY2);
    }

    [Fact]
    public void AnalyzeFrame_ArbitraryAngleTrail_IsDetected()
    {
        const int width = 400;
        const int height = 240;
        var pixels = new float[width * height];
        Array.Fill(pixels, 500f);

        var slope = Math.Tan(17.0 * Math.PI / 180.0);
        for (var x = 24; x < 320; x++)
        {
            var y = 80 + (int)Math.Round((x - 24) * slope);
            for (var offset = -1; offset <= 1; offset++)
            {
                if (y + offset >= 0 && y + offset < height)
                {
                    pixels[((y + offset) * width) + x] = 1800f;
                }
            }
        }

        var metrics = new RustafitsService().AnalyzeFrame(new RustafitsService.LoadedFrame(pixels, width, height));

        Assert.True(metrics.SatelliteTrailConfidence > 0);
        Assert.NotNull(metrics.TrailX1);
        Assert.NotNull(metrics.TrailY1);
        Assert.NotNull(metrics.TrailX2);
        Assert.NotNull(metrics.TrailY2);
    }

    private static double NextGaussian(Random rng, double mean, double stdDev)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        var normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + (stdDev * normal);
    }

    private static void AddGaussianStar(float[] pixels, int width, int height, double cx, double cy, double peak, double sigma)
    {
        var r = (int)Math.Ceiling(sigma * 4);
        for (var y = (int)cy - r; y <= (int)cy + r; y++)
        {
            for (var x = (int)cx - r; x <= (int)cx + r; x++)
            {
                if (x < 0 || x >= width || y < 0 || y >= height)
                {
                    continue;
                }
                var dx = x - cx;
                var dy = y - cy;
                var v = peak * Math.Exp(-((dx * dx) + (dy * dy)) / (2 * sigma * sigma));
                pixels[(y * width) + x] += (float)v;
            }
        }
    }

    private static void WriteFitsFile(string filePath, int width, int height, short[] pixels)
    {
        var cards = new[]
        {
            CreateFitsCard("SIMPLE", "T"),
            CreateFitsCard("BITPIX", "16"),
            CreateFitsCard("NAXIS", "2"),
            CreateFitsCard("NAXIS1", width.ToString()),
            CreateFitsCard("NAXIS2", height.ToString()),
            CreateFitsCard("EXPTIME", "60.0"),
            CreateFitsCard("FILTER", "'L'"),
            CreateFitsCard("END", string.Empty, isEnd: true),
        };

        var headerText = string.Concat(cards);
        var headerBytes = Encoding.ASCII.GetBytes(headerText);
        var paddedHeaderLength = ((headerBytes.Length + 2879) / 2880) * 2880;
        Array.Resize(ref headerBytes, paddedHeaderLength);
        for (var index = headerText.Length; index < headerBytes.Length; index++)
        {
            headerBytes[index] = (byte)' ';
        }

        var dataBytes = new byte[pixels.Length * sizeof(short)];
        for (var index = 0; index < pixels.Length; index++)
        {
            BinaryPrimitives.WriteInt16BigEndian(dataBytes.AsSpan(index * sizeof(short), sizeof(short)), pixels[index]);
        }

        var paddedDataLength = ((dataBytes.Length + 2879) / 2880) * 2880;
        Array.Resize(ref dataBytes, paddedDataLength);

        File.WriteAllBytes(filePath, [.. headerBytes, .. dataBytes]);
    }

    private static string CreateFitsCard(string key, string value, bool isEnd = false)
    {
        if (isEnd)
        {
            return "END".PadRight(80, ' ');
        }

        var content = $"{key.PadRight(8)}= {value}";
        return content.Length >= 80 ? content[..80] : content.PadRight(80, ' ');
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Rejector.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}