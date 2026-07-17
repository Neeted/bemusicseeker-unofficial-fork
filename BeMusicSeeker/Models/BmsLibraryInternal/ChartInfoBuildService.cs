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
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 現在所持している譜面から chart_info と不足している chart_digest_map を構築します。
/// 行の削除は担わず、未所持譜面のメタデータを残せるようにしています。
/// </summary>
internal sealed class ChartInfoBuildService
{
    private const int DefaultCommitChunkSize = 10000;

    private static readonly TimeSpan DefaultParseTimeout = TimeSpan.FromSeconds(60.0);

    private const int MaxPersistedParseFailureMessageLength = 1024;

    private readonly Func<string, byte[]> readAllBytes;

    private readonly int? workerCountOverride;

    private readonly int? commitChunkSizeOverride;

    private readonly TimeSpan? parseTimeoutOverride;

    public ChartInfoBuildService()
        : this(LongPathFileSystem.ReadAllBytes, null, null, null)
    {
    }

    /// <summary>
    /// テストや検証で reader、worker 数、chunk サイズ、timeout を固定できる backfill service を作成します。
    /// 本番では byte[] 読み取り reader と解析 worker を分けて動かします。
    /// </summary>
    /// <param name="readAllBytes">譜面ファイルを byte[] として読み取る関数。</param>
    /// <param name="workerCountOverride">解析 worker 数。null の場合は CPU 数から自動決定します。</param>
    /// <param name="commitChunkSizeOverride">DB 保存 chunk サイズ。null の場合は既定値を使います。</param>
    /// <param name="parseTimeoutOverride">1譜面あたりの解析 timeout。null の場合は既定値を使います。</param>
    internal ChartInfoBuildService(Func<string, byte[]> readAllBytes, int? workerCountOverride = null, int? commitChunkSizeOverride = null, TimeSpan? parseTimeoutOverride = null)
    {
        this.readAllBytes = readAllBytes ?? LongPathFileSystem.ReadAllBytes;
        this.workerCountOverride = workerCountOverride;
        this.commitChunkSizeOverride = commitChunkSizeOverride;
        this.parseTimeoutOverride = parseTimeoutOverride;
    }

    /// <summary>
    /// 不足または古い chart_info 行と、不足している BMS SHA-256 digest を構築します。
    /// ファイル読み取り reader は byte[] の取得だけを行い、hash 計算と chart_info 解析は worker 側で行います。
    /// </summary>
    /// <param name="dbGateway">song.db へのアクセス手段。</param>
    /// <param name="currentCharts">現在所持している譜面。</param>
    /// <param name="reportProgress">進捗通知 callback。total, processed, currentPath を渡します。</param>
    /// <param name="logInstallPerformance">性能ログ callback。</param>
    /// <param name="logInstallPerformanceWarn">解析を継続できない譜面を逐次 WARN 出力する callback。</param>
    /// <param name="chartInfoRowsCommitted">DB commit 成功後に保存済み chart_info 行を通知する callback。</param>
    /// <param name="existingRowsSnapshot">hydration 済みの chart_info index snapshot。full backfill 時の DB 全件再読込を避けるために使います。</param>
    /// <returns>構築結果。</returns>
    public ChartInfoBackfillResult BackfillChartInfos(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<ChartFile> currentCharts,
        Action<int, int, string> reportProgress = null,
        Action<string> logInstallPerformance = null,
        Action<string> logInstallPerformanceWarn = null,
        Action<IReadOnlyList<LR2SongDBExtended.chart_info>> chartInfoRowsCommitted = null,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> existingRowsSnapshot = null,
        Action<IReadOnlyList<ChartDigestBackfillEntry>, IReadOnlyList<LR2SongDBExtended.chart_info>, IReadOnlyList<LR2SongDBExtended.chart_info_parse_failure>, IReadOnlyList<string>> chartInfoChunkWriter = null)
    {
        return BackfillChartInfosCore(
            dbGateway,
            currentCharts,
            "full",
            reportProgress,
            logInstallPerformance,
            logInstallPerformanceWarn,
            chartInfoRowsCommitted,
            existingRowsSnapshot,
            chartInfoChunkWriter);
    }

