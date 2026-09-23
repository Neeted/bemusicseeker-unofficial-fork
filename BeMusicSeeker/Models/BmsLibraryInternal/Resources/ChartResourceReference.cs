namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal readonly struct ChartResourceReference(
    ChartResourceKind kind,
    string rawPath,
    string normalizedPath)
{
    public ChartResourceKind Kind { get; } = kind;

    public string RawPath { get; } = rawPath ?? string.Empty;

    public string NormalizedPath { get; } = normalizedPath ?? string.Empty;
}
