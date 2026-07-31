using Rejector.Core.Models;

namespace Rejector.Core.Services;

public sealed class HeadlessRunner
{
    private readonly FrameDiscoveryService _discovery = new();
    private readonly RustafitsService _analysis = new();
    private readonly FrameRejectionService _rejection = new();
    private readonly FrameMoveService _move = new();

    public async Task<int> RunAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

        Console.WriteLine($"Discovered {frames.Count} candidate frame(s) in '{options.InputFolder}'.");

        var processed = new List<ProcessedFrame>(frames.Count);
        var failed = 0;

        foreach (var framePath in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var frame = await _analysis.ProcessFrameAsync(framePath, cancellationToken).ConfigureAwait(false);
                frame.SetAutomaticRejected(_rejection.ShouldReject(frame, options.Thresholds));
                processed.Add(frame);

                Console.WriteLine($"{frame.FileName}: FWHM={frame.Metrics.Fwhm:F2}px FWHM\"={(frame.Metrics.FwhmArcsec?.ToString("F2") ?? "n/a")} Stars={frame.Metrics.StarCount} HFR={frame.Metrics.Hfr:F2} Ecc={frame.Metrics.Eccentricity:F3} BG={frame.Metrics.MeanBackground:F1} Focal={(frame.Metrics.FocalLengthMm?.ToString("F1") ?? "n/a")}mm Pixel={(frame.Metrics.PixelSizeUm?.ToString("F2") ?? "n/a")}um Trail={frame.Metrics.SatelliteTrailConfidence}% Reject={frame.IsRejected}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"Skipped {Path.GetFileName(framePath)}: {ex.Message}");
            }
        }

        var moved = _move.MoveRejected(processed, options.RejectedFolder);
        Console.WriteLine($"Processed {processed.Count} frame(s); skipped {failed}; moved {moved.Count} rejected frame(s) to '{options.RejectedFolder}'.");
        return failed == frames.Count ? 1 : 0;
    }
}