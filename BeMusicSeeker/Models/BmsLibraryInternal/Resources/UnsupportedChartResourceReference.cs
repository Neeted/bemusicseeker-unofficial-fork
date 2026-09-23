namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal readonly struct UnsupportedChartResourceReference(
    ChartResourceKind kind,
    string rawPath,
    ChartResourcePathNormalizationStatus reason)
{
    public ChartResourceKind Kind { get; } = kind;

    public string RawPath { get; } = rawPath ?? string.Empty;

    public ChartResourcePathNormalizationStatus Reason { get; } = reason;
}