    internal InlineChartInfoBuildResult BuildInlineChartInfo(
        ChartFileSnapshot snapshot,
        ChartFile chart,
        IDictionary<string, LR2SongDBExtended.chart_info> existingRows,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        Action<string> logInstallPerformance = null,
        Action<string> logInstallPerformanceWarn = null)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }
        if (chart == null)
        {
            throw new ArgumentNullException(nameof(chart));
        }
        if (!ChartInfoBuildTargetMapper.HasSingleStorageOwner(chart))
        {
            throw new ArgumentException("Exactly one chart model must be specified.");
        }

        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(chart);
        string md5 = string.IsNullOrWhiteSpace(snapshot.Md5) ? target.Md5 : snapshot.Md5;
        string sha256 = string.IsNullOrWhiteSpace(snapshot.Sha256) ? target.Sha256 : snapshot.Sha256;
        TimeSpan parseTimeout = ResolveParseTimeout();

        if (IsCurrent(existingRows, sha256))
        {
            LR2SongDBExtended.chart_info row = existingRows[sha256];
            return InlineChartInfoBuildResult.CreateCurrentRowSkipped(row, snapshot.Length);
        }
        if (IsCurrentParseFailure(currentFailures, md5))
        {
            return InlineChartInfoBuildResult.CreateFailureSkipped(snapshot.Length);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            // maintenance.encoding is for list/LR2 song display correction. chart_info must use
            // the parser's beatoraja-compatible default BMS decoding instead of that UI hint.
            ChartInfoParser.ChartInfoParseResult parseResult = ChartInfoParser.ParseBytesDetailed(
                snapshot.Bytes,
                target.Path,
                md5,
                sha256,
                encodingName: null,
                timeout: parseTimeout);
            stopwatch.Stop();
            LogParseDiagnostics(logInstallPerformance, target, md5, sha256, parseResult.Diagnostics);
            LR2SongDBExtended.chart_info row = parseResult.Row;
            return InlineChartInfoBuildResult.CreateSuccess(row, md5, stopwatch.ElapsedMilliseconds, snapshot.Length);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logInstallPerformanceWarn?.Invoke(BuildParseFailureLogMessage(target, md5, sha256, ex));
            bool timeoutFailed = ex is ChartInfoParser.ChartInfoParseTimeoutException;
            return InlineChartInfoBuildResult.CreateParseFailure(
                new LR2SongDBExtended.chart_info_parse_failure
                {
                    md5 = md5,
                    sha256 = sha256,
                    path = target.Path,
                    parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    failure_kind = timeoutFailed ? "timeout" : "parse_failed",
                    exception_type = ex.GetType().Name,
                    message = NormalizePersistedParseFailureMessage(ex.Message),
                    parse_timeout_ms = timeoutFailed ? ResolveTimeoutMilliseconds(parseTimeout) : null,
                    updated_at = DateTime.UtcNow
                },
                timeoutFailed,
                stopwatch.ElapsedMilliseconds,
                snapshot.Length);
        }
    }

    private ChartInfoBackfillResult BackfillChartInfosCore(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<ChartFile> currentCharts,
        string mode,
        Action<int, int, string> reportProgress,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        Action<IReadOnlyList<LR2SongDBExtended.chart_info>> chartInfoRowsCommitted,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> existingRowsSnapshot,
        Action<IReadOnlyList<ChartDigestBackfillEntry>, IReadOnlyList<LR2SongDBExtended.chart_info>, IReadOnlyList<LR2SongDBExtended.chart_info_parse_failure>, IReadOnlyList<string>> chartInfoChunkWriter)
    {
        var result = new ChartInfoBackfillResult
        {
            Mode = string.IsNullOrWhiteSpace(mode) ? "full" : mode
        };
        if (dbGateway == null)
        {
            return result;
        }
        var stopwatchTotal = Stopwatch.StartNew();
        int commitChunkSize = ResolveCommitChunkSize();
        TimeSpan parseTimeout = ResolveParseTimeout();
        List<ChartFile> chartList = [.. (currentCharts ?? []).Where(chart => chart != null)];
        dbGateway.EnsureChartInfoBackfillSchema();
        var existingRowsStopwatch = Stopwatch.StartNew();
        string existingRowsSource;
        Dictionary<string, LR2SongDBExtended.chart_info> existingRows;
        if (existingRowsSnapshot != null)
        {
            existingRowsSource = "index";
            existingRows = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, LR2SongDBExtended.chart_info> pair in existingRowsSnapshot)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                {
                    existingRows[pair.Key] = pair.Value;
                }
            }
        }
        else
        {
            existingRowsSource = "db";
            existingRows = dbGateway.LoadChartInfoMap();
        }
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures = dbGateway.LoadCurrentChartInfoParseFailureMap(parseTimeout);
        existingRowsStopwatch.Stop();
        string existingRowsLogValue = existingRows.Count.ToString();
        var targetBuildStopwatch = Stopwatch.StartNew();
        List<ChartInfoBuildTarget> targets = BuildTargets(chartList, existingRows, currentFailures, result);
        targetBuildStopwatch.Stop();
        result.TargetCount = targets.Count;
        result.WorkerCount = ResolveWorkerCount();
        result.ReaderCount = ChartFileReadPipelinePolicy.ResolveReaderDegree(Environment.ProcessorCount, targets.Count);
        result.QueueCapacity = ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(result.WorkerCount, result.ReaderCount);
        reportProgress?.Invoke(result.TargetCount, 0, string.Empty);
        logInstallPerformance?.Invoke(BuildStartLogMessage(result, existingRowsLogValue, existingRowsSource, existingRowsStopwatch.ElapsedMilliseconds, targetBuildStopwatch.ElapsedMilliseconds, commitChunkSize, parseTimeout, mode));
        if (targets.Count == 0)
        {
            stopwatchTotal.Stop();
            result.TotalMs = stopwatchTotal.ElapsedMilliseconds;
            logInstallPerformance?.Invoke(BuildLogMessage(result));
            return result;
        }

        var queue = new BlockingCollection<QueuedChartBytes>(result.QueueCapacity);
        BlockingCollection<ChartInfoBuildItemResult> itemResults = [];
        BlockingCollection<ChartInfoCommitChunk> commitChunks = [];
        long readTicks = 0L;
        long parseTicks = 0L;
        long fileReadCount = 0L;
        long fileReadBytes = 0L;
        int[] processedCount = new int[1];

        var commitWriter = Task.Run(delegate
        {
            ConsumeCommitChunks(
                dbGateway,
                commitChunks.GetConsumingEnumerable(),
                result,
                logInstallPerformance,
                logInstallPerformanceWarn,
                chartInfoRowsCommitted,
                chartInfoChunkWriter);
        });

        var resultCollector = Task.Run(delegate
        {
            try
            {
                ConsumeBuildResults(
                    itemResults.GetConsumingEnumerable(),
                    commitChunks,
                    result,
                    commitChunkSize,
                    logInstallPerformance,
                    reportProgress,
                    processedCount);
            }
            finally
            {
                commitChunks.CompleteAdding();
            }
        });

        List<Task> workers = [.. Enumerable.Range(0, result.WorkerCount)
            .Select(_ => Task.Run(delegate
            {
                foreach (QueuedChartBytes item in queue.GetConsumingEnumerable())
                {
                    var parseStopwatch = Stopwatch.StartNew();
                    ChartInfoBuildItemResult itemResult = ParseQueuedItem(item, existingRows, currentFailures, logInstallPerformance, logInstallPerformanceWarn, parseTimeout);
                    parseStopwatch.Stop();
                    Interlocked.Add(ref parseTicks, parseStopwatch.ElapsedTicks);
                    itemResult.ParseMs = parseStopwatch.ElapsedMilliseconds;
                    itemResult.ByteCount = item.Bytes.LongLength;
                    itemResults.Add(itemResult);
                }
            }))];

        int nextReadIndex = -1;
        Task[] readers = [.. Enumerable.Range(0, result.ReaderCount)
            .Select(_ => Task.Run(delegate
            {
                while (true)
                {
                    int targetIndex = Interlocked.Increment(ref nextReadIndex);
                    if (targetIndex >= targets.Count)
                    {
                        break;
                    }

                    ChartInfoBuildTarget target = targets[targetIndex];
                    try
                    {
                        var readStopwatch = Stopwatch.StartNew();
                        byte[] bytes = readAllBytes(target.Path);
                        readStopwatch.Stop();
                        Interlocked.Add(ref readTicks, readStopwatch.ElapsedTicks);
                        Interlocked.Increment(ref fileReadCount);
                        Interlocked.Add(ref fileReadBytes, bytes?.LongLength ?? 0L);
                        queue.Add(new QueuedChartBytes(target, bytes));
                    }
                    catch (Exception ex)
                    {
                        logInstallPerformanceWarn?.Invoke(BuildReadFailureLogMessage(target, ex));
                        itemResults.Add(ChartInfoBuildItemResult.CreateReadFailed(target));
                    }
                }
            }))];

        Exception readerException = null;
        try
        {
            Task.WaitAll(readers);
        }
        catch (Exception ex)
        {
            readerException = ex;
        }
        finally
        {
            queue.CompleteAdding();
        }
        Exception workerException = null;
        try
        {
            Task.WaitAll([.. workers]);
        }
        catch (Exception ex)
        {
            workerException = ex;
        }
        finally
        {
            itemResults.CompleteAdding();
        }
        Exception pipelineException = null;
        try
        {
            resultCollector.Wait();
        }
        catch (Exception ex)
        {
            pipelineException = ex;
        }
        try
        {
            commitWriter.Wait();
        }
        catch (Exception ex)
        {
            pipelineException ??= ex;
        }
        if (workerException != null)
        {
            throw workerException;
        }
        if (readerException != null)
        {
            throw readerException;
        }
        if (pipelineException != null)
        {
            throw pipelineException;
        }

        result.ProcessedCount = Volatile.Read(ref processedCount[0]);
        result.FileReadCount = (int)Math.Min(int.MaxValue, Volatile.Read(ref fileReadCount));
        result.FileReadBytes = Volatile.Read(ref fileReadBytes);
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
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        TimeSpan parseTimeout)
    {
        ChartInfoBuildTarget target = item.Target;
        string md5 = string.IsNullOrWhiteSpace(target.Md5) ? ComputeHash(item.Bytes, MD5.Create()) : target.Md5;
        string sha256 = string.IsNullOrWhiteSpace(target.Sha256) ? ComputeHash(item.Bytes, SHA256.Create()) : target.Sha256;
        if (IsCurrentParseFailure(currentFailures, md5))
        {
            return new ChartInfoBuildItemResult(target, sha256, null, reusedExistingRow: false, parseFailed: false, skippedPersistedFailure: true);
        }
        if (IsCurrent(existingRows, sha256))
        {
            return new ChartInfoBuildItemResult(target, sha256, existingRows[sha256], reusedExistingRow: true, parseFailed: false);
        }
        try
        {
            // maintenance.encoding is for list/LR2 song display correction. chart_info must use
            // the parser's beatoraja-compatible default BMS decoding instead of that UI hint.
            ChartInfoParser.ChartInfoParseResult parseResult = ChartInfoParser.ParseBytesDetailed(item.Bytes, target.Path, md5, sha256, encodingName: null, timeout: parseTimeout);
            LogParseDiagnostics(logInstallPerformance, target, md5, sha256, parseResult.Diagnostics);
            LR2SongDBExtended.chart_info row = parseResult.Row;
            return new ChartInfoBuildItemResult(target, sha256, row, reusedExistingRow: false, parseFailed: false);
        }
        catch (Exception ex)
        {
            logInstallPerformanceWarn?.Invoke(BuildParseFailureLogMessage(target, md5, sha256, ex));
            bool timeoutFailed = ex is ChartInfoParser.ChartInfoParseTimeoutException;
            return new ChartInfoBuildItemResult(
                target,
                sha256,
                null,
                reusedExistingRow: false,
                parseFailed: true,
                timeoutFailed: timeoutFailed,
                failureExceptionType: ex.GetType().Name,
                failureMessage: NormalizePersistedParseFailureMessage(ex.Message),
                parseTimeoutMs: ResolveTimeoutMilliseconds(parseTimeout));
        }
    }

    private static void ConsumeBuildResults(
        IEnumerable<ChartInfoBuildItemResult> itemResults,
        BlockingCollection<ChartInfoCommitChunk> commitChunks,
        ChartInfoBackfillResult result,
        int commitChunkSize,
        Action<string> logInstallPerformance,
        Action<int, int, string> reportProgress,
        int[] processedCount)
    {
        var commitBuffer = new ChartInfoCommitBuffer();
        List<long> parseDurations = [];
        List<SlowParseRecord> slowParseRecords = [];
        int parseSucceededCount = 0;
        foreach (ChartInfoBuildItemResult itemResult in itemResults)
        {
            ApplyBuildResult(itemResult, result, commitBuffer, parseDurations, slowParseRecords, ref parseSucceededCount);
            int processed = Interlocked.Increment(ref processedCount[0]);
            result.ProcessedCount = processed;
            reportProgress?.Invoke(result.TargetCount, processed, itemResult.Target?.Path ?? string.Empty);
            if (commitBuffer.TargetCount >= commitChunkSize)
            {
                EnqueueCommitBuffer(commitChunks, commitBuffer);
            }
        }
        EnqueueCommitBuffer(commitChunks, commitBuffer);
        ApplyParseMetrics(result, parseDurations);
        logInstallPerformance?.Invoke(BuildParseDoneLogMessage(result, parseSucceededCount));
        LogSlowParseRecords(logInstallPerformance, slowParseRecords);
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
        if (itemResult.SkippedPersistedFailure)
        {
            result.FailureSkippedCount += itemResult.Target?.OwnerCount ?? 1;
            return;
        }
        if (itemResult.Row != null)
        {
            parseSucceededCount++;
            if (!itemResult.ReusedExistingRow)
            {
                commitBuffer.AddChartInfo(itemResult.Target, itemResult.Row);
            }
            commitBuffer.AddParseFailureDelete(itemResult.Target);
        }
        if (itemResult.ParseFailed)
        {
            result.ParseFailedCount++;
            result.FailedCount++;
            if (itemResult.TimeoutFailed)
            {
                result.TimeoutFailedCount++;
            }
            commitBuffer.AddParseFailure(itemResult);
            result.FailedPaths.Add(itemResult.Target.Path);
        }
    }

    private static void EnqueueCommitBuffer(
        BlockingCollection<ChartInfoCommitChunk> commitChunks,
        ChartInfoCommitBuffer commitBuffer)
    {
        if (!commitBuffer.HasPendingDbRows)
        {
            commitBuffer.Clear();
            return;
        }
        commitChunks.Add(commitBuffer.ToChunk());
        commitBuffer.Clear();
    }

    private static void ConsumeCommitChunks(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<ChartInfoCommitChunk> commitChunks,
        ChartInfoBackfillResult result,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        Action<IReadOnlyList<LR2SongDBExtended.chart_info>> chartInfoRowsCommitted,
        Action<IReadOnlyList<ChartDigestBackfillEntry>, IReadOnlyList<LR2SongDBExtended.chart_info>, IReadOnlyList<LR2SongDBExtended.chart_info_parse_failure>, IReadOnlyList<string>> chartInfoChunkWriter)
    {
        foreach (ChartInfoCommitChunk chunk in commitChunks)
        {
            FlushCommitChunk(dbGateway, chunk, result, logInstallPerformance, logInstallPerformanceWarn, chartInfoRowsCommitted, chartInfoChunkWriter);
        }
    }

    private static void FlushCommitChunk(
        BmsLibraryDbGateway dbGateway,
        ChartInfoCommitChunk commitChunk,
        ChartInfoBackfillResult result,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        Action<IReadOnlyList<LR2SongDBExtended.chart_info>> chartInfoRowsCommitted,
        Action<IReadOnlyList<ChartDigestBackfillEntry>, IReadOnlyList<LR2SongDBExtended.chart_info>, IReadOnlyList<LR2SongDBExtended.chart_info_parse_failure>, IReadOnlyList<string>> chartInfoChunkWriter)
    {
        int chunkNumber = result.CommitChunks + 1;
        logInstallPerformance?.Invoke(BuildCommitStartLogMessage(chunkNumber, commitChunk));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (chartInfoChunkWriter == null)
            {
                throw new InvalidOperationException("Chart-info backfill requires a catalog mutation writer.");
            }
            chartInfoChunkWriter(
                commitChunk.DigestEntries,
                commitChunk.ChartInfoRows,
                commitChunk.ParseFailureRows,
                commitChunk.ParseFailureDeleteMd5s);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logInstallPerformanceWarn?.Invoke(BuildCommitFailureLogMessage(chunkNumber, commitChunk, stopwatch.ElapsedMilliseconds, ex));
            throw;
        }
        stopwatch.Stop();
        result.CommitChunks++;
        result.DbCommitMs += stopwatch.ElapsedMilliseconds;
        result.DbCommitMaxChunkMs = Math.Max(result.DbCommitMaxChunkMs, stopwatch.ElapsedMilliseconds);
        if (commitChunk.ChartInfoRows.Count > 0)
        {
            chartInfoRowsCommitted?.Invoke(commitChunk.ChartInfoRows);
        }
        foreach (PendingDigestApplication application in commitChunk.DigestApplications)
        {
            result.DigestBackfilledCount += application.Target.ApplyDigest(application.Sha256, null, result.DigestChanges);
        }
        foreach (PendingChartInfoApplication application in commitChunk.ChartInfoApplications)
        {
            result.BackfilledCount++;
        }
        result.FailurePersistedCount += commitChunk.ParseFailureRows.Count;
        result.FailureClearedCount += commitChunk.ParseFailureDeleteMd5s.Count;
        logInstallPerformance?.Invoke(BuildCommitDoneLogMessage(chunkNumber, commitChunk, stopwatch.ElapsedMilliseconds));
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
            .OrderByDescending(item => item.ElapsedMs)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
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

    private static void LogParseDiagnostics(
        Action<string> logInstallPerformance,
        ChartInfoBuildTarget target,
        string md5,
        string sha256,
        IEnumerable<ChartInfoParser.ChartInfoParseDiagnostic> diagnostics)
    {
        if (logInstallPerformance == null || diagnostics == null)
        {
            return;
        }
        foreach (ChartInfoParser.ChartInfoParseDiagnostic diagnostic in diagnostics)
        {
            if (!string.Equals(diagnostic?.Code, "BMS_JAVA_INT_TIME_WRAP", StringComparison.Ordinal))
            {
                continue;
            }
            logInstallPerformance(BuildParseDiagnosticLogMessage(target, md5, sha256, diagnostic));
        }
    }

    private static List<ChartInfoBuildTarget> BuildTargets(
        IEnumerable<ChartFile> currentCharts,
        IDictionary<string, LR2SongDBExtended.chart_info> existingRows,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        ChartInfoBackfillResult result)
    {
        var targets = new Dictionary<string, ChartInfoBuildTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in currentCharts ?? [])
        {
            if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(chart.Sha256) && IsCurrent(existingRows, chart.Sha256))
            {
                result.CurrentRowSkippedCount++;
                continue;
            }
            if (ChartInfoBuildTargetMapper.ShouldSkipBackfillTarget(chart))
            {
                continue;
            }
            if (IsCurrentParseFailure(currentFailures, chart.Md5))
            {
                result.FailureSkippedCount++;
                continue;
            }
            string key = ChartInfoBuildTargetMapper.BuildKey(chart);
            if (!targets.TryGetValue(key, out ChartInfoBuildTarget target))
            {
                target = ChartInfoBuildTargetMapper.Create(chart);
                targets[key] = target;
            }
            else
            {
                target.AddChart(chart);
            }
            if (ChartInfoBuildTargetMapper.IsDigestBackfillTarget(chart))
            {
                result.DigestTargetCount++;
            }
        }
        return [.. targets.Values];
    }

    private static bool IsCurrent(IDictionary<string, LR2SongDBExtended.chart_info> existingRows, string sha256)
    {
        return existingRows != null
            && !string.IsNullOrWhiteSpace(sha256)
            && existingRows.TryGetValue(sha256, out LR2SongDBExtended.chart_info row)
            && row != null
            && row.parser_version >= BmsLibraryDbGateway.CurrentChartInfoParserVersion;
    }

    private static bool IsCurrentParseFailure(IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures, string md5)
    {
        return currentFailures != null
            && !string.IsNullOrWhiteSpace(md5)
            && currentFailures.ContainsKey(md5);
    }

    private int ResolveWorkerCount()
    {
        if (workerCountOverride.HasValue)
        {
            return Math.Max(1, workerCountOverride.Value);
        }
        return Math.Max(1, Environment.ProcessorCount - 1);
    }

    private int ResolveCommitChunkSize()
    {
        if (commitChunkSizeOverride.HasValue)
        {
            return Math.Max(1, commitChunkSizeOverride.Value);
        }
        return DefaultCommitChunkSize;
    }

    internal static int ResolveDefaultCommitChunkSize()
    {
        return DefaultCommitChunkSize;
    }

    private TimeSpan ResolveParseTimeout()
    {
        return parseTimeoutOverride ?? DefaultParseTimeout;
    }

    internal TimeSpan CurrentParseTimeout => ResolveParseTimeout();

    private static long TicksToMilliseconds(long ticks)
    {
        return (long)(ticks * 1000.0 / Stopwatch.Frequency);
    }

    private static string ComputeHash(byte[] bytes, HashAlgorithm algorithm)
    {
        using (algorithm)
        {
            byte[] hash = algorithm.ComputeHash(bytes ?? []);
            var builder = new StringBuilder(hash.Length * 2);
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
            + " mode=" + (string.IsNullOrWhiteSpace(result.Mode) ? "full" : result.Mode)
            + " success=" + result.BackfilledCount
            + " failed=" + result.FailedCount
            + " digestBackfilled=" + result.DigestBackfilledCount
            + " infoBackfilled=" + result.BackfilledCount
            + " readFailed=" + result.ReadFailedCount
            + " parseFailed=" + result.ParseFailedCount
            + " timeoutFailed=" + result.TimeoutFailedCount
            + " currentRowSkipped=" + result.CurrentRowSkippedCount
            + " failureSkipped=" + result.FailureSkippedCount
            + " parseFailureSkipped=" + result.FailureSkippedCount
            + " failurePersisted=" + result.FailurePersistedCount
            + " failureCleared=" + result.FailureClearedCount
            + " fileReadCount=" + result.FileReadCount
            + " fileReadBytes=" + result.FileReadBytes
            + " workerCount=" + result.WorkerCount
            + " readerCount=" + result.ReaderCount
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

    private static string BuildStartLogMessage(ChartInfoBackfillResult result, string existingRowCount, string existingRowsSource, long existingRowsLoadMs, long targetBuildMs, int commitChunkSize, TimeSpan parseTimeout, string mode)
    {
        return "chart_info_backfill start"
            + " mode=" + (string.IsNullOrWhiteSpace(mode) ? "full" : mode)
            + " targets=" + result.TargetCount
            + " digestTargets=" + result.DigestTargetCount
            + " currentRowSkipped=" + result.CurrentRowSkippedCount
            + " failureSkipped=" + result.FailureSkippedCount
            + " parseFailureSkipped=" + result.FailureSkippedCount
            + " existingRows=" + (existingRowCount ?? "0")
            + " existingRowsSource=" + (string.IsNullOrWhiteSpace(existingRowsSource) ? "db" : existingRowsSource)
            + " existingRowsLoadMs=" + existingRowsLoadMs
            + " targetBuildMs=" + targetBuildMs
            + " workerCount=" + result.WorkerCount
            + " readerCount=" + result.ReaderCount
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
            + " failureSkipped=" + result.FailureSkippedCount
            + " timeoutFailed=" + result.TimeoutFailedCount
            + " avgParseMs=" + result.ParseAvgMs
            + " maxParseMs=" + result.ParseMaxMs
            + " parseP95Ms=" + result.ParseP95Ms;
    }

    private static string BuildCommitStartLogMessage(int chunkNumber, ChartInfoCommitChunk commitChunk)
    {
        return "chart_info_backfill db_commit_chunk_start"
            + " chunk=" + chunkNumber
            + " targets=" + commitChunk.TargetCount
            + " digestRows=" + commitChunk.DigestEntries.Count
            + " infoRows=" + commitChunk.ChartInfoRows.Count
            + " failureRows=" + commitChunk.ParseFailureRows.Count
            + " failureDeletes=" + commitChunk.ParseFailureDeleteMd5s.Count;
    }

    private static string BuildCommitDoneLogMessage(int chunkNumber, ChartInfoCommitChunk commitChunk, long elapsedMs)
    {
        return "chart_info_backfill db_commit_chunk_done"
            + " chunk=" + chunkNumber
            + " targets=" + commitChunk.TargetCount
            + " digestRows=" + commitChunk.DigestEntries.Count
            + " infoRows=" + commitChunk.ChartInfoRows.Count
            + " failureRows=" + commitChunk.ParseFailureRows.Count
            + " failureDeletes=" + commitChunk.ParseFailureDeleteMd5s.Count
            + " elapsedMs=" + elapsedMs;
    }

    private static string BuildCommitFailureLogMessage(int chunkNumber, ChartInfoCommitChunk commitChunk, long elapsedMs, Exception ex)
    {
        return "chart_info_backfill db_commit_chunk_failed"
            + " chunk=" + chunkNumber
            + " targets=" + commitChunk.TargetCount
            + " digestRows=" + commitChunk.DigestEntries.Count
            + " infoRows=" + commitChunk.ChartInfoRows.Count
            + " failureRows=" + commitChunk.ParseFailureRows.Count
            + " failureDeletes=" + commitChunk.ParseFailureDeleteMd5s.Count
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

    private static string BuildParseDiagnosticLogMessage(ChartInfoBuildTarget target, string md5, string sha256, ChartInfoParser.ChartInfoParseDiagnostic diagnostic)
    {
        return "chart_info_backfill parse_diagnostic"
            + " path=" + QuoteLogValue(target?.Path)
            + " md5=" + QuoteLogValue(md5)
            + " sha256=" + QuoteLogValue(sha256)
            + " parserVersion=" + BmsLibraryDbGateway.CurrentChartInfoParserVersion
            + " parseFailed=false"
            + " severity=" + QuoteLogValue((diagnostic?.Severity.ToString() ?? string.Empty).ToLowerInvariant())
            + " code=" + QuoteLogValue(diagnostic?.Code)
            + " message=" + QuoteLogValue(diagnostic?.Message);
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

    private static string NormalizePersistedParseFailureMessage(string message)
    {
        string normalized = (message ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
        if (normalized.Length <= MaxPersistedParseFailureMessageLength)
        {
            return normalized;
        }
        return normalized.Substring(0, MaxPersistedParseFailureMessageLength);
    }

    private static int ResolveTimeoutMilliseconds(TimeSpan parseTimeout)
    {
        double milliseconds = Math.Ceiling(parseTimeout.TotalMilliseconds);
        if (milliseconds <= 0.0)
        {
            return 0;
        }
        if (milliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }
        return (int)milliseconds;
    }

    private sealed class QueuedChartBytes(ChartInfoBuildTarget target, byte[] bytes)
    {
        public ChartInfoBuildTarget Target { get; } = target;

        public byte[] Bytes { get; } = bytes ?? [];
    }

    internal sealed class InlineChartInfoBuildResult
    {
        private InlineChartInfoBuildResult(
            LR2SongDBExtended.chart_info row,
            bool shouldPersistRow,
            bool currentRowSkipped,
            bool skippedPersistedFailure,
            bool parseFailed,
            bool timeoutFailed,
            LR2SongDBExtended.chart_info_parse_failure parseFailureRow,
            string parseFailureDeleteMd5,
            long parseMs,
            long byteCount)
        {
            Row = row;
            ShouldPersistRow = shouldPersistRow;
            CurrentRowSkipped = currentRowSkipped;
            SkippedPersistedFailure = skippedPersistedFailure;
            ParseFailed = parseFailed;
            TimeoutFailed = timeoutFailed;
            ParseFailureRow = parseFailureRow;
            ParseFailureDeleteMd5 = parseFailureDeleteMd5;
            ParseMs = parseMs;
            ByteCount = byteCount;
        }

        public LR2SongDBExtended.chart_info Row { get; }

        public bool ShouldPersistRow { get; }

        public bool CurrentRowSkipped { get; }

        public bool SkippedPersistedFailure { get; }

        public bool ParseFailed { get; }

        public bool TimeoutFailed { get; }

        public LR2SongDBExtended.chart_info_parse_failure ParseFailureRow { get; }

        public string ParseFailureDeleteMd5 { get; }

        public long ParseMs { get; }

        public long ByteCount { get; }

        public static InlineChartInfoBuildResult CreateSuccess(LR2SongDBExtended.chart_info row, string parseFailureDeleteMd5, long parseMs, long byteCount)
        {
            return new InlineChartInfoBuildResult(
                row,
                shouldPersistRow: true,
                currentRowSkipped: false,
                skippedPersistedFailure: false,
                parseFailed: false,
                timeoutFailed: false,
                parseFailureRow: null,
                parseFailureDeleteMd5: parseFailureDeleteMd5,
                parseMs: parseMs,
                byteCount: byteCount);
        }

        public static InlineChartInfoBuildResult CreateCurrentRowSkipped(LR2SongDBExtended.chart_info row, long byteCount)
        {
            return new InlineChartInfoBuildResult(
                row,
                shouldPersistRow: false,
                currentRowSkipped: true,
                skippedPersistedFailure: false,
                parseFailed: false,
                timeoutFailed: false,
                parseFailureRow: null,
                parseFailureDeleteMd5: null,
                parseMs: 0L,
                byteCount: byteCount);
        }

        public static InlineChartInfoBuildResult CreateFailureSkipped(long byteCount)
        {
            return new InlineChartInfoBuildResult(
                null,
                shouldPersistRow: false,
                currentRowSkipped: false,
                skippedPersistedFailure: true,
                parseFailed: false,
                timeoutFailed: false,
                parseFailureRow: null,
                parseFailureDeleteMd5: null,
                parseMs: 0L,
                byteCount: byteCount);
        }

        public static InlineChartInfoBuildResult CreateParseFailure(LR2SongDBExtended.chart_info_parse_failure parseFailureRow, bool timeoutFailed, long parseMs, long byteCount)
        {
            return new InlineChartInfoBuildResult(
                null,
                shouldPersistRow: false,
                currentRowSkipped: false,
                skippedPersistedFailure: false,
                parseFailed: true,
                timeoutFailed: timeoutFailed,
                parseFailureRow: parseFailureRow,
                parseFailureDeleteMd5: null,
                parseMs: parseMs,
                byteCount: byteCount);
        }
    }

    private sealed class ChartInfoBuildItemResult(
        ChartInfoBuildTarget target,
        string sha256,
        LR2SongDBExtended.chart_info row,
        bool reusedExistingRow,
        bool parseFailed,
        bool timeoutFailed = false,
        bool readFailed = false,
        bool skippedPersistedFailure = false,
        string failureExceptionType = null,
        string failureMessage = null,
        int? parseTimeoutMs = null)
    {
        public ChartInfoBuildTarget Target { get; } = target;

        public string Sha256 { get; } = sha256;

        public LR2SongDBExtended.chart_info Row { get; } = row;

        public bool ReusedExistingRow { get; } = reusedExistingRow;

        public bool ParseFailed { get; } = parseFailed;

        public bool TimeoutFailed { get; } = timeoutFailed;

        public bool ReadFailed { get; } = readFailed;

        public bool SkippedPersistedFailure { get; } = skippedPersistedFailure;

        public string FailureExceptionType { get; } = failureExceptionType;

        public string FailureMessage { get; } = failureMessage;

        public int? ParseTimeoutMs { get; } = parseTimeoutMs;

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
                if (SkippedPersistedFailure)
                {
                    return "skipped_failure";
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
        public List<ChartDigestBackfillEntry> DigestEntries { get; } = [];

        public List<PendingDigestApplication> DigestApplications { get; } = [];

        public List<LR2SongDBExtended.chart_info> ChartInfoRows { get; } = [];

        public List<PendingChartInfoApplication> ChartInfoApplications { get; } = [];

        public List<LR2SongDBExtended.chart_info_parse_failure> ParseFailureRows { get; } = [];

        public List<string> ParseFailureDeleteMd5s { get; } = [];

        public int TargetCount { get; set; }

        public bool HasPendingDbRows => DigestEntries.Count > 0 || ChartInfoRows.Count > 0 || ParseFailureRows.Count > 0 || ParseFailureDeleteMd5s.Count > 0;

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

        public void AddParseFailure(ChartInfoBuildItemResult itemResult)
        {
            if (itemResult?.Target == null || string.IsNullOrWhiteSpace(itemResult.Target.Md5))
            {
                return;
            }
            ParseFailureRows.Add(new LR2SongDBExtended.chart_info_parse_failure
            {
                md5 = itemResult.Target.Md5,
                sha256 = itemResult.Sha256,
                path = itemResult.Target.Path,
                parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                failure_kind = itemResult.TimeoutFailed ? "timeout" : "parse_failed",
                exception_type = itemResult.FailureExceptionType,
                message = itemResult.FailureMessage,
                parse_timeout_ms = itemResult.TimeoutFailed ? itemResult.ParseTimeoutMs : null,
                updated_at = DateTime.UtcNow
            });
        }

        public void AddParseFailureDelete(ChartInfoBuildTarget target)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.Md5))
            {
                return;
            }
            if (!ParseFailureDeleteMd5s.Contains(target.Md5, StringComparer.OrdinalIgnoreCase))
            {
                ParseFailureDeleteMd5s.Add(target.Md5);
            }
        }

        public void Clear()
        {
            DigestEntries.Clear();
            DigestApplications.Clear();
            ChartInfoRows.Clear();
            ChartInfoApplications.Clear();
            ParseFailureRows.Clear();
            ParseFailureDeleteMd5s.Clear();
            TargetCount = 0;
        }

        public ChartInfoCommitChunk ToChunk()
        {
            return new ChartInfoCommitChunk(
                TargetCount,
                [.. DigestEntries],
                [.. DigestApplications],
                [.. ChartInfoRows],
                [.. ChartInfoApplications],
                [.. ParseFailureRows],
                [.. ParseFailureDeleteMd5s]);
        }
    }

    private sealed class ChartInfoCommitChunk(
        int targetCount,
        IReadOnlyList<ChartDigestBackfillEntry> digestEntries,
        IReadOnlyList<ChartInfoBuildService.PendingDigestApplication> digestApplications,
        IReadOnlyList<LR2SongDBExtended.chart_info> chartInfoRows,
        IReadOnlyList<ChartInfoBuildService.PendingChartInfoApplication> chartInfoApplications,
        IReadOnlyList<LR2SongDBExtended.chart_info_parse_failure> parseFailureRows,
        IReadOnlyList<string> parseFailureDeleteMd5s)
    {
        public int TargetCount { get; } = targetCount;

        public IReadOnlyList<ChartDigestBackfillEntry> DigestEntries { get; } = digestEntries ?? [];

        public IReadOnlyList<PendingDigestApplication> DigestApplications { get; } = digestApplications ?? [];

        public IReadOnlyList<LR2SongDBExtended.chart_info> ChartInfoRows { get; } = chartInfoRows ?? [];

        public IReadOnlyList<PendingChartInfoApplication> ChartInfoApplications { get; } = chartInfoApplications ?? [];

        public IReadOnlyList<LR2SongDBExtended.chart_info_parse_failure> ParseFailureRows { get; } = parseFailureRows ?? [];

        public IReadOnlyList<string> ParseFailureDeleteMd5s { get; } = parseFailureDeleteMd5s ?? [];
    }

    private sealed class PendingDigestApplication(ChartInfoBuildTarget target, string sha256)
    {
        public ChartInfoBuildTarget Target { get; } = target;

        public string Sha256 { get; } = sha256 ?? string.Empty;
    }

    private sealed class PendingChartInfoApplication(ChartInfoBuildTarget target, LR2SongDBExtended.chart_info row)
    {
        public ChartInfoBuildTarget Target { get; } = target;

        public LR2SongDBExtended.chart_info Row { get; } = row;
    }

    private sealed class SlowParseRecord(long elapsedMs, string status, long byteCount, string path, string md5, string sha256)
    {
        public long ElapsedMs { get; } = elapsedMs;

        public string Status { get; } = status ?? string.Empty;

        public long ByteCount { get; } = byteCount;

        public string Path { get; } = path ?? string.Empty;

        public string Md5 { get; } = md5 ?? string.Empty;

        public string Sha256 { get; } = sha256 ?? string.Empty;
    }

}
