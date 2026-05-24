using blink_o_mat.Models;
using System.IO;

namespace blink_o_mat.Services;

public sealed class HeadlessRunner
{
    private readonly FrameDiscoveryService _discovery = new();
    private readonly RustafitsService _rustafits = new();
    private readonly FrameRejectionService _rejection = new();
    private readonly FrameMoveService _move = new();

    public async Task<int> RunAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.InputFolder) || string.IsNullOrWhiteSpace(options.RejectedFolder))
        {
            Console.WriteLine("Headless mode requires --input and --rejected.");
            return 2;
        }

        var frames = _discovery.Discover(options.InputFolder);
        if (frames.Count == 0)
        {
            Console.WriteLine("No FITS/XISF files found.");
            return 0;
        }

        var tempThumbs = Path.Combine(Path.GetTempPath(), "Rejector-thumbs", DateTime.Now.ToString("yyyyMMddHHmmss"));
        var processed = new List<FrameItem>(frames.Count);

        foreach (var framePath in frames)
        {
            var frame = await _rustafits.ProcessFrameAsync(framePath, tempThumbs, cancellationToken);
            frame.SetAutomaticRejected(_rejection.ShouldReject(frame, options.Thresholds));
            processed.Add(frame);

            Console.WriteLine($"{frame.FileName}: FWHM={frame.Metrics.Fwhm:F2}px FWHM\"={(frame.Metrics.FwhmArcsec?.ToString("F2") ?? "n/a")} Stars={frame.Metrics.StarCount} HFR={frame.Metrics.Hfr:F2} Ecc={frame.Metrics.Eccentricity:F3} BG={frame.Metrics.MeanBackground:F1} Focal={(frame.Metrics.FocalLengthMm?.ToString("F1") ?? "n/a")}mm Pixel={(frame.Metrics.PixelSizeUm?.ToString("F2") ?? "n/a")}um Trail={frame.Metrics.SatelliteTrailConfidence}% Reject={frame.IsRejected}");
        }

        var moved = _move.MoveRejected(processed, options.RejectedFolder);
        Console.WriteLine($"Moved {moved} rejected frames to '{options.RejectedFolder}'.");
        return 0;
    }
}
