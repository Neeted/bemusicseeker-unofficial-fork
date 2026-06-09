using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoInlineBuildService(
    ChartInfoBuildService chartInfoBuildService,
    int parserDegree,
    int? batchSizeOverride = null,
    IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentRowsBySha256 = null)
{
    // Shared inline chart_info builder for file diff and package install.
    // It consumes short-lived ChartFileSnapshot bytes and never keeps them in long-lived models.
    public const int DefaultBatchSize = 512;

    private readonly ChartInfoBuildService chartInfoBuildService = chartInfoBuildService ?? new ChartInfoBuildService();

    private readonly int parserDegree = Math.Max(1, parserDegree);

    private readonly int batchSize = Math.Max(1, batchSizeOverride ?? DefaultBatchSize);

    private readonly IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> preloadedCurrentRowsBySha256 = currentRowsBySha256;

    public int BatchSize => batchSize;

    public ChartInfoInlineBuildResult BuildForSnapshots(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<InlineChartSnapshotTarget> charts,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        Action<string> logInstallPerformance = null,
        Action<string> logInstallPerformanceWarn = null)
    {
        var result = new ChartInfoInlineBuildResult();
        List<InlineChartSnapshotTarget> targets = [.. (charts ?? []).Where(item => item?.Snapshot != null && item.Chart != null)];
        foreach (List<InlineChartSnapshotTarget> batch in CreateBatches(targets, batchSize))
        {
            Dictionary<string, LR2SongDBExtended.chart_info> currentRows = LoadCurrentRows(dbGateway, batch.Select(target => target.Snapshot));
            var stopwatch = Stopwatch.StartNew();
            var inlineResults = new List<ChartInfoBuildService.InlineChartInfoBuildResult>(batch.Count);
            foreach (InlineChartSnapshotTarget target in batch)
            {
                inlineResults.Add(chartInfoBuildService.BuildInlineChartInfo(
                    target.Snapshot,
                    target.Chart,
                    currentRows,
                    currentFailures,
                    logInstallPerformance,
                    logInstallPerformanceWarn));
            }
            stopwatch.Stop();
            result.ParseMs += stopwatch.ElapsedMilliseconds;
            result.TargetCount += batch.Count;
            foreach (ChartInfoBuildService.InlineChartInfoBuildResult inlineResult in inlineResults)
            {
                ApplyResult(result, inlineResult);
            }
        }
        return result;
    }

    public ChartInfoInlineBuildResult BuildForExistingCharts(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<ChartFile> charts,
        Action<string> logInstallPerformance = null,
        Action<string> logInstallPerformanceWarn = null)
    {
        var total = new ChartInfoInlineBuildResult();
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures = dbGateway != null
            ? dbGateway.LoadCurrentChartInfoParseFailureMap(chartInfoBuildService.CurrentParseTimeout)
            : new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase);
        List<ChartFile> targets = [.. (charts ?? []).Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.Path))];
        foreach (List<ChartFile> batch in CreateBatches(targets, batchSize))
        {
            List<InlineChartSnapshotTarget> snapshots = [];
            foreach (ChartFile target in batch)
            {
                try
                {
                    ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(target.Path);
                    if (ChartStorageOwnerMutator.ApplySnapshotDigest(target, snapshot, total.DigestChanges))
                    {
                        snapshots.Add(InlineChartSnapshotTarget.FromChart(target, snapshot));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    total.ReadFailedCount++;
                    logInstallPerformanceWarn?.Invoke("chart_info_inline read_failed path=" + QuoteLogValue(target.Path) + " exception=" + ex.GetType().Name + " message=" + QuoteLogValue(ex.Message));
                }
            }
            ChartInfoInlineBuildResult batchResult = BuildForSnapshots(dbGateway, snapshots, currentFailures, logInstallPerformance, logInstallPerformanceWarn);
            Add(total, batchResult);
        }
        return total;
    }

    internal static void ApplyResult(ChartInfoInlineBuildResult result, ChartInfoBuildService.InlineChartInfoBuildResult inlineResult)
    {
        if (result == null || inlineResult == null)
        {
            return;
        }
        if (inlineResult.Row != null)
        {
            result.AppliedRows.Add(inlineResult.Row);
        }
        if (inlineResult.ShouldPersistRow && inlineResult.Row != null)
        {
            result.ChartInfoRows.Add(inlineResult.Row);
            result.SuccessCount++;
        }
        if (inlineResult.CurrentRowSkipped)
        {
            result.CurrentSkippedCount++;
        }
        if (inlineResult.SkippedPersistedFailure)
        {
            result.FailureSkippedCount++;
        }
        if (inlineResult.ParseFailed)
        {
            result.ParseFailedCount++;
        }
        if (inlineResult.ParseFailureRow != null)
        {
            result.ParseFailureRows.Add(inlineResult.ParseFailureRow);
            result.FailurePersistedCount++;
        }
        if (!string.IsNullOrWhiteSpace(inlineResult.ParseFailureDeleteMd5)
            && !result.ParseFailureDeleteMd5s.Contains(inlineResult.ParseFailureDeleteMd5, StringComparer.OrdinalIgnoreCase))
        {
            result.ParseFailureDeleteMd5s.Add(inlineResult.ParseFailureDeleteMd5);
            result.FailureClearedCount++;
        }
    }

    private static void Add(ChartInfoInlineBuildResult total, ChartInfoInlineBuildResult source)
    {
        if (total == null || source == null)
        {
            return;
        }
        total.ChartInfoRows.AddRange(source.ChartInfoRows);
        total.DigestChanges.AddRange(source.DigestChanges);
        total.AppliedRows.AddRange(source.AppliedRows);
        total.ParseFailureRows.AddRange(source.ParseFailureRows);
        foreach (string md5 in source.ParseFailureDeleteMd5s)
        {
            if (!total.ParseFailureDeleteMd5s.Contains(md5, StringComparer.OrdinalIgnoreCase))
            {
                total.ParseFailureDeleteMd5s.Add(md5);
            }
        }
        total.TargetCount += source.TargetCount;
        total.SuccessCount += source.SuccessCount;
        total.CurrentSkippedCount += source.CurrentSkippedCount;
        total.FailureSkippedCount += source.FailureSkippedCount;
        total.ParseFailedCount += source.ParseFailedCount;
        total.FailurePersistedCount += source.FailurePersistedCount;
        total.FailureClearedCount += source.FailureClearedCount;
        total.ReadFailedCount += source.ReadFailedCount;
        total.ParseMs += source.ParseMs;
    }

    private Dictionary<string, LR2SongDBExtended.chart_info> LoadCurrentRows(BmsLibraryDbGateway dbGateway, IEnumerable<ChartFileSnapshot> snapshots)
    {
        var sha256s = new HashSet<string>(
            (snapshots ?? [])
                .Where(snapshot => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Sha256))
                .Select(snapshot => snapshot.Sha256),
            StringComparer.OrdinalIgnoreCase);
        if (sha256s.Count == 0)
        {
            return new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        }
        if (preloadedCurrentRowsBySha256 != null)
        {
            var rows = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
            foreach (string sha256 in sha256s)
            {
                if (preloadedCurrentRowsBySha256.TryGetValue(sha256, out LR2SongDBExtended.chart_info row)
                    && row != null)
                {
                    rows[sha256] = row;
                }
            }
            return rows;
        }
        return dbGateway == null
            ? new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase)
            : dbGateway.LoadChartInfosBySha256(sha256s);
    }

    private static IEnumerable<List<T>> CreateBatches<T>(IEnumerable<T> source, int batchSize)
    {
        var batch = new List<T>(Math.Max(1, batchSize));
        foreach (T item in source ?? [])
        {
            batch.Add(item);
            if (batch.Count >= batchSize)
            {
                yield return batch;
                batch = new List<T>(Math.Max(1, batchSize));
            }
        }
        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static string QuoteLogValue(string value)
    {
        if (value == null)
        {
            return "\"\"";
        }
        string escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", " ")
            .Replace("\n", " ");
        return "\"" + escaped + "\"";
    }

}

internal sealed class InlineChartSnapshotTarget
{
    private InlineChartSnapshotTarget(ChartFile chart, ChartFileSnapshot snapshot)
    {
        Chart = chart;
        Snapshot = snapshot;
    }

    public ChartFile Chart { get; }

    public ChartFileSnapshot Snapshot { get; }

    public static InlineChartSnapshotTarget FromBmsFile(BMSFile file, ChartFileSnapshot snapshot)
    {
        return FromChart(ChartFileProjection.FromBmsFile(
            file,
            includeWarningSnapshot: false,
            includeResourceReferences: false), snapshot);
    }

    public static InlineChartSnapshotTarget FromBmsonSong(LR2SongDBExtended.bmson_song song, ChartFileSnapshot snapshot)
    {
        return FromChart(ChartFileProjection.FromBmsonSong(
            song,
            includeWarningSnapshot: false,
            includeResourceReferences: false), snapshot);
    }

    public static InlineChartSnapshotTarget FromChart(ChartFile chart, ChartFileSnapshot snapshot)
    {
        return chart == null || snapshot == null ? null : new InlineChartSnapshotTarget(chart, snapshot);
    }
}
