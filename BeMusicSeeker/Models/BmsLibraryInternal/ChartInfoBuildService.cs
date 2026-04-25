using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 現在所持している譜面から chart_info と不足している chart_digest_map を構築します。
/// 行の削除は担わず、未所持譜面のメタデータを残せるようにしています。
/// </summary>
internal sealed class ChartInfoBuildService
{
    private readonly Func<string, byte[]> readAllBytes;

    private readonly int? workerCountOverride;

    public ChartInfoBuildService()
        : this(File.ReadAllBytes, null)
    {
    }

    internal ChartInfoBuildService(Func<string, byte[]> readAllBytes, int? workerCountOverride = null)
    {
        this.readAllBytes = readAllBytes ?? File.ReadAllBytes;
        this.workerCountOverride = workerCountOverride;
    }

    /// <summary>
    /// 不足または古い chart_info 行と、不足している BMS SHA-256 digest を構築します。
    /// ファイル読み取りは単一 reader で行い、読み取った byte[] を worker が並列解析します。
    /// </summary>
    /// <param name="dbGateway">song.db へのアクセス手段。</param>
    /// <param name="currentFiles">現在所持している BMS 譜面。</param>
    /// <param name="currentBmsonSongs">現在所持している bmson 譜面。</param>
    /// <param name="reportProgress">進捗通知 callback。total, processed, currentPath を渡します。</param>
    /// <param name="logInstallPerformance">性能ログ callback。</param>
    /// <param name="logInstallPerformanceWarn">解析を継続できない譜面を逐次 WARN 出力する callback。</param>
    /// <returns>構築結果。</returns>
    public ChartInfoBackfillResult BackfillChartInfos(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<BMSFile> currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs,
        Action<int, int, string> reportProgress = null,
        Action<string> logInstallPerformance = null,
        Action<string> logInstallPerformanceWarn = null)
    {
        ChartInfoBackfillResult result = new ChartInfoBackfillResult();
        if (dbGateway == null)
        {
            return result;
        }
        Stopwatch stopwatchTotal = Stopwatch.StartNew();
        Dictionary<string, LR2SongDBExtended.chart_info> existingRows = dbGateway.LoadChartInfoMap();
        List<ChartInfoBuildTarget> targets = BuildTargets(currentFiles, currentBmsonSongs, existingRows, result);
        result.TargetCount = targets.Count;
        result.WorkerCount = ResolveWorkerCount();
        result.QueueCapacity = Math.Max(1, result.WorkerCount * 2);
        reportProgress?.Invoke(result.TargetCount, 0, string.Empty);
        if (targets.Count == 0)
        {
            stopwatchTotal.Stop();
            result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
            logInstallPerformance?.Invoke(BuildLogMessage(result));
            return result;
        }

        BlockingCollection<QueuedChartBytes> queue = new BlockingCollection<QueuedChartBytes>(result.QueueCapacity);
        ConcurrentQueue<ChartInfoBuildItemResult> itemResults = new ConcurrentQueue<ChartInfoBuildItemResult>();
        long readTicks = 0L;
        long parseTicks = 0L;
        int processedCount = 0;

        List<Task> workers = Enumerable.Range(0, result.WorkerCount)
            .Select(_ => Task.Run(delegate
            {
                foreach (QueuedChartBytes item in queue.GetConsumingEnumerable())
                {
                    Stopwatch parseStopwatch = Stopwatch.StartNew();
                    ChartInfoBuildItemResult itemResult = ParseQueuedItem(item, existingRows, logInstallPerformanceWarn);
                    parseStopwatch.Stop();
                    Interlocked.Add(ref parseTicks, parseStopwatch.ElapsedTicks);
                    itemResults.Enqueue(itemResult);
                    int processed = Interlocked.Increment(ref processedCount);
                    reportProgress?.Invoke(result.TargetCount, processed, item.Target.Path);
                }
            }))
            .ToList();

        foreach (ChartInfoBuildTarget target in targets)
        {
            try
            {
                reportProgress?.Invoke(result.TargetCount, Volatile.Read(ref processedCount), target.Path);
                Stopwatch readStopwatch = Stopwatch.StartNew();
                byte[] bytes = readAllBytes(target.Path);
                readStopwatch.Stop();
                Interlocked.Add(ref readTicks, readStopwatch.ElapsedTicks);
                queue.Add(new QueuedChartBytes(target, bytes));
            }
            catch (Exception ex)
            {
                result.ReadFailedCount++;
                result.FailedCount++;
                if (target.NeedsDigest)
                {
                    result.DigestFailedCount += target.MissingDigestOwnerCount;
                }
                result.FailedPaths.Add(target.Path);
                logInstallPerformanceWarn?.Invoke(BuildReadFailureLogMessage(target, ex));
                int processed = Interlocked.Increment(ref processedCount);
                reportProgress?.Invoke(result.TargetCount, processed, target.Path);
            }
        }
        queue.CompleteAdding();
        Task.WaitAll(workers.ToArray());

        result.ProcessedCount = processedCount;
        result.ReadMs = TicksToMilliseconds(readTicks);
        result.ParseMs = TicksToMilliseconds(parseTicks);
        result.ComputeMs = result.ReadMs + result.ParseMs;

        List<LR2SongDBExtended.chart_info> completedRows = new List<LR2SongDBExtended.chart_info>();
        List<BMSFile> completedDigestFiles = new List<BMSFile>();
        foreach (ChartInfoBuildItemResult itemResult in itemResults)
        {
            if (itemResult.DigestSucceeded)
            {
                result.DigestBackfilledCount += itemResult.Target.ApplyDigest(itemResult.Sha256, completedDigestFiles);
            }
            else if (itemResult.Target.NeedsDigest)
            {
                result.DigestFailedCount += itemResult.Target.MissingDigestOwnerCount;
            }
            if (itemResult.Row != null)
            {
                itemResult.Target.ApplyChartInfo(itemResult.Row);
                if (!itemResult.ReusedExistingRow)
                {
                    completedRows.Add(itemResult.Row);
                    result.BackfilledCount++;
                }
            }
            if (itemResult.ParseFailed)
            {
                result.ParseFailedCount++;
                result.FailedCount++;
                result.FailedPaths.Add(itemResult.Target.Path);
            }
        }

        Stopwatch stopwatchDbCommit = Stopwatch.StartNew();
        if (completedDigestFiles.Count > 0)
        {
            dbGateway.UpsertChartDigests(completedDigestFiles);
        }
        if (completedRows.Count > 0)
        {
            dbGateway.UpsertChartInfos(completedRows);
        }
        stopwatchDbCommit.Stop();
        result.DbCommitMs = stopwatchDbCommit.ElapsedMilliseconds;
        stopwatchTotal.Stop();
        result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
        logInstallPerformance?.Invoke(BuildLogMessage(result));
        return result;
    }

