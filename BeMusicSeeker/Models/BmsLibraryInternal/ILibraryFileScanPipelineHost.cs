using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Provides the application-owned state and side effects required by the library file-scan pipeline.
/// </summary>
internal interface ILibraryFileScanPipelineHost
{
    BmsLibraryDbGateway DbGateway { get; }

    IReadOnlyList<BMSFile> BmsFiles { get; }

    IReadOnlyList<LR2SongDBExtended.bmson_song> BmsonSongs { get; }

    IBmsLibraryDialogService DialogService { get; }

    bool EverythingScanLoggingEnabled { get; }

    void ThrowIfLr2SongDbSyncMutationBlocked(string operation);

    void ReportLibraryInitializationProgress(
        BMSLibrary.LibraryInitializationProgressStage stage,
        string scannerLabel = null,
        int totalCount = 0,
        int processedCount = 0,
        string currentPath = null,
        bool force = false);

    void CompleteLibraryFileEnumerationProgress();

    void CompleteLibraryFileDiffProgress();

    void LogInstallPerformance(string message);

    void LogInstallPerformanceWarn(string message);

    void LogEverythingScan(string message);

    void LogStartupMemoryCheckpoint(string phase, string point);

    string GetDisplayedExceptionMessage(Exception exception);

    void QueueEverythingFallbackWarning(string fallbackReason);

    void QueueFileScanSkippedIncompleteWarning(string failureReason);

    void QueueEmptyScanWithExistingDbWarning(string failureReason);

    /// <summary>
    /// Applies the catalog storage replacement phase for a completed file scan.
    /// </summary>
    /// <param name="fileCheckResult">The immutable-by-convention scan result owned by the pipeline.</param>
    /// <param name="reason">The file-scan operation reason used for dispatch diagnostics.</param>
    void ApplyFileScanStorageMutation(SongTableFileCheckResult fileCheckResult, string reason);

    bool ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(BmsLibraryOptionsSnapshot options);

    void CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult fileCheckResult,
        string reason);

    void UpsertChartInfoIndexRows(
        IEnumerable<LR2SongDBExtended.chart_info> rows,
        string reason,
        bool dispatchPresentation = true);

    void DispatchWarningPresentationChanged(string reason);

    void CaptureLr2SongDbSyncScanSurface(
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        SongTableFileCheckResult fileCheckResult);

    void CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult fileCheckResult,
        string reason);

    void MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult result);
}
