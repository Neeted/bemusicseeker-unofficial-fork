using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartInfoInlineBuildService
{
    // Shared inline chart_info builder for file diff and package install.
    // It consumes short-lived ChartFileSnapshot bytes and never keeps them in long-lived models.
    public const int DefaultBatchSize = 512;

    private readonly ChartInfoBuildService chartInfoBuildService;

    private readonly int parserDegree;

    private readonly int batchSize;

    public ChartInfoInlineBuildService(ChartInfoBuildService chartInfoBuildService, int parserDegree, int? batchSizeOverride = null)
    {
        this.chartInfoBuildService = chartInfoBuildService ?? new ChartInfoBuildService();
        this.parserDegree = Math.Max(1, parserDegree);
        batchSize = Math.Max(1, batchSizeOverride ?? DefaultBatchSize);
    }

    public int BatchSize => batchSize;

    public ChartInfoInlineBuildResult BuildForSnapshots(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<InlineBmsChartSnapshot> bmsCharts,
        IEnumerable<InlineBmsonChartSnapshot> bmsonCharts,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        Action<string> logInstallPerformance = null,
        Action<string> logInstallPerformanceWarn = null)
    {
        ChartInfoInlineBuildResult result = new ChartInfoInlineBuildResult();
        List<InlineChartSnapshotTarget> targets = (bmsCharts ?? Enumerable.Empty<InlineBmsChartSnapshot>())
            .Where((InlineBmsChartSnapshot item) => item?.Snapshot != null && item.File != null)
            .Select((InlineBmsChartSnapshot item) => InlineChartSnapshotTarget.FromBms(item))
            .Concat((bmsonCharts ?? Enumerable.Empty<InlineBmsonChartSnapshot>())
                .Where((InlineBmsonChartSnapshot item) => item?.Snapshot != null && item.Song != null)
                .Select((InlineBmsonChartSnapshot item) => InlineChartSnapshotTarget.FromBmson(item)))
            .ToList();
        foreach (List<InlineChartSnapshotTarget> batch in CreateBatches(targets, batchSize))
        {
            Dictionary<string, LR2SongDBExtended.chart_info> currentRows = LoadCurrentRows(dbGateway, batch.Select((InlineChartSnapshotTarget target) => target.Snapshot));
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<ChartInfoBuildService.InlineChartInfoBuildResult> inlineResults = new List<ChartInfoBuildService.InlineChartInfoBuildResult>(batch.Count);
            foreach (InlineChartSnapshotTarget target in batch)
            {
                inlineResults.Add(chartInfoBuildService.BuildInlineChartInfo(
                    target.Snapshot,
                    target.BmsFile,
                    target.BmsonSong,
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

    public ChartInfoInlineBuildResult BuildForExistingFiles(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs,
        Action<string> logInstallPerformance = null,
        Action<string> logInstallPerformanceWarn = null)
    {
        ChartInfoInlineBuildResult total = new ChartInfoInlineBuildResult();
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures = dbGateway != null
            ? dbGateway.LoadCurrentChartInfoParseFailureMap(chartInfoBuildService.CurrentParseTimeout)
            : new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase);
        List<InlineFileChartTarget> targets = (bmsFiles ?? Enumerable.Empty<BMSFile>())
            .Where((BMSFile file) => file != null && !string.IsNullOrWhiteSpace(file.path))
            .Select((BMSFile file) => InlineFileChartTarget.FromBms(file))
            .Concat((bmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
                .Where((LR2SongDBExtended.bmson_song song) => song != null && !string.IsNullOrWhiteSpace(song.path))
                .Select((LR2SongDBExtended.bmson_song song) => InlineFileChartTarget.FromBmson(song)))
            .ToList();
        foreach (List<InlineFileChartTarget> batch in CreateBatches(targets, batchSize))
        {
            List<InlineBmsChartSnapshot> bmsSnapshots = new List<InlineBmsChartSnapshot>();
            List<InlineBmsonChartSnapshot> bmsonSnapshots = new List<InlineBmsonChartSnapshot>();
            foreach (InlineFileChartTarget target in batch)
            {
                try
                {
                    ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(target.Path);
                    if (target.BmsFile != null)
                    {
                        target.BmsFile.ApplySnapshotDigest(snapshot.Md5, snapshot.Sha256);
                        bmsSnapshots.Add(new InlineBmsChartSnapshot(target.BmsFile, snapshot));
                    }
                    else if (target.BmsonSong != null)
                    {
                        target.BmsonSong.md5 = snapshot.Md5;
                        target.BmsonSong.sha256 = snapshot.Sha256;
                        target.BmsonSong.updated_at = snapshot.LastWriteTimeUtc;
                        bmsonSnapshots.Add(new InlineBmsonChartSnapshot(target.BmsonSong, snapshot));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    total.ReadFailedCount++;
                    logInstallPerformanceWarn?.Invoke("chart_info_inline read_failed path=" + QuoteLogValue(target.Path) + " exception=" + ex.GetType().Name + " message=" + QuoteLogValue(ex.Message));
                }
            }
            ChartInfoInlineBuildResult batchResult = BuildForSnapshots(dbGateway, bmsSnapshots, bmsonSnapshots, currentFailures, logInstallPerformance, logInstallPerformanceWarn);
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

    private static Dictionary<string, LR2SongDBExtended.chart_info> LoadCurrentRows(BmsLibraryDbGateway dbGateway, IEnumerable<ChartFileSnapshot> snapshots)
    {
        if (dbGateway == null)
        {
            return new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        }
        HashSet<string> sha256s = new HashSet<string>(
            (snapshots ?? Enumerable.Empty<ChartFileSnapshot>())
                .Where((ChartFileSnapshot snapshot) => snapshot != null && !string.IsNullOrWhiteSpace(snapshot.Sha256))
                .Select((ChartFileSnapshot snapshot) => snapshot.Sha256),
            StringComparer.OrdinalIgnoreCase);
        return sha256s.Count == 0
            ? new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase)
            : dbGateway.LoadChartInfosBySha256(sha256s);
    }

    private static IEnumerable<List<T>> CreateBatches<T>(IEnumerable<T> source, int batchSize)
    {
        List<T> batch = new List<T>(Math.Max(1, batchSize));
        foreach (T item in source ?? Enumerable.Empty<T>())
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

    private sealed class InlineChartSnapshotTarget
    {
        private InlineChartSnapshotTarget(BMSFile bmsFile, LR2SongDBExtended.bmson_song bmsonSong, ChartFileSnapshot snapshot)
        {
            BmsFile = bmsFile;
            BmsonSong = bmsonSong;
            Snapshot = snapshot;
        }

        public BMSFile BmsFile { get; }

        public LR2SongDBExtended.bmson_song BmsonSong { get; }

        public ChartFileSnapshot Snapshot { get; }

        public static InlineChartSnapshotTarget FromBms(InlineBmsChartSnapshot item)
        {
            return new InlineChartSnapshotTarget(item.File, null, item.Snapshot);
        }

        public static InlineChartSnapshotTarget FromBmson(InlineBmsonChartSnapshot item)
        {
            return new InlineChartSnapshotTarget(null, item.Song, item.Snapshot);
        }
    }

    private sealed class InlineFileChartTarget
    {
        private InlineFileChartTarget(BMSFile bmsFile, LR2SongDBExtended.bmson_song bmsonSong, string path)
        {
            BmsFile = bmsFile;
            BmsonSong = bmsonSong;
            Path = path ?? string.Empty;
        }

        public BMSFile BmsFile { get; }

        public LR2SongDBExtended.bmson_song BmsonSong { get; }

        public string Path { get; }

        public static InlineFileChartTarget FromBms(BMSFile file)
        {
            return new InlineFileChartTarget(file, null, file?.path);
        }

        public static InlineFileChartTarget FromBmson(LR2SongDBExtended.bmson_song song)
        {
            return new InlineFileChartTarget(null, song, song?.path);
        }
    }
}

internal sealed class InlineBmsChartSnapshot
{
    public InlineBmsChartSnapshot(BMSFile file, ChartFileSnapshot snapshot)
    {
        File = file;
        Snapshot = snapshot;
    }

    public BMSFile File { get; }

    public ChartFileSnapshot Snapshot { get; }
}

internal sealed class InlineBmsonChartSnapshot
{
    public InlineBmsonChartSnapshot(LR2SongDBExtended.bmson_song song, ChartFileSnapshot snapshot)
    {
        Song = song;
        Snapshot = snapshot;
    }

    public LR2SongDBExtended.bmson_song Song { get; }

    public ChartFileSnapshot Snapshot { get; }
}