    private ChartInfoBuildItemResult ParseQueuedItem(
        QueuedChartBytes item,
        IDictionary<string, LR2SongDBExtended.chart_info> existingRows,
        Action<string> logInstallPerformanceWarn)
    {
        ChartInfoBuildTarget target = item.Target;
        string md5 = string.IsNullOrWhiteSpace(target.Md5) ? ComputeHash(item.Bytes, MD5.Create()) : target.Md5;
        string sha256 = string.IsNullOrWhiteSpace(target.Sha256) ? ComputeHash(item.Bytes, SHA256.Create()) : target.Sha256;
        if (IsCurrent(existingRows, sha256))
        {
            return new ChartInfoBuildItemResult(target, sha256, existingRows[sha256], reusedExistingRow: true, parseFailed: false);
        }
        try
        {
            LR2SongDBExtended.chart_info row = ChartInfoParser.ParseBytes(item.Bytes, target.Path, md5, sha256, target.EncodingName);
            return new ChartInfoBuildItemResult(target, sha256, row, reusedExistingRow: false, parseFailed: false);
        }
        catch (Exception ex)
        {
            logInstallPerformanceWarn?.Invoke(BuildParseFailureLogMessage(target, md5, sha256, ex));
            return new ChartInfoBuildItemResult(target, sha256, null, reusedExistingRow: false, parseFailed: true);
        }
    }

