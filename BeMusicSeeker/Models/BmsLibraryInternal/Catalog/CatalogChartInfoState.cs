using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoBackfillRequest
{
    private ChartInfoBackfillRequest(string reason)
    {
        Reason = reason ?? "unknown";
    }

    public string Reason { get; }

    public static ChartInfoBackfillRequest Full(string reason)
    {
        return new ChartInfoBackfillRequest(reason);
    }
}
internal sealed class ChartInfoHydrationResult
{
    public int TotalRows { get; set; }

    public int ChartInfoRows { get; set; }

    public int AppliedBmsCount { get; set; }

    public int AppliedBmsonCount { get; set; }

    public int OwnerApplyUpdatedCount { get; set; }

    public int OwnerApplySkippedCount { get; set; }

    public int OwnerApplySilentCount { get; set; }

    public int OwnerApplyNotifiedCount { get; set; }

    public int OwnerCount { get; set; }

    public int CurrentChartInfoOwnerCount { get; set; }

    public int CurrentParseFailureOwnerCount { get; set; }

    public int BackfillCandidateOwnerCount { get; set; }

    public long LoadMs { get; set; }

    public long ApplyMs { get; set; }

    public long DbLoadMs { get; set; }

    public long DbMaterializeMs { get; set; }

    public string DbMaterializeMode { get; set; }

    public int DbRawRows { get; set; }

    public long DbRawReadMs { get; set; }

    public long DbRawObjectMs { get; set; }

    public bool DbReadOnly { get; set; }

    public long DbLockWaitMs { get; set; }

    public int ParseFailureRows { get; set; }

    public long IndexBuildMs { get; set; }

    public long OwnerApplyMs { get; set; }

    public long TotalMs { get; set; }

    public bool Succeeded { get; set; }
}

internal sealed class ChartInfoHydrationAllCurrentSnapshot
{
    public int OwnedCollectionVersion { get; set; }

    public int BmsRowsVersion { get; set; }

    public int BmsonRowsVersion { get; set; }

    public int OwnerCount { get; set; }

    public int CurrentChartInfoOwnerCount { get; set; }

    public int CurrentParseFailureOwnerCount { get; set; }

    public int ParserVersion { get; set; }

    public long ParseTimeoutMs { get; set; }
}

internal sealed class ChartInfoOwnerVersionSnapshot
{
    public int OwnedCollectionVersion { get; set; }

    public int BmsRowsVersion { get; set; }

    public int BmsonRowsVersion { get; set; }

    public int BmsOwnerCount { get; set; }

    public int BmsonOwnerCount { get; set; }

    public int OwnerCount => BmsOwnerCount + BmsonOwnerCount;
}

internal sealed class ChartInfoIndexUpdateResult
{
    public int InputRows { get; set; }

    public int BySha256Count { get; set; }

    public int ByMd5Count { get; set; }

    public int Version { get; set; }

    public bool HydrationChanged { get; set; }
}

internal enum CatalogChartInfoOwnerEventKind
{
    WarningPresentationChanged,
    StartupMemoryCheckpoint,
    IndexChanged
}

/// <summary>
/// Immutable presentation facts emitted by the chart-info owner. Composition
/// decides how catalog-wide projections and UI notifications consume them.
/// </summary>
internal sealed class CatalogChartInfoOwnerEvent
{
    private CatalogChartInfoOwnerEvent(
        CatalogChartInfoOwnerEventKind kind,
        string reason,
        string checkpointStage,
        string checkpointStatus)
    {
        Kind = kind;
        Reason = reason ?? string.Empty;
        CheckpointStage = checkpointStage ?? string.Empty;
        CheckpointStatus = checkpointStatus ?? string.Empty;
    }

    internal CatalogChartInfoOwnerEventKind Kind { get; }

    internal string Reason { get; }

    internal string CheckpointStage { get; }

    internal string CheckpointStatus { get; }

    internal static CatalogChartInfoOwnerEvent Warning(string reason)
    {
        return new(
            CatalogChartInfoOwnerEventKind.WarningPresentationChanged,
            reason,
            null,
            null);
    }

    internal static CatalogChartInfoOwnerEvent Checkpoint(string stage, string status)
    {
        return new(
            CatalogChartInfoOwnerEventKind.StartupMemoryCheckpoint,
            null,
            stage,
            status);
    }

    internal static CatalogChartInfoOwnerEvent IndexChanged(string reason)
    {
        return new(
            CatalogChartInfoOwnerEventKind.IndexChanged,
            reason,
            null,
            null);
    }
}
