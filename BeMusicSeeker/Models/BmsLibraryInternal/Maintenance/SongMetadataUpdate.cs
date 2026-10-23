namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>同じ完全一致パス・同一MD5の既存songへ反映する生メタデータと譜面更新時刻だけを保持します。</summary>
internal sealed record SongMetadataUpdate(
    string Path, string Hash, string RawTitle, string RawSubtitle, string RawArtist,
    string RawSubartist, string RawGenre, int? Date)
{
    /// <summary>再復号済みの共通値から限定保存値を捕捉します。行の新設や生成列の再評価は要求しません。</summary>
    internal static SongMetadataUpdate FromChart(ChartFile chart) => new(
        chart.Path, chart.Md5, chart.RawTitle, chart.RawSubtitle, chart.RawArtist, chart.Subartist, chart.Genre, chart.Date);
}
