using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable chart-info persistence facts submitted to the catalog mutation owner.
/// </summary>
internal sealed class CatalogChartInfoWriteRequest
{
    internal CatalogChartInfoWriteRequest(
        IEnumerable<ChartDigestBackfillEntry> digestEntries = null,
        IEnumerable<LR2SongDBExtended.chart_info> chartInfoRows = null,
        IEnumerable<LR2SongDBExtended.chart_info_parse_failure> parseFailureRows = null,
        IEnumerable<string> parseFailureDeleteMd5s = null)
    {
        DigestEntries = Array.AsReadOnly([.. (digestEntries ?? [])
            .Where(entry => entry != null
                && !string.IsNullOrWhiteSpace(entry.Md5)
                && !string.IsNullOrWhiteSpace(entry.Sha256))
            .Select(entry => new ChartDigestBackfillEntry(entry.Md5, entry.Sha256))]);
        ChartInfoRows = Array.AsReadOnly([.. (chartInfoRows ?? [])
            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.sha256))
            .Select(CreateChartInfoCopy)]);
        ParseFailureRows = Array.AsReadOnly([.. (parseFailureRows ?? [])
            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.md5))
            .Select(CreateParseFailureCopy)]);
        ParseFailureDeleteMd5s = Array.AsReadOnly([.. (parseFailureDeleteMd5s ?? [])
            .Where(md5 => !string.IsNullOrWhiteSpace(md5))
            .Select(md5 => md5.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    internal IReadOnlyList<ChartDigestBackfillEntry> DigestEntries { get; }

    internal IReadOnlyList<LR2SongDBExtended.chart_info> ChartInfoRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.chart_info_parse_failure> ParseFailureRows { get; }

    internal IReadOnlyList<string> ParseFailureDeleteMd5s { get; }

    internal bool HasChanges => DigestEntries.Count > 0
        || ChartInfoRows.Count > 0
        || ParseFailureRows.Count > 0
        || ParseFailureDeleteMd5s.Count > 0;

    private static LR2SongDBExtended.chart_info CreateChartInfoCopy(LR2SongDBExtended.chart_info source)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = source.sha256,
            md5 = source.md5,
            charthash = source.charthash,
            level = source.level,
            difficulty = source.difficulty,
            difficulty_defined = source.difficulty_defined,
            mainbpm = source.mainbpm,
            maxbpm = source.maxbpm,
            minbpm = source.minbpm,
            length = source.length,
            mode = source.mode,
            judge = source.judge,
            bga = source.bga,
            exlevel = source.exlevel,
            feature = source.feature,
            notes = source.notes,
            n = source.n,
            ln = source.ln,
            s = source.s,
            ls = source.ls,
            total = source.total,
            total_defined = source.total_defined,
            density = source.density,
            peakdensity = source.peakdensity,
            enddensity = source.enddensity,
            distribution = source.distribution,
            speedchange = source.speedchange,
            speedchange_count = source.speedchange_count,
            lanenotes = source.lanenotes,
            parser_version = source.parser_version,
            updated_at = source.updated_at
        };
    }

    private static LR2SongDBExtended.chart_info_parse_failure CreateParseFailureCopy(
        LR2SongDBExtended.chart_info_parse_failure source)
    {
        return new LR2SongDBExtended.chart_info_parse_failure
        {
            md5 = source.md5,
            sha256 = source.sha256,
            path = source.path,
            parser_version = source.parser_version,
            failure_kind = source.failure_kind,
            exception_type = source.exception_type,
            message = source.message,
            parse_timeout_ms = source.parse_timeout_ms,
            updated_at = source.updated_at
        };
    }
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
/// Immutable chart-info storage request. Inline BMS/BMSON rows, full-backfill narrow song
/// projections, and chart-info facts are committed by one catalog mutation command.
/// </summary>
internal sealed class CatalogChartInfoStorageWriteRequest
{
    internal CatalogChartInfoStorageWriteRequest(
        IEnumerable<BMSFile> bmsRows,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonRows,
        CatalogChartInfoWriteRequest chartInfo,
        IEnumerable<Lr2ChartInfoSongProjection> chartInfoSongProjections = null)
    {
        BmsRows = Array.AsReadOnly([.. (bmsRows ?? [])
            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.path))
            .Select(row => row.CreateSongRowPersistenceCopy())]);
        BmsonRows = Array.AsReadOnly([.. (bmsonRows ?? [])
            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.path))
            .Select(CatalogMaintenanceWriteRequest.CreateBmsonPersistenceCopy)]);
        ChartInfo = chartInfo ?? new CatalogChartInfoWriteRequest();
        ChartInfoSongProjections = Array.AsReadOnly([.. (chartInfoSongProjections ?? [])
            .Where(projection => projection != null)
            .GroupBy(projection => projection.Identity)
            .Select(group => group.Last())]);
    }

    internal IReadOnlyList<BMSFile> BmsRows { get; }

    internal IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonRows { get; }

    internal CatalogChartInfoWriteRequest ChartInfo { get; }

    internal IReadOnlyList<Lr2ChartInfoSongProjection> ChartInfoSongProjections { get; }

    internal bool HasChanges => BmsRows.Count > 0
        || BmsonRows.Count > 0
        || ChartInfoSongProjections.Count > 0
        || ChartInfo.HasChanges;
}

/// <summary>
/// Reports the durable rows and narrow projections applied by one chart-info storage transaction.
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
