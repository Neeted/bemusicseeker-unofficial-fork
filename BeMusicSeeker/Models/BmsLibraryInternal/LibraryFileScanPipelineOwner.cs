using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ChartScanPrefetchInfo
{
    internal ChartScanExecutionResult ScanResult { get; set; }

    internal long ElapsedMs { get; set; }
}

/// <summary>
/// Owns the shared chart file scan, diff, parse, commit, and terminal apply route.
/// </summary>
internal sealed class LibraryFileScanPipelineOwner
{
    private sealed class ActiveFileScan
    {
        internal long Generation { get; init; }

        internal BmsLibraryOptionsSnapshot Options { get; init; }

        internal List<string> RootDirectories { get; init; }

        internal string Reason { get; init; }

        internal Task<ChartScanPrefetchInfo> ChartScanPrefetchTask { get; set; }

        internal Task<Lr2NormalFolderMtimeSnapshot> NormalFolderMtimeSnapshotTask { get; set; }

        internal bool Applying { get; set; }
    }

    private readonly ILibraryFileScanPipelineHost host;

    private readonly ILibraryFileScanLr2FolderHost lr2Host;

    private readonly Lr2FolderFileDiffOwner lr2FolderFileDiffOwner;

    private readonly BmsLibraryInitializationService initializationService;

    private readonly FileScanParseCommitOwner fileScanParseCommitOwner;

    private readonly Func<LibraryFileScanStorageMutationCoordinator> storageMutationCoordinatorFactory;

    private readonly Func<LibraryMutationDeltaApplyCoordinator> mutationDeltaApplyCoordinatorFactory;

    private readonly object fileScanGate = new();

    private ActiveFileScan activeFileScan;

    private long fileScanGeneration;

    internal LibraryFileScanPipelineOwner(
        ILibraryFileScanPipelineHost host,
        ILibraryFileScanLr2FolderHost lr2Host,
        BmsLibraryInitializationService initializationService,
        Func<LibraryFileScanStorageMutationCoordinator> storageMutationCoordinatorFactory,
        Func<LibraryMutationDeltaApplyCoordinator> mutationDeltaApplyCoordinatorFactory,
        FileScanParseCommitOwner fileScanParseCommitOwner = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.lr2Host = lr2Host ?? throw new ArgumentNullException(nameof(lr2Host));
        lr2FolderFileDiffOwner = new Lr2FolderFileDiffOwner(this.host, lr2Host);
        this.initializationService = initializationService ?? throw new ArgumentNullException(nameof(initializationService));
        this.fileScanParseCommitOwner = fileScanParseCommitOwner ?? this.initializationService.ParseCommitOwner;
        this.storageMutationCoordinatorFactory = storageMutationCoordinatorFactory ?? throw new ArgumentNullException(nameof(storageMutationCoordinatorFactory));
        this.mutationDeltaApplyCoordinatorFactory = mutationDeltaApplyCoordinatorFactory ?? throw new ArgumentNullException(nameof(mutationDeltaApplyCoordinatorFactory));
    }

    internal long BeginFileScanRequest(
        BmsLibraryOptionsSnapshot options,
        List<string> rootDirectories,
        string reason,
        Action<string> reportScanner = null)
    {
        ActiveFileScan scan;
        lock (fileScanGate)
        {
            if (activeFileScan != null)
            {
                throw new InvalidOperationException(
                    "A library file scan is already active. Complete or abort it before starting another scan.");
            }

            scan = new ActiveFileScan
            {
                Generation = checked(++fileScanGeneration),
                Options = options,
                RootDirectories = [.. rootDirectories ?? []],
                Reason = reason ?? string.Empty
            };
            activeFileScan = scan;
        }

        if (scan.RootDirectories.Count == 0)
        {
            return scan.Generation;
        }

        try
        {
            scan.ChartScanPrefetchTask = Task.Run(() =>
            {
                var stopwatchPrefetch = Stopwatch.StartNew();
                ChartScanExecutionResult scanResult = ExecuteChartScanWithManagedFallback(
                    scan.RootDirectories,
                    BMSLibrary.ShouldIncludeLr2TextSurface(scan.Options),
                    BMSLibrary.ShouldIncludeLr2DirectorySurface(scan.Options),
                    reportScanner,
                    () => IsActiveGeneration(scan.Generation));
                stopwatchPrefetch.Stop();
                return new ChartScanPrefetchInfo
                {
                    ScanResult = scanResult,
                    ElapsedMs = stopwatchPrefetch.ElapsedMilliseconds
                };
            });
            return scan.Generation;
        }
        catch
        {
            lock (fileScanGate)
            {
                if (ReferenceEquals(activeFileScan, scan))
                {
                    activeFileScan = null;
                }
            }
            throw;
        }
    }

