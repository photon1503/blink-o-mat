using System.Buffers.Binary;
using System.Text;
using Rejector.Core.Models;
using Rejector.Core.Services;

namespace Rejector.Core.Tests;

public sealed class HeadlessRunnerTests
{
    [Fact]
    public async Task RunAsync_ProcessesAndMovesRejectedFrames()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var inputDir = Path.Combine(tempRoot, "input");
            var rejectedDir = Path.Combine(tempRoot, "rejected");
            Directory.CreateDirectory(inputDir);
            Directory.CreateDirectory(rejectedDir);

            var sourceFile = Path.Combine(inputDir, "reject-me.fit");
            WriteFitsFile(sourceFile, 32, 32, BuildSyntheticPixels(32, 32));

            var runner = new HeadlessRunner();
            var options = new CommandLineOptions
            {
                IsHeadless = true,
                InputFolder = inputDir,
                RejectedFolder = rejectedDir,
                Thresholds = new Thresholds
                {
                    MaxFwhm = 0.5,
                    MaxFwhmArcsec = 1000,
                    MaxHfr = 1000,
                    MaxEccentricity = 1,
                    MaxMeanBackground = double.MaxValue,
                    MinSatelliteConfidence = 0,
                },
            };

            var exitCode = await runner.RunAsync(options, CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(sourceFile));
            Assert.Single(Directory.GetFiles(rejectedDir, "*.fit"));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
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