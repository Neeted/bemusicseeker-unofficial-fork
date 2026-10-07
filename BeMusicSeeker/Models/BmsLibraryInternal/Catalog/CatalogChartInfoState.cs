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



    public int OwnerCount { get; set; }

    public int CurrentChartInfoOwnerCount { get; set; }

    public int CurrentParseFailureOwnerCount { get; set; }

    public int ParserVersion { get; set; }

    public long ParseTimeoutMs { get; set; }
}

internal sealed class ChartInfoOwnerVersionSnapshot
{
    public int OwnedCollectionVersion { get; set; }



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

    /// <summary>確定した基本値を同じ所持識別へ適用した対象です。</summary>
    internal IReadOnlyList<BeMusicSeeker.Models.ChartFile> ChangedCharts { get; set; } = [];

    /// <summary>確定した詳細値が変わったMD5です。</summary>
    internal IReadOnlyList<string> ChangedMd5s { get; set; } = [];

    /// <summary>確定した詳細値が変わったSHA-256です。</summary>
    internal IReadOnlyList<string> ChangedSha256s { get; set; } = [];

    public bool HydrationChanged { get; set; }
}

internal enum CatalogChartInfoOwnerEventKind
{
    WarningPresentationChanged,
    StartupMemoryCheckpoint,
    IndexChanged
}

/// <summary>
/// 詳細所有者から出力する不変の通知事実です。変更ハッシュ・共通値を既存の表示通知へ接続します。
/// </summary>
internal sealed class CatalogChartInfoOwnerEvent
{
    private CatalogChartInfoOwnerEvent(
        CatalogChartInfoOwnerEventKind kind,
        string reason,
        string checkpointStage,
        string checkpointStatus,
        int indexVersion = 0, IReadOnlyList<string> changedMd5s = null, IReadOnlyList<string> changedSha256s = null, IReadOnlyList<BeMusicSeeker.Models.ChartFile> changedCharts = null)
    {
        Kind = kind;
        Reason = reason ?? string.Empty;
        CheckpointStage = checkpointStage ?? string.Empty;
        CheckpointStatus = checkpointStatus ?? string.Empty;
        IndexVersion = indexVersion; ChangedMd5s = changedMd5s ?? []; ChangedSha256s = changedSha256s ?? []; ChangedCharts = changedCharts ?? [];
    }

    internal CatalogChartInfoOwnerEventKind Kind { get; }

    internal string Reason { get; }

    internal string CheckpointStage { get; }

    internal string CheckpointStatus { get; }

    /// <summary>詳細索引の確定版と変更キーです。集合識別には使用しません。</summary>
    internal int IndexVersion { get; }
    internal IReadOnlyList<BeMusicSeeker.Models.ChartFile> ChangedCharts { get; }
    internal IReadOnlyList<string> ChangedMd5s { get; }
    internal IReadOnlyList<string> ChangedSha256s { get; }

    internal static CatalogChartInfoOwnerEvent Warning(string reason, IReadOnlyList<BeMusicSeeker.Models.ChartFile> charts = null, IReadOnlyList<string> md5s = null)
    {
        return new(
            CatalogChartInfoOwnerEventKind.WarningPresentationChanged,
            reason,
            null,
            null, changedMd5s: md5s, changedCharts: charts);
    }

    internal static CatalogChartInfoOwnerEvent Checkpoint(string stage, string status)
    {
        return new(
            CatalogChartInfoOwnerEventKind.StartupMemoryCheckpoint,
            null,
            stage,
            status);
    }

    internal static CatalogChartInfoOwnerEvent IndexChanged(string reason, ChartInfoIndexUpdateResult result)
    {
        return new(
            CatalogChartInfoOwnerEventKind.IndexChanged,
            reason,
            null,
            null, result.Version, result.ChangedMd5s, result.ChangedSha256s, result.ChangedCharts);
    }
}