    internal void StartActiveNormalFolderMtimeSnapshot(long generation)
    {
        ActiveFileScan scan = GetActiveFileScan(generation);
        lock (fileScanGate)
        {
            if (!ReferenceEquals(activeFileScan, scan)
                || scan.Applying)
            {
                throw new InvalidOperationException("The active library file scan is no longer available.");
            }
            if (scan.Options?.OperationModeLR2DB != true
                || scan.RootDirectories.Count == 0
                || scan.NormalFolderMtimeSnapshotTask != null)
            {
                return;
            }

            scan.NormalFolderMtimeSnapshotTask = Task.Run(() =>
                initializationService.LoadNormalFolderMtimeSnapshot(
                    host.DbGateway,
                    scan.Options,
                    scan.RootDirectories,
                    message =>
                    {
                        if (IsActiveGeneration(scan.Generation))
                        {
                            host.LogInstallPerformance(message);
                        }
                    })).Logging("Lr2NormalFolderMtimeSnapshotPrefetch");
        }
    }

    internal SongTableFileCheckResult ApplyActiveFileScan(
        long generation,
        bool trackLibraryFileCheckProgress)
    {
        ActiveFileScan scan = GetActiveFileScan(generation);
        lock (fileScanGate)
        {
            if (!ReferenceEquals(activeFileScan, scan))
            {
                throw new InvalidOperationException("The active library file scan is no longer available.");
            }
            if (scan.Applying)
            {
                throw new InvalidOperationException("The active library file scan is already being applied.");
            }
            scan.Applying = true;
        }

        try
        {
            ChartScanPrefetchInfo chartScanPrefetchInfo = ResolveChartScanPrefetch(scan);
            SongTableFileCheckResult result = ApplyFileScanDiff(
                scan.Options,
                scan.RootDirectories,
                chartScanPrefetchInfo,
                scan.NormalFolderMtimeSnapshotTask,
                trackLibraryFileCheckProgress,
                scan.Reason);
            CompleteFileScan(scan);
            return result;
        }
        catch
        {
            CompleteFileScan(scan);
            throw;
        }
    }

    internal void AbortActiveFileScan(long generation)
    {
        ActiveFileScan scan;
        lock (fileScanGate)
        {
            scan = activeFileScan;
            if (scan == null || scan.Generation != generation || scan.Applying)
            {
                return;
            }
            activeFileScan = null;
        }
        ObserveTaskFailure(scan.ChartScanPrefetchTask);
        ObserveTaskFailure(scan.NormalFolderMtimeSnapshotTask);
    }

