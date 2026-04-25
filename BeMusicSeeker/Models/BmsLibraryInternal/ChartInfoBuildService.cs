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
    private const int DefaultCommitChunkSize = 1000;

    private static readonly TimeSpan DefaultParseTimeout = TimeSpan.FromSeconds(60.0);

    private readonly Func<string, byte[]> readAllBytes;

    private readonly int? workerCountOverride;

    private readonly int? commitChunkSizeOverride;

    private readonly TimeSpan? parseTimeoutOverride;

    public ChartInfoBuildService()
        : this(File.ReadAllBytes, null, null, null)
    {
    }

    /// <summary>
    /// テストや検証で reader、worker 数、chunk サイズ、timeout を固定できる backfill service を作成します。
    /// 本番では file IO 競合を避けるため reader は呼び出し側から1本だけ使われます。
    /// </summary>
    /// <param name="readAllBytes">譜面ファイルを byte[] として読み取る関数。</param>
    /// <param name="workerCountOverride">解析 worker 数。null の場合は CPU 数から自動決定します。</param>
    /// <param name="commitChunkSizeOverride">DB 保存 chunk サイズ。null の場合は既定値を使います。</param>
    /// <param name="parseTimeoutOverride">1譜面あたりの解析 timeout。null の場合は既定値を使います。</param>
    internal ChartInfoBuildService(Func<string, byte[]> readAllBytes, int? workerCountOverride = null, int? commitChunkSizeOverride = null, TimeSpan? parseTimeoutOverride = null)
    {
        this.readAllBytes = readAllBytes ?? File.ReadAllBytes;
        this.workerCountOverride = workerCountOverride;
        this.commitChunkSizeOverride = commitChunkSizeOverride;
        this.parseTimeoutOverride = parseTimeoutOverride;
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
        return BackfillChartInfosCore(
            dbGateway,
            currentFiles,
            currentBmsonSongs,
            "full",
            reportProgress,
            logInstallPerformance,
            logInstallPerformanceWarn);
    }

    /// <summary>
    /// 新規追加された譜面だけを対象に、不足または古い chart_info 行と不足している BMS SHA-256 digest を構築します。
    /// </summary>
    /// <param name="dbGateway">song.db へのアクセス手段。</param>
    /// <param name="targetFiles">新規追加された BMS 譜面。</param>
    /// <param name="targetBmsonSongs">新規追加された bmson 譜面。</param>
    /// <param name="reportProgress">進捗通知 callback。total, processed, currentPath を渡します。</param>
    /// <param name="logInstallPerformance">性能ログ callback。</param>
    /// <param name="logInstallPerformanceWarn">解析を継続できない譜面を逐次 WARN 出力する callback。</param>
    /// <returns>構築結果。</returns>
    public ChartInfoBackfillResult BackfillChartInfosForTargets(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<BMSFile> targetFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> targetBmsonSongs,
        Action<int, int, string> reportProgress = null,
        Action<string> logInstallPerformance = null,
        Action<string> logInstallPerformanceWarn = null)
    {
        return BackfillChartInfosCore(
            dbGateway,
            targetFiles,
            targetBmsonSongs,
            "added",
            reportProgress,
            logInstallPerformance,
            logInstallPerformanceWarn);
    }

    private ChartInfoBackfillResult BackfillChartInfosCore(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<BMSFile> currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs,
        string mode,
        Action<int, int, string> reportProgress,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        ChartInfoBackfillResult result = new ChartInfoBackfillResult();
        if (dbGateway == null)
        {
            return result;
        }
        Stopwatch stopwatchTotal = Stopwatch.StartNew();
        int commitChunkSize = ResolveCommitChunkSize();
        TimeSpan parseTimeout = ResolveParseTimeout();
        dbGateway.EnsureChartInfoBackfillSchema();
        Dictionary<string, LR2SongDBExtended.chart_info> existingRows = dbGateway.LoadChartInfoMap();
        List<ChartInfoBuildTarget> targets = BuildTargets(currentFiles, currentBmsonSongs, existingRows, result);
        result.TargetCount = targets.Count;
        result.WorkerCount = ResolveWorkerCount();
        result.QueueCapacity = Math.Max(1, result.WorkerCount * 2);
        reportProgress?.Invoke(result.TargetCount, 0, string.Empty);
        logInstallPerformance?.Invoke(BuildStartLogMessage(result, existingRows.Count, commitChunkSize, parseTimeout, mode));
        if (targets.Count == 0)
        {
            stopwatchTotal.Stop();
            result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
            logInstallPerformance?.Invoke(BuildLogMessage(result));
            return result;
        }

        BlockingCollection<QueuedChartBytes> queue = new BlockingCollection<QueuedChartBytes>(result.QueueCapacity);
        BlockingCollection<ChartInfoBuildItemResult> itemResults = new BlockingCollection<ChartInfoBuildItemResult>();
        long readTicks = 0L;
        long parseTicks = 0L;
        int[] processedCount = new int[1];

        Task resultWriter = Task.Run(delegate
        {
            ConsumeBuildResults(
                dbGateway,
                itemResults.GetConsumingEnumerable(),
                result,
                commitChunkSize,
                logInstallPerformance,
                logInstallPerformanceWarn,
                reportProgress,
                processedCount);
        });

        List<Task> workers = Enumerable.Range(0, result.WorkerCount)
            .Select(_ => Task.Run(delegate
            {
                foreach (QueuedChartBytes item in queue.GetConsumingEnumerable())
                {
                    Stopwatch parseStopwatch = Stopwatch.StartNew();
                    ChartInfoBuildItemResult itemResult = ParseQueuedItem(item, existingRows, logInstallPerformanceWarn, parseTimeout);
                    parseStopwatch.Stop();
                    Interlocked.Add(ref parseTicks, parseStopwatch.ElapsedTicks);
                    itemResult.ParseMs = parseStopwatch.ElapsedMilliseconds;
                    itemResult.ByteCount = item.Bytes.LongLength;
                    itemResults.Add(itemResult);
                }
            }))
            .ToList();

        try
        {
            foreach (ChartInfoBuildTarget target in targets)
            {
                try
                {
                    reportProgress?.Invoke(result.TargetCount, Volatile.Read(ref processedCount[0]), target.Path);
                    Stopwatch readStopwatch = Stopwatch.StartNew();
                    byte[] bytes = readAllBytes(target.Path);
                    readStopwatch.Stop();
                    Interlocked.Add(ref readTicks, readStopwatch.ElapsedTicks);
                    queue.Add(new QueuedChartBytes(target, bytes));
                }
                catch (Exception ex)
                {
                    logInstallPerformanceWarn?.Invoke(BuildReadFailureLogMessage(target, ex));
                    itemResults.Add(ChartInfoBuildItemResult.CreateReadFailed(target));
                }
            }
        }
        finally
        {
            queue.CompleteAdding();
        }
        Exception workerException = null;
        try
        {
            Task.WaitAll(workers.ToArray());
        }
        catch (Exception ex)
        {
            workerException = ex;
        }
        finally
        {
            itemResults.CompleteAdding();
        }
        resultWriter.Wait();
        if (workerException != null)
        {
            throw workerException;
        }

        result.ProcessedCount = Volatile.Read(ref processedCount[0]);
        result.ReadMs = TicksToMilliseconds(readTicks);
        result.ParseMs = TicksToMilliseconds(parseTicks);
        result.ComputeMs = result.ReadMs + result.ParseMs;
        stopwatchTotal.Stop();
        result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
        logInstallPerformance?.Invoke(BuildLogMessage(result));
        return result;
    }

    private ChartInfoBuildItemResult ParseQueuedItem(
        QueuedChartBytes item,
        IDictionary<string, LR2SongDBExtended.chart_info> existingRows,
        Action<string> logInstallPerformanceWarn,
        TimeSpan parseTimeout)
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
            LR2SongDBExtended.chart_info row = ChartInfoParser.ParseBytesDetailed(item.Bytes, target.Path, md5, sha256, target.EncodingName, parseTimeout).Row;
            return new ChartInfoBuildItemResult(target, sha256, row, reusedExistingRow: false, parseFailed: false);
        }
        catch (Exception ex)
        {
            logInstallPerformanceWarn?.Invoke(BuildParseFailureLogMessage(target, md5, sha256, ex));
            return new ChartInfoBuildItemResult(target, sha256, null, reusedExistingRow: false, parseFailed: true, timeoutFailed: ex is ChartInfoParser.ChartInfoParseTimeoutException);
        }
    }

    private static void ConsumeBuildResults(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<ChartInfoBuildItemResult> itemResults,
        ChartInfoBackfillResult result,
        int commitChunkSize,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        Action<int, int, string> reportProgress,
        int[] processedCount)
    {
        ChartInfoCommitBuffer commitBuffer = new ChartInfoCommitBuffer();
        List<long> parseDurations = new List<long>();
        List<SlowParseRecord> slowParseRecords = new List<SlowParseRecord>();
        int parseSucceededCount = 0;
        foreach (ChartInfoBuildItemResult itemResult in itemResults)
        {
            ApplyBuildResult(itemResult, result, commitBuffer, parseDurations, slowParseRecords, ref parseSucceededCount);
            int processed = Interlocked.Increment(ref processedCount[0]);
            result.ProcessedCount = processed;
            reportProgress?.Invoke(result.TargetCount, processed, itemResult.Target?.Path ?? string.Empty);
            if (commitBuffer.TargetCount >= commitChunkSize)
            {
                FlushCommitBuffer(dbGateway, commitBuffer, result, logInstallPerformance, logInstallPerformanceWarn);
            }
        }
        ApplyParseMetrics(result, parseDurations);
        logInstallPerformance?.Invoke(BuildParseDoneLogMessage(result, parseSucceededCount));
        LogSlowParseRecords(logInstallPerformance, slowParseRecords);
        FlushCommitBuffer(dbGateway, commitBuffer, result, logInstallPerformance, logInstallPerformanceWarn);
    }

    private static void ApplyBuildResult(
        ChartInfoBuildItemResult itemResult,
        ChartInfoBackfillResult result,
        ChartInfoCommitBuffer commitBuffer,
        ICollection<long> parseDurations,
        ICollection<SlowParseRecord> slowParseRecords,
        ref int parseSucceededCount)
    {
        commitBuffer.TargetCount++;
        if (!itemResult.ReadFailed)
        {
            parseDurations.Add(itemResult.ParseMs);
            AddSlowParseRecord(slowParseRecords, itemResult);
        }
        if (itemResult.ReadFailed)
        {
            result.ReadFailedCount++;
            result.FailedCount++;
            if (itemResult.Target != null && itemResult.Target.NeedsDigest)
            {
                result.DigestFailedCount += itemResult.Target.MissingDigestOwnerCount;
            }
            if (itemResult.Target != null)
            {
                result.FailedPaths.Add(itemResult.Target.Path);
            }
            return;
        }
        if (itemResult.DigestSucceeded)
        {
            commitBuffer.AddDigest(itemResult.Target, itemResult.Sha256);
        }
        else if (itemResult.Target.NeedsDigest)
        {
            result.DigestFailedCount += itemResult.Target.MissingDigestOwnerCount;
        }
        if (itemResult.Row != null)
        {
            parseSucceededCount++;
            if (itemResult.ReusedExistingRow)
            {
                itemResult.Target.ApplyChartInfo(itemResult.Row);
            }
            else
            {
                commitBuffer.AddChartInfo(itemResult.Target, itemResult.Row);
            }
        }
        if (itemResult.ParseFailed)
        {
            result.ParseFailedCount++;
            result.FailedCount++;
            if (itemResult.TimeoutFailed)
            {
                result.TimeoutFailedCount++;
            }
            result.FailedPaths.Add(itemResult.Target.Path);
        }
    }

    private static void FlushCommitBuffer(
        BmsLibraryDbGateway dbGateway,
        ChartInfoCommitBuffer commitBuffer,
        ChartInfoBackfillResult result,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (!commitBuffer.HasPendingDbRows)
        {
            commitBuffer.Clear();
            return;
        }
        int chunkNumber = result.CommitChunks + 1;
        logInstallPerformance?.Invoke(BuildCommitStartLogMessage(chunkNumber, commitBuffer));
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            dbGateway.UpsertChartInfoBackfillChunk(commitBuffer.DigestEntries, commitBuffer.ChartInfoRows);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logInstallPerformanceWarn?.Invoke(BuildCommitFailureLogMessage(chunkNumber, commitBuffer, stopwatch.ElapsedMilliseconds, ex));
            throw;
        }
        stopwatch.Stop();
        result.CommitChunks++;
        result.DbCommitMs += stopwatch.ElapsedMilliseconds;
        result.DbCommitMaxChunkMs = Math.Max(result.DbCommitMaxChunkMs, stopwatch.ElapsedMilliseconds);
        foreach (PendingDigestApplication application in commitBuffer.DigestApplications)
        {
            result.DigestBackfilledCount += application.Target.ApplyDigest(application.Sha256, null);
        }
        foreach (PendingChartInfoApplication application in commitBuffer.ChartInfoApplications)
        {
            application.Target.ApplyChartInfo(application.Row);
            result.BackfilledCount++;
        }
        logInstallPerformance?.Invoke(BuildCommitDoneLogMessage(chunkNumber, commitBuffer, stopwatch.ElapsedMilliseconds));
        commitBuffer.Clear();
    }

    private static void ApplyParseMetrics(ChartInfoBackfillResult result, List<long> parseDurations)
    {
        if (parseDurations.Count == 0)
        {
            return;
        }
        parseDurations.Sort();
        result.ParseAvgMs = (long)parseDurations.Average();
        result.ParseMaxMs = parseDurations[parseDurations.Count - 1];
        int p95Index = Math.Min(parseDurations.Count - 1, Math.Max(0, (int)Math.Ceiling(parseDurations.Count * 0.95) - 1));
        result.ParseP95Ms = parseDurations[p95Index];
    }

    private static void AddSlowParseRecord(ICollection<SlowParseRecord> slowParseRecords, ChartInfoBuildItemResult itemResult)
    {
        slowParseRecords.Add(new SlowParseRecord(
            itemResult.ParseMs,
            itemResult.Status,
            itemResult.ByteCount,
            itemResult.Target?.Path,
            itemResult.Target?.Md5,
            itemResult.Sha256));
    }

    private static void LogSlowParseRecords(Action<string> logInstallPerformance, IEnumerable<SlowParseRecord> slowParseRecords)
    {
        if (logInstallPerformance == null)
        {
            return;
        }
        int rank = 1;
        foreach (SlowParseRecord record in slowParseRecords
            .OrderByDescending((SlowParseRecord item) => item.ElapsedMs)
            .ThenBy((SlowParseRecord item) => item.Path, StringComparer.OrdinalIgnoreCase)
            .Take(10))
        {
            logInstallPerformance("chart_info_backfill slow_parse_top"
                + " rank=" + rank
                + " elapsedMs=" + record.ElapsedMs
                + " status=" + record.Status
                + " bytes=" + record.ByteCount
                + " path=" + QuoteLogValue(record.Path)
                + " md5=" + QuoteLogValue(record.Md5)
                + " sha256=" + QuoteLogValue(record.Sha256));
            rank++;
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

    private int ResolveCommitChunkSize()
    {
        if (commitChunkSizeOverride.HasValue)
        {
            return Math.Max(1, commitChunkSizeOverride.Value);
        }
        return DefaultCommitChunkSize;
    }

    private TimeSpan ResolveParseTimeout()
    {
        return parseTimeoutOverride ?? DefaultParseTimeout;
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
            + " timeoutFailed=" + result.TimeoutFailedCount
            + " workerCount=" + result.WorkerCount
            + " queueCapacity=" + result.QueueCapacity
            + " readMs=" + result.ReadMs
            + " parseMs=" + result.ParseMs
            + " parseAvgMs=" + result.ParseAvgMs
            + " parseMaxMs=" + result.ParseMaxMs
            + " parseP95Ms=" + result.ParseP95Ms
            + " dbCommitMs=" + result.DbCommitMs
            + " commitChunks=" + result.CommitChunks
            + " dbCommitMaxChunkMs=" + result.DbCommitMaxChunkMs
            + " computeMs=" + result.ComputeMs
            + " totalMs=" + result.TotalMs;
    }

    private static string BuildStartLogMessage(ChartInfoBackfillResult result, int existingRowCount, int commitChunkSize, TimeSpan parseTimeout, string mode)
    {
        return "chart_info_backfill start"
            + " mode=" + (string.IsNullOrWhiteSpace(mode) ? "full" : mode)
            + " targets=" + result.TargetCount
            + " digestTargets=" + result.DigestTargetCount
            + " existingRows=" + existingRowCount
            + " workerCount=" + result.WorkerCount
            + " queueCapacity=" + result.QueueCapacity
            + " chunkSize=" + commitChunkSize
            + " parserTimeoutMs=" + (long)parseTimeout.TotalMilliseconds
            + " parserVersion=" + BmsLibraryDbGateway.CurrentChartInfoParserVersion;
    }

    private static string BuildParseDoneLogMessage(ChartInfoBackfillResult result, int parseSucceededCount)
    {
        return "chart_info_backfill parse_done"
            + " processed=" + result.ProcessedCount
            + " success=" + parseSucceededCount
            + " failed=" + result.FailedCount
            + " timeoutFailed=" + result.TimeoutFailedCount
            + " avgParseMs=" + result.ParseAvgMs
            + " maxParseMs=" + result.ParseMaxMs
            + " parseP95Ms=" + result.ParseP95Ms;
    }

    private static string BuildCommitStartLogMessage(int chunkNumber, ChartInfoCommitBuffer commitBuffer)
    {
        return "chart_info_backfill db_commit_chunk_start"
            + " chunk=" + chunkNumber
            + " targets=" + commitBuffer.TargetCount
            + " digestRows=" + commitBuffer.DigestEntries.Count
            + " infoRows=" + commitBuffer.ChartInfoRows.Count;
    }

    private static string BuildCommitDoneLogMessage(int chunkNumber, ChartInfoCommitBuffer commitBuffer, long elapsedMs)
    {
        return "chart_info_backfill db_commit_chunk_done"
            + " chunk=" + chunkNumber
            + " targets=" + commitBuffer.TargetCount
            + " digestRows=" + commitBuffer.DigestEntries.Count
            + " infoRows=" + commitBuffer.ChartInfoRows.Count
            + " elapsedMs=" + elapsedMs;
    }

    private static string BuildCommitFailureLogMessage(int chunkNumber, ChartInfoCommitBuffer commitBuffer, long elapsedMs, Exception ex)
    {
        return "chart_info_backfill db_commit_chunk_failed"
            + " chunk=" + chunkNumber
            + " targets=" + commitBuffer.TargetCount
            + " digestRows=" + commitBuffer.DigestEntries.Count
            + " infoRows=" + commitBuffer.ChartInfoRows.Count
            + " elapsedMs=" + elapsedMs
            + " exception=" + QuoteLogValue(ex?.GetType().Name)
            + " message=" + QuoteLogValue(ex?.Message);
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
        public ChartInfoBuildItemResult(ChartInfoBuildTarget target, string sha256, LR2SongDBExtended.chart_info row, bool reusedExistingRow, bool parseFailed, bool timeoutFailed = false, bool readFailed = false)
        {
            Target = target;
            Sha256 = sha256;
            Row = row;
            ReusedExistingRow = reusedExistingRow;
            ParseFailed = parseFailed;
            TimeoutFailed = timeoutFailed;
            ReadFailed = readFailed;
        }

        public ChartInfoBuildTarget Target { get; }

        public string Sha256 { get; }

        public LR2SongDBExtended.chart_info Row { get; }

        public bool ReusedExistingRow { get; }

        public bool ParseFailed { get; }

        public bool TimeoutFailed { get; }

        public bool ReadFailed { get; }

        public long ParseMs { get; set; }

        public long ByteCount { get; set; }

        public string Status
        {
            get
            {
                if (ReadFailed)
                {
                    return "read_failed";
                }
                if (TimeoutFailed)
                {
                    return "timeout";
                }
                if (ParseFailed)
                {
                    return "failed";
                }
                return ReusedExistingRow ? "reused" : "success";
            }
        }

        public bool DigestSucceeded => !ReadFailed && !string.IsNullOrWhiteSpace(Sha256);

        public static ChartInfoBuildItemResult CreateReadFailed(ChartInfoBuildTarget target)
        {
            return new ChartInfoBuildItemResult(target, null, null, reusedExistingRow: false, parseFailed: false, readFailed: true);
        }
    }

    private sealed class ChartInfoCommitBuffer
    {
        public List<ChartDigestBackfillEntry> DigestEntries { get; } = new List<ChartDigestBackfillEntry>();

        public List<PendingDigestApplication> DigestApplications { get; } = new List<PendingDigestApplication>();

        public List<LR2SongDBExtended.chart_info> ChartInfoRows { get; } = new List<LR2SongDBExtended.chart_info>();

        public List<PendingChartInfoApplication> ChartInfoApplications { get; } = new List<PendingChartInfoApplication>();

        public int TargetCount { get; set; }

        public bool HasPendingDbRows => DigestEntries.Count > 0 || ChartInfoRows.Count > 0;

        public void AddDigest(ChartInfoBuildTarget target, string sha256)
        {
            if (target == null || !target.NeedsDigest || string.IsNullOrWhiteSpace(target.Md5) || string.IsNullOrWhiteSpace(sha256))
            {
                return;
            }
            DigestEntries.Add(new ChartDigestBackfillEntry(target.Md5, sha256));
            DigestApplications.Add(new PendingDigestApplication(target, sha256));
        }

        public void AddChartInfo(ChartInfoBuildTarget target, LR2SongDBExtended.chart_info row)
        {
            if (target == null || row == null)
            {
                return;
            }
            ChartInfoRows.Add(row);
            ChartInfoApplications.Add(new PendingChartInfoApplication(target, row));
        }

        public void Clear()
        {
            DigestEntries.Clear();
            DigestApplications.Clear();
            ChartInfoRows.Clear();
            ChartInfoApplications.Clear();
            TargetCount = 0;
        }
    }

    private sealed class PendingDigestApplication
    {
        public PendingDigestApplication(ChartInfoBuildTarget target, string sha256)
        {
            Target = target;
            Sha256 = sha256 ?? string.Empty;
        }

        public ChartInfoBuildTarget Target { get; }

        public string Sha256 { get; }
    }

    private sealed class PendingChartInfoApplication
    {
        public PendingChartInfoApplication(ChartInfoBuildTarget target, LR2SongDBExtended.chart_info row)
        {
            Target = target;
            Row = row;
        }

        public ChartInfoBuildTarget Target { get; }

        public LR2SongDBExtended.chart_info Row { get; }
    }

    private sealed class SlowParseRecord
    {
        public SlowParseRecord(long elapsedMs, string status, long byteCount, string path, string md5, string sha256)
        {
            ElapsedMs = elapsedMs;
            Status = status ?? string.Empty;
            ByteCount = byteCount;
            Path = path ?? string.Empty;
            Md5 = md5 ?? string.Empty;
            Sha256 = sha256 ?? string.Empty;
        }

        public long ElapsedMs { get; }

        public string Status { get; }

        public long ByteCount { get; }

        public string Path { get; }

        public string Md5 { get; }

        public string Sha256 { get; }
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
