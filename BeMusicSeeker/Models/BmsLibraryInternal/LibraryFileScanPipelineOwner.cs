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
    private readonly ILibraryFileScanPipelineHost host;

    private readonly BmsLibraryInitializationService initializationService;

    internal LibraryFileScanPipelineOwner(
        ILibraryFileScanPipelineHost host,
        BmsLibraryInitializationService initializationService)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.initializationService = initializationService ?? throw new ArgumentNullException(nameof(initializationService));
    }

    internal ChartScanExecutionResult ExecuteChartScanWithManagedFallback(
        List<string> bmsDirectories,
        bool includeTextSurface,
        bool includeDirectorySurface,
        Action<string> reportScanner = null)
    {
        IChartFileScanner scanner = new EverythingFileScanner();
        reportScanner?.Invoke("Native");
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
            host.LogEverythingScan("chart native file scan failed reason=" + nativeFailureReason);
            throw new InvalidOperationException("chart native file scan failed: " + nativeFailureReason);
        }

        host.LogEverythingScan("chart native file scan unavailable reason=" + nativeFailureReason + " fallback=managed");
        host.QueueEverythingFallbackWarning(nativeFailureReason);
        reportScanner?.Invoke("Fallback");
        ChartScanExecutionResult fallbackResult = new FastDirectoryFileScanner().Scan(
            bmsDirectories,
            ChartDirectoryScanBuilder.ChartExtensions,
            host.EverythingScanLoggingEnabled,
            includeTextSurface,
            includeDirectorySurface);
        if (!IsAuthoritativeChartScan(fallbackResult))
        {
            string fallbackFailureReason = GetChartScanFailureReason(fallbackResult);
            host.LogEverythingScan("chart fallback file scan failed nativeReason=" + nativeFailureReason + " fallbackReason=" + fallbackFailureReason);
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
        host.LogEverythingScan("chart fallback file scan succeeded nativeReason=" + nativeFailureReason + " charts=" + fallbackResult.Result.ChartFilePaths.Count + " dirs=" + fallbackResult.Result.ChartDirectories.Count);
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
        Lr2SongDbSyncAppManagedOutputScope initialAppManagedOutputScope = host.CreateLr2SongDbSyncAppManagedOutputScope();
        Task<Lr2FolderFileDiffPreparationResult> lr2FolderFileDiffPreparationTask = null;
        bool protectExistingBmsRowsFromLr2SongDbSyncMigration = host.ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(options);
        void StartLr2FolderFileDiffPreparation(SongTableFileCheckResult partialResult)
        {
            if (lr2FolderFileDiffPreparationTask != null
                || !host.CanPrepareLr2FolderFileDiff(options, partialResult))
            {
                return;
            }

            lr2FolderFileDiffPreparationTask = Task.Run(() =>
                host.PrepareLr2FolderFileDiffSync(options, bmsDirectories, partialResult, reason))
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
            currentInstallDestinationCharts,
            bmsDirectories,
            bmsDirectories,
            host.CreateCurrentLr2BuiltinCustomFolderSettings(DateTime.UtcNow),
            null,
            resolveNormalFolderMtimeSnapshot,
            StartLr2FolderFileDiffPreparation,
            protectExistingBmsRowsFromLr2SongDbSyncMigration: protectExistingBmsRowsFromLr2SongDbSyncMigration,
            lr2FolderExcludedDirectories: initialAppManagedOutputScope.IsComplete
                ? initialAppManagedOutputScope.Directories
                : []);
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
        host.ApplyLr2FolderFileDiffSync(options, bmsDirectories, fileCheckResult, reason, lr2FolderFileDiffPreparationTask);
        completeFileEnumerationOnce();
        host.ApplyLibraryFileScanStorageMutation(fileCheckResult, reason);
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
        host.ApplyLibraryMutationDelta(fileCheckResult.MutationDelta);
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
