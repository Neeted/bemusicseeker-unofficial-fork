using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 詳細情報・失敗・ハッシュ対応の不変な保存変更事実です。DB行への変換は保存境界で行います。
/// </summary>
internal sealed class CatalogChartInfoWriteRequest
{
    internal CatalogChartInfoWriteRequest(
        IEnumerable<ChartDigestBackfillEntry> digestEntries = null,
        IEnumerable<BeMusicSeeker.Models.ChartDetails> chartInfoRows = null,
        IEnumerable<BeMusicSeeker.Models.ChartParseFailure> parseFailureRows = null,
        IEnumerable<string> parseFailureDeleteMd5s = null)
    {
        DigestEntries = Array.AsReadOnly([.. (digestEntries ?? [])
            .Where(entry => entry != null
                && !string.IsNullOrWhiteSpace(entry.Md5)
                && !string.IsNullOrWhiteSpace(entry.Sha256))
            .Select(entry => new ChartDigestBackfillEntry(entry.Md5, entry.Sha256))]);
        ChartInfoRows = Array.AsReadOnly([.. (chartInfoRows ?? [])
            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.sha256))]);
        ParseFailureRows = Array.AsReadOnly([.. (parseFailureRows ?? [])
            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.md5))]);
        ParseFailureDeleteMd5s = Array.AsReadOnly([.. (parseFailureDeleteMd5s ?? [])
            .Where(md5 => !string.IsNullOrWhiteSpace(md5))
            .Select(md5 => md5.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    internal IReadOnlyList<ChartDigestBackfillEntry> DigestEntries { get; }

    internal IReadOnlyList<BeMusicSeeker.Models.ChartDetails> ChartInfoRows { get; }

    internal IReadOnlyList<BeMusicSeeker.Models.ChartParseFailure> ParseFailureRows { get; }

    internal IReadOnlyList<string> ParseFailureDeleteMd5s { get; }

    internal bool HasChanges => DigestEntries.Count > 0
        || ChartInfoRows.Count > 0
        || ParseFailureRows.Count > 0
        || ParseFailureDeleteMd5s.Count > 0;


}

internal sealed class CatalogChartInfoWriteReceipt
{
    internal static CatalogChartInfoWriteReceipt NotApplied { get; } =
        new(false, 0, 0, 0, 0);

    internal CatalogChartInfoWriteReceipt(
        bool applied,
        int digestEntryCount,
        int chartInfoRowCount,
        int parseFailureRowCount,
        int parseFailureDeleteCount)
    {
        Applied = applied;
        DigestEntryCount = digestEntryCount;
        ChartInfoRowCount = chartInfoRowCount;
        ParseFailureRowCount = parseFailureRowCount;
        ParseFailureDeleteCount = parseFailureDeleteCount;
    }

    internal bool Applied { get; }

    internal int DigestEntryCount { get; }

    internal int ChartInfoRowCount { get; }

    internal int ParseFailureRowCount { get; }

    internal int ParseFailureDeleteCount { get; }
}

/// <summary>
/// 共通基本値、既存song行だけへの限定投影、詳細情報を一つの保存コマンドで確定する不変要求です。
/// </summary>
internal sealed class CatalogChartInfoStorageWriteRequest
{
    /// <summary>DB確定前に、共通の保存変更事実と既存行だけへの限定投影を固定します。</summary>
    internal CatalogChartInfoStorageWriteRequest(
        IEnumerable<ChartFile> charts,
        CatalogChartInfoWriteRequest chartInfo,
        IEnumerable<Lr2ChartInfoSongProjection> chartInfoSongProjections = null)
    {
        Charts = Array.AsReadOnly([.. (charts ?? [])
            .Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))]);
        ChartInfo = chartInfo ?? new CatalogChartInfoWriteRequest();
        ChartInfoSongProjections = Array.AsReadOnly([.. (chartInfoSongProjections ?? [])
            .Where(projection => projection != null)
            .GroupBy(projection => projection.Identity)
            .Select(group => group.Last())]);
    }

    /// <summary>生成列を書き込む共通の譜面値です。</summary>
    internal IReadOnlyList<ChartFile> Charts { get; }

    internal CatalogChartInfoWriteRequest ChartInfo { get; }

    internal IReadOnlyList<Lr2ChartInfoSongProjection> ChartInfoSongProjections { get; }

    internal bool HasChanges => Charts.Count > 0
        || ChartInfoSongProjections.Count > 0
        || ChartInfo.HasChanges;
}

/// <summary>
/// 一つの詳細保存トランザクションで永続確定した件数と限定投影の結果です。
/// </summary>
internal sealed class CatalogChartInfoStorageWriteReceipt
{
    internal static CatalogChartInfoStorageWriteReceipt NotApplied { get; } =
        new(false, 0, 0, CatalogChartInfoWriteReceipt.NotApplied, Lr2ChartInfoSongProjectionWriteResult.Empty);

    internal CatalogChartInfoStorageWriteReceipt(
        bool applied,
        int bmsRowCount,
        int bmsonRowCount,
        CatalogChartInfoWriteReceipt chartInfo,
        Lr2ChartInfoSongProjectionWriteResult chartInfoSongProjections = null)
    {
        Applied = applied;
        BmsRowCount = bmsRowCount;
        BmsonRowCount = bmsonRowCount;
        ChartInfo = chartInfo ?? CatalogChartInfoWriteReceipt.NotApplied;
        ChartInfoSongProjections = chartInfoSongProjections ?? Lr2ChartInfoSongProjectionWriteResult.Empty;
    }

    internal bool Applied { get; }

    internal int BmsRowCount { get; }

    internal int BmsonRowCount { get; }

    internal CatalogChartInfoWriteReceipt ChartInfo { get; }

    internal Lr2ChartInfoSongProjectionWriteResult ChartInfoSongProjections { get; }
}