    private static void ObserveTaskFailure(Task task)
    {
        if (task == null)
        {
            return;
        }

        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    private void CompleteFileScan(ActiveFileScan scan)
    {
        lock (fileScanGate)
        {
            if (ReferenceEquals(activeFileScan, scan))
            {
                activeFileScan = null;
            }
        }
    }

    private ActiveFileScan GetActiveFileScan(long generation)
    {
        lock (fileScanGate)
        {
            if (activeFileScan == null)
            {
                throw new InvalidOperationException("No active library file scan exists.");
            }
            if (activeFileScan.Generation != generation)
            {
                throw new InvalidOperationException("The active library file scan generation is stale.");
            }
            return activeFileScan;
        }
    }

    private bool IsActiveGeneration(long generation)
    {
        lock (fileScanGate)
        {
            return activeFileScan?.Generation == generation;
        }
    }

    private ChartScanPrefetchInfo ResolveChartScanPrefetch(ActiveFileScan scan)
    {
        if (scan?.ChartScanPrefetchTask == null)
        {
            return null;
        }

        try
        {
            return scan.ChartScanPrefetchTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            host.LogEverythingScan("chart_scan_prefetch failed message=" + ex.Message);
            return null;
        }
    }

    internal ChartScanExecutionResult ExecuteChartScanWithManagedFallback(
        List<string> bmsDirectories,
        bool includeTextSurface,
        bool includeDirectorySurface,
        Action<string> reportScanner = null)
    {
        return ExecuteChartScanWithManagedFallback(
            bmsDirectories,
            includeTextSurface,
            includeDirectorySurface,
            reportScanner,
            () => true);
    }

    private ChartScanExecutionResult ExecuteChartScanWithManagedFallback(
        List<string> bmsDirectories,
        bool includeTextSurface,
        bool includeDirectorySurface,
        Action<string> reportScanner,
        Func<bool> isActive)
    {
        IChartFileScanner scanner = new EverythingFileScanner();
        void ReportScanner(string label)
        {
            if (isActive())
            {
                reportScanner?.Invoke(label);
            }
        }

        void LogEverythingScan(string message)
        {
            if (isActive())
            {
                host.LogEverythingScan(message);
            }
        }

        void QueueEverythingFallbackWarning(string reason)
        {
            if (isActive())
            {
                host.QueueEverythingFallbackWarning(reason);
            }
        }

        ReportScanner("Native");
        ChartScanExecutionResult scanResult = scanner.Scan(
            bmsDirectories,
            ChartDirectoryScanBuilder.ChartExtensions,
            host.EverythingScanLoggingEnabled,
            includeTextSurface,
            includeDirectorySurface);
        if (IsAuthoritativeChartScan(scanResult))
        {
            return scanResult;
        }

        string nativeFailureReason = scanResult?.ErrorReason ?? "unknown";
        if (IsNativeBridgeContractFailure(nativeFailureReason))
        {
            LogEverythingScan("chart native file scan failed reason=" + nativeFailureReason);
            throw new InvalidOperationException("chart native file scan failed: " + nativeFailureReason);
        }

        LogEverythingScan("chart native file scan unavailable reason=" + nativeFailureReason + " fallback=managed");
        QueueEverythingFallbackWarning(nativeFailureReason);
        ReportScanner("Fallback");
        ChartScanExecutionResult fallbackResult = new FastDirectoryFileScanner().Scan(
            bmsDirectories,
            ChartDirectoryScanBuilder.ChartExtensions,
            host.EverythingScanLoggingEnabled,
            includeTextSurface,
            includeDirectorySurface);
        if (!IsAuthoritativeChartScan(fallbackResult))
        {
            string fallbackFailureReason = GetChartScanFailureReason(fallbackResult);
            LogEverythingScan("chart fallback file scan failed nativeReason=" + nativeFailureReason + " fallbackReason=" + fallbackFailureReason);
            return new ChartScanExecutionResult
            {
                ScanSource = ChartScanSource.Fallback,
                Success = false,
                IsComplete = false,
                ErrorReason = "chart_fallback_file_scan_failed:" + fallbackFailureReason + " (native: " + nativeFailureReason + ")",
                IncompleteReason = fallbackFailureReason,
                FallbackUsed = true,
                FallbackReason = nativeFailureReason
            };
        }
        fallbackResult.ScanSource = ChartScanSource.Fallback;
        fallbackResult.FallbackUsed = true;
        fallbackResult.FallbackReason = nativeFailureReason;
        LogEverythingScan("chart fallback file scan succeeded nativeReason=" + nativeFailureReason + " charts=" + fallbackResult.Result.ChartFilePaths.Count + " dirs=" + fallbackResult.Result.ChartDirectories.Count);
        return fallbackResult;
    }

    internal SongTableFileCheckResult ApplyFileScanDiff(
        BmsLibraryOptionsSnapshot options,
        List<string> bmsDirectories,
        ChartScanPrefetchInfo chartScanPrefetchInfo,
        Task<Lr2NormalFolderMtimeSnapshot> normalFolderMtimeSnapshotTask,
        bool trackLibraryFileCheckProgress,
        string reason)
    {
        host.ThrowIfLr2SongDbSyncMutationBlocked("ApplyLibraryFileScanDiff");
        var emptyResult = new SongTableFileCheckResult();
        if (bmsDirectories == null || bmsDirectories.Count == 0)
        {
            if (trackLibraryFileCheckProgress)
            {
                host.CompleteLibraryFileEnumerationProgress();
                host.CompleteLibraryFileDiffProgress();
            }
            host.LogInstallPerformance("song_tbl_file_check skipped reason=no_bms_directories operation=" + (reason ?? string.Empty));
            return emptyResult;
        }

        host.LogStartupMemoryCheckpoint("file_diff", "before");
        bool fileEnumerationCompleted = false;
        void completeFileEnumerationOnce()
        {
            if (fileEnumerationCompleted)
            {
                return;
            }
            fileEnumerationCompleted = true;
            if (trackLibraryFileCheckProgress)
            {
                host.CompleteLibraryFileEnumerationProgress();
            }
        }
        ChartScanPrefetchInfo resolvedChartScanPrefetchInfo = chartScanPrefetchInfo;
        if (resolvedChartScanPrefetchInfo?.ScanResult == null)
        {
            var stopwatchResolveScan = Stopwatch.StartNew();
            ChartScanExecutionResult resolvedScanResult = ExecuteChartScanWithManagedFallback(
                bmsDirectories,
                BMSLibrary.ShouldIncludeLr2TextSurface(options),
                BMSLibrary.ShouldIncludeLr2DirectorySurface(options),
                scannerLabel =>
                {
                    if (trackLibraryFileCheckProgress)
                    {
                        host.ReportLibraryInitializationProgress(
                            BMSLibrary.LibraryInitializationProgressStage.FileEnumeration,
                            scannerLabel,
                            force: true);
                    }
                });
            stopwatchResolveScan.Stop();
            resolvedChartScanPrefetchInfo = new ChartScanPrefetchInfo
            {
                ScanResult = resolvedScanResult,
                ElapsedMs = stopwatchResolveScan.ElapsedMilliseconds
            };
        }
        if (!IsAuthoritativeChartScan(resolvedChartScanPrefetchInfo?.ScanResult))
        {
            string failureReason = GetChartScanFailureReason(resolvedChartScanPrefetchInfo?.ScanResult);
            host.LogEverythingScan("song_tbl_file_check skipped reason=incomplete_file_scan operation=" + (reason ?? string.Empty) + " detail=" + failureReason);
            host.QueueFileScanSkippedIncompleteWarning(failureReason);
            completeFileEnumerationOnce();
            if (trackLibraryFileCheckProgress)
            {
                host.CompleteLibraryFileDiffProgress();
            }
            return emptyResult;
        }
        Lr2NormalFolderMtimeSnapshot resolveNormalFolderMtimeSnapshot()
        {
            if (normalFolderMtimeSnapshotTask == null)
            {
                return null;
            }

            var stopwatchSnapshotWait = Stopwatch.StartNew();
            try
            {
                Lr2NormalFolderMtimeSnapshot snapshot = normalFolderMtimeSnapshotTask.GetAwaiter().GetResult();
                stopwatchSnapshotWait.Stop();
                host.LogInstallPerformance("lr2_normal_folder_mtime_snapshot_prefetch_wait"
                    + " status=completed"
                    + " waitMs=" + stopwatchSnapshotWait.ElapsedMilliseconds
                    + " rows=" + (snapshot?.ExistingRowCount ?? 0)
                    + " elapsedMs=" + (snapshot?.ElapsedMs ?? 0L));
                return snapshot;
            }
            catch (Exception ex)
            {
                stopwatchSnapshotWait.Stop();
                host.LogInstallPerformanceWarn("lr2_normal_folder_mtime_snapshot_prefetch failed"
                    + " waitMs=" + stopwatchSnapshotWait.ElapsedMilliseconds
                    + " message=" + host.GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
                return null;
            }
        }

        List<LR2SongDBExtended.chart_info> committedInlineChartInfoRows = [];
        List<ChartFile> currentInstallDestinationCharts = host.CreateCurrentInstallDestinationCleanupCharts();
        Lr2SongDbSyncAppManagedOutputScope initialAppManagedOutputScope = lr2Host.CreateLr2SongDbSyncAppManagedOutputScope();
        Task<Lr2FolderFileDiffPreparationResult> lr2FolderFileDiffPreparationTask = null;
        bool protectExistingBmsRowsFromLr2SongDbSyncMigration = host.ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(options);
        void StartLr2FolderFileDiffPreparation(SongTableFileCheckResult partialResult)
        {
            if (lr2FolderFileDiffPreparationTask != null
                || !lr2FolderFileDiffOwner.CanPrepare(options, partialResult))
            {
                return;
            }

            lr2FolderFileDiffPreparationTask = Task.Run(() =>
                lr2FolderFileDiffOwner.Prepare(options, bmsDirectories, partialResult, reason))
                .Logging("Lr2FolderFileDiffPrepare");
        }

        SongTableFileCheckResult fileCheckResult = initializationService.ApplyFileScanDiff(
            host.DbGateway,
            options,
            host.BmsFiles,
            resolvedChartScanPrefetchInfo.ScanResult,
            resolvedChartScanPrefetchInfo.ElapsedMs,
            null,
            host.DialogService,
            host.LogInstallPerformance,
            host.LogEverythingScan,
            host.BmsonSongs,
            null,
            completeFileEnumerationOnce,
            () =>
            {
                if (trackLibraryFileCheckProgress)
                {
                    host.ReportLibraryInitializationProgress(BMSLibrary.LibraryInitializationProgressStage.FileDiff, force: true);
                }
            },
            (total, processed, path) =>
            {
                if (trackLibraryFileCheckProgress)
                {
                    host.ReportLibraryInitializationProgress(
                        BMSLibrary.LibraryInitializationProgressStage.FileDiff,
                        totalCount: total,
                        processedCount: processed,
                        currentPath: path,
                        force: processed >= total);
                }
            },
            host.LogInstallPerformanceWarn,
            rows =>
            {
                if (rows == null || rows.Count == 0)
                {
                    return;
                }
                committedInlineChartInfoRows.AddRange(rows.Where(row => row != null));
            },
            bmsDirectories,
            bmsDirectories,
            lr2Host.CreateCurrentLr2BuiltinCustomFolderSettings(DateTime.UtcNow),
            null,
            resolveNormalFolderMtimeSnapshot,
            StartLr2FolderFileDiffPreparation,
            protectExistingBmsRowsFromLr2SongDbSyncMigration: protectExistingBmsRowsFromLr2SongDbSyncMigration,
            lr2FolderExcludedDirectories: initialAppManagedOutputScope.IsComplete
                ? initialAppManagedOutputScope.Directories
                : [],
            fileScanParseCommitOwner: fileScanParseCommitOwner,
            catalogProjectionApplied: projectionResult => ApplyCatalogProjection(projectionResult, currentInstallDestinationCharts));
        if (fileCheckResult.EmptyScanWithExistingDbSkipped)
        {
            string skipReason = string.IsNullOrWhiteSpace(fileCheckResult.EmptyScanWithExistingDbSkipReason)
                ? "empty_scan_with_existing_db"
                : fileCheckResult.EmptyScanWithExistingDbSkipReason;
            host.LogEverythingScan("song_tbl_file_check skipped reason=empty_scan_with_existing_db operation=" + (reason ?? string.Empty) + " detail=" + skipReason);
            host.QueueEmptyScanWithExistingDbWarning(skipReason);
            completeFileEnumerationOnce();
            if (trackLibraryFileCheckProgress)
            {
                host.CompleteLibraryFileDiffProgress();
            }
            return fileCheckResult;
        }
        LogFileScanFailures(fileCheckResult, reason);
        lr2FolderFileDiffOwner.Apply(options, bmsDirectories, fileCheckResult, reason, lr2FolderFileDiffPreparationTask);
        completeFileEnumerationOnce();
        storageMutationCoordinatorFactory().Apply(fileCheckResult, reason);
        host.CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(options, fileCheckResult, reason);
        if (committedInlineChartInfoRows.Count > 0)
        {
            host.UpsertChartInfoIndexRows(committedInlineChartInfoRows, "file_diff_inline");
            committedInlineChartInfoRows.Clear();
        }
        if (fileCheckResult.InlineChartInfoParseFailureRows.Count > 0
            || fileCheckResult.InlineChartInfoParseFailureDeleteMd5s.Count > 0
            || fileCheckResult.InlineChartInfoFailurePersistedCount > 0
            || fileCheckResult.InlineChartInfoFailureClearedCount > 0)
        {
            host.DispatchWarningPresentationChanged("file_diff_inline_chart_info_parse_failure");
        }
        mutationDeltaApplyCoordinatorFactory().Apply(fileCheckResult.MutationDelta);
        host.CaptureLr2SongDbSyncScanSurface(options, bmsDirectories, fileCheckResult);
        host.CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(options, fileCheckResult, reason);
        host.MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(options, fileCheckResult);
        if (trackLibraryFileCheckProgress)
        {
            host.CompleteLibraryFileDiffProgress();
        }
        fileCheckResult.ReleasePostApplyTransientBuffers();
        host.LogStartupMemoryCheckpoint("file_diff", "after_release");
        host.LogInstallPerformance("library_file_scan_pipeline completed operation=" + (reason ?? string.Empty));
        return fileCheckResult;
    }

    internal void ApplyCatalogProjection(
        SongTableFileCheckResult fileCheckResult,
        IEnumerable<ChartFile> currentInstallDestinationCharts)
    {
        ApplyCatalogProjection(
            fileCheckResult,
            host.BmsFiles,
            host.BmsonSongs,
            currentInstallDestinationCharts);
    }

    internal static void ApplyCatalogProjection(
        SongTableFileCheckResult fileCheckResult,
        IEnumerable<BMSFile> currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs,
        IEnumerable<ChartFile> currentInstallDestinationCharts)
    {
        if (fileCheckResult == null)
        {
            throw new ArgumentNullException(nameof(fileCheckResult));
        }

        var stopwatchApply = Stopwatch.StartNew();
        var deletedPathSet = new HashSet<string>(fileCheckResult.DeletedPaths, StringComparer.Ordinal);
        foreach (BMSFile addedFile in fileCheckResult.AddedFiles)
        {
            if (addedFile != null && !string.IsNullOrWhiteSpace(addedFile.path))
            {
                deletedPathSet.Add(addedFile.path);
            }
        }

        fileCheckResult.NextFiles.Clear();
        fileCheckResult.NextFiles.AddRange((currentFiles ?? [])
            .Where(file => file != null && !deletedPathSet.Contains(file.path)));
        fileCheckResult.NextFiles.AddRange(fileCheckResult.AddedFiles);

        var removedBmsonPaths = new HashSet<string>(fileCheckResult.DeletedBmsonPaths, StringComparer.Ordinal);
        foreach (LR2SongDBExtended.bmson_song addedSong in fileCheckResult.AddedBmsonSongs)
        {
            if (addedSong != null && !string.IsNullOrWhiteSpace(addedSong.path))
            {
                removedBmsonPaths.Add(addedSong.path);
            }
        }

        List<LR2SongDBExtended.bmson_song> nextBmsonSongs = [.. (currentBmsonSongs ?? [])
            .Where(song => song != null
                && !string.IsNullOrWhiteSpace(song.path)
                && !removedBmsonPaths.Contains(song.path))];
        nextBmsonSongs.AddRange(fileCheckResult.AddedBmsonSongs);

        var directoryKeys = new HashSet<string>(
            fileCheckResult.NextDirectoryResourceLookupCache?.Keys ?? [],
            StringComparer.OrdinalIgnoreCase);
        var stopwatchInstlDstCleanup = Stopwatch.StartNew();
        int clearedInstallDestinationCountBefore = fileCheckResult.MutationDelta.UpdatedInstallDestinations.Count;
        var nextFileOwners = new HashSet<BMSFile>(fileCheckResult.NextFiles.Where(file => file != null));
        var nextFilePaths = new HashSet<string>(
            fileCheckResult.NextFiles.Select(file => file?.path).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        var nextBmsonOwners = new HashSet<LR2SongDBExtended.bmson_song>(nextBmsonSongs.Where(song => song != null));
        var nextBmsonPaths = new HashSet<string>(
            nextBmsonSongs.Select(song => song?.path).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        foreach (ChartFile chart in (currentInstallDestinationCharts ?? [])
            .Where(IsCurrentChartOwner)
            .Where(chart => !string.IsNullOrWhiteSpace(chart.InstallDestination)))
        {
            if (!directoryKeys.Contains(chart.InstallDestination))
            {
                fileCheckResult.MutationDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    Chart = chart,
                    NewInstallDestination = null,
                    ClearInstallDestinationState = true
                });
            }
        }
        if (fileCheckResult.MutationDelta.UpdatedInstallDestinations.Count > clearedInstallDestinationCountBefore)
        {
            fileCheckResult.MutationDelta.InvalidateInstalledDirectoryIndex = true;
            fileCheckResult.MutationDelta.ClearDuplicatedCache = true;
        }

        stopwatchInstlDstCleanup.Stop();
        fileCheckResult.InstlDstCleanupMs = stopwatchInstlDstCleanup.ElapsedMilliseconds;
        stopwatchApply.Stop();
        fileCheckResult.ApplyMs = stopwatchApply.ElapsedMilliseconds;
        fileCheckResult.NextBmsonSongs.Clear();
        fileCheckResult.NextBmsonSongs.AddRange(nextBmsonSongs);

        bool IsCurrentChartOwner(ChartFile chart)
        {
            BMSFile bmsOwner = chart?.GetBmsStorageOwner();
            if (bmsOwner != null)
            {
                return nextFileOwners.Contains(bmsOwner)
                    || (!string.IsNullOrWhiteSpace(chart.Path) && nextFilePaths.Contains(chart.Path));
            }

            LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
            return (bmsonOwner != null && nextBmsonOwners.Contains(bmsonOwner))
                || (!string.IsNullOrWhiteSpace(chart?.Path) && nextBmsonPaths.Contains(chart.Path));
        }
    }

    private static bool IsAuthoritativeChartScan(ChartScanExecutionResult scanResult)
    {
        return scanResult?.Success == true
            && scanResult.IsComplete
            && scanResult.Result != null;
    }

    private static string GetChartScanFailureReason(ChartScanExecutionResult scanResult)
    {
        if (scanResult == null)
        {
            return "scan_result_missing";
        }
        if (!string.IsNullOrWhiteSpace(scanResult.IncompleteReason))
        {
            return scanResult.IncompleteReason;
        }
        if (!string.IsNullOrWhiteSpace(scanResult.ErrorReason))
        {
            return scanResult.ErrorReason;
        }
        if (scanResult.Result == null)
        {
            return "scan_result_missing";
        }
        return scanResult.Success ? "scan_incomplete" : "scan_failed";
    }

    private static bool IsNativeBridgeContractFailure(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }
        if (reason.StartsWith("directory_surface_failed:", StringComparison.OrdinalIgnoreCase))
        {
            return RootFileEnumerationService.IsBridgeContractFailure(reason.Substring("directory_surface_failed:".Length));
        }
        return RootFileEnumerationService.IsBridgeContractFailure(reason);
    }

    private void LogFileScanFailures(SongTableFileCheckResult result, string reason)
    {
        if (result?.FileScanFailures == null || result.FileScanFailures.Count == 0)
        {
            return;
        }

        host.LogInstallPerformanceWarn("song_tbl_file_check_failures reason=" + (reason ?? string.Empty) + " count=" + result.FileScanFailures.Count);
        foreach (ChartFileScanFailure failure in result.FileScanFailures)
        {
            if (failure == null)
            {
                continue;
            }

            host.LogInstallPerformanceWarn(
                "song_tbl_file_check_file_failed kind=" + failure.ChartKind
                + " stage=" + failure.Stage
                + " exception=" + failure.ExceptionType
                + " path=" + (failure.Path ?? string.Empty)
                + " message=" + (failure.Message ?? string.Empty).Replace(Environment.NewLine, " | "));
        }
    }
}
