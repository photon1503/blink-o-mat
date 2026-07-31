namespace Rejector.Core.Models;

public sealed class ProcessedFrame
{
    private bool _automaticRejected;
    private bool? _manualRejectedOverride;

    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public string? RelativePath { get; init; }
    public required AstroMetrics Metrics { get; init; }
    public OrientationDebugInfo? OrientationDebug { get; init; }
    public DateTimeOffset? ExposureDateTime { get; init; }
    public double? ExposureSeconds { get; init; }
    public string? FilterName { get; init; }
    public double OverallScore { get; set; }

    public bool AutomaticRejected => _automaticRejected;

    public bool? ManualRejectedOverride => _manualRejectedOverride;

    public bool IsRejected => _manualRejectedOverride ?? _automaticRejected;

    public void SetAutomaticRejected(bool rejected)
    {
        _automaticRejected = rejected;
    }

    public void SetManualRejectedOverride(bool? rejected)
    {
        _manualRejectedOverride = rejected;
    }
}