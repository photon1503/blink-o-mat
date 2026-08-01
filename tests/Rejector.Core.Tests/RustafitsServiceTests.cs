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