    private static List<ChartInfoBuildTarget> BuildTargets(
        IEnumerable<BMSFile> currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs,
        IDictionary<string, LR2SongDBExtended.chart_info> existingRows,
        ChartInfoBackfillResult result)
    {
        Dictionary<string, ChartInfoBuildTarget> targets = new Dictionary<string, ChartInfoBuildTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile file in currentFiles ?? Enumerable.Empty<BMSFile>())
        {
            if (file == null || string.IsNullOrWhiteSpace(file.path))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(file.sha256) && IsCurrent(existingRows, file.sha256))
            {
                file.SetChartInfo(existingRows[file.sha256]);
                continue;
            }
            if (string.IsNullOrWhiteSpace(file.sha256) && string.IsNullOrWhiteSpace(file.hash))
            {
                continue;
            }
            string key = BuildBmsTargetKey(file.sha256, file.hash, file.path);
            if (!targets.TryGetValue(key, out ChartInfoBuildTarget target))
            {
                target = ChartInfoBuildTarget.FromBmsFile(file);
                targets[key] = target;
            }
            else
            {
                target.AddBmsFile(file);
            }
            if (string.IsNullOrWhiteSpace(file.sha256))
            {
                result.DigestTargetCount++;
            }
        }
        foreach (LR2SongDBExtended.bmson_song song in currentBmsonSongs ?? Enumerable.Empty<LR2SongDBExtended.bmson_song>())
        {
            if (song == null || string.IsNullOrWhiteSpace(song.path))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(song.sha256) && IsCurrent(existingRows, song.sha256))
            {
                song.ChartInfo = existingRows[song.sha256];
                continue;
            }
            string key = BuildTargetKey(song.sha256, song.md5, song.path);
            if (!targets.TryGetValue(key, out ChartInfoBuildTarget target))
            {
                target = ChartInfoBuildTarget.FromBmsonSong(song);
                targets[key] = target;
            }
            else
            {
                target.AddBmsonSong(song);
            }
        }
        return targets.Values.ToList();
    }

    private static string BuildTargetKey(string sha256, string md5, string path)
    {
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            return "sha256:" + sha256;
        }
        if (!string.IsNullOrWhiteSpace(md5))
        {
            return "md5:" + md5;
        }
        return "path:" + (path ?? string.Empty);
    }

    private static string BuildBmsTargetKey(string sha256, string md5, string path)
    {
        if (!string.IsNullOrWhiteSpace(md5))
        {
            return "md5:" + md5;
        }
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            return "sha256:" + sha256;
        }
        return "path:" + (path ?? string.Empty);
    }

    private static bool IsCurrent(IDictionary<string, LR2SongDBExtended.chart_info> existingRows, string sha256)
    {
        return existingRows != null
            && !string.IsNullOrWhiteSpace(sha256)
            && existingRows.TryGetValue(sha256, out LR2SongDBExtended.chart_info row)
            && row != null
            && row.parser_version >= BmsLibraryDbGateway.CurrentChartInfoParserVersion;
    }

    private int ResolveWorkerCount()
    {
        if (workerCountOverride.HasValue)
        {
            return Math.Max(1, workerCountOverride.Value);
        }
        return Math.Min(4, Math.Max(1, Environment.ProcessorCount - 1));
    }

    private static long TicksToMilliseconds(long ticks)
    {
        return (long)(ticks * 1000.0 / Stopwatch.Frequency);
    }

    private static string ComputeHash(byte[] bytes, HashAlgorithm algorithm)
    {
        using (algorithm)
        {
            byte[] hash = algorithm.ComputeHash(bytes ?? Array.Empty<byte>());
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            foreach (byte value in hash)
            {
                builder.Append(value.ToString("x2"));
            }
            return builder.ToString();
        }
    }

    private static string BuildLogMessage(ChartInfoBackfillResult result)
    {
        return "chart_info_backfill total=" + result.TargetCount
            + " success=" + result.BackfilledCount
            + " failed=" + result.FailedCount
            + " digestBackfilled=" + result.DigestBackfilledCount
            + " infoBackfilled=" + result.BackfilledCount
            + " readFailed=" + result.ReadFailedCount
            + " parseFailed=" + result.ParseFailedCount
            + " workerCount=" + result.WorkerCount
            + " queueCapacity=" + result.QueueCapacity
            + " readMs=" + result.ReadMs
            + " parseMs=" + result.ParseMs
            + " dbCommitMs=" + result.DbCommitMs
            + " computeMs=" + result.ComputeMs
            + " totalMs=" + result.TotalMs;
    }

    private static string BuildReadFailureLogMessage(ChartInfoBuildTarget target, Exception ex)
    {
        return "chart_info_backfill read_failed"
            + " path=" + QuoteLogValue(target?.Path)
            + " parserVersion=" + BmsLibraryDbGateway.CurrentChartInfoParserVersion
            + " exception=" + QuoteLogValue(ex?.GetType().Name)
            + " message=" + QuoteLogValue(ex?.Message);
    }

    private static string BuildParseFailureLogMessage(ChartInfoBuildTarget target, string md5, string sha256, Exception ex)
    {
        return "chart_info_backfill parse_failed"
            + " path=" + QuoteLogValue(target?.Path)
            + " md5=" + QuoteLogValue(md5)
            + " sha256=" + QuoteLogValue(sha256)
            + " parserVersion=" + BmsLibraryDbGateway.CurrentChartInfoParserVersion
            + " exception=" + QuoteLogValue(ex?.GetType().Name)
            + " message=" + QuoteLogValue(ex?.Message);
    }

    private static string QuoteLogValue(string value)
    {
        string escaped = (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", " ")
            .Replace("\n", " ");
        return "\"" + escaped + "\"";
    }

    private sealed class QueuedChartBytes
    {
        public QueuedChartBytes(ChartInfoBuildTarget target, byte[] bytes)
        {
            Target = target;
            Bytes = bytes ?? Array.Empty<byte>();
        }

        public ChartInfoBuildTarget Target { get; }

        public byte[] Bytes { get; }
    }

    private sealed class ChartInfoBuildItemResult
    {
        public ChartInfoBuildItemResult(ChartInfoBuildTarget target, string sha256, LR2SongDBExtended.chart_info row, bool reusedExistingRow, bool parseFailed)
        {
            Target = target;
            Sha256 = sha256;
            Row = row;
            ReusedExistingRow = reusedExistingRow;
            ParseFailed = parseFailed;
        }

        public ChartInfoBuildTarget Target { get; }

        public string Sha256 { get; }

        public LR2SongDBExtended.chart_info Row { get; }

        public bool ReusedExistingRow { get; }

        public bool ParseFailed { get; }

        public bool DigestSucceeded => !string.IsNullOrWhiteSpace(Sha256);
    }

    private sealed class ChartInfoBuildTarget
    {
        private readonly List<BMSFile> bmsFiles = new List<BMSFile>();

        private readonly List<LR2SongDBExtended.bmson_song> bmsonSongs = new List<LR2SongDBExtended.bmson_song>();

        private ChartInfoBuildTarget(string path, string md5, string sha256, string encodingName)
        {
            Path = path;
            Md5 = md5;
            Sha256 = sha256;
            EncodingName = encodingName;
        }

        public string Path { get; }

        public string Md5 { get; }

        public string Sha256 { get; }

        public string EncodingName { get; }

        public bool NeedsDigest => bmsFiles.Any((BMSFile file) => file != null && string.IsNullOrWhiteSpace(file.sha256));

        public int MissingDigestOwnerCount => bmsFiles.Count((BMSFile file) => file != null && string.IsNullOrWhiteSpace(file.sha256));

        public static ChartInfoBuildTarget FromBmsFile(BMSFile file)
        {
            ChartInfoBuildTarget target = new ChartInfoBuildTarget(
                file.path,
                file.hash,
                file.sha256,
                file.maintenanceInfo?.encoding);
            target.AddBmsFile(file);
            return target;
        }

        public static ChartInfoBuildTarget FromBmsonSong(LR2SongDBExtended.bmson_song song)
        {
            ChartInfoBuildTarget target = new ChartInfoBuildTarget(
                song.path,
                song.md5,
                song.sha256,
                null);
            target.AddBmsonSong(song);
            return target;
        }

        public void AddBmsFile(BMSFile file)
        {
            if (file != null)
            {
                bmsFiles.Add(file);
            }
        }

        public void AddBmsonSong(LR2SongDBExtended.bmson_song song)
        {
            if (song != null)
            {
                bmsonSongs.Add(song);
            }
        }

        public int ApplyDigest(string sha256, ICollection<BMSFile> completedDigestFiles)
        {
            if (string.IsNullOrWhiteSpace(sha256))
            {
                return 0;
            }
            int applied = 0;
            foreach (BMSFile file in bmsFiles)
            {
                if (file == null || !string.IsNullOrWhiteSpace(file.sha256))
                {
                    continue;
                }
                file.ApplySha256(sha256);
                completedDigestFiles?.Add(file);
                applied++;
            }
            return applied;
        }

        public void ApplyChartInfo(LR2SongDBExtended.chart_info row)
        {
            if (row == null)
            {
                return;
            }
            foreach (BMSFile file in bmsFiles)
            {
                file?.SetChartInfo(row);
            }
            foreach (LR2SongDBExtended.bmson_song song in bmsonSongs)
            {
                if (song != null)
                {
                    song.ChartInfo = row;
                }
            }
        }
    }
}
