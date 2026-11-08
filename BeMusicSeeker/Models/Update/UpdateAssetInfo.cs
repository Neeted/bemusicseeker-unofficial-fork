namespace BeMusicSeeker.Models.Update;

/// <summary>update.json の資産のうち、取得・検証と表示に使う値を保持します。</summary>
internal sealed class UpdateAssetInfo
{
    public string FileName { get; set; }

    public string Url { get; set; }

    public string Sha256 { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>ZIP がメタデータを同梱するかを表し、更新ダイアログの名前と説明を決めます。</summary>
    public bool IncludesChartInfoMetadata { get; set; }
}
