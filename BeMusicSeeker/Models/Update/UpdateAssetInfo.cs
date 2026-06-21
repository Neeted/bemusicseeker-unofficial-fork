namespace BeMusicSeeker.Models.Update;

internal sealed class UpdateAssetInfo
{
    public string Kind { get; set; }

    public string Label { get; set; }

    public string FileName { get; set; }

    public string Url { get; set; }

    public string Sha256 { get; set; }

    public long SizeBytes { get; set; }

    public bool IncludesChartInfoMetadata { get; set; }
}
