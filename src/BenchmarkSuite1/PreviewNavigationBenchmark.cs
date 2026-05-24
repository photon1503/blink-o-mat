using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using blink_o_mat.Services;

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 5)]
public class PreviewNavigationBenchmark
{
    private RustafitsService _service = null!;
    private RustafitsService.LoadedFrame _frame = null!;
    private blink_o_mat.Models.StfParameters _stf;
    [GlobalSetup]
    public void Setup()
    {
        _service = new RustafitsService();
        const int width = 3096;
        const int height = 2080;
        var rng = new Random(42);
        var pixels = new float[width * height];
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = (float)(rng.NextDouble() * 65535.0);
        _frame = new RustafitsService.LoadedFrame(pixels, width, height, 65535.0);
        _stf = new blink_o_mat.Models.StfParameters(0.0, 0.25, 1.0);
    }

    [Benchmark(Description = "RenderFullBitmapAsync (cache miss path)")]
    public async Task<BitmapSource> RenderFullBitmap()
    {
        return await _service.RenderFullBitmapAsync(_frame, _stf, CancellationToken.None);
    }

    [Benchmark(Description = "RenderScaledPreviewBitmapAsync (interactive path, 1600px)")]
    public async Task<BitmapSource> RenderScaledPreview()
    {
        return await _service.RenderScaledPreviewBitmapAsync(_frame, 1600, 1067, _stf, CancellationToken.None);
    }

    [Benchmark(Description = "PixelArray clone (ExpandFrame cost)")]
    public float[] PixelArrayClone()
    {
        return (float[])_frame.Pixels.Clone();
    }

    [Benchmark(Description = "SolidColorBrush allocation per marker (RedrawCacheIndicators)")]
    public SolidColorBrush[] BrushAllocation()
    {
        const int markerCount = 30;
        var brushes = new SolidColorBrush[markerCount];
        for (var i = 0; i < markerCount; i++)
            brushes[i] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x39, 0xD3, 0x53));
        return brushes;
    }
}