using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Util.Extensions;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class FileScanParseCommitOwner
{
    private const int DefaultInlineChartInfoBatchSize = 2048;
    private const int DefaultFileDiffCommitChunkSize = 10000;
    private const int DefaultFileDiffCommitWriterQueueCapacity = 2;
    private const int DefaultSlowFileDiffPostParseItemLogThresholdMs = 2000;

    private readonly int? fileDiffParserDegreeOverride;
    private readonly ChartInfoBuildService chartInfoBuildService;
    private readonly int? inlineChartInfoBatchSizeOverride;
    private readonly int? fileDiffCommitChunkSizeOverride;

    internal FileScanParseCommitOwner(
        int? fileDiffParserDegreeOverride,
        ChartInfoBuildService chartInfoBuildService = null,
        int? inlineChartInfoBatchSizeOverride = null,
        int? fileDiffCommitChunkSizeOverride = null)
    {
        this.fileDiffParserDegreeOverride = fileDiffParserDegreeOverride;
        this.chartInfoBuildService = chartInfoBuildService ?? new ChartInfoBuildService();
        this.inlineChartInfoBatchSizeOverride = inlineChartInfoBatchSizeOverride;
        this.fileDiffCommitChunkSizeOverride = fileDiffCommitChunkSizeOverride;
    }


    internal void ApplyFileDiff(
        BmsLibraryDbGateway dbGateway,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<BMSFile> currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs,
        ChartScanResult mergedScanResult,
        bool bmsFileScanSucceeded,
        SongTableFileCheckResult result,
        IBmsLibraryDialogService dialogService,
        Action<string> logEverythingScan,
        Action<int, int, string> reportParseProgress,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        Action<IReadOnlyList<LR2SongDBExtended.chart_info>> inlineChartInfoRowsCommitted,
        bool protectExistingBmsRowsFromLr2SongDbSyncMigration,
        Action<SongTableFileCheckResult> catalogProjectionApplied)
    {
        var stopwatchDiff = Stopwatch.StartNew();
        var stopwatchCurrentIndex = Stopwatch.StartNew();
        var currentBmsByPath = new Dictionary<string, BMSFile>(StringComparer.Ordinal);
        foreach (BMSFile file in currentFiles ?? [])
        {
            if (file == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(file.path) && !currentBmsByPath.ContainsKey(file.path))
            {
                currentBmsByPath[file.path] = file;
            }
        }
        var currentBmsonByPath = new Dictionary<string, LR2SongDBExtended.bmson_song>(StringComparer.Ordinal);
        foreach (LR2SongDBExtended.bmson_song song in currentBmsonSongs ?? [])
        {
            if (song == null || string.IsNullOrWhiteSpace(song.path))
            {
                continue;
            }
            currentBmsonByPath[song.path] = song;
        }
        stopwatchCurrentIndex.Stop();
        result.DiffCurrentIndexMs = stopwatchCurrentIndex.ElapsedMilliseconds;

        var stopwatchScannedSplit = Stopwatch.StartNew();
        var scannedPaths = new HashSet<string>(StringComparer.Ordinal);
        var scannedBmsonPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in mergedScanResult.ChartFilePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            if (IsBmsonChartPath(path))
            {
                scannedBmsonPaths.Add(path);
            }
            else
            {
                scannedPaths.Add(path);
            }
        }
        stopwatchScannedSplit.Stop();
        result.DiffScannedSplitMs = stopwatchScannedSplit.ElapsedMilliseconds;
        result.BmsPathCount = scannedPaths.Count;

        var stopwatchDeleted = Stopwatch.StartNew();
        foreach (string currentPath in currentBmsByPath.Keys)
        {
            if (!scannedPaths.Contains(currentPath))
            {
                result.DeletedPaths.Add(currentPath);
            }
        }
        foreach (string currentPath in currentBmsonByPath.Keys)
        {
            if (!scannedBmsonPaths.Contains(currentPath))
            {
                result.DeletedBmsonPaths.Add(currentPath);
            }
        }
        stopwatchDeleted.Stop();
        result.DiffDeletedMs = stopwatchDeleted.ElapsedMilliseconds;

        HashSet<string> textFileDirectories = mergedScanResult.ChartDirectoriesWithTextFiles ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool textGroupSurfaceAvailable = options?.OperationModeLR2DB == true
            && bmsFileScanSucceeded;
        IReadOnlyDictionary<string, RootFileEnumerationEntry> chartFileEntriesByPath =
            mergedScanResult.ChartFileEntriesByPath ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.Ordinal);
        Dictionary<string, Queue<BMSFile>> movedBmsSourcesByMd5 = BuildQueueByMd5(
            result.DeletedPaths
                .Select(path => currentBmsByPath.TryGetValue(path, out BMSFile file) ? file : null)
                .Where(file => file != null),
            file => file.hash);
        IReadOnlyDictionary<string, Lr2SongUserColumns> movedBmsUserColumnsByDeletedPath =
            dbGateway?.CreateSongUserColumnSnapshot(result.DeletedPaths)
            ?? new Dictionary<string, Lr2SongUserColumns>(StringComparer.Ordinal);
        List<FileDiffParseTarget> bmsParseTargets = [];
        var stopwatchBmsTargets = Stopwatch.StartNew();
        foreach (string path in scannedPaths)
        {
            FileDiffParseTarget target = CreateBmsFileDiffTarget(
                path,
                currentBmsByPath,
                textFileDirectories,
                textGroupSurfaceAvailable,
                chartFileEntriesByPath,
                result,
                protectExistingBmsRowsFromLr2SongDbSyncMigration);
            if (target != null)
            {
                bmsParseTargets.Add(target);
            }
        }
        stopwatchBmsTargets.Stop();
        result.DiffBmsTargetMs = stopwatchBmsTargets.ElapsedMilliseconds;

        List<string> addedOrUpdatedBmsonPaths = [];
        var stopwatchBmsonTargets = Stopwatch.StartNew();
        foreach (string path in scannedBmsonPaths)
        {
            if (!currentBmsonByPath.TryGetValue(path, out LR2SongDBExtended.bmson_song existing))
            {
                addedOrUpdatedBmsonPaths.Add(path);
                continue;
            }

            DateTime lastWriteTimeUtc = ResolveScannedChartLastWriteTimeUtc(
                path,
                chartFileEntriesByPath,
                result,
                isBmson: true);
            if (lastWriteTimeUtc != DateTime.MinValue && existing.updated_at != lastWriteTimeUtc)
            {
                addedOrUpdatedBmsonPaths.Add(path);
            }
        }
        stopwatchBmsonTargets.Stop();
        result.DiffBmsonTargetMs = stopwatchBmsonTargets.ElapsedMilliseconds;
        stopwatchDiff.Stop();
        result.DiffMs = stopwatchDiff.ElapsedMilliseconds;
        result.BmsAddedTargetCount = bmsParseTargets.Count;
        result.BmsDeletedTargetCount = result.DeletedPaths.Count;
        result.BmsonUpsertTargetCount = addedOrUpdatedBmsonPaths.Count;
        result.BmsonDeletedTargetCount = result.DeletedBmsonPaths.Count;
        result.FileDiffParserDegree = ResolveFileDiffParserDegree();
        result.ParseReadBytesEstimate = SaturatingAdd(
            EstimateCurrentFileDiffReadBytes(bmsParseTargets.Select(target => target.Path)),
            EstimateCurrentFileDiffReadBytes(addedOrUpdatedBmsonPaths));


        int parseTargetCount = bmsParseTargets.Count + addedOrUpdatedBmsonPaths.Count;
        int parseProcessedCount = 0;
        if (parseTargetCount > 0)
        {
            reportParseProgress?.Invoke(parseTargetCount, 0, string.Empty);
        }
        result.InlineChartInfoBatchSize = ResolveInlineChartInfoBatchSize();
        result.DbCommitChunkSize = ResolveFileDiffCommitChunkSize();
        var inlineMaintenanceLookupContext = new ResourceHealthLookupContext(
            result.NextDirectoryResourceLookupCache);
        Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentChartInfoParseFailures =
            parseTargetCount > 0 && dbGateway != null
                ? dbGateway.LoadCurrentChartInfoParseFailureMap(chartInfoBuildService.CurrentParseTimeout)
                : new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase);
        using var commitContext = new FileDiffStreamingCommitContext(
            dbGateway,
            options,
            result,
            logInstallPerformance,
            logInstallPerformanceWarn,
            inlineChartInfoRowsCommitted);
        FileDiffParsePipelineResult pipelineResult = RunFileDiffParsePipeline(
            bmsParseTargets,
            addedOrUpdatedBmsonPaths,
            dbGateway,
            currentChartInfoParseFailures,
            result,
            parseTargetCount,
            ref parseProcessedCount,
            dialogService,
            logEverythingScan,
            reportParseProgress,
            logInstallPerformance,
            logInstallPerformanceWarn,
            inlineMaintenanceLookupContext,
            movedBmsSourcesByMd5,
            movedBmsUserColumnsByDeletedPath,
            commitContext);
        result.NewFileParseMs = pipelineResult.BmsParseMs;
        result.BmsParseMs = result.NewFileParseMs;
        result.BmsonParseMs = pipelineResult.BmsonParseMs;
        result.FileDiffReadMs = pipelineResult.ReadMs;
        result.FileDiffDigestMs = pipelineResult.DigestMs;
        result.FileDiffParseMs = pipelineResult.BmsParseMs + pipelineResult.BmsonParseMs;
        result.SnapshotQueueHighWatermark = pipelineResult.SnapshotQueueHighWatermark;
        result.InlineMaintenanceSharedResourceCacheEntries = inlineMaintenanceLookupContext.SharedResourceCacheEntryCount;
        result.InlineMaintenanceResourceSetCacheEntries = inlineMaintenanceLookupContext.ResourceHealthSetCacheEntryCount;
        result.AddedFiles.AddRange(pipelineResult.AddedFiles);
        foreach (string path in pipelineResult.NewlyInsertedBmsPaths)
        {
            result.NewlyInsertedBmsPaths.Add(path);
        }
        result.AddedBmsonSongs.AddRange(pipelineResult.ParsedBmsonSongs);
        result.BmsDateOnlyUpdateCount = pipelineResult.BmsDateOnlyUpdateCount;
        result.BmsTextOnlyUpdateCount = pipelineResult.BmsTextOnlyUpdateCount;
        result.BmsMovedHashRelinkCount = pipelineResult.BmsMovedHashRelinkCount;
        result.BmsMovedHashRelinkAmbiguousCount = pipelineResult.BmsMovedHashRelinkAmbiguousCount;
        foreach (string deletedPath in result.DeletedPaths)
        {
            commitContext.AddDeletedBmsPath(deletedPath);
        }
        foreach (string deletedBmsonPath in result.DeletedBmsonPaths)
        {
            commitContext.AddDeletedBmsonPath(deletedBmsonPath);
        }

        catalogProjectionApplied?.Invoke(result);
        logEverythingScan?.Invoke("bmson_scan totalPaths=" + scannedBmsonPaths.Count + " deleted=" + result.DeletedBmsonPaths.Count + " upserted=" + result.AddedBmsonSongs.Count);
        result.DirectoryCount = result.NextDirectoryResourceLookupCache?.Count ?? 0;
        result.HasDbDiff = result.DeletedPaths.Count > 0
            || result.AddedFiles.Count > 0
            || result.BmsDateOnlyUpdateCount > 0
            || result.BmsTextOnlyUpdateCount > 0
            || result.DeletedBmsonPaths.Count > 0
            || result.AddedBmsonSongs.Count > 0;
        if (result.HasDbDiff)
        {
            commitContext.Flush();
            commitContext.RestoreSongUserColumns(pipelineResult.BmsMovedHashRelinkUserColumnRestores);
            if (inlineChartInfoRowsCommitted != null)
            {
                result.InlineChartInfoRows.Clear();
                result.InlineChartInfoAppliedRows.Clear();
                result.InlineChartInfoParseFailureRows.Clear();
                result.InlineChartInfoParseFailureDeleteMd5s.Clear();
            }
        }

    }

    private FileDiffParsePipelineResult RunFileDiffParsePipeline(
        IReadOnlyList<FileDiffParseTarget> bmsTargets,
        IReadOnlyList<string> bmsonPaths,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentChartInfoParseFailures,
        SongTableFileCheckResult result,
        int parseTargetCount,
        ref int parseProcessedCount,
        IBmsLibraryDialogService dialogService,
        Action<string> logEverythingScan,
        Action<int, int, string> reportParseProgress,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        ResourceHealthLookupContext inlineMaintenanceLookupContext,
        Dictionary<string, Queue<BMSFile>> movedBmsSourcesByMd5,
        IReadOnlyDictionary<string, Lr2SongUserColumns> movedBmsUserColumnsByDeletedPath,
        FileDiffStreamingCommitContext commitContext)
    {
        var pipelineResult = new FileDiffParsePipelineResult();
        if (parseTargetCount <= 0)
        {
            return pipelineResult;
        }
        PerformanceInteraction performanceInteraction =
            PerformanceInteraction.Start("managed_scan_parse");
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                performanceInteraction,
                "owner_started",
                "targets=" + parseTargetCount
                + " bmsTargets=" + (bmsTargets?.Count ?? 0)
                + " bmsonTargets=" + (bmsonPaths?.Count ?? 0));
        }

        List<FileDiffParseTarget> parseTargets = [.. EnumerateFileDiffTargets(bmsTargets, bmsonPaths)];
        int parserDegree = Math.Max(1, result.FileDiffParserDegree);
        int postParseWorkerDegree = ResolveFileDiffPostParseWorkerDegree(parserDegree);
        int readerDegree = ChartFileReadPipelinePolicy.ResolveReaderDegree(Environment.ProcessorCount, parseTargets.Count);
        int chartInfoBatchSize = Math.Max(1, result.InlineChartInfoBatchSize);
        int readQueueCapacity = ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(parserDegree, readerDegree);
        int parsedQueueCapacity = ResolveFileDiffParsedQueueCapacity(parserDegree);
        int postParseQueueCapacity = ResolveFileDiffPostParseQueueCapacity(postParseWorkerDegree);
        int postParseResultQueueCapacity = Math.Max(postParseWorkerDegree * 2, postParseWorkerDegree + 1);
        bool streamCommitChunks = commitContext != null;
        int commitQueueCapacity = streamCommitChunks
            ? Math.Max(1, Math.Min(4, parserDegree))
            : 0;
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256 =
            LoadFileDiffCurrentChartInfoRows(dbGateway, parseTargets.Count, chartInfoBatchSize, logInstallPerformance);
        if (currentChartInfoRowsBySha256 == null && parseTargets.Count > 1)
        {
            currentChartInfoRowsBySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
            logInstallPerformance?.Invoke("file_diff_chart_info_snapshot"
                + " status=suppressed"
                + " reason=multi_post_parse_without_snapshot"
                + " parseTargets=" + parseTargets.Count
                + " inlineChartInfoBatchSize=" + chartInfoBatchSize);
        }
        result.FileDiffReaderDegree = readerDegree;
        result.FileDiffPostParseWorkerDegree = postParseWorkerDegree;
        result.ReadQueueCapacity = readQueueCapacity;
        result.ParsedQueueCapacity = parsedQueueCapacity;
        result.PostParseQueueCapacity = postParseQueueCapacity;
        result.PostParseResultQueueCapacity = postParseResultQueueCapacity;
        result.CommitQueueCapacity = commitQueueCapacity;
        result.CommitStreamingEnabled = streamCommitChunks;
        result.CommitStreamingBarrierReason = streamCommitChunks
            ? "none"
            : commitContext == null
                ? "no_commit_context"
                : "no_commit_context";
        result.InlineMaintenanceDegree = 1;
        var readQueue = new BlockingCollection<FileDiffReadCandidate>(readQueueCapacity);
        var parsedQueue = new BlockingCollection<FileDiffParsedCandidate>(parsedQueueCapacity);
        var postParseQueue = new BlockingCollection<FileDiffPostParseWorkItem>(postParseQueueCapacity);
        var postParseResultQueue = new BlockingCollection<FileDiffPostParseResult>(postParseResultQueueCapacity);
        BlockingCollection<FileScanDiffCommitChunk> commitQueue = streamCommitChunks
            ? new BlockingCollection<FileScanDiffCommitChunk>(commitQueueCapacity)
            : [];
        long readTicks = 0L;
        long digestTicks = 0L;
        long bmsParseTicks = 0L;
        long bmsonParseTicks = 0L;
        long readerOutputWaitTicks = 0L;
        long parserOutputWaitTicks = 0L;
        long postParseQueueWaitTicks = 0L;
        long postParseOutputWaitTicks = 0L;
        long commitQueueWaitTicks = 0L;
        long postParseTicks = 0L;
        long postParseWallStartTimestamp = 0L;
        long postParseWallEndTimestamp = 0L;
        long postParseMaxItemTicks = 0L;
        long inlineChartInfoWallTicks = 0L;
        long inlineMaintenanceWallTicks = 0L;
        long inlineBmsMaintenanceWallTicks = 0L;
        long inlineBmsonMaintenanceWallTicks = 0L;
        int postParseWorkItemCount = 0;
        int postParseProgressCount = parseProcessedCount;
        void ReportPostParsePreparedProgress(string path)
        {
            int processed = Interlocked.Increment(ref postParseProgressCount);
            reportParseProgress?.Invoke(parseTargetCount, Math.Min(parseTargetCount, processed), path ?? string.Empty);
        }
        int snapshotQueueHighWatermark = 0;
        Exception postParseException = null;
        var pipelineException = new PipelineExceptionSignal();
        var folderParentHashCache = new Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache();
        var parsedCommitChunks = new List<FileScanDiffCommitChunk>();

        Task[] postParseWorkerTasks = [.. Enumerable.Range(0, postParseWorkerDegree)
            .Select(_ => Task.Run(delegate
            {
                try
                {
                    foreach (FileDiffPostParseWorkItem workItem in postParseQueue.GetConsumingEnumerable())
                    {
                        Interlocked.CompareExchange(ref postParseWallStartTimestamp, Stopwatch.GetTimestamp(), 0L);
                        FileDiffPostParseResult postParseResult = BuildFileDiffPostParseResult(
                            workItem.Sequence,
                            workItem.ParsedCandidate,
                            dbGateway,
                            currentChartInfoParseFailures,
                            parserDegree,
                            chartInfoBatchSize,
                            Math.Max(1, result.InlineMaintenanceDegree),
                            logInstallPerformance,
                            logInstallPerformanceWarn,
                            inlineMaintenanceLookupContext,
                            currentChartInfoRowsBySha256,
                            ReportPostParsePreparedProgress);
                        AddWithWait(postParseResultQueue, postParseResult, ref postParseOutputWaitTicks, pipelineException);
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref postParseException, ex);
                    CompleteAddingSilently(postParseQueue);
                    CompleteAddingSilently(postParseResultQueue);
                    throw;
                }
            }))];
        Task postParseWorkerCompletionTask = Task.WhenAll(postParseWorkerTasks).ContinueWith(_ => CompleteAddingSilently(postParseResultQueue));

        var postParseCollectorTask = Task.Run(delegate
        {
            try
            {
                int nextPostParseSequence = 0;
                var pendingPostParseResults = new SortedDictionary<int, FileDiffPostParseResult>();
                foreach (FileDiffPostParseResult postParseResult in postParseResultQueue.GetConsumingEnumerable())
                {
                    if (postParseResult == null)
                    {
                        continue;
                    }
                    pendingPostParseResults[postParseResult.Sequence] = postParseResult;
                    while (pendingPostParseResults.TryGetValue(nextPostParseSequence, out FileDiffPostParseResult current))
                    {
                        pendingPostParseResults.Remove(nextPostParseSequence);
                        ApplyOrderedFileDiffPostParseResult(
                            current,
                            result,
                            pipelineResult,
                            dialogService,
                            logEverythingScan,
                            logInstallPerformance,
                            commitQueue,
                            pipelineException,
                            ref commitQueueWaitTicks,
                            ref postParseWorkItemCount,
                            ref postParseTicks,
                            ref postParseMaxItemTicks,
                            ref inlineChartInfoWallTicks,
                            ref inlineMaintenanceWallTicks,
                            ref inlineBmsMaintenanceWallTicks,
                            ref inlineBmsonMaintenanceWallTicks);
                        Interlocked.Exchange(ref postParseWallEndTimestamp, Stopwatch.GetTimestamp());
                        nextPostParseSequence++;
                    }
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref postParseException, ex);
                pipelineException.Set(ex);
                CompleteAddingSilently(postParseQueue);
                CompleteAddingSilently(postParseResultQueue);
                throw;
            }
            finally
            {
                CompleteAddingSilently(commitQueue);
            }
        });
        var commitCollectorTask = Task.Run(delegate
        {
            try
            {
                foreach (FileScanDiffCommitChunk chunk in commitQueue.GetConsumingEnumerable())
                {
                    if (streamCommitChunks)
                    {
                        commitContext.AddChunk(chunk);
                    }
                    else
                    {
                        parsedCommitChunks.Add(chunk);
                    }
                }
                if (streamCommitChunks)
                {
                    commitContext.Flush();
                }
            }
            catch (Exception ex)
            {
                pipelineException.Set(ex);
                try
                {
                    commitQueue.CompleteAdding();
                }
                catch (InvalidOperationException)
                {
                }
                throw;
            }
            finally
            {
                if (streamCommitChunks)
                {
                    commitContext.ReleaseSongDb();
                }
            }
        });

        int nextReadIndex = -1;
        Task[] readerTasks = [.. Enumerable.Range(0, readerDegree)
            .Select(_ => Task.Run(delegate
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref nextReadIndex);
                    if (index >= parseTargets.Count)
                    {
                        break;
                    }

                    FileDiffReadCandidate candidate = ReadFileDiffTarget(parseTargets[index], ref readTicks);
                    AddWithWait(readQueue, candidate, ref readerOutputWaitTicks, pipelineException);
                    UpdateHighWatermark(ref snapshotQueueHighWatermark, readQueue.Count);
                }
            }))];
        Task readerCompletionTask = Task.WhenAll(readerTasks).ContinueWith(_ => readQueue.CompleteAdding());

        Task[] workerTasks = [.. Enumerable.Range(0, parserDegree)
            .Select(_ => Task.Run(delegate
            {
                foreach (FileDiffReadCandidate readCandidate in readQueue.GetConsumingEnumerable())
                {
                    FileDiffParsedCandidate parsedCandidate = ParseFileDiffCandidate(readCandidate, folderParentHashCache, ref digestTicks, ref bmsParseTicks, ref bmsonParseTicks);
                    AddWithWait(parsedQueue, parsedCandidate, ref parserOutputWaitTicks, pipelineException);
                }
            }))];
        Task parserCompletionTask = Task.WhenAll(workerTasks).ContinueWith(_ => parsedQueue.CompleteAdding());

        Exception pipelineFailure = null;
        try
        {
            int postParseWorkItemSequence = 0;
            foreach (FileDiffParsedCandidate parsedCandidate in parsedQueue.GetConsumingEnumerable())
            {
                EnqueuePostParseWorkItem(
                    postParseQueue,
                    new FileDiffPostParseWorkItem(postParseWorkItemSequence++, parsedCandidate),
                    pipelineException,
                    ref postParseQueueWaitTicks,
                    ref postParseException);
            }
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        finally
        {
            CompleteAddingSilently(postParseQueue);
            if (pipelineFailure != null)
            {
                CompleteAddingSilently(readQueue);
                CompleteAddingSilently(parsedQueue);
                CompleteAddingSilently(commitQueue);
            }
        }

        try
        {
            Task.WaitAll(readerTasks);
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            readerCompletionTask.Wait();
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            Task.WaitAll(workerTasks);
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            parserCompletionTask.Wait();
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            Task.WaitAll(postParseWorkerTasks);
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            postParseWorkerCompletionTask.Wait();
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            postParseCollectorTask.Wait();
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        try
        {
            commitCollectorTask.Wait();
        }
        catch (Exception ex)
        {
            CapturePipelineException(ex, pipelineException, ref pipelineFailure);
        }
        if (pipelineFailure != null)
        {
            throw new AggregateException(pipelineFailure);
        }
        PrepareMovedBmsUserColumnRestores(
            pipelineResult,
            movedBmsSourcesByMd5,
            movedBmsUserColumnsByDeletedPath,
            logInstallPerformanceWarn);
        foreach (FileScanDiffCommitChunk chunk in parsedCommitChunks)
        {
            commitContext?.AddChunk(chunk);
        }
        if (!streamCommitChunks)
        {
            commitContext?.Flush();
        }
        parseProcessedCount = Math.Max(parseProcessedCount, Volatile.Read(ref postParseProgressCount));

        pipelineResult.ReadMs = TicksToMilliseconds(readTicks);
        pipelineResult.DigestMs = TicksToMilliseconds(digestTicks);
        pipelineResult.BmsParseMs = TicksToMilliseconds(bmsParseTicks);
        pipelineResult.BmsonParseMs = TicksToMilliseconds(bmsonParseTicks);
        pipelineResult.SnapshotQueueHighWatermark = snapshotQueueHighWatermark;
        result.ReaderOutputWaitMs = TicksToMilliseconds(readerOutputWaitTicks);
        result.ParserOutputWaitMs = TicksToMilliseconds(parserOutputWaitTicks);
        result.PostParseQueueWaitMs = TicksToMilliseconds(postParseQueueWaitTicks);
        result.PostParseOutputWaitMs = TicksToMilliseconds(postParseOutputWaitTicks);
        result.CommitQueueWaitMs = TicksToMilliseconds(commitQueueWaitTicks);
        result.PostParseWorkItemCount = postParseWorkItemCount;
        long postParseWallStart = Interlocked.Read(ref postParseWallStartTimestamp);
        long postParseWallEnd = Interlocked.Read(ref postParseWallEndTimestamp);
        result.PostParseWallMs = postParseWallStart > 0L && postParseWallEnd >= postParseWallStart
            ? TicksToMilliseconds(postParseWallEnd - postParseWallStart)
            : 0L;
        result.PostParseMaxItemMs = TicksToMilliseconds(postParseMaxItemTicks);
        result.InlineChartInfoWallMs = TicksToMilliseconds(inlineChartInfoWallTicks);
        result.InlineMaintenanceWallMs = TicksToMilliseconds(inlineMaintenanceWallTicks);
        result.InlineBmsMaintenanceWallMs = TicksToMilliseconds(inlineBmsMaintenanceWallTicks);
        result.InlineBmsonMaintenanceWallMs = TicksToMilliseconds(inlineBmsonMaintenanceWallTicks);
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(
                performanceInteraction,
                "snapshot_query_projection",
                "targets=" + parseTargetCount
                + " readMs=" + pipelineResult.ReadMs
                + " digestMs=" + pipelineResult.DigestMs
                + " bmsParseMs=" + pipelineResult.BmsParseMs
                + " bmsonParseMs=" + pipelineResult.BmsonParseMs
                + " postParseMs=" + result.PostParseWallMs
                + " commitChunks=" + result.DbCommitChunks);
        }
        return pipelineResult;
    }

    private static IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> LoadFileDiffCurrentChartInfoRows(
        BmsLibraryDbGateway dbGateway,
        int parseTargetCount,
        int inlineChartInfoBatchSize,
        Action<string> logInstallPerformance)
    {
        if (dbGateway == null || parseTargetCount <= 1)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        Dictionary<string, LR2SongDBExtended.chart_info> rows = dbGateway.TryLoadCurrentChartInfoMapReadOnly();
        stopwatch.Stop();
        if (rows == null)
        {
            logInstallPerformance?.Invoke("file_diff_chart_info_snapshot"
                + " status=skipped"
                + " reason=read_only_schema_not_current"
                + " parseTargets=" + parseTargetCount
                + " inlineChartInfoBatchSize=" + inlineChartInfoBatchSize
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return null;
        }
        logInstallPerformance?.Invoke("file_diff_chart_info_snapshot"
            + " status=loaded"
            + " rows=" + rows.Count
            + " parseTargets=" + parseTargetCount
            + " inlineChartInfoBatchSize=" + inlineChartInfoBatchSize
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        return rows;
    }

    private static IEnumerable<FileDiffParseTarget> EnumerateFileDiffTargets(IReadOnlyList<FileDiffParseTarget> bmsTargets, IReadOnlyList<string> bmsonPaths)
    {
        foreach (FileDiffParseTarget target in bmsTargets ?? [])
        {
            if (target != null)
            {
                yield return target;
            }
        }
        foreach (string path in bmsonPaths ?? [])
        {
            yield return new FileDiffParseTarget(FileDiffChartKind.Bmson, path);
        }
    }

    internal static FileDiffParseTarget CreateBmsFileDiffTarget(
        string path,
        IReadOnlyDictionary<string, BMSFile> currentBmsByPath,
        ISet<string> textFileDirectories,
        bool textGroupSurfaceAvailable,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> chartFileEntriesByPath,
        SongTableFileCheckResult result,
        bool protectExistingBmsRowsFromLr2SongDbSyncMigration)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        if (currentBmsByPath == null || !currentBmsByPath.TryGetValue(path, out BMSFile existing))
        {
            int? scannedTextFlag = textGroupSurfaceAvailable
                ? ResolveTextGroupFlag(path, textFileDirectories)
                : null;
            return new FileDiffParseTarget(FileDiffChartKind.Bms, path, null, scannedTextFlag.GetValueOrDefault());
        }
        if (protectExistingBmsRowsFromLr2SongDbSyncMigration)
        {
            result.BmsLegacyExistingProtectedCount++;
            return null;
        }
        int? existingTextFlag = textGroupSurfaceAvailable
            ? ResolveTextGroupFlag(path, textFileDirectories)
            : null;
        DateTime lastWriteTimeUtc = ResolveScannedChartLastWriteTimeUtc(
            path,
            chartFileEntriesByPath,
            result,
            isBmson: false);
        if (lastWriteTimeUtc == DateTime.MinValue)
        {
            return null;
        }
        int currentDate = Lr2SongRowEnricher.ToLr2UnixSeconds(lastWriteTimeUtc);
        int targetTextFlag = existingTextFlag ?? existing.txt.GetValueOrDefault();
        bool textChanged = existingTextFlag.HasValue
            && existing.txt.GetValueOrDefault() != existingTextFlag.Value;
        if (existing.date == currentDate && !textChanged)
        {
            return null;
        }
        return new FileDiffParseTarget(FileDiffChartKind.Bms, path, existing, targetTextFlag);
    }

    internal static DateTime ResolveScannedChartLastWriteTimeUtc(
        string path,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> chartFileEntriesByPath,
        SongTableFileCheckResult result,
        bool isBmson)
    {
        if (!string.IsNullOrWhiteSpace(path)
            && chartFileEntriesByPath != null
            && chartFileEntriesByPath.TryGetValue(path, out RootFileEnumerationEntry entry)
            && entry?.LastWriteTimeUtc != null)
        {
            return entry.LastWriteTimeUtc.Value;
        }

        if (isBmson)
        {
            if (result != null)
            {
                result.BmsonMtimeFallbackCount++;
            }
        }
        else if (result != null)
        {
            result.BmsMtimeFallbackCount++;
        }

        return SafeGetLastWriteTimeUtc(path);
    }

    private static int ResolveTextGroupFlag(string path, ISet<string> textFileDirectories)
    {
        if (string.IsNullOrWhiteSpace(path) || textFileDirectories == null || textFileDirectories.Count == 0)
        {
            return 0;
        }
        string directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory) && textFileDirectories.Contains(directory) ? 1 : 0;
    }

    internal static bool IsBmsonChartPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(".bmson", StringComparison.OrdinalIgnoreCase);
    }

    private static FileDiffReadCandidate ReadFileDiffTarget(FileDiffParseTarget target, ref long readTicks)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            ChartFileReadBuffer buffer = ChartFileContentReader.ReadBuffer(target.Path);
            stopwatch.Stop();
            Interlocked.Add(ref readTicks, stopwatch.ElapsedTicks);
            return FileDiffReadCandidate.CreateSuccess(target.Kind, target.Path, buffer, target.ExistingBmsFile, target.TextFlag);
        }
        catch (Exception ex) when (IsRecoverableChartFileIoException(ex))
        {
            return FileDiffReadCandidate.CreateFailure(target.Kind, target.Path, target.ExistingBmsFile, target.TextFlag, ex);
        }
    }

    private static FileDiffParsedCandidate ParseFileDiffCandidate(
        FileDiffReadCandidate candidate,
        Lr2SongFolderParentNormalizer.Lr2FolderParentHashCache folderParentHashCache,
        ref long digestTicks,
        ref long bmsParseTicks,
        ref long bmsonParseTicks)
    {
        ChartFileSnapshot snapshot = CreateFileDiffSnapshot(candidate, ref digestTicks);
        if (candidate.Kind == FileDiffChartKind.Bms)
        {
            if (candidate.Exception != null)
            {
                return FileDiffParsedCandidate.FromBms(InlineBmsParseCandidate.CreateFailure(candidate.Path, candidate.ExistingBmsFile, candidate.Exception, "read"));
            }
            try
            {
                var stopwatch = Stopwatch.StartNew();
                BMSFile file = Lr2SongRowEnricher.CreateParsedSongRowFromSnapshot(
                    snapshot,
                    candidate.TextFlag,
                    candidate.ExistingBmsFile,
                    folderParentHashCache);
                stopwatch.Stop();
                Interlocked.Add(ref bmsParseTicks, stopwatch.ElapsedTicks);
                return FileDiffParsedCandidate.FromBms(InlineBmsParseCandidate.CreateSuccess(candidate.Path, snapshot, file, candidate.ExistingBmsFile));
            }
            catch (Exception ex) when (IsRecoverableChartFileIoException(ex))
            {
                return FileDiffParsedCandidate.FromBms(InlineBmsParseCandidate.CreateFailure(candidate.Path, candidate.ExistingBmsFile, ex, "parse"));
            }
        }

        if (candidate.Exception != null)
        {
            return FileDiffParsedCandidate.FromBmson(InlineBmsonParseCandidate.CreateFailure(candidate.Path, candidate.Exception, "read"));
        }
        try
        {
            var stopwatch = Stopwatch.StartNew();
            LR2SongDBExtended.bmson_song song = BmsonSongParser.ParseSnapshot(snapshot);
            stopwatch.Stop();
            Interlocked.Add(ref bmsonParseTicks, stopwatch.ElapsedTicks);
            return FileDiffParsedCandidate.FromBmson(InlineBmsonParseCandidate.CreateSuccess(candidate.Path, snapshot, song));
        }
        catch (Exception ex)
        {
            return FileDiffParsedCandidate.FromBmson(InlineBmsonParseCandidate.CreateFailure(candidate.Path, ex, "parse"));
        }
    }

    private static bool IsRecoverableChartFileIoException(Exception ex)
    {
        return ex is IOException
            || ex is UnauthorizedAccessException
            || ex is ArgumentException
            || ex is NotSupportedException
            || ex is PathTooLongException;
    }

    private static ChartFileSnapshot CreateFileDiffSnapshot(FileDiffReadCandidate candidate, ref long digestTicks)
    {
        if (candidate?.Buffer == null)
        {
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        ChartFileSnapshot snapshot = ChartFileContentReader.CreateSnapshot(candidate.Buffer);
        stopwatch.Stop();
        Interlocked.Add(ref digestTicks, stopwatch.ElapsedTicks);
        return snapshot;
    }

    private FileDiffPostParseResult BuildFileDiffPostParseResult(
        int sequence,
        FileDiffParsedCandidate parsedCandidate,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentChartInfoParseFailures,
        int chartInfoParserDegree,
        int inlineChartInfoBatchSize,
        int inlineMaintenanceDegree,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        ResourceHealthLookupContext inlineMaintenanceLookupContext,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256,
        Action<string> reportPostParsePreparedProgress)
    {
        var postResult = new FileDiffPostParseResult(sequence);
        FileDiffPostParseItemMetrics metrics = postResult.Metrics;
        InlineBmsParseCandidate bmsCandidate = parsedCandidate?.BmsCandidate;
        InlineBmsonParseCandidate bmsonCandidate = parsedCandidate?.BmsonCandidate;
        metrics.BmsCount = bmsCandidate != null ? 1 : 0;
        metrics.BmsonCount = bmsonCandidate != null ? 1 : 0;
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            if (bmsCandidate == null && bmsonCandidate == null)
            {
                return postResult;
            }

            List<InlineBmsParseCandidate> bmsItems = bmsCandidate == null
                ? []
                : [bmsCandidate];
            List<InlineBmsParseCandidate> fullBmsBatch = bmsItems.Count == 0
                ? []
                : [.. bmsItems.Where(candidate => !IsBmsDateOnlyCandidate(candidate))];
            List<InlineBmsonParseCandidate> bmsonItems = bmsonCandidate == null
                ? []
                : [bmsonCandidate];
            var chartInfoStopwatch = Stopwatch.StartNew();
            ChartInfoInlineBuildResult bmsChartInfoResult = BuildInlineBmsChartInfo(
                fullBmsBatch,
                dbGateway,
                currentChartInfoParseFailures,
                chartInfoParserDegree,
                inlineChartInfoBatchSize,
                currentChartInfoRowsBySha256,
                logInstallPerformance,
                logInstallPerformanceWarn);
            ChartInfoInlineBuildResult bmsonChartInfoResult = BuildInlineBmsonChartInfo(
                bmsonItems,
                dbGateway,
                currentChartInfoParseFailures,
                chartInfoParserDegree,
                inlineChartInfoBatchSize,
                currentChartInfoRowsBySha256,
                logInstallPerformance,
                logInstallPerformanceWarn);
            AddInlineChartInfoBuildResult(postResult.ChartInfoResult, bmsChartInfoResult);
            AddInlineChartInfoBuildResult(postResult.ChartInfoResult, bmsonChartInfoResult);
            chartInfoStopwatch.Stop();
            metrics.ChartInfoTicks = chartInfoStopwatch.ElapsedTicks;

            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> chartInfoByMd5 = BuildQueueByMd5(postResult.ChartInfoResult.ChartInfoRows, row => row?.md5);
            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> appliedChartInfoByMd5 = BuildQueueByMd5(postResult.ChartInfoResult.AppliedRows, row => row?.md5);
            Dictionary<string, Queue<LR2SongDBExtended.chart_info_parse_failure>> failureByMd5 = BuildQueueByMd5(postResult.ChartInfoResult.ParseFailureRows, row => row?.md5);
            Dictionary<string, LR2SongDBExtended.chart_info> appliedChartInfoByPath = BuildAppliedChartInfoByPath(fullBmsBatch, postResult.ChartInfoResult.AppliedRows);
            var failureDeletes = new HashSet<string>(postResult.ChartInfoResult.ParseFailureDeleteMd5s, StringComparer.OrdinalIgnoreCase);
            FileScanDiffCommitChunk commitChunk = postResult.CommitChunk;
            var maintenanceStopwatch = Stopwatch.StartNew();
            var bmsMaintenanceStopwatch = Stopwatch.StartNew();
            InlineMaintenanceItemResult[] bmsMaintenanceResults = BuildInlineBmsMaintenanceBatch(
                fullBmsBatch,
                inlineMaintenanceLookupContext,
                inlineMaintenanceDegree,
                logInstallPerformanceWarn);
            bmsMaintenanceStopwatch.Stop();
            var bmsonMaintenanceStopwatch = Stopwatch.StartNew();
            InlineMaintenanceItemResult[] bmsonMaintenanceResults = BuildInlineBmsonMaintenanceBatch(
                bmsonItems,
                inlineMaintenanceLookupContext,
                inlineMaintenanceDegree,
                logInstallPerformanceWarn);
            bmsonMaintenanceStopwatch.Stop();
            maintenanceStopwatch.Stop();
            metrics.MaintenanceTicks = maintenanceStopwatch.ElapsedTicks;
            metrics.BmsMaintenanceTicks = bmsMaintenanceStopwatch.ElapsedTicks;
            metrics.BmsonMaintenanceTicks = bmsonMaintenanceStopwatch.ElapsedTicks;
            postResult.MaintenanceResults.AddRange(bmsMaintenanceResults.Where(item => item != null));
            postResult.MaintenanceResults.AddRange(bmsonMaintenanceResults.Where(item => item != null));
            metrics.HealthMs = SumInlineMaintenanceHealthMs(bmsMaintenanceResults) + SumInlineMaintenanceHealthMs(bmsonMaintenanceResults);
            metrics.EncodingMs = SumInlineMaintenanceEncodingMs(bmsMaintenanceResults);
            metrics.EncodingReloadMs = SumInlineMaintenanceEncodingReloadMs(bmsMaintenanceResults);
            metrics.EncodingMaxMs = MaxInlineMaintenanceEncodingMs(bmsMaintenanceResults);
            metrics.CacheHitCount = SumInlineMaintenanceCacheHitCount(bmsMaintenanceResults) + SumInlineMaintenanceCacheHitCount(bmsonMaintenanceResults);
            metrics.ResourceIndexHitCount = SumInlineMaintenanceResourceIndexHitCount(bmsMaintenanceResults) + SumInlineMaintenanceResourceIndexHitCount(bmsonMaintenanceResults);
            metrics.FileExistsFallbackCount = SumInlineMaintenanceFileExistsFallbackCount(bmsMaintenanceResults) + SumInlineMaintenanceFileExistsFallbackCount(bmsonMaintenanceResults);

            if (bmsItems.Count > 0)
            {
                foreach (InlineBmsParseCandidate candidate in bmsItems)
                {
                    if (IsBmsDateOnlyCandidate(candidate))
                    {
                        int date = candidate.File.date.GetValueOrDefault();
                        int textFlag = candidate.File.txt.GetValueOrDefault();
                        bool dateChanged = candidate.ExistingFile.date != date;
                        bool textChanged = candidate.ExistingFile.txt.GetValueOrDefault() != textFlag;
                        candidate.ExistingFile.date = date;
                        candidate.ExistingFile.SetTextGroupFlag(textFlag);
                        if (dateChanged)
                        {
                            postResult.BmsDateOnlyUpdateCount++;
                        }
                        if (textChanged)
                        {
                            postResult.BmsTextOnlyUpdateCount++;
                        }
                        if (dateChanged || textChanged)
                        {
                            commitChunk.AddUpdatedBmsMetadata(candidate.File.path, date, textChanged ? textFlag : null);
                        }
                        reportPostParsePreparedProgress?.Invoke(candidate.Path);
                    }
                }
                for (int i = 0; i < fullBmsBatch.Count; i++)
                {
                    InlineBmsParseCandidate candidate = fullBmsBatch[i];
                    if (candidate.Exception != null)
                    {
                        postResult.BmsParseFailures.Add(candidate);
                    }
                    if (candidate.File != null)
                    {
                        postResult.TrackBmsRelinkDestinationCandidate(candidate);
                        if (bmsMaintenanceResults != null && i < bmsMaintenanceResults.Length && bmsMaintenanceResults[i]?.Succeeded == true)
                        {
                            commitChunk.AddMaintenanceInfoRow(candidate.File.maintenanceInfo);
                        }
                        Lr2SongRowEnricher.EnrichFromChartInfo(candidate.File, ResolveAppliedChartInfo(candidate, appliedChartInfoByPath));
                        postResult.SuccessfullyReplacedBmsPaths.Add(candidate.Path);
                        postResult.AddedFiles.Add(candidate.File);
                        if (candidate.ExistingFile == null && !string.IsNullOrWhiteSpace(candidate.File.path))
                        {
                            postResult.NewlyInsertedBmsPaths.Add(candidate.File.path);
                        }
                        commitChunk.AddAddedBmsFile(candidate.File);
                        AttachInlineChartInfoRows(commitChunk, candidate.File.hash, chartInfoByMd5, appliedChartInfoByMd5, failureByMd5, failureDeletes);
                        reportPostParsePreparedProgress?.Invoke(candidate.Path);
                    }
                    else
                    {
                        reportPostParsePreparedProgress?.Invoke(candidate.Path);
                    }
                }
            }
            if (bmsonItems.Count > 0)
            {
                for (int i = 0; i < bmsonItems.Count; i++)
                {
                    InlineBmsonParseCandidate candidate = bmsonItems[i];
                    if (candidate.Exception != null)
                    {
                        postResult.BmsonParseFailures.Add(candidate);
                    }
                    if (candidate.Song != null)
                    {
                        if (bmsonMaintenanceResults != null && i < bmsonMaintenanceResults.Length && bmsonMaintenanceResults[i]?.Succeeded == true)
                        {
                            commitChunk.AddMaintenanceInfoRow(candidate.Song.MaintenanceInfo);
                        }
                        postResult.SuccessfullyParsedBmsonPaths.Add(candidate.Path);
                        postResult.ParsedBmsonSongs.Add(candidate.Song);
                        commitChunk.AddUpsertBmsonSong(candidate.Song);
                        AttachInlineChartInfoRows(commitChunk, candidate.Song.md5, chartInfoByMd5, appliedChartInfoByMd5, failureByMd5, failureDeletes);
                        reportPostParsePreparedProgress?.Invoke(candidate.Path);
                    }
                    else
                    {
                        reportPostParsePreparedProgress?.Invoke(candidate.Path);
                    }
                }
            }
            AddRemainingInlineRows([], ref commitChunk, chartInfoByMd5, appliedChartInfoByMd5, failureByMd5, failureDeletes, int.MaxValue);
            return postResult;
        }
        finally
        {
            totalStopwatch.Stop();
            metrics.TotalTicks = totalStopwatch.ElapsedTicks;
        }
    }

    private static void ApplyOrderedFileDiffPostParseResult(
        FileDiffPostParseResult postResult,
        SongTableFileCheckResult result,
        FileDiffParsePipelineResult pipelineResult,
        IBmsLibraryDialogService dialogService,
        Action<string> logEverythingScan,
        Action<string> logInstallPerformance,
        BlockingCollection<FileScanDiffCommitChunk> commitQueue,
        PipelineExceptionSignal pipelineException,
        ref long commitQueueWaitTicks,
        ref int postParseWorkItemCount,
        ref long postParseTicks,
        ref long postParseMaxItemTicks,
        ref long inlineChartInfoWallTicks,
        ref long inlineMaintenanceWallTicks,
        ref long inlineBmsMaintenanceWallTicks,
        ref long inlineBmsonMaintenanceWallTicks)
    {
        if (postResult == null)
        {
            return;
        }

        long commitWaitBefore = Interlocked.Read(ref commitQueueWaitTicks);
        ApplyFileDiffPostParseResult(
            postResult,
            result,
            pipelineResult,
            dialogService,
            logEverythingScan,
            commitQueue,
            pipelineException,
            ref commitQueueWaitTicks);
        long commitWaitAfter = Interlocked.Read(ref commitQueueWaitTicks);
        FileDiffPostParseItemMetrics metrics = postResult.Metrics;
        metrics.CommitQueueWaitTicks = Math.Max(0L, commitWaitAfter - commitWaitBefore);
        postParseWorkItemCount++;
        postParseTicks += metrics.TotalTicks;
        UpdateMaxTicks(ref postParseMaxItemTicks, metrics.TotalTicks);
        inlineChartInfoWallTicks += metrics.ChartInfoTicks;
        inlineMaintenanceWallTicks += metrics.MaintenanceTicks;
        inlineBmsMaintenanceWallTicks += metrics.BmsMaintenanceTicks;
        inlineBmsonMaintenanceWallTicks += metrics.BmsonMaintenanceTicks;

        long totalMs = TicksToMilliseconds(metrics.TotalTicks);
        if (totalMs >= DefaultSlowFileDiffPostParseItemLogThresholdMs)
        {
            logInstallPerformance?.Invoke("song_tbl_file_check_post_parse_item_slow"
                + " item=" + postParseWorkItemCount
                + " sequence=" + postResult.Sequence
                + " totalMs=" + totalMs
                + " chartInfoMs=" + TicksToMilliseconds(metrics.ChartInfoTicks)
                + " maintenanceMs=" + TicksToMilliseconds(metrics.MaintenanceTicks)
                + " bmsMaintenanceMs=" + TicksToMilliseconds(metrics.BmsMaintenanceTicks)
                + " bmsonMaintenanceMs=" + TicksToMilliseconds(metrics.BmsonMaintenanceTicks)
                + " healthMs=" + metrics.HealthMs
                + " encodingMs=" + metrics.EncodingMs
                + " encodingReloadMs=" + metrics.EncodingReloadMs
                + " encodingMaxMs=" + metrics.EncodingMaxMs
                + " cacheHit=" + metrics.CacheHitCount
                + " resourceIndexHit=" + metrics.ResourceIndexHitCount
                + " fileExistsFallback=" + metrics.FileExistsFallbackCount
                + " commitQueueMs=" + TicksToMilliseconds(metrics.CommitQueueWaitTicks)
                + " bms=" + metrics.BmsCount
                + " bmson=" + metrics.BmsonCount);
        }
    }

    private static void ApplyFileDiffPostParseResult(
        FileDiffPostParseResult postResult,
        SongTableFileCheckResult result,
        FileDiffParsePipelineResult pipelineResult,
        IBmsLibraryDialogService dialogService,
        Action<string> logEverythingScan,
        BlockingCollection<FileScanDiffCommitChunk> commitQueue,
        PipelineExceptionSignal pipelineException,
        ref long commitQueueWaitTicks)
    {
        if (postResult == null)
        {
            return;
        }

        ApplyInlineChartInfoResult(result, postResult.ChartInfoResult, storeRows: false);
        ApplyInlineMaintenanceResults(result, postResult.MaintenanceResults);
        foreach (InlineBmsParseCandidate candidate in postResult.BmsParseFailures)
        {
            if (candidate?.Exception != null)
            {
                result.FileScanFailures.Add(ChartFileScanFailure.FromException(candidate.Path, "bms", candidate.FailureStage, candidate.Exception));
                logEverythingScan?.Invoke("bms_scan_failed stage=" + candidate.FailureStage + " path=" + candidate.Path + " message=" + candidate.Exception.Message);
            }
        }
        foreach (InlineBmsonParseCandidate candidate in postResult.BmsonParseFailures)
        {
            if (candidate?.Exception != null)
            {
                result.FileScanFailures.Add(ChartFileScanFailure.FromException(candidate.Path, "bmson", candidate.FailureStage, candidate.Exception));
                logEverythingScan?.Invoke("bmson_scan_failed stage=" + candidate.FailureStage + " path=" + candidate.Path + " message=" + candidate.Exception.Message);
            }
        }

        pipelineResult.BmsDateOnlyUpdateCount += postResult.BmsDateOnlyUpdateCount;
        pipelineResult.BmsTextOnlyUpdateCount += postResult.BmsTextOnlyUpdateCount;
        pipelineResult.AddedFiles.AddRange(postResult.AddedFiles);
        foreach (string path in postResult.NewlyInsertedBmsPaths)
        {
            pipelineResult.NewlyInsertedBmsPaths.Add(path);
        }
        foreach (string path in postResult.SuccessfullyReplacedBmsPaths)
        {
            pipelineResult.SuccessfullyReplacedBmsPaths.Add(path);
        }
        pipelineResult.BmsRelinkDestinationCandidates.AddRange(postResult.BmsRelinkDestinationCandidates);
        pipelineResult.ParsedBmsonSongs.AddRange(postResult.ParsedBmsonSongs);
        foreach (string path in postResult.SuccessfullyParsedBmsonPaths)
        {
            pipelineResult.SuccessfullyParsedBmsonPaths.Add(path);
        }
        AddCommitChunkWithWait(commitQueue, postResult.CommitChunk, pipelineException, ref commitQueueWaitTicks);
    }

    private static bool IsBmsDateOnlyCandidate(InlineBmsParseCandidate candidate)
    {
        return candidate?.File != null
            && candidate.ExistingFile != null
            && candidate.File.date.HasValue
            && string.Equals(candidate.File.hash, candidate.ExistingFile.hash, StringComparison.OrdinalIgnoreCase);
    }

    private static void PrepareMovedBmsUserColumnRestores(
        FileDiffParsePipelineResult pipelineResult,
        Dictionary<string, Queue<BMSFile>> movedBmsSourcesByMd5,
        IReadOnlyDictionary<string, Lr2SongUserColumns> movedBmsUserColumnsByDeletedPath,
        Action<string> logInstallPerformanceWarn)
    {
        if (pipelineResult == null || movedBmsSourcesByMd5 == null || movedBmsSourcesByMd5.Count == 0)
        {
            return;
        }

        var destinationsByMd5 = pipelineResult.BmsRelinkDestinationCandidates
            .Where(candidate => candidate?.File != null && !string.IsNullOrWhiteSpace(candidate.File.hash))
            .GroupBy(candidate => candidate.File.hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Queue<BMSFile>> sourceEntry in movedBmsSourcesByMd5)
        {
            string md5 = sourceEntry.Key;
            if (string.IsNullOrWhiteSpace(md5)
                || !destinationsByMd5.TryGetValue(md5, out List<BmsRelinkDestinationCandidate> destinations)
                || destinations == null
                || destinations.Count == 0)
            {
                continue;
            }

            int sourceCount = sourceEntry.Value?.Count ?? 0;
            if (sourceCount == 1 && destinations.Count == 1)
            {
                BMSFile source = sourceEntry.Value.Peek();
                BmsRelinkDestinationCandidate destination = destinations[0];
                if (source == null
                    || string.IsNullOrWhiteSpace(source.path)
                    || destination?.File == null
                    || string.IsNullOrWhiteSpace(destination.File.path)
                    || IsCaseOnlyPathPair(source.path, destination.File.path)
                    || movedBmsUserColumnsByDeletedPath == null
                    || !movedBmsUserColumnsByDeletedPath.TryGetValue(source.path, out Lr2SongUserColumns userColumns)
                    || userColumns == null)
                {
                    continue;
                }
                BmsLibraryDbGateway.ApplySongUserColumns(destination.File, userColumns);
                pipelineResult.BmsMovedHashRelinkUserColumnRestores[destination.File.path] = userColumns;
                pipelineResult.BmsMovedHashRelinkCount++;
                continue;
            }

            pipelineResult.BmsMovedHashRelinkAmbiguousCount += destinations.Count;
            logInstallPerformanceWarn?.Invoke("lr2_song_relink_ambiguous md5=" + md5
                + " sourceCount=" + sourceCount
                + " destinationCount=" + destinations.Count);
        }
    }

    private static bool IsCaseOnlyPathPair(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && !string.Equals(left, right, StringComparison.Ordinal)
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static InlineMaintenanceItemResult[] BuildInlineBmsMaintenanceBatch(
        IReadOnlyList<InlineBmsParseCandidate> candidates,
        ResourceHealthLookupContext lookupContext,
        int degree,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return [];
        }
        var results = new InlineMaintenanceItemResult[candidates.Count];
        Parallel.For(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree) }, delegate (int index)
        {
            InlineBmsParseCandidate candidate = candidates[index];
            BMSFile file = candidate?.File;
            if (file != null)
            {
                results[index] = BuildInlineBmsMaintenance(file, candidate.Snapshot, CreateInlineResourceLookupScope(lookupContext));
            }
        });
        LogInlineMaintenanceWarnings(results, logInstallPerformanceWarn);
        return results;
    }

    private static InlineMaintenanceItemResult[] BuildInlineBmsonMaintenanceBatch(
        IReadOnlyList<InlineBmsonParseCandidate> candidates,
        ResourceHealthLookupContext lookupContext,
        int degree,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return [];
        }
        var results = new InlineMaintenanceItemResult[candidates.Count];
        Parallel.For(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, degree) }, delegate (int index)
        {
            LR2SongDBExtended.bmson_song song = candidates[index]?.Song;
            if (song != null)
            {
                results[index] = BuildInlineBmsonMaintenance(song, CreateInlineResourceLookupScope(lookupContext));
            }
        });
        LogInlineMaintenanceWarnings(results, logInstallPerformanceWarn);
        return results;
    }

    private static ResourceHealthLookupContext CreateInlineResourceLookupScope(ResourceHealthLookupContext lookupContext)
    {
        return lookupContext?.CreateCounterScope() ?? new ResourceHealthLookupContext(null);
    }

    private static InlineMaintenanceItemResult BuildInlineBmsMaintenance(
        BMSFile file,
        ChartFileSnapshot snapshot,
        ResourceHealthLookupContext lookupContext)
    {
        if (file == null)
        {
            return InlineMaintenanceItemResult.Empty;
        }
        var stopwatch = Stopwatch.StartNew();
        long healthMs = 0L;
        long encodingMs = 0L;
        long encodingReloadMs = 0L;
        long cacheHitCount = 0L;
        long resourceIndexHitCount = 0L;
        long resourceHealthSetCacheHitCount = 0L;
        long fileExistsFallbackCount = 0L;
        int encodingReloadCount = 0;
        bool completed = false;
        string warning = null;
        BMSFile.BmsEncodingDetectionResult detectionResult = null;
        try
        {
            BmsLibraryMaintenanceService.MaintenanceEvaluationResult maintenanceResult =
                BmsLibraryMaintenanceService.EvaluateBmsMaintenanceForInline(
                    file,
                    snapshot,
                    lookupContext,
                    componentReferencesAlreadyApplied: true);
            healthMs = TicksToMilliseconds(maintenanceResult.HealthElapsedTicks);
            encodingMs = TicksToMilliseconds(maintenanceResult.EncodingElapsedTicks);
            encodingReloadMs = TicksToMilliseconds(maintenanceResult.EncodingReloadElapsedTicks);
            encodingReloadCount = maintenanceResult.EncodingReloadCount;
            detectionResult = maintenanceResult.EncodingDetectionResult;
            cacheHitCount = maintenanceResult.CacheHitCount;
            resourceIndexHitCount = maintenanceResult.ResourceIndexHitCount;
            resourceHealthSetCacheHitCount = maintenanceResult.ResourceHealthSetCacheHitCount;
            fileExistsFallbackCount = maintenanceResult.FileExistsFallbackCount;
            completed = file.maintenanceInfo?.IsInformationChecked() == true;
        }
        catch (Exception ex) when (IsInlineMaintenanceRecoverable(ex))
        {
            warning = "inline_maintenance_failed kind=bms path=" + QuoteLogValue(file.path)
                + " exception=" + ex.GetType().Name
                + " message=" + QuoteLogValue(ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            file.ClearResourceReferenceCollections();
        }
        return new InlineMaintenanceItemResult(
            kind: FileDiffChartKind.Bms,
            targetCount: 1,
            successCount: completed ? 1 : 0,
            failedCount: completed ? 0 : 1,
            elapsedMs: stopwatch.ElapsedMilliseconds,
            healthMs: healthMs,
            encodingMs: encodingMs,
            encodingReloadMs: encodingReloadMs,
            encodingReloadCount: encodingReloadCount,
            encodingDetectionResult: detectionResult,
            cacheHitCount: cacheHitCount,
            resourceIndexHitCount: resourceIndexHitCount,
            resourceHealthSetCacheHitCount: resourceHealthSetCacheHitCount,
            fileExistsFallbackCount: fileExistsFallbackCount,
            warningMessage: warning);
    }

    private static InlineMaintenanceItemResult BuildInlineBmsonMaintenance(
        LR2SongDBExtended.bmson_song song,
        ResourceHealthLookupContext lookupContext)
    {
        if (song == null)
        {
            return InlineMaintenanceItemResult.Empty;
        }
        var stopwatch = Stopwatch.StartNew();
        long healthMs = 0L;
        long cacheHitCount = 0L;
        long resourceIndexHitCount = 0L;
        long resourceHealthSetCacheHitCount = 0L;
        long fileExistsFallbackCount = 0L;
        bool completed = false;
        string warning = null;
        try
        {
            BmsLibraryMaintenanceService.MaintenanceEvaluationResult maintenanceResult =
                BmsLibraryMaintenanceService.EvaluateBmsonMaintenanceForInline(song, lookupContext);
            completed = maintenanceResult.MaintenanceInfo?.IsInformationChecked() == true;
            healthMs = TicksToMilliseconds(maintenanceResult.HealthElapsedTicks);
            cacheHitCount = maintenanceResult.CacheHitCount;
            resourceIndexHitCount = maintenanceResult.ResourceIndexHitCount;
            resourceHealthSetCacheHitCount = maintenanceResult.ResourceHealthSetCacheHitCount;
            fileExistsFallbackCount = maintenanceResult.FileExistsFallbackCount;
        }
        catch (Exception ex) when (IsInlineMaintenanceRecoverable(ex))
        {
            warning = "inline_maintenance_failed kind=bmson path=" + QuoteLogValue(song.path)
                + " exception=" + ex.GetType().Name
                + " message=" + QuoteLogValue(ex.Message);
        }
        finally
        {
            stopwatch.Stop();
        }
        return new InlineMaintenanceItemResult(
            kind: FileDiffChartKind.Bmson,
            targetCount: 1,
            successCount: completed ? 1 : 0,
            failedCount: completed ? 0 : 1,
            elapsedMs: stopwatch.ElapsedMilliseconds,
            healthMs: healthMs,
            encodingMs: 0,
            encodingReloadMs: 0,
            encodingReloadCount: 0,
            encodingDetectionResult: null,
            cacheHitCount: cacheHitCount,
            resourceIndexHitCount: resourceIndexHitCount,
            resourceHealthSetCacheHitCount: resourceHealthSetCacheHitCount,
            fileExistsFallbackCount: fileExistsFallbackCount,
            warningMessage: warning);
    }

    private static void ApplyInlineMaintenanceResults(SongTableFileCheckResult result, IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        if (result == null || itemResults == null)
        {
            return;
        }
        foreach (InlineMaintenanceItemResult itemResult in itemResults)
        {
            if (itemResult == null || itemResult.TargetCount <= 0)
            {
                continue;
            }
            result.InlineMaintenanceTargetCount += itemResult.TargetCount;
            if (itemResult.Kind == FileDiffChartKind.Bmson)
            {
                result.InlineMaintenanceBmsonCount += itemResult.TargetCount;
            }
            else
            {
                result.InlineMaintenanceBmsCount += itemResult.TargetCount;
            }
            result.InlineMaintenanceSuccessCount += itemResult.SuccessCount;
            result.InlineMaintenanceFailedCount += itemResult.FailedCount;
            result.InlineMaintenanceMs += itemResult.ElapsedMs;
            result.InlineHealthWallMs += itemResult.HealthMs;
            result.InlineEncodingWallMs += itemResult.EncodingMs;
            result.InlineEncodingReloadWallMs += itemResult.EncodingReloadMs;
            result.InlineEncodingReloadCount += itemResult.EncodingReloadCount;
            result.InlineEncodingDetectCount += itemResult.EncodingDetectCount;
            result.InlineEncodingFastAsciiCount += itemResult.EncodingFastAsciiCount;
            result.InlineEncodingShiftJisCount += itemResult.EncodingShiftJisCount;
            result.InlineEncodingShiftJisQuestionCount += itemResult.EncodingShiftJisQuestionCount;
            result.InlineEncodingKoreanCount += itemResult.EncodingKoreanCount;
            result.InlineEncodingKoreanQuestionCount += itemResult.EncodingKoreanQuestionCount;
            result.InlineEncodingUtf8Count += itemResult.EncodingUtf8Count;
            result.InlineEncodingUnknownCount += itemResult.EncodingUnknownCount;
            result.InlineEncodingOtherCount += itemResult.EncodingOtherCount;
            result.InlineEncodingMaxItemMs = Math.Max(result.InlineEncodingMaxItemMs, itemResult.EncodingMs);
            result.InlineMaintenanceCacheHitCount += itemResult.CacheHitCount;
            result.InlineMaintenanceResourceIndexHitCount += itemResult.ResourceIndexHitCount;
            result.InlineMaintenanceResourceSetCacheHitCount += itemResult.ResourceHealthSetCacheHitCount;
            result.InlineMaintenanceFileExistsFallbackCount += itemResult.FileExistsFallbackCount;
        }
    }

    private static long SumInlineMaintenanceEncodingMs(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.EncodingMs) ?? 0L;
    }

    private static long SumInlineMaintenanceHealthMs(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.HealthMs) ?? 0L;
    }

    private static long SumInlineMaintenanceCacheHitCount(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.CacheHitCount) ?? 0L;
    }

    private static long SumInlineMaintenanceResourceIndexHitCount(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.ResourceIndexHitCount) ?? 0L;
    }

    private static long SumInlineMaintenanceFileExistsFallbackCount(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.FileExistsFallbackCount) ?? 0L;
    }

    private static long SumInlineMaintenanceEncodingReloadMs(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Sum(item => item.EncodingReloadMs) ?? 0L;
    }

    private static long MaxInlineMaintenanceEncodingMs(IEnumerable<InlineMaintenanceItemResult> itemResults)
    {
        return itemResults?.Where(item => item != null).Select(item => item.EncodingMs).DefaultIfEmpty(0L).Max() ?? 0L;
    }

    private static void LogInlineMaintenanceWarnings(IEnumerable<InlineMaintenanceItemResult> itemResults, Action<string> logInstallPerformanceWarn)
    {
        if (logInstallPerformanceWarn == null || itemResults == null)
        {
            return;
        }
        foreach (InlineMaintenanceItemResult itemResult in itemResults)
        {
            if (!string.IsNullOrWhiteSpace(itemResult?.WarningMessage))
            {
                logInstallPerformanceWarn(itemResult.WarningMessage);
            }
        }
    }

    private static bool IsInlineMaintenanceRecoverable(Exception ex)
    {
        return ex != null;
    }

    private static void UpdateHighWatermark(ref int highWatermark, int value)
    {
        int observed;
        do
        {
            observed = highWatermark;
            if (value <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref highWatermark, value, observed) != observed);
    }

    private static void UpdateMaxTicks(ref long maxTicks, long value)
    {
        long observed;
        do
        {
            observed = Interlocked.Read(ref maxTicks);
            if (value <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maxTicks, value, observed) != observed);
    }

    private static void AddWithWait<T>(
        BlockingCollection<T> queue,
        T item,
        ref long waitTicks,
        PipelineExceptionSignal exceptionSignal = null)
    {
        if (queue == null)
        {
            return;
        }
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            ThrowIfSignaled(exceptionSignal);
            try
            {
                if (queue.TryAdd(item, 100))
                {
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    if (elapsed > 0L)
                    {
                        Interlocked.Add(ref waitTicks, elapsed);
                    }
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                ThrowIfSignaled(exceptionSignal);
                throw;
            }
        }
    }

    private static void AddCommitChunkWithWait(
        BlockingCollection<FileScanDiffCommitChunk> queue,
        FileScanDiffCommitChunk chunk,
        PipelineExceptionSignal writerException,
        ref long waitTicks)
    {
        if (queue == null || chunk == null || !chunk.HasItems)
        {
            return;
        }

        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            ThrowIfSignaled(writerException);
            try
            {
                if (queue.TryAdd(chunk, 100))
                {
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    if (elapsed > 0L)
                    {
                        Interlocked.Add(ref waitTicks, elapsed);
                    }
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                ThrowIfSignaled(writerException);
                throw;
            }
        }
    }

    private static void ThrowIfSignaled(PipelineExceptionSignal exceptionSignal)
    {
        Exception exception = exceptionSignal?.Get();
        if (exception != null)
        {
            throw new AggregateException(exception);
        }
    }

    private static void CapturePipelineException(
        Exception exception,
        PipelineExceptionSignal exceptionSignal,
        ref Exception pipelineFailure)
    {
        if (exception == null)
        {
            return;
        }
        pipelineFailure ??= exception;
        exceptionSignal?.Set(exception);
    }

    private static void CompleteAddingSilently<T>(BlockingCollection<T> queue)
    {
        if (queue == null)
        {
            return;
        }
        try
        {
            queue.CompleteAdding();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void EnqueuePostParseWorkItem(
        BlockingCollection<FileDiffPostParseWorkItem> queue,
        FileDiffPostParseWorkItem workItem,
        PipelineExceptionSignal pipelineException,
        ref long waitTicks,
        ref Exception postParseException)
    {
        ThrowIfSignaled(pipelineException);
        Exception capturedException = Volatile.Read(ref postParseException);
        if (capturedException != null)
        {
            throw new AggregateException(capturedException);
        }
        try
        {
            AddWithWait(queue, workItem, ref waitTicks, pipelineException);
        }
        catch (InvalidOperationException) when (Volatile.Read(ref postParseException) != null)
        {
            throw new AggregateException(Volatile.Read(ref postParseException));
        }
    }

    private sealed class PipelineExceptionSignal
    {
        private Exception exception;

        public Exception Get()
        {
            return Volatile.Read(ref exception);
        }

        public void Set(Exception value)
        {
            if (value != null)
            {
                Interlocked.CompareExchange(ref exception, value, null);
            }
        }
    }
    private sealed class FileDiffCommitWriterItem : IDisposable
    {
        private readonly ManualResetEventSlim completion;

        private FileDiffCommitWriterItem(FileScanDiffCommitChunk chunk, bool isBarrier)
        {
            Chunk = chunk;
            IsBarrier = isBarrier;
            completion = isBarrier ? new ManualResetEventSlim(false) : null;
        }

        public FileScanDiffCommitChunk Chunk { get; }

        public bool IsBarrier { get; }

        public Exception Exception { get; private set; }

        public static FileDiffCommitWriterItem CreateChunk(FileScanDiffCommitChunk chunk)
        {
            return new FileDiffCommitWriterItem(chunk, isBarrier: false);
        }

        public static FileDiffCommitWriterItem CreateBarrier()
        {
            return new FileDiffCommitWriterItem(null, isBarrier: true);
        }

        public bool Wait(int millisecondsTimeout)
        {
            return completion?.Wait(millisecondsTimeout) == true;
        }

        public void SignalComplete(Exception exception)
        {
            Exception = exception;
            completion?.Set();
        }

        public void Dispose()
        {
            completion?.Dispose();
        }
    }

    private sealed class FileDiffStreamingCommitContext : IDisposable
    {
        private readonly BmsLibraryDbGateway dbGateway;

        private readonly BmsLibraryOptionsSnapshot options;

        private readonly SongTableFileCheckResult result;

        private readonly Action<string> logInstallPerformance;

        private readonly Action<string> logInstallPerformanceWarn;

        private readonly Action<IReadOnlyList<LR2SongDBExtended.chart_info>> inlineChartInfoRowsCommitted;

        private readonly int chunkSize;

        private readonly BlockingCollection<FileDiffCommitWriterItem> writerQueue;

        private readonly Task writerTask;

        private readonly Stopwatch lifetimeStopwatch = Stopwatch.StartNew();

        private readonly PipelineExceptionSignal writerException = new();

        private FileScanDiffCommitChunk pendingChunk = new();

        private LR2SongDBExtended songDb;

        private bool pragmasApplied;

        private bool writerQueueCompleted;

        private bool writerWaitCompleted;

        private long writerQueueWaitTicks;

        private int writerQueueHighWatermark;

        public FileDiffStreamingCommitContext(
            BmsLibraryDbGateway dbGateway,
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult result,
            Action<string> logInstallPerformance,
            Action<string> logInstallPerformanceWarn,
            Action<IReadOnlyList<LR2SongDBExtended.chart_info>> inlineChartInfoRowsCommitted)
        {
            this.dbGateway = dbGateway;
            this.options = options;
            this.result = result;
            this.logInstallPerformance = logInstallPerformance;
            this.logInstallPerformanceWarn = logInstallPerformanceWarn;
            this.inlineChartInfoRowsCommitted = inlineChartInfoRowsCommitted;
            chunkSize = Math.Max(1, result?.DbCommitChunkSize ?? DefaultFileDiffCommitChunkSize);
            int writerQueueCapacity = Math.Max(1, DefaultFileDiffCommitWriterQueueCapacity);
            if (result != null)
            {
                result.CommitWriterQueueCapacity = writerQueueCapacity;
            }
            writerQueue = new BlockingCollection<FileDiffCommitWriterItem>(writerQueueCapacity);
            writerTask = Task.Run(WriterLoop);
        }

        public bool ShouldPruneCommittedInlineRows => inlineChartInfoRowsCommitted != null;

        public void AddDeletedBmsPath(string path)
        {
            pendingChunk.AddDeletedBmsPath(path);
            FlushIfNeeded();
        }

        public void AddDeletedBmsonPath(string path)
        {
            pendingChunk.AddDeletedBmsonPath(path);
            FlushIfNeeded();
        }

        public void AddChunk(FileScanDiffCommitChunk chunk)
        {
            if (chunk == null || !chunk.HasItems)
            {
                return;
            }

            Dictionary<string, Queue<BMSFileMaintenanceInfo>> maintenanceByPath =
                FileScanParseCommitOwner.BuildQueueByMd5(chunk.MaintenanceInfoRows, row => row?.path);
            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> chartInfoByMd5 =
                FileScanParseCommitOwner.BuildQueueByMd5(chunk.ChartInfoRows, row => row?.md5);
            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> appliedChartInfoByMd5 =
                FileScanParseCommitOwner.BuildQueueByMd5(chunk.AppliedChartInfoRows, row => row?.md5);
            Dictionary<string, Queue<LR2SongDBExtended.chart_info_parse_failure>> failureByMd5 =
                FileScanParseCommitOwner.BuildQueueByMd5(chunk.ParseFailureRows, row => row?.md5);
            var failureDeleteMd5s = new HashSet<string>(chunk.ParseFailureDeleteMd5s, StringComparer.OrdinalIgnoreCase);

            foreach (string path in chunk.DeletedBmsPaths)
            {
                pendingChunk.AddDeletedBmsPath(path);
                FlushIfNeeded();
            }
            foreach (BMSFile file in chunk.AddedBmsFiles)
            {
                pendingChunk.AddAddedBmsFile(file);
                AddMatchingMaintenanceInfoRows(maintenanceByPath, file?.path);
                FileScanParseCommitOwner.AttachInlineChartInfoRows(
                    pendingChunk,
                    file?.hash,
                    chartInfoByMd5,
                    appliedChartInfoByMd5,
                    failureByMd5,
                    failureDeleteMd5s);
                FlushIfNeeded();
            }
            foreach (BmsDateOnlyUpdate update in chunk.UpdatedBmsDates)
            {
                if (update != null)
                {
                    pendingChunk.AddUpdatedBmsMetadata(update.Path, update.Date, update.TextFlag);
                    FlushIfNeeded();
                }
            }
            foreach (string path in chunk.DeletedBmsonPaths)
            {
                pendingChunk.AddDeletedBmsonPath(path);
                FlushIfNeeded();
            }
            foreach (LR2SongDBExtended.bmson_song song in chunk.UpsertBmsonSongs)
            {
                pendingChunk.AddUpsertBmsonSong(song);
                AddMatchingMaintenanceInfoRows(maintenanceByPath, song?.path);
                FileScanParseCommitOwner.AttachInlineChartInfoRows(
                    pendingChunk,
                    song?.md5,
                    chartInfoByMd5,
                    appliedChartInfoByMd5,
                    failureByMd5,
                    failureDeleteMd5s);
                FlushIfNeeded();
            }

            AddRemainingMaintenanceInfoRows(maintenanceByPath);
            AddRemainingChartInfoRows(chartInfoByMd5, appliedChartInfoByMd5, failureByMd5, failureDeleteMd5s);
            FlushIfNeeded();
        }

        public void Flush()
        {
            if (pendingChunk.HasItems)
            {
                EnqueuePendingChunk();
            }
            WaitForWriterBarrier();
        }

        public void RestoreSongUserColumns(IEnumerable<KeyValuePair<string, Lr2SongUserColumns>> userColumnsByPath)
        {
            List<KeyValuePair<string, Lr2SongUserColumns>> rows = [.. (userColumnsByPath ?? [])
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)];
            if (rows.Count == 0)
            {
                return;
            }

            EnsureSongDb();
            int chunkNumber = result.DbCommitChunks + 1;
            logInstallPerformance?.Invoke("song_tbl_file_check user_column_restore_start"
                + " chunk=" + chunkNumber
                + " rows=" + rows.Count);
            string savepoint = songDb.SaveTransactionPoint();
            var restoreStopwatch = Stopwatch.StartNew();
            try
            {
                foreach (KeyValuePair<string, Lr2SongUserColumns> row in rows)
                {
                    BmsLibraryDbGateway.ApplySongUserColumns(songDb, row.Key, row.Value);
                }
                songDb.Commit();
            }
            catch (Exception ex)
            {
                restoreStopwatch.Stop();
                try
                {
                    songDb.RollbackTo(savepoint);
                }
                catch
                {
                }
                logInstallPerformanceWarn?.Invoke("song_tbl_file_check user_column_restore_failed"
                    + " chunk=" + chunkNumber
                    + " rows=" + rows.Count
                    + " elapsedMs=" + restoreStopwatch.ElapsedMilliseconds
                    + " exception=" + ex.GetType().Name
                    + " message=" + QuoteLogValue(ex.Message));
                ReleaseSongDb();
                throw;
            }
            restoreStopwatch.Stop();
            result.DbCommitChunks++;
            result.DbCommitMaxChunkMs = Math.Max(result.DbCommitMaxChunkMs, restoreStopwatch.ElapsedMilliseconds);
            result.DbCommitMs += restoreStopwatch.ElapsedMilliseconds;
            logInstallPerformance?.Invoke("song_tbl_file_check user_column_restore_done"
                + " chunk=" + chunkNumber
                + " rows=" + rows.Count
                + " elapsedMs=" + restoreStopwatch.ElapsedMilliseconds);
            ReleaseSongDb();
        }

        public void Dispose()
        {
            try
            {
                CompleteWriterQueue();
                WaitWriterTask();
            }
            catch (Exception ex)
            {
                logInstallPerformanceWarn?.Invoke("song_tbl_file_check commit_writer_dispose_failed"
                    + " exception=" + ex.GetType().Name
                    + " message=" + QuoteLogValue(ex.Message));
            }
            finally
            {
                writerQueue?.Dispose();
                ReleaseSongDb();
            }
        }

        public void ReleaseSongDb()
        {
            songDb?.Dispose();
            songDb = null;
            pragmasApplied = false;
        }

        private void FlushIfNeeded()
        {
            if (pendingChunk.MutationCount >= chunkSize)
            {
                EnqueuePendingChunk();
            }
        }

        private void EnqueuePendingChunk()
        {
            if (!pendingChunk.HasItems)
            {
                return;
            }
            FileScanDiffCommitChunk chunk = pendingChunk;
            pendingChunk = new FileScanDiffCommitChunk();
            AddWriterItemWithWait(FileDiffCommitWriterItem.CreateChunk(chunk));
        }

        private void WaitForWriterBarrier()
        {
            ThrowIfWriterFailed();
            using FileDiffCommitWriterItem barrier = FileDiffCommitWriterItem.CreateBarrier();
            AddWriterItemWithWait(barrier);
            while (!barrier.Wait(100))
            {
                ThrowIfWriterFailed();
                if (writerTask.IsCompleted)
                {
                    WaitWriterTask();
                    ThrowIfWriterFailed();
                    throw new InvalidOperationException("File diff commit writer completed before the flush barrier.");
                }
            }
            if (barrier.Exception != null)
            {
                throw new AggregateException(barrier.Exception);
            }
            ThrowIfWriterFailed();
            if (result != null)
            {
                result.CommitWriterQueueWaitMs = TicksToMilliseconds(Interlocked.Read(ref writerQueueWaitTicks));
            }
        }

        private void AddWriterItemWithWait(FileDiffCommitWriterItem item)
        {
            if (item == null)
            {
                return;
            }

            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                ThrowIfWriterFailed();
                try
                {
                    if (writerQueue.TryAdd(item, 100))
                    {
                        UpdateHighWatermark(ref writerQueueHighWatermark, writerQueue.Count);
                        if (result != null)
                        {
                            result.CommitWriterQueueHighWatermark = Math.Max(
                                result.CommitWriterQueueHighWatermark,
                                Volatile.Read(ref writerQueueHighWatermark));
                        }
                        long elapsed = Stopwatch.GetTimestamp() - start;
                        if (elapsed > 0L)
                        {
                            Interlocked.Add(ref writerQueueWaitTicks, elapsed);
                        }
                        return;
                    }
                }
                catch (InvalidOperationException)
                {
                    ThrowIfWriterFailed();
                    throw;
                }
            }
        }

        private void WriterLoop()
        {
            try
            {
                foreach (FileDiffCommitWriterItem item in writerQueue.GetConsumingEnumerable())
                {
                    if (item == null)
                    {
                        continue;
                    }
                    if (item.IsBarrier)
                    {
                        item.SignalComplete(null);
                        continue;
                    }
                    CommitChunk(item.Chunk);
                }
            }
            catch (Exception ex)
            {
                writerException.Set(ex);
                throw;
            }
            finally
            {
                ReleaseSongDb();
            }
        }

        private void CompleteWriterQueue()
        {
            if (writerQueueCompleted)
            {
                return;
            }
            try
            {
                writerQueue.CompleteAdding();
            }
            catch (InvalidOperationException)
            {
            }
            writerQueueCompleted = true;
        }

        private void WaitWriterTask()
        {
            if (writerWaitCompleted)
            {
                ThrowIfWriterFailed();
                return;
            }
            try
            {
                writerTask.Wait();
                writerWaitCompleted = true;
            }
            catch (AggregateException ex)
            {
                writerException.Set(ex.InnerException ?? ex);
                throw;
            }
            ThrowIfWriterFailed();
        }

        private void ThrowIfWriterFailed()
        {
            Exception exception = writerException.Get();
            if (exception != null)
            {
                throw new AggregateException(exception);
            }
        }

        private void AddMatchingMaintenanceInfoRows(
            Dictionary<string, Queue<BMSFileMaintenanceInfo>> maintenanceByPath,
            string path)
        {
            if (string.IsNullOrWhiteSpace(path)
                || maintenanceByPath == null
                || !maintenanceByPath.TryGetValue(path, out Queue<BMSFileMaintenanceInfo> rows))
            {
                return;
            }
            while (rows.Count > 0)
            {
                pendingChunk.AddMaintenanceInfoRow(rows.Dequeue());
            }
            maintenanceByPath.Remove(path);
        }

        private void AddRemainingMaintenanceInfoRows(Dictionary<string, Queue<BMSFileMaintenanceInfo>> maintenanceByPath)
        {
            if (maintenanceByPath == null || maintenanceByPath.Count == 0)
            {
                return;
            }
            foreach (Queue<BMSFileMaintenanceInfo> rows in maintenanceByPath.Values)
            {
                while (rows.Count > 0)
                {
                    pendingChunk.AddMaintenanceInfoRow(rows.Dequeue(), countMutation: true);
                    FlushIfNeeded();
                }
            }
        }

        private void AddRemainingChartInfoRows(
            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> chartInfoByMd5,
            Dictionary<string, Queue<LR2SongDBExtended.chart_info>> appliedChartInfoByMd5,
            Dictionary<string, Queue<LR2SongDBExtended.chart_info_parse_failure>> failureByMd5,
            HashSet<string> failureDeleteMd5s)
        {
            foreach (Queue<LR2SongDBExtended.chart_info> rows in chartInfoByMd5.Values)
            {
                while (rows.Count > 0)
                {
                    pendingChunk.AddChartInfoRow(rows.Dequeue());
                    FlushIfNeeded();
                }
            }
            foreach (Queue<LR2SongDBExtended.chart_info> rows in appliedChartInfoByMd5.Values)
            {
                while (rows.Count > 0)
                {
                    pendingChunk.AddAppliedChartInfoRow(rows.Dequeue());
                }
            }
            foreach (Queue<LR2SongDBExtended.chart_info_parse_failure> rows in failureByMd5.Values)
            {
                while (rows.Count > 0)
                {
                    pendingChunk.AddParseFailureRow(rows.Dequeue());
                    FlushIfNeeded();
                }
            }
            foreach (string md5 in failureDeleteMd5s)
            {
                pendingChunk.AddParseFailureDeleteMd5(md5);
                FlushIfNeeded();
            }
        }

        private void CommitChunk(FileScanDiffCommitChunk chunk)
        {
            if (dbGateway == null || result == null || chunk == null || !chunk.HasItems)
            {
                return;
            }
            EnsureSongDb();
            int chunkNumber = result.DbCommitChunks + 1;
            if (chunkNumber == 1 && result.DbCommitFirstChunkStartMs <= 0L)
            {
                result.DbCommitFirstChunkStartMs = lifetimeStopwatch.ElapsedMilliseconds;
            }
            logInstallPerformance?.Invoke("song_tbl_file_check db_commit_chunk_start chunk=" + chunkNumber
                + " deleted=" + chunk.DeletedBmsPaths.Count
                + " added=" + chunk.AddedBmsFiles.Count
                + " bmsDateOnly=" + chunk.UpdatedBmsDates.Count
                + " bmsonDeleted=" + chunk.DeletedBmsonPaths.Count
                + " bmsonUpsert=" + chunk.UpsertBmsonSongs.Count
                + " maintenance=" + chunk.MaintenanceInfoRows.Count
                + " chartInfo=" + chunk.ChartInfoRows.Count
                + " appliedChartInfo=" + chunk.AppliedChartInfoRows.Count
                + " failures=" + chunk.ParseFailureRows.Count
                + " failureDeletes=" + chunk.ParseFailureDeleteMd5s.Count
                + " mutations=" + chunk.MutationCount);
            string savepoint = songDb.SaveTransactionPoint();
            var chunkStopwatch = Stopwatch.StartNew();
            var metrics = new FileScanDiffCommitMetrics();
            long sqliteCommitMs = 0L;
            try
            {
                metrics = BmsLibraryDbGateway.CommitFileScanDiffChunk(songDb, chunk);
                var commitStopwatch = Stopwatch.StartNew();
                songDb.Commit();
                commitStopwatch.Stop();
                sqliteCommitMs = commitStopwatch.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                chunkStopwatch.Stop();
                try
                {
                    songDb.RollbackTo(savepoint);
                }
                catch
                {
                }
                logInstallPerformanceWarn?.Invoke("song_tbl_file_check db_commit_chunk_failed chunk=" + chunkNumber
                    + " elapsedMs=" + chunkStopwatch.ElapsedMilliseconds
                    + " exception=" + ex.GetType().Name
                    + " message=" + QuoteLogValue(ex.Message));
                ReleaseSongDb();
                throw;
            }
            chunkStopwatch.Stop();
            result.DbCommitChunks++;
            result.DbCommitMaxChunkMs = Math.Max(result.DbCommitMaxChunkMs, chunkStopwatch.ElapsedMilliseconds);
            result.DbCommitMs += chunkStopwatch.ElapsedMilliseconds;
            result.DbCommitApplyMs += metrics.ApplyMs;
            result.DbCommitSchemaMs += metrics.SchemaMs;
            result.DbCommitBmsDeleteMs += metrics.BmsDeleteMs;
            result.DbCommitBmsDateUpdateMs += metrics.BmsDateUpdateMs;
            result.DbCommitBmsUpsertMs += metrics.BmsUpsertMs;
            result.DbCommitBmsChangedCount += metrics.BmsChangedCount;
            result.DbCommitBmsonDeleteMs += metrics.BmsonDeleteMs;
            result.DbCommitBmsonUpsertMs += metrics.BmsonUpsertMs;
            result.DbCommitMaintenanceUpsertMs += metrics.MaintenanceUpsertMs;
            result.DbCommitChartInfoMs += metrics.ChartInfoMs;
            result.DbCommitSqliteCommitMs += sqliteCommitMs;
            logInstallPerformance?.Invoke("song_tbl_file_check db_commit_chunk_done chunk=" + chunkNumber
                + " elapsedMs=" + chunkStopwatch.ElapsedMilliseconds
                + " applyMs=" + metrics.ApplyMs
                + " schemaMs=" + metrics.SchemaMs
                + " bmsDeleteMs=" + metrics.BmsDeleteMs
                + " bmsDateUpdateMs=" + metrics.BmsDateUpdateMs
                + " bmsUpsertMs=" + metrics.BmsUpsertMs
                + " bmsChanged=" + metrics.BmsChangedCount
                + " bmsonDeleteMs=" + metrics.BmsonDeleteMs
                + " bmsonUpsertMs=" + metrics.BmsonUpsertMs
                + " maintenanceUpsertMs=" + metrics.MaintenanceUpsertMs
                + " chartInfoMs=" + metrics.ChartInfoMs
                + " sqliteCommitMs=" + sqliteCommitMs
                + " deleted=" + chunk.DeletedBmsPaths.Count
                + " added=" + chunk.AddedBmsFiles.Count
                + " bmsDateOnly=" + chunk.UpdatedBmsDates.Count
                + " bmsonDeleted=" + chunk.DeletedBmsonPaths.Count
                + " bmsonUpsert=" + chunk.UpsertBmsonSongs.Count
                + " maintenance=" + chunk.MaintenanceInfoRows.Count
                + " chartInfo=" + chunk.ChartInfoRows.Count
                + " appliedChartInfo=" + chunk.AppliedChartInfoRows.Count
                + " failures=" + chunk.ParseFailureRows.Count
                + " failureDeletes=" + chunk.ParseFailureDeleteMd5s.Count);
            if (chunk.ChartInfoRows.Count > 0 && inlineChartInfoRowsCommitted != null)
            {
                result.InlineChartInfoIndexPublishedCount += chunk.ChartInfoRows.Count;
                inlineChartInfoRowsCommitted(chunk.ChartInfoRows);
            }
            ReleaseSongDb();
        }

        private void EnsureSongDb()
        {
            songDb ??= dbGateway.OpenSongDb();
            if (!pragmasApplied)
            {
                List<string> pragmas = songDb.TryApplyReadOptimizedPragmas(options?.EnableReadOptimizedPragmas ?? false);
                result.Pragmas.AddRange(pragmas);
                if (pragmas.Count > 0)
                {
                    logInstallPerformance?.Invoke("db_read_pragmas scope=song_tbl_file_check " + string.Join(" ", pragmas));
                }
                pragmasApplied = true;
            }
        }
    }

    internal static Dictionary<string, Queue<T>> BuildQueueByMd5<T>(IEnumerable<T> rows, Func<T, string> md5Selector)
    {
        var result = new Dictionary<string, Queue<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (T row in rows ?? [])
        {
            string md5 = md5Selector(row);
            if (string.IsNullOrWhiteSpace(md5))
            {
                continue;
            }
            if (!result.TryGetValue(md5, out Queue<T> queue))
            {
                queue = new Queue<T>();
                result[md5] = queue;
            }
            queue.Enqueue(row);
        }
        return result;
    }

    private static void AttachInlineChartInfoRows(
        FileScanDiffCommitChunk chunk,
        string md5,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info>> chartInfoByMd5,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info>> appliedChartInfoByMd5,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info_parse_failure>> failureByMd5,
        HashSet<string> failureDeleteMd5s)
    {
        if (chunk == null || string.IsNullOrWhiteSpace(md5))
        {
            return;
        }
        if (chartInfoByMd5.TryGetValue(md5, out Queue<LR2SongDBExtended.chart_info> chartInfoRows))
        {
            while (chartInfoRows.Count > 0)
            {
                chunk.AddChartInfoRow(chartInfoRows.Dequeue(), countMutation: false);
            }
            chartInfoByMd5.Remove(md5);
        }
        if (appliedChartInfoByMd5.TryGetValue(md5, out Queue<LR2SongDBExtended.chart_info> appliedRows))
        {
            while (appliedRows.Count > 0)
            {
                chunk.AddAppliedChartInfoRow(appliedRows.Dequeue());
            }
            appliedChartInfoByMd5.Remove(md5);
        }
        if (failureByMd5.TryGetValue(md5, out Queue<LR2SongDBExtended.chart_info_parse_failure> failureRows))
        {
            while (failureRows.Count > 0)
            {
                chunk.AddParseFailureRow(failureRows.Dequeue(), countMutation: false);
            }
            failureByMd5.Remove(md5);
        }
        if (failureDeleteMd5s.Remove(md5))
        {
            chunk.AddParseFailureDeleteMd5(md5, countMutation: false);
        }
    }

    private static Dictionary<string, LR2SongDBExtended.chart_info> BuildAppliedChartInfoByPath(
        IEnumerable<InlineBmsParseCandidate> candidates,
        IEnumerable<LR2SongDBExtended.chart_info> appliedRows)
    {
        var rowsBySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.chart_info row in appliedRows ?? [])
        {
            if (row == null || string.IsNullOrWhiteSpace(row.sha256))
            {
                continue;
            }
            rowsBySha256[row.sha256] = row;
        }
        if (rowsBySha256.Count == 0)
        {
            return new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        foreach (InlineBmsParseCandidate candidate in candidates ?? [])
        {
            if (candidate?.File == null || string.IsNullOrWhiteSpace(candidate.Path) || string.IsNullOrWhiteSpace(candidate.File.sha256))
            {
                continue;
            }
            if (rowsBySha256.TryGetValue(candidate.File.sha256, out LR2SongDBExtended.chart_info row))
            {
                result[candidate.Path] = row;
            }
        }
        return result;
    }

    private static LR2SongDBExtended.chart_info ResolveAppliedChartInfo(
        InlineBmsParseCandidate candidate,
        Dictionary<string, LR2SongDBExtended.chart_info> appliedChartInfoByPath)
    {
        if (candidate == null
            || string.IsNullOrWhiteSpace(candidate.Path)
            || appliedChartInfoByPath == null
            || !appliedChartInfoByPath.TryGetValue(candidate.Path, out LR2SongDBExtended.chart_info row))
        {
            return null;
        }
        return row;
    }

    private static void AddRemainingInlineRows(
        List<FileScanDiffCommitChunk> chunks,
        ref FileScanDiffCommitChunk current,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info>> chartInfoByMd5,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info>> appliedChartInfoByMd5,
        Dictionary<string, Queue<LR2SongDBExtended.chart_info_parse_failure>> failureByMd5,
        HashSet<string> failureDeleteMd5s,
        int chunkSize)
    {
        foreach (Queue<LR2SongDBExtended.chart_info> queue in chartInfoByMd5.Values)
        {
            while (queue.Count > 0)
            {
                current.AddChartInfoRow(queue.Dequeue());
                if (current.MutationCount >= chunkSize)
                {
                    chunks.Add(current);
                    current = new FileScanDiffCommitChunk();
                }
            }
        }
        foreach (Queue<LR2SongDBExtended.chart_info> queue in appliedChartInfoByMd5.Values)
        {
            while (queue.Count > 0)
            {
                current.AddAppliedChartInfoRow(queue.Dequeue());
                if (current.MutationCount >= chunkSize)
                {
                    chunks.Add(current);
                    current = new FileScanDiffCommitChunk();
                }
            }
        }
        foreach (Queue<LR2SongDBExtended.chart_info_parse_failure> queue in failureByMd5.Values)
        {
            while (queue.Count > 0)
            {
                current.AddParseFailureRow(queue.Dequeue());
                if (current.MutationCount >= chunkSize)
                {
                    chunks.Add(current);
                    current = new FileScanDiffCommitChunk();
                }
            }
        }
        foreach (string md5 in failureDeleteMd5s)
        {
            current.AddParseFailureDeleteMd5(md5);
            if (current.MutationCount >= chunkSize)
            {
                chunks.Add(current);
                current = new FileScanDiffCommitChunk();
            }
        }
    }

    private ChartInfoInlineBuildResult BuildInlineBmsChartInfo(
        IReadOnlyList<InlineBmsParseCandidate> candidates,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        int chartInfoParserDegree,
        int inlineChartInfoBatchSize,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return new ChartInfoInlineBuildResult();
        }
        List<InlineBmsParseCandidate> parsedCandidates = [.. candidates.Where(candidate => candidate?.File != null && candidate.Snapshot != null)];
        if (parsedCandidates.Count == 0)
        {
            return new ChartInfoInlineBuildResult();
        }
        var inlineBuildService = new ChartInfoInlineBuildService(chartInfoBuildService, chartInfoParserDegree, inlineChartInfoBatchSize, currentChartInfoRowsBySha256);
        return inlineBuildService.BuildForSnapshots(
            dbGateway,
            parsedCandidates.Select(candidate => InlineChartSnapshotTarget.FromBmsFile(candidate.File, candidate.Snapshot)),
            currentFailures,
            logInstallPerformance,
            logInstallPerformanceWarn);
    }

    private ChartInfoInlineBuildResult BuildInlineBmsonChartInfo(
        IReadOnlyList<InlineBmsonParseCandidate> candidates,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        int chartInfoParserDegree,
        int inlineChartInfoBatchSize,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return new ChartInfoInlineBuildResult();
        }
        List<InlineBmsonParseCandidate> parsedCandidates = [.. candidates.Where(candidate => candidate?.Song != null && candidate.Snapshot != null)];
        if (parsedCandidates.Count == 0)
        {
            return new ChartInfoInlineBuildResult();
        }
        var inlineBuildService = new ChartInfoInlineBuildService(chartInfoBuildService, chartInfoParserDegree, inlineChartInfoBatchSize, currentChartInfoRowsBySha256);
        return inlineBuildService.BuildForSnapshots(
            dbGateway,
            parsedCandidates.Select(candidate => InlineChartSnapshotTarget.FromBmsonSong(candidate.Song, candidate.Snapshot)),
            currentFailures,
            logInstallPerformance,
            logInstallPerformanceWarn);
    }

    private static void AddInlineChartInfoBuildResult(ChartInfoInlineBuildResult total, ChartInfoInlineBuildResult source)
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

    private void ProcessInlineBmsChartInfo(
        IReadOnlyList<InlineBmsParseCandidate> candidates,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        SongTableFileCheckResult result,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return;
        }
        List<InlineBmsParseCandidate> parsedCandidates = [.. candidates.Where(candidate => candidate?.File != null && candidate.Snapshot != null)];
        if (parsedCandidates.Count == 0)
        {
            return;
        }
        var inlineBuildService = new ChartInfoInlineBuildService(chartInfoBuildService, result.FileDiffParserDegree, result.InlineChartInfoBatchSize, currentChartInfoRowsBySha256);
        ChartInfoInlineBuildResult inlineResult = inlineBuildService.BuildForSnapshots(
            dbGateway,
            parsedCandidates.Select(candidate => InlineChartSnapshotTarget.FromBmsFile(candidate.File, candidate.Snapshot)),
            currentFailures,
            logInstallPerformance,
            logInstallPerformanceWarn);
        ApplyInlineChartInfoResult(result, inlineResult);
    }

    private void ProcessInlineBmsonChartInfo(
        IReadOnlyList<InlineBmsonParseCandidate> candidates,
        BmsLibraryDbGateway dbGateway,
        IDictionary<string, LR2SongDBExtended.chart_info_parse_failure> currentFailures,
        SongTableFileCheckResult result,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> currentChartInfoRowsBySha256,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return;
        }
        List<InlineBmsonParseCandidate> parsedCandidates = [.. candidates.Where(candidate => candidate?.Song != null && candidate.Snapshot != null)];
        if (parsedCandidates.Count == 0)
        {
            return;
        }
        var inlineBuildService = new ChartInfoInlineBuildService(chartInfoBuildService, result.FileDiffParserDegree, result.InlineChartInfoBatchSize, currentChartInfoRowsBySha256);
        ChartInfoInlineBuildResult inlineResult = inlineBuildService.BuildForSnapshots(
            dbGateway,
            parsedCandidates.Select(candidate => InlineChartSnapshotTarget.FromBmsonSong(candidate.Song, candidate.Snapshot)),
            currentFailures,
            logInstallPerformance,
            logInstallPerformanceWarn);
        ApplyInlineChartInfoResult(result, inlineResult);
    }

    private static void ApplyInlineChartInfoResult(SongTableFileCheckResult result, ChartInfoInlineBuildResult inlineResult, bool storeRows = true)
    {
        if (result == null || inlineResult == null)
        {
            return;
        }
        if (storeRows)
        {
            result.InlineChartInfoRows.AddRange(inlineResult.ChartInfoRows);
            result.InlineChartInfoAppliedRows.AddRange(inlineResult.AppliedRows);
            result.InlineChartInfoParseFailureRows.AddRange(inlineResult.ParseFailureRows);
            foreach (string md5 in inlineResult.ParseFailureDeleteMd5s)
            {
                if (!result.InlineChartInfoParseFailureDeleteMd5s.Contains(md5, StringComparer.OrdinalIgnoreCase))
                {
                    result.InlineChartInfoParseFailureDeleteMd5s.Add(md5);
                }
            }
        }
        result.InlineChartInfoTargetCount += inlineResult.TargetCount;
        result.InlineChartInfoSuccessCount += inlineResult.SuccessCount;
        result.InlineChartInfoCurrentSkippedCount += inlineResult.CurrentSkippedCount;
        result.InlineChartInfoFailureSkippedCount += inlineResult.FailureSkippedCount;
        result.InlineChartInfoParseFailedCount += inlineResult.ParseFailedCount;
        result.InlineChartInfoFailurePersistedCount += inlineResult.FailurePersistedCount;
        result.InlineChartInfoFailureClearedCount += inlineResult.FailureClearedCount;
        result.InlineChartInfoParseMs += inlineResult.ParseMs;
    }

    private static void RemoveRangeIfAny<T>(List<T> list, int index, int count)
    {
        if (list == null || count <= 0 || index < 0 || index >= list.Count)
        {
            return;
        }
        int safeCount = Math.Min(count, list.Count - index);
        if (safeCount > 0)
        {
            list.RemoveRange(index, safeCount);
        }
    }

    private static IEnumerable<List<string>> CreateBatches(IEnumerable<string> paths, int batchSize)
    {
        var batch = new List<string>(Math.Max(1, batchSize));
        foreach (string path in paths ?? [])
        {
            batch.Add(path);
            if (batch.Count >= batchSize)
            {
                yield return batch;
                batch = new List<string>(Math.Max(1, batchSize));
            }
        }
        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    internal enum FileDiffChartKind
    {
        Bms,
        Bmson
    }

    internal sealed class FileDiffParseTarget(FileScanParseCommitOwner.FileDiffChartKind kind, string path, BMSFile existingBmsFile = null, int textFlag = 0)
    {
        public FileDiffChartKind Kind { get; } = kind;

        public string Path { get; } = path ?? string.Empty;

        public BMSFile ExistingBmsFile { get; } = existingBmsFile;

        public int TextFlag { get; } = textFlag == 0 ? 0 : 1;
    }

    private sealed class FileDiffReadCandidate
    {
        private FileDiffReadCandidate(FileDiffChartKind kind, string path, ChartFileReadBuffer buffer, BMSFile existingBmsFile, int textFlag, Exception exception)
        {
            Kind = kind;
            Path = path ?? string.Empty;
            Buffer = buffer;
            ExistingBmsFile = existingBmsFile;
            TextFlag = textFlag == 0 ? 0 : 1;
            Exception = exception;
        }

        public FileDiffChartKind Kind { get; }

        public string Path { get; }

        public ChartFileReadBuffer Buffer { get; }

        public BMSFile ExistingBmsFile { get; }

        public int TextFlag { get; }

        public Exception Exception { get; }

        public static FileDiffReadCandidate CreateSuccess(FileDiffChartKind kind, string path, ChartFileReadBuffer buffer, BMSFile existingBmsFile, int textFlag)
        {
            return new FileDiffReadCandidate(kind, path, buffer, existingBmsFile, textFlag, null);
        }

        public static FileDiffReadCandidate CreateFailure(FileDiffChartKind kind, string path, BMSFile existingBmsFile, int textFlag, Exception exception)
        {
            return new FileDiffReadCandidate(kind, path, null, existingBmsFile, textFlag, exception);
        }
    }

    private sealed class FileDiffParsedCandidate
    {
        private FileDiffParsedCandidate(InlineBmsParseCandidate bmsCandidate, InlineBmsonParseCandidate bmsonCandidate)
        {
            BmsCandidate = bmsCandidate;
            BmsonCandidate = bmsonCandidate;
        }

        public InlineBmsParseCandidate BmsCandidate { get; }

        public InlineBmsonParseCandidate BmsonCandidate { get; }

        public string Path => BmsCandidate?.Path ?? BmsonCandidate?.Path ?? string.Empty;

        public static FileDiffParsedCandidate FromBms(InlineBmsParseCandidate candidate)
        {
            return new FileDiffParsedCandidate(candidate, null);
        }

        public static FileDiffParsedCandidate FromBmson(InlineBmsonParseCandidate candidate)
        {
            return new FileDiffParsedCandidate(null, candidate);
        }
    }

    private sealed class FileDiffPostParseWorkItem(int sequence, FileDiffParsedCandidate parsedCandidate)
    {
        public int Sequence { get; } = sequence;

        public FileDiffParsedCandidate ParsedCandidate { get; } = parsedCandidate;
    }

    private sealed class FileDiffPostParseResult(int sequence)
    {
        public int Sequence { get; } = sequence;

        public FileDiffPostParseItemMetrics Metrics { get; } = new FileDiffPostParseItemMetrics();

        public ChartInfoInlineBuildResult ChartInfoResult { get; } = new ChartInfoInlineBuildResult();

        public List<InlineMaintenanceItemResult> MaintenanceResults { get; } = [];

        public FileScanDiffCommitChunk CommitChunk { get; } = new FileScanDiffCommitChunk();

        public List<InlineBmsParseCandidate> BmsParseFailures { get; } = [];

        public List<InlineBmsonParseCandidate> BmsonParseFailures { get; } = [];

        public List<BMSFile> AddedFiles { get; } = [];

        public HashSet<string> NewlyInsertedBmsPaths { get; } = new HashSet<string>(StringComparer.Ordinal);

        public HashSet<string> SuccessfullyReplacedBmsPaths { get; } = new HashSet<string>(StringComparer.Ordinal);

        public int BmsDateOnlyUpdateCount { get; set; }

        public int BmsTextOnlyUpdateCount { get; set; }

        public List<BmsRelinkDestinationCandidate> BmsRelinkDestinationCandidates { get; } = [];

        public List<LR2SongDBExtended.bmson_song> ParsedBmsonSongs { get; } = [];

        public HashSet<string> SuccessfullyParsedBmsonPaths { get; } = new HashSet<string>(StringComparer.Ordinal);

        public void TrackBmsRelinkDestinationCandidate(InlineBmsParseCandidate candidate)
        {
            if (candidate?.File != null && candidate.ExistingFile == null)
            {
                BmsRelinkDestinationCandidates.Add(new BmsRelinkDestinationCandidate(candidate.File));
            }
        }
    }

    private sealed class FileDiffPostParseItemMetrics
    {
        public int BmsCount { get; set; }

        public int BmsonCount { get; set; }

        public long ChartInfoTicks { get; set; }

        public long MaintenanceTicks { get; set; }

        public long BmsMaintenanceTicks { get; set; }

        public long BmsonMaintenanceTicks { get; set; }

        public long HealthMs { get; set; }

        public long EncodingMs { get; set; }

        public long EncodingReloadMs { get; set; }

        public long EncodingMaxMs { get; set; }

        public long CacheHitCount { get; set; }

        public long ResourceIndexHitCount { get; set; }

        public long FileExistsFallbackCount { get; set; }

        public long CommitQueueWaitTicks { get; set; }

        public long TotalTicks { get; set; }
    }

    private sealed class FileDiffParsePipelineResult
    {
        public List<BMSFile> AddedFiles { get; } = [];

        public HashSet<string> NewlyInsertedBmsPaths { get; } = new HashSet<string>(StringComparer.Ordinal);

        public HashSet<string> SuccessfullyReplacedBmsPaths { get; } = new HashSet<string>(StringComparer.Ordinal);

        public int BmsDateOnlyUpdateCount { get; set; }

        public int BmsTextOnlyUpdateCount { get; set; }

        public int BmsMovedHashRelinkCount { get; set; }

        public int BmsMovedHashRelinkAmbiguousCount { get; set; }

        public List<BmsRelinkDestinationCandidate> BmsRelinkDestinationCandidates { get; } = [];

        public Dictionary<string, Lr2SongUserColumns> BmsMovedHashRelinkUserColumnRestores { get; } =
            new Dictionary<string, Lr2SongUserColumns>(StringComparer.Ordinal);

        public List<LR2SongDBExtended.bmson_song> ParsedBmsonSongs { get; } = [];

        public HashSet<string> SuccessfullyParsedBmsonPaths { get; } = new HashSet<string>(StringComparer.Ordinal);

        public long ReadMs { get; set; }

        public long DigestMs { get; set; }

        public long BmsParseMs { get; set; }

        public long BmsonParseMs { get; set; }

        public int SnapshotQueueHighWatermark { get; set; }

        public void TrackBmsRelinkDestinationCandidate(InlineBmsParseCandidate candidate)
        {
            if (candidate?.File != null && candidate.ExistingFile == null)
            {
                BmsRelinkDestinationCandidates.Add(new BmsRelinkDestinationCandidate(candidate.File));
            }
        }
    }

    private sealed class BmsRelinkDestinationCandidate(BMSFile file)
    {
        public BMSFile File { get; } = file;
    }

    private sealed class InlineMaintenanceItemResult
    {
        public static InlineMaintenanceItemResult Empty { get; } = new InlineMaintenanceItemResult(
            FileDiffChartKind.Bms,
            targetCount: 0,
            successCount: 0,
            failedCount: 0,
            elapsedMs: 0,
            healthMs: 0,
            encodingMs: 0,
            encodingReloadMs: 0,
            encodingReloadCount: 0,
            encodingDetectionResult: null,
            cacheHitCount: 0,
            resourceIndexHitCount: 0,
            resourceHealthSetCacheHitCount: 0,
            fileExistsFallbackCount: 0,
            warningMessage: null);

        public InlineMaintenanceItemResult(
            FileDiffChartKind kind,
            int targetCount,
            int successCount,
            int failedCount,
            long elapsedMs,
            long healthMs,
            long encodingMs,
            long encodingReloadMs,
            int encodingReloadCount,
            BMSFile.BmsEncodingDetectionResult encodingDetectionResult,
            long cacheHitCount,
            long resourceIndexHitCount,
            long resourceHealthSetCacheHitCount,
            long fileExistsFallbackCount,
            string warningMessage)
        {
            Kind = kind;
            TargetCount = Math.Max(0, targetCount);
            SuccessCount = Math.Max(0, successCount);
            FailedCount = Math.Max(0, failedCount);
            ElapsedMs = Math.Max(0L, elapsedMs);
            HealthMs = Math.Max(0L, healthMs);
            EncodingMs = Math.Max(0L, encodingMs);
            EncodingReloadMs = Math.Max(0L, encodingReloadMs);
            EncodingReloadCount = Math.Max(0, encodingReloadCount);
            EncodingDetectCount = encodingDetectionResult == null ? 0 : 1;
            EncodingFastAsciiCount = encodingDetectionResult?.FastAscii == true ? 1 : 0;
            SetEncodingOutcomeCounts(encodingDetectionResult?.Outcome ?? BMSFile.EncodingDetectionOutcome.Other, EncodingDetectCount);
            CacheHitCount = Math.Max(0L, cacheHitCount);
            ResourceIndexHitCount = Math.Max(0L, resourceIndexHitCount);
            ResourceHealthSetCacheHitCount = Math.Max(0L, resourceHealthSetCacheHitCount);
            FileExistsFallbackCount = Math.Max(0L, fileExistsFallbackCount);
            WarningMessage = warningMessage;
            Succeeded = SuccessCount > 0;
        }

        private void SetEncodingOutcomeCounts(BMSFile.EncodingDetectionOutcome outcome, int count)
        {
            if (count <= 0)
            {
                return;
            }
            switch (outcome)
            {
                case BMSFile.EncodingDetectionOutcome.ShiftJis:
                    EncodingShiftJisCount = count;
                    break;
                case BMSFile.EncodingDetectionOutcome.ShiftJisQuestion:
                    EncodingShiftJisQuestionCount = count;
                    break;
                case BMSFile.EncodingDetectionOutcome.Korean:
                    EncodingKoreanCount = count;
                    break;
                case BMSFile.EncodingDetectionOutcome.KoreanQuestion:
                    EncodingKoreanQuestionCount = count;
                    break;
                case BMSFile.EncodingDetectionOutcome.Utf8:
                    EncodingUtf8Count = count;
                    break;
                case BMSFile.EncodingDetectionOutcome.Unknown:
                    EncodingUnknownCount = count;
                    break;
                default:
                    EncodingOtherCount = count;
                    break;
            }
        }

        public FileDiffChartKind Kind { get; }

        public int TargetCount { get; }

        public int SuccessCount { get; }

        public int FailedCount { get; }

        public long ElapsedMs { get; }

        public long HealthMs { get; }

        public long EncodingMs { get; }

        public long EncodingReloadMs { get; }

        public int EncodingReloadCount { get; }

        public int EncodingDetectCount { get; private set; }

        public int EncodingFastAsciiCount { get; private set; }

        public int EncodingShiftJisCount { get; private set; }

        public int EncodingShiftJisQuestionCount { get; private set; }

        public int EncodingKoreanCount { get; private set; }

        public int EncodingKoreanQuestionCount { get; private set; }

        public int EncodingUtf8Count { get; private set; }

        public int EncodingUnknownCount { get; private set; }

        public int EncodingOtherCount { get; private set; }

        public long CacheHitCount { get; }

        public long ResourceIndexHitCount { get; }

        public long ResourceHealthSetCacheHitCount { get; }

        public long FileExistsFallbackCount { get; }

        public string WarningMessage { get; }

        public bool Succeeded { get; }
    }

    private sealed class InlineBmsParseCandidate
    {
        private InlineBmsParseCandidate(string path, ChartFileSnapshot snapshot, BMSFile file, BMSFile existingFile, Exception exception, string failureStage)
        {
            Path = path ?? string.Empty;
            Snapshot = snapshot;
            File = file;
            ExistingFile = existingFile;
            Exception = exception;
            FailureStage = failureStage ?? string.Empty;
        }

        public string Path { get; }

        public ChartFileSnapshot Snapshot { get; }

        public BMSFile File { get; }

        public BMSFile ExistingFile { get; }

        public Exception Exception { get; }

        public string FailureStage { get; }

        public static InlineBmsParseCandidate CreateSuccess(string path, ChartFileSnapshot snapshot, BMSFile file, BMSFile existingFile)
        {
            return new InlineBmsParseCandidate(path, snapshot, file, existingFile, null, string.Empty);
        }

        public static InlineBmsParseCandidate CreateFailure(string path, BMSFile existingFile, Exception exception, string failureStage)
        {
            return new InlineBmsParseCandidate(path, null, null, existingFile, exception, failureStage);
        }
    }

    private sealed class InlineBmsonParseCandidate
    {
        private InlineBmsonParseCandidate(string path, ChartFileSnapshot snapshot, LR2SongDBExtended.bmson_song song, Exception exception, string failureStage)
        {
            Path = path ?? string.Empty;
            Snapshot = snapshot;
            Song = song;
            Exception = exception;
            FailureStage = failureStage ?? string.Empty;
        }

        public string Path { get; }

        public ChartFileSnapshot Snapshot { get; }

        public LR2SongDBExtended.bmson_song Song { get; }

        public Exception Exception { get; }

        public string FailureStage { get; }

        public static InlineBmsonParseCandidate CreateSuccess(string path, ChartFileSnapshot snapshot, LR2SongDBExtended.bmson_song song)
        {
            return new InlineBmsonParseCandidate(path, snapshot, song, null, string.Empty);
        }

        public static InlineBmsonParseCandidate CreateFailure(string path, Exception exception, string failureStage)
        {
            return new InlineBmsonParseCandidate(path, null, null, exception, failureStage);
        }
    }

    internal static long EstimateCurrentFileDiffReadBytes(IEnumerable<string> paths)
    {
        long total = 0L;
        foreach (string path in paths ?? [])
        {
            long length;
            try
            {
                length = LongPathFileSystem.GetFileLength(path);
            }
            catch
            {
                continue;
            }
            if (length <= 0L)
            {
                continue;
            }
            if (long.MaxValue - total < length)
            {
                return long.MaxValue;
            }
            total += length;
        }
        return total;
    }

    internal static int ResolveDefaultFileDiffParserDegree()
    {
        return ResolveDefaultFileDiffParserDegree(Environment.ProcessorCount);
    }

    internal static int ResolveDefaultFileDiffParserDegree(int processorCount)
    {
        int normalizedProcessorCount = Math.Max(1, processorCount);
        if (normalizedProcessorCount <= 2)
        {
            return 1;
        }
        if (normalizedProcessorCount <= 4)
        {
            return 2;
        }
        return Math.Max(2, Math.Min(normalizedProcessorCount - 2, (normalizedProcessorCount + 1) / 2));
    }

    internal static int ResolveDefaultFileDiffPostParseWorkerDegree(int processorCount, int parserDegree)
    {
        int normalizedProcessorCount = Math.Max(1, processorCount);
        int normalizedParserDegree = Math.Max(1, parserDegree);
        if (normalizedProcessorCount <= 2)
        {
            return normalizedParserDegree;
        }
        int targetPostParseDegree = normalizedParserDegree + Math.Max(1, normalizedParserDegree / 2);
        return Math.Max(normalizedParserDegree, Math.Min(normalizedProcessorCount, targetPostParseDegree));
    }

    internal static int ResolveFileDiffParsedQueueCapacity(int parserDegree)
    {
        int normalizedParserDegree = Math.Max(1, parserDegree);
        return Math.Max(1, normalizedParserDegree * 2);
    }

    internal static int ResolveFileDiffPostParseQueueCapacity(int postParseWorkerDegree)
    {
        int normalizedWorkerDegree = Math.Max(1, postParseWorkerDegree);
        return Math.Max(1, normalizedWorkerDegree * 2);
    }

    private int ResolveFileDiffParserDegree()
    {
        if (fileDiffParserDegreeOverride.HasValue)
        {
            return Math.Max(1, fileDiffParserDegreeOverride.Value);
        }
        return ResolveDefaultFileDiffParserDegree();
    }

    private int ResolveFileDiffPostParseWorkerDegree(int parserDegree)
    {
        int normalizedParserDegree = Math.Max(1, parserDegree);
        if (fileDiffParserDegreeOverride.HasValue)
        {
            return normalizedParserDegree;
        }
        return ResolveDefaultFileDiffPostParseWorkerDegree(Environment.ProcessorCount, normalizedParserDegree);
    }

    private int ResolveInlineChartInfoBatchSize()
    {
        if (inlineChartInfoBatchSizeOverride.HasValue)
        {
            return Math.Max(1, inlineChartInfoBatchSizeOverride.Value);
        }
        return ResolveDefaultInlineChartInfoBatchSize();
    }

    internal static int ResolveDefaultInlineChartInfoBatchSize()
    {
        return DefaultInlineChartInfoBatchSize;
    }

    internal static int ResolveDefaultFileDiffCommitChunkSize()
    {
        return DefaultFileDiffCommitChunkSize;
    }

    private int ResolveFileDiffCommitChunkSize()
    {
        if (fileDiffCommitChunkSizeOverride.HasValue)
        {
            return Math.Max(1, fileDiffCommitChunkSizeOverride.Value);
        }
        return ResolveDefaultFileDiffCommitChunkSize();
    }



    private static long TicksToMilliseconds(long ticks)
    {
        return (long)(ticks * 1000.0 / Stopwatch.Frequency);
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return LongPathFileSystem.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string QuoteLogValue(string value)
    {
        if (value == null)
        {
            return "\"\"";
        }
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (left >= long.MaxValue || right >= long.MaxValue || long.MaxValue - left < right)
        {
            return long.MaxValue;
        }
        return left + right;
    }

}
