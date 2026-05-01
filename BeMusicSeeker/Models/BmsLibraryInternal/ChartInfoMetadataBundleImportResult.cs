namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoMetadataBundleImportResult
{
    public bool Skipped { get; set; }

    public string SkipReason { get; set; }

    public string BundleId { get; set; }

    public string BundleSha256 { get; set; }

    public int SourceChartInfoCount { get; set; }

    public int SourceDigestCount { get; set; }

    public int ImportedChartInfoCount { get; set; }

    public int ImportedDigestCount { get; set; }

    public int FailureClearedCount { get; set; }

    public long ElapsedMs { get; set; }
}
