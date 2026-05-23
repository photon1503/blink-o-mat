using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using blink_o_mat.Models;
using blink_o_mat.Services;
using Microsoft.VSDiagnostics;

[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 5)]
[CPUUsageDiagnoser]
public class FrameLoadBenchmark
{
    private RustafitsService _service = null!;
    private RustafitsService.LoadedFrame _frame = null!;
    private AstroMetrics _metrics;
    private StfParameters _stf;
    [GlobalSetup]
    public void Setup()
    {
        _service = new RustafitsService();
        const int width = 4656;
        const int height = 3520;
        var rng = new Random(42);
        var pixels = new float[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (float)(rng.NextDouble() * 65535.0);
        for (var s = 0; s < 500; s++)
        {
            var cx = 10 + rng.Next(width - 20);
            var cy = 10 + rng.Next(height - 20);
            for (var dy = -3; dy <= 3; dy++)
                for (var dx = -3; dx <= 3; dx++)
                    pixels[(cy + dy) * width + (cx + dx)] = 60000f + (float)(rng.NextDouble() * 5000);
        }

        _frame = new RustafitsService.LoadedFrame(pixels, width, height, 65535.0);
        _stf = new StfParameters(0.0, 0.25, 1.0);
        _metrics = _service.AnalyzeFrame(_frame);
    }

    [Benchmark(Description = "AnalyzeFrame (statistics + star detection)")]
    public AstroMetrics AnalyzeFrame() => _service.AnalyzeFrame(_frame);
    [Benchmark(Description = "RenderPreviewBitmapsAsync (160px full + ROI thumbnails)")]
    public async Task<(BitmapSource Full, BitmapSource Roi)> RenderPreviews() => await _service.RenderPreviewBitmapsAsync(_frame, _stf, null, _metrics, CancellationToken.None);
}