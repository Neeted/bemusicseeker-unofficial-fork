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

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly CatalogStorageRowsOwner catalogStorageRowsOwner;

    private readonly IBmsLibraryDialogService dialogService;

    private readonly Func<bool> everythingScanLoggingEnabled;

    private readonly Action<BMSLibrary.LibraryInitializationProgressStage, string, int, int, string, bool> reportLibraryInitializationProgress;

    private readonly Action completeLibraryFileEnumerationProgress;

    private readonly Action completeLibraryFileDiffProgress;

    private readonly Action<string> logInstallPerformance;

    private readonly Action<string> logInstallPerformanceWarn;

    private readonly Action<string> logEverythingScan;

    private readonly Action<string, string> logStartupMemoryCheckpoint;

    private readonly Func<Exception, string> getDisplayedExceptionMessage;

    private readonly Action<string> queueEverythingFallbackWarning;

    private readonly Action<string> queueFileScanSkippedIncompleteWarning;

    private readonly Action<string> queueEmptyScanWithExistingDbWarning;

    private readonly Action<string> dispatchWarningPresentationChanged;

    private readonly BMSLibrary.Lr2SynchronizationOwner lr2Synchronization;

    private readonly CatalogMutationOwner catalogMutationOwner;

    private readonly CatalogChartInfoOwner catalogChartInfoOwner;

    private readonly ResourceHealthIndexOwner resourceHealthOwner;

    private readonly Action<FileScanCatalogReplacementEvent> publishCatalogReplacement;

    private readonly Action<FileScanCatalogReplacementFailureEvent> publishCatalogReplacementFailure;

    private readonly Action<FileScanCatalogResidualEvent> publishCatalogResidual;

    private readonly Lr2FolderFileDiffOwner lr2FolderFileDiffOwner;

    private readonly BmsLibraryInitializationService initializationService;

    private readonly object fileScanGate = new();

    private ActiveFileScan activeFileScan;

    private long fileScanGeneration;

    internal LibraryFileScanPipelineOwner(
        BmsLibraryDbGateway dbGateway,
        CatalogStorageRowsOwner catalogStorageRowsOwner,
        IBmsLibraryDialogService dialogService,
        Func<bool> everythingScanLoggingEnabled,
        Action<BMSLibrary.LibraryInitializationProgressStage, string, int, int, string, bool> reportLibraryInitializationProgress,
        Action completeLibraryFileEnumerationProgress,
        Action completeLibraryFileDiffProgress,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn,
        Action<string> logEverythingScan,
        Action<string, string> logStartupMemoryCheckpoint,
        Func<Exception, string> getDisplayedExceptionMessage,
        Action<string> queueEverythingFallbackWarning,
        Action<string> queueFileScanSkippedIncompleteWarning,
        Action<string> queueEmptyScanWithExistingDbWarning,
        Action<string> dispatchWarningPresentationChanged,
        BMSLibrary.Lr2SynchronizationOwner lr2Synchronization,
        CatalogMutationOwner catalogMutationOwner,
        CatalogChartInfoOwner catalogChartInfoOwner,
        ResourceHealthIndexOwner resourceHealthOwner,
        Action<FileScanCatalogReplacementEvent> publishCatalogReplacement,
        Action<FileScanCatalogReplacementFailureEvent> publishCatalogReplacementFailure,
        Action<FileScanCatalogResidualEvent> publishCatalogResidual,
        BmsLibraryInitializationService initializationService)
    {
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        this.catalogStorageRowsOwner = catalogStorageRowsOwner ?? throw new ArgumentNullException(nameof(catalogStorageRowsOwner));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.everythingScanLoggingEnabled = everythingScanLoggingEnabled ?? throw new ArgumentNullException(nameof(everythingScanLoggingEnabled));
        this.reportLibraryInitializationProgress = reportLibraryInitializationProgress ?? throw new ArgumentNullException(nameof(reportLibraryInitializationProgress));
        this.completeLibraryFileEnumerationProgress = completeLibraryFileEnumerationProgress ?? throw new ArgumentNullException(nameof(completeLibraryFileEnumerationProgress));
        this.completeLibraryFileDiffProgress = completeLibraryFileDiffProgress ?? throw new ArgumentNullException(nameof(completeLibraryFileDiffProgress));
        this.logInstallPerformance = logInstallPerformance ?? throw new ArgumentNullException(nameof(logInstallPerformance));
        this.logInstallPerformanceWarn = logInstallPerformanceWarn ?? throw new ArgumentNullException(nameof(logInstallPerformanceWarn));
        this.logEverythingScan = logEverythingScan ?? throw new ArgumentNullException(nameof(logEverythingScan));
        this.logStartupMemoryCheckpoint = logStartupMemoryCheckpoint ?? throw new ArgumentNullException(nameof(logStartupMemoryCheckpoint));
        this.getDisplayedExceptionMessage = getDisplayedExceptionMessage ?? throw new ArgumentNullException(nameof(getDisplayedExceptionMessage));
        this.queueEverythingFallbackWarning = queueEverythingFallbackWarning ?? throw new ArgumentNullException(nameof(queueEverythingFallbackWarning));
        this.queueFileScanSkippedIncompleteWarning = queueFileScanSkippedIncompleteWarning ?? throw new ArgumentNullException(nameof(queueFileScanSkippedIncompleteWarning));
        this.queueEmptyScanWithExistingDbWarning = queueEmptyScanWithExistingDbWarning ?? throw new ArgumentNullException(nameof(queueEmptyScanWithExistingDbWarning));
        this.dispatchWarningPresentationChanged = dispatchWarningPresentationChanged ?? throw new ArgumentNullException(nameof(dispatchWarningPresentationChanged));
        this.lr2Synchronization = lr2Synchronization ?? throw new ArgumentNullException(nameof(lr2Synchronization));
        this.catalogMutationOwner = catalogMutationOwner ?? throw new ArgumentNullException(nameof(catalogMutationOwner));
        this.catalogChartInfoOwner = catalogChartInfoOwner ?? throw new ArgumentNullException(nameof(catalogChartInfoOwner));
        this.resourceHealthOwner = resourceHealthOwner ?? throw new ArgumentNullException(nameof(resourceHealthOwner));
        this.publishCatalogReplacement = publishCatalogReplacement ?? throw new ArgumentNullException(nameof(publishCatalogReplacement));
        this.publishCatalogReplacementFailure = publishCatalogReplacementFailure ?? throw new ArgumentNullException(nameof(publishCatalogReplacementFailure));
        this.publishCatalogResidual = publishCatalogResidual ?? throw new ArgumentNullException(nameof(publishCatalogResidual));
        lr2FolderFileDiffOwner = new Lr2FolderFileDiffOwner(
            logInstallPerformance,
            logInstallPerformanceWarn,
            getDisplayedExceptionMessage,
            logEverythingScan,
            lr2Synchronization);
        this.initializationService = initializationService ?? throw new ArgumentNullException(nameof(initializationService));
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
                    () => IsActiveGeneration(scan.Generation),
                    reason => QueueFallbackWarningForGeneration(scan.Generation, reason));
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
                    dbGateway,
                    scan.Options,
                    scan.RootDirectories,
                    message =>
                    {
                        if (IsActiveGeneration(scan.Generation))
                        {
                            logInstallPerformance(message);
                        }
                    })).Logging("Lr2NormalFolderMtimeSnapshotPrefetch");
        }
    }

    internal SongTableFileCheckResult ApplyActiveFileScan(
        long generation,
        bool trackLibraryFileCheckProgress,
        InstallDestinationCleanupSnapshot installDestinationCleanupSnapshot)
    {
        if (installDestinationCleanupSnapshot == null)
        {
            throw new ArgumentNullException(nameof(installDestinationCleanupSnapshot));
        }
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
                scan.Reason,
                installDestinationCleanupSnapshot);
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

    private void QueueFallbackWarningForGeneration(long generation, string reason)
    {
        lock (fileScanGate)
        {
            if (activeFileScan?.Generation == generation)
            {
                queueEverythingFallbackWarning(reason);
            }
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
            logEverythingScan("chart_scan_prefetch failed message=" + ex.Message);
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
            () => true,
            reason => queueEverythingFallbackWarning(reason));
    }

    private ChartScanExecutionResult ExecuteChartScanWithManagedFallback(
        List<string> bmsDirectories,
        bool includeTextSurface,
        bool includeDirectorySurface,
        Action<string> reportScanner,
        Func<bool> isActive,
        Action<string> queueFallbackWarning)
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
                logEverythingScan(message);
            }
        }

        void QueueEverythingFallbackWarning(string reason)
        {
            queueFallbackWarning(reason);
        }

        ReportScanner("Native");
        ChartScanExecutionResult scanResult = scanner.Scan(
            bmsDirectories,
            ChartDirectoryScanBuilder.ChartExtensions,
            everythingScanLoggingEnabled(),
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
            everythingScanLoggingEnabled(),
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
        string reason,
        InstallDestinationCleanupSnapshot installDestinationCleanupSnapshot)
    {
        if (installDestinationCleanupSnapshot == null)
        {
            throw new ArgumentNullException(nameof(installDestinationCleanupSnapshot));
        }
        lr2Synchronization.ThrowIfLr2SongDbSyncMutationBlocked("ApplyLibraryFileScanDiff");
        var emptyResult = new SongTableFileCheckResult();
        if (bmsDirectories == null || bmsDirectories.Count == 0)
        {
            if (trackLibraryFileCheckProgress)
            {
                completeLibraryFileEnumerationProgress();
                completeLibraryFileDiffProgress();
            }
            logInstallPerformance("song_tbl_file_check skipped reason=no_bms_directories operation=" + (reason ?? string.Empty));
            return emptyResult;
        }

        logStartupMemoryCheckpoint("file_diff", "before");
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
                completeLibraryFileEnumerationProgress();
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
                        reportLibraryInitializationProgress(
                            BMSLibrary.LibraryInitializationProgressStage.FileEnumeration,
                            scannerLabel,
                            0,
                            0,
                            null,
                            true);
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
            logEverythingScan("song_tbl_file_check skipped reason=incomplete_file_scan operation=" + (reason ?? string.Empty) + " detail=" + failureReason);
            queueFileScanSkippedIncompleteWarning(failureReason);
            completeFileEnumerationOnce();
            if (trackLibraryFileCheckProgress)
            {
                completeLibraryFileDiffProgress();
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
                logInstallPerformance("lr2_normal_folder_mtime_snapshot_prefetch_wait"
                    + " status=completed"
                    + " waitMs=" + stopwatchSnapshotWait.ElapsedMilliseconds
                    + " rows=" + (snapshot?.ExistingRowCount ?? 0)
                    + " elapsedMs=" + (snapshot?.ElapsedMs ?? 0L));
                return snapshot;
            }
            catch (Exception ex)
            {
                stopwatchSnapshotWait.Stop();
                logInstallPerformanceWarn("lr2_normal_folder_mtime_snapshot_prefetch failed"
                    + " waitMs=" + stopwatchSnapshotWait.ElapsedMilliseconds
                    + " message=" + getDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
                return null;
            }
        }

        List<LR2SongDBExtended.chart_info> committedInlineChartInfoRows = [];
        IReadOnlyList<ChartFile> currentInstallDestinationCharts = installDestinationCleanupSnapshot.Charts;
        Lr2SongDbSyncAppManagedOutputScope initialAppManagedOutputScope = lr2Synchronization.CreateLr2SongDbSyncAppManagedOutputScope();
        Task<Lr2FolderFileDiffPreparationResult> lr2FolderFileDiffPreparationTask = null;
        bool protectExistingBmsRowsFromLr2SongDbSyncMigration = lr2Synchronization.ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(options);
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

        CatalogStorageRowsSnapshot storageRowsSnapshot = catalogStorageRowsOwner.CaptureSnapshot();
        SongTableFileCheckResult fileCheckResult = initializationService.ApplyFileScanDiff(
            dbGateway,
            options,
            storageRowsSnapshot.BmsRows,
            resolvedChartScanPrefetchInfo.ScanResult,
            resolvedChartScanPrefetchInfo.ElapsedMs,
            null,
            dialogService,
            logInstallPerformance,
            logEverythingScan,
            storageRowsSnapshot.BmsonRows,
            null,
            completeFileEnumerationOnce,
            () =>
            {
                if (trackLibraryFileCheckProgress)
                {
                    reportLibraryInitializationProgress(BMSLibrary.LibraryInitializationProgressStage.FileDiff, null, 0, 0, null, true);
                }
            },
            (total, processed, path) =>
            {
                if (trackLibraryFileCheckProgress)
                {
                    reportLibraryInitializationProgress(
                        BMSLibrary.LibraryInitializationProgressStage.FileDiff,
                        null,
                        total,
                        processed,
                        path,
                        processed >= total);
                }
            },
            logInstallPerformanceWarn,
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
            lr2Synchronization.CreateCurrentLr2BuiltinCustomFolderSettings(DateTime.UtcNow),
            null,
            resolveNormalFolderMtimeSnapshot,
            StartLr2FolderFileDiffPreparation,
            protectExistingBmsRowsFromLr2SongDbSyncMigration: protectExistingBmsRowsFromLr2SongDbSyncMigration,
            lr2FolderExcludedDirectories: initialAppManagedOutputScope.IsComplete
                ? initialAppManagedOutputScope.Directories
                : [],
            catalogProjectionApplied: projectionResult => ApplyCatalogProjection(
                projectionResult,
                currentInstallDestinationCharts,
                storageRowsSnapshot));
        if (fileCheckResult.EmptyScanWithExistingDbSkipped)
        {
            string skipReason = string.IsNullOrWhiteSpace(fileCheckResult.EmptyScanWithExistingDbSkipReason)
                ? "empty_scan_with_existing_db"
                : fileCheckResult.EmptyScanWithExistingDbSkipReason;
            logEverythingScan("song_tbl_file_check skipped reason=empty_scan_with_existing_db operation=" + (reason ?? string.Empty) + " detail=" + skipReason);
            queueEmptyScanWithExistingDbWarning(skipReason);
            completeFileEnumerationOnce();
            if (trackLibraryFileCheckProgress)
            {
                completeLibraryFileDiffProgress();
            }
            return fileCheckResult;
        }
        LogFileScanFailures(fileCheckResult, reason);
        lr2FolderFileDiffOwner.Apply(options, bmsDirectories, fileCheckResult, reason, lr2FolderFileDiffPreparationTask);
        completeFileEnumerationOnce();
        ApplyCatalogStorageReplacement(fileCheckResult, reason, storageRowsSnapshot);
        lr2Synchronization.CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(options, fileCheckResult, reason);
        if (committedInlineChartInfoRows.Count > 0)
        {
            catalogChartInfoOwner.UpsertIndex(committedInlineChartInfoRows, "file_diff_inline", true);
            committedInlineChartInfoRows.Clear();
        }
        if (fileCheckResult.InlineChartInfoParseFailureRows.Count > 0
            || fileCheckResult.InlineChartInfoParseFailureDeleteMd5s.Count > 0
            || fileCheckResult.InlineChartInfoFailurePersistedCount > 0
            || fileCheckResult.InlineChartInfoFailureClearedCount > 0)
        {
            dispatchWarningPresentationChanged("file_diff_inline_chart_info_parse_failure");
        }
        publishCatalogResidual(FileScanCatalogResidualEvent.Create(fileCheckResult.MutationDelta, reason));
        lr2Synchronization.CaptureLr2SongDbSyncScanSurface(options, bmsDirectories, fileCheckResult);
        lr2Synchronization.CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(options, fileCheckResult, reason);
        lr2Synchronization.MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(options, fileCheckResult);
        if (trackLibraryFileCheckProgress)
        {
            completeLibraryFileDiffProgress();
        }
        fileCheckResult.ReleasePostApplyTransientBuffers();
        logStartupMemoryCheckpoint("file_diff", "after_release");
        logInstallPerformance("library_file_scan_pipeline completed operation=" + (reason ?? string.Empty));
        return fileCheckResult;
    }

    internal void ApplyCatalogStorageReplacement(
        SongTableFileCheckResult fileCheckResult,
        string reason,
        CatalogStorageRowsSnapshot expectedCurrentRows = null)
    {
        using IDisposable mutationSequence = lr2Synchronization.EnterLr2MutationSequence();
        CatalogFileScanStorageReplacementRequest request = null;
        FileScanCatalogReplacementEvent replacementEvent = null;
        using (ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation = resourceHealthOwner.BeginInputMutation())
        {
            try
            {
                request = catalogMutationOwner.CreateFileScanStorageReplacementRequest(
                    fileCheckResult.HasDbDiff,
                    fileCheckResult.NextFiles,
                    fileCheckResult.NextBmsonSongs,
                    fileCheckResult.DeletedPaths,
                    fileCheckResult.DeletedBmsonPaths,
                    fileCheckResult.AddedFiles,
                    fileCheckResult.AddedBmsonSongs);
                if (expectedCurrentRows != null
                    && (request.PreviousBmsRowsVersion != expectedCurrentRows.BmsRowsVersion
                        || request.PreviousBmsonRowsVersion != expectedCurrentRows.BmsonRowsVersion))
                {
                    throw new InvalidOperationException(
                        "The catalog storage rows changed while a file-scan projection was being prepared.");
                }
                CatalogFileScanStorageReplacementReceipt receipt = catalogMutationOwner.ApplyFileScanStorageReplacement(request);
                replacementEvent = new FileScanCatalogReplacementEvent(
                    request,
                    receipt,
                    fileCheckResult.NextResourceIndex,
                    resourceHealthMutation.BaseIndexCurrent,
                    reason);
            }
            catch
            {
                if (request != null)
                {
                    publishCatalogReplacementFailure(new FileScanCatalogReplacementFailureEvent(
                        request,
                        resourceHealthMutation.BaseIndexCurrent,
                        reason));
                }
                throw;
            }
        }
        publishCatalogReplacement(replacementEvent);
    }

    internal void ApplyCatalogProjection(
        SongTableFileCheckResult fileCheckResult,
        IEnumerable<ChartFile> currentInstallDestinationCharts,
        CatalogStorageRowsSnapshot storageRowsSnapshot = null)
    {
        storageRowsSnapshot ??= catalogStorageRowsOwner.CaptureSnapshot();
        ApplyCatalogProjection(
            fileCheckResult,
            storageRowsSnapshot.BmsRows,
            storageRowsSnapshot.BmsonRows,
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
            if (chart?.Kind == ChartFileKind.Bms)
            {
                return (bmsOwner != null && nextFileOwners.Contains(bmsOwner))
                    || (!string.IsNullOrWhiteSpace(chart.Path) && nextFilePaths.Contains(chart.Path));
            }

            LR2SongDBExtended.bmson_song bmsonOwner = chart?.GetBmsonStorageOwner();
            return chart?.Kind == ChartFileKind.Bmson
                && ((bmsonOwner != null && nextBmsonOwners.Contains(bmsonOwner))
                    || (!string.IsNullOrWhiteSpace(chart?.Path) && nextBmsonPaths.Contains(chart.Path)));
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

        logInstallPerformanceWarn("song_tbl_file_check_failures reason=" + (reason ?? string.Empty) + " count=" + result.FileScanFailures.Count);
        foreach (ChartFileScanFailure failure in result.FileScanFailures)
        {
            if (failure == null)
            {
                continue;
            }

            logInstallPerformanceWarn(
                "song_tbl_file_check_file_failed kind=" + failure.ChartKind
                + " stage=" + failure.Stage
                + " exception=" + failure.ExceptionType
                + " path=" + (failure.Path ?? string.Empty)
                + " message=" + (failure.Message ?? string.Empty).Replace(Environment.NewLine, " | "));
        }
    }
}
