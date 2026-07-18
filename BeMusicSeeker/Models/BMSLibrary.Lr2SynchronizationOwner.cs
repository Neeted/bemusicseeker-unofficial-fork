using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    /// <summary>
    /// Owns the LR2 synchronization request lifecycle and its cross-route state.
    /// The facade exposes only the application-facing observable projection.
    /// </summary>
    internal sealed class Lr2SynchronizationOwner : ILr2SongDbSyncRequestHost, ILr2SynchronizationScanPort
    {
        private readonly BMSLibrary library;

        internal Lr2SynchronizationOwner(BMSLibrary library)
        {
            this.library = library ?? throw new ArgumentNullException(nameof(library));
        }

        internal object RequestGate { get; } = new();

        internal object StatusGate { get; } = new();

        internal object ScanSurfaceGate { get; } = new();

        internal object FileDiffFreshnessGate { get; } = new();

        internal int RequestedVersion { get; set; }

        internal int CompletedVersion { get; set; }

        internal int FailedVersion { get; set; }

        internal bool Running { get; set; }

        internal bool PreparationInProgress { get; set; }

        internal int MutationInProgress { get; set; }

        internal CancellationTokenSource Cancellation { get; set; }

        internal Lr2SongDbSyncStatusSnapshot Status { get; set; } = new()
        {
            Status = Lr2SongDbSyncStatusKind.NotNeeded
        };

        internal Lr2SongDbSyncScanSurfaceSnapshot ScanSurfaceSnapshot { get; set; }

        internal CustomFolderOutputPhysicalSurface AppManagedCustomFolderOutputPhysicalSurface { get; set; } = new(
            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
            discoveryComplete: false);

        internal int ScanSurfaceGeneration { get; set; }

        internal Lr2SongDbSyncFileDiffFreshnessSnapshot FileDiffFreshnessSnapshot { get; set; }

        internal Lr2SongDbSyncPreparedDataSurface PreparedDataSurface { get; set; } =
            Lr2SongDbSyncPreparedDataSurface.Empty;

        internal int PreparedDataSurfaceAppliedScanGeneration { get; set; }

        internal bool ObservableRunning { get; set; }

        internal int ObservableRequestedVersion { get; set; }

        internal int ObservableCompletedVersion { get; set; }

        internal int ObservableFailedVersion { get; set; }

        internal int ObservableTotalCount { get; set; }

        internal int ObservableProcessedCount { get; set; }

        internal string ObservableStage { get; set; } = string.Empty;

        internal int ObservableStageProcessedCount { get; set; }

        internal int ObservableStageTotalCount { get; set; }

        internal string ObservableFailureMessage { get; set; } = string.Empty;

        internal int ObservableStatusVersion { get; set; }

        BmsLibraryOptionsSnapshot ILr2SongDbSyncRequestHost.CurrentOptionsSnapshot =>
            library.CurrentOptionsSnapshot;

        BmsLibraryOptionsSnapshot ILr2SynchronizationScanPort.CurrentOptionsSnapshot =>
            library.CurrentOptionsSnapshot;

        void ILr2SynchronizationScanPort.ThrowIfLr2SongDbSyncMutationBlocked(string operation) =>
            library.ThrowIfLr2SongDbSyncMutationBlocked(operation);

        Lr2SongDbSyncAppManagedOutputScope ILr2SynchronizationScanPort.CreateLr2SongDbSyncAppManagedOutputScope() =>
            library.CreateLr2SongDbSyncAppManagedOutputScope();

        Lr2BuiltinCustomFolderSettings ILr2SynchronizationScanPort.CreateCurrentLr2BuiltinCustomFolderSettings(DateTime nowUtc) =>
            library.CreateCurrentLr2BuiltinCustomFolderSettings(nowUtc);

        List<string> ILr2SynchronizationScanPort.CreateLr2SongDbSyncBuiltinFolderSourceDirectories(BmsLibraryOptionsSnapshot options) =>
            library.CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options);

        List<string> ILr2SynchronizationScanPort.CreateLr2SongDbSyncLr2FolderPruneDirectories(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> builtinSourceDirectories,
            BmsLibraryOptionsSnapshot options) =>
            library.CreateLr2SongDbSyncLr2FolderPruneDirectories(rootDirectories, builtinSourceDirectories, options: options);

        Lr2FolderFileDbSyncResult ILr2SynchronizationScanPort.SyncLr2FolderFileRows(
            BmsLibraryOptionsSnapshot options,
            Lr2SongDbSyncRequest request,
            string reason,
            string logName,
            bool allowPrune,
            IReadOnlyCollection<string> pruneExcludedDirectories,
            IReadOnlyCollection<string> pruneExcludedPaths,
            bool scopeReadLr2FolderRowsOnly,
            bool updateParentDirectoryRowsForPreservedItems) =>
            library.SyncLr2FolderFileRows(
                options,
                request,
                reason,
                logName,
                allowPrune,
                pruneExcludedDirectories,
                pruneExcludedPaths,
                scopeReadLr2FolderRowsOnly,
                updateParentDirectoryRowsForPreservedItems);

        bool ILr2SynchronizationScanPort.ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(BmsLibraryOptionsSnapshot options) =>
            library.ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(options);

        void ILr2SynchronizationScanPort.CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult fileCheckResult,
            string reason) =>
            library.CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(options, fileCheckResult, reason);

        void ILr2SynchronizationScanPort.CaptureLr2SongDbSyncScanSurface(
            BmsLibraryOptionsSnapshot options,
            IEnumerable<string> rootDirectories,
            SongTableFileCheckResult fileCheckResult) =>
            library.CaptureLr2SongDbSyncScanSurface(options, rootDirectories, fileCheckResult);

        void ILr2SynchronizationScanPort.CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult fileCheckResult,
            string reason) =>
            library.CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(options, fileCheckResult, reason);

        void ILr2SynchronizationScanPort.MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult result) =>
            library.MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(options, result);

        bool ILr2SongDbSyncRequestHost.IsShutdownRequested => library.IsShutdownRequested;

        Func<string, string, string, Func<Task>, bool> ILr2SongDbSyncRequestHost.StartupBackgroundTaskScheduler =>
            library.StartupBackgroundTaskScheduler;

        int ILr2SongDbSyncRequestHost.GetLr2SongDbSyncMutationInProgress()
        {
            lock (RequestGate)
            {
                return MutationInProgress;
            }
        }

        Lr2SongDbSyncRuntimeSnapshot ILr2SongDbSyncRequestHost.GetLr2SongDbSyncRuntimeSnapshot()
        {
            lock (RequestGate)
            {
                return CreateRuntimeSnapshotUnsafe();
            }
        }

        bool ILr2SongDbSyncRequestHost.TryReserveLr2SongDbSyncPreparation(
            bool requiresPreparation,
            out Lr2SongDbSyncRuntimeSnapshot blockingSnapshot)
        {
            lock (RequestGate)
            {
                if (Running || PreparationInProgress || MutationInProgress > 0)
                {
                    blockingSnapshot = CreateRuntimeSnapshotUnsafe();
                    return false;
                }
                if (requiresPreparation)
                {
                    PreparationInProgress = true;
                }
                blockingSnapshot = CreateRuntimeSnapshotUnsafe();
                return true;
            }
        }

        Lr2SongDbSyncStatusSnapshot ILr2SongDbSyncRequestHost.GetLr2SongDbSyncStatusSnapshot()
        {
            return GetStatusSnapshot();
        }

        LR2SongDBExtended ILr2SongDbSyncRequestHost.OpenSongDb() => library.dbGateway.OpenSongDb();

        IDisposable ILr2SongDbSyncRequestHost.EnterLr2SongDbSyncCatalogMutationLease() =>
            library.catalogMutationOwner.EnterMaintenanceWriteGuard();

        void ILr2SongDbSyncRequestHost.PublishLr2SongDbSyncStatus(Lr2SongDbSyncStatusSnapshot status)
        {
            PublishStatus(status);
        }

        bool ILr2SongDbSyncRequestHost.TrySkipForShutdown(string operation, string reason) =>
            library.TrySkipForShutdown(operation, reason);

        void ILr2SongDbSyncRequestHost.ClearLr2SongDbSyncPreparedDataSurface(string reason) =>
            ClearPreparedDataSurface(reason);

        void ILr2SongDbSyncRequestHost.ApplyLr2SongDbSyncPreparedDataSurface(
            string reason,
            Lr2SongDbSyncPreparedDataSurface preparedSurface) =>
            library.ApplyLr2SongDbSyncPreparedDataSurface(reason, preparedSurface);

        void ILr2SongDbSyncRequestHost.ClearLr2SongDbSyncPrepareReservation()
        {
            ClearPreparation();
        }

        bool ILr2SongDbSyncRequestHost.TryBeginLr2SongDbSyncRequest(out int requestVersion)
        {
            lock (RequestGate)
            {
                if (Running || MutationInProgress > 0)
                {
                    requestVersion = RequestedVersion;
                    return false;
                }
                RequestedVersion++;
                requestVersion = RequestedVersion;
                Cancellation?.Dispose();
                Cancellation = new CancellationTokenSource();
                library.Lr2SongDbSyncRequestedVersion = requestVersion;
                library.Lr2SongDbSyncTotalCount = 0;
                library.Lr2SongDbSyncProcessedCount = 0;
                library.Lr2SongDbSyncStage = "queued";
                library.Lr2SongDbSyncStageProcessedCount = 0;
                library.Lr2SongDbSyncStageTotalCount = 0;
                library.Lr2SongDbSyncFailureMessage = string.Empty;
                Running = true;
                library.Lr2SongDbSyncRunning = true;
                return true;
            }
        }

        void ILr2SongDbSyncRequestHost.CompleteLr2SongDbSyncRequest(int requestVersion, string stage)
        {
            CompleteRequest(requestVersion, stage);
        }

        void ILr2SongDbSyncRequestHost.FailLr2SongDbSyncRequest(
            int requestVersion,
            Lr2SongDbSyncStatusKind status,
            string stage,
            string message)
        {
            FailRequest(requestVersion, status, stage, message);
        }

        void ILr2SongDbSyncRequestHost.RunLr2SongDbSync(string reason, string signature, int requestVersion) =>
            Lr2SongDbSyncRequestCoordinator.Run(this, reason, signature, requestVersion);

        CancellationToken ILr2SongDbSyncRequestHost.GetLr2SongDbSyncCancellationToken()
        {
            lock (RequestGate)
            {
                return Cancellation?.Token ?? CancellationToken.None;
            }
        }

        Lr2SongDbSyncInput ILr2SongDbSyncRequestHost.CreateLr2SongDbSyncInput() =>
            library.CreateLr2SongDbSyncInput();

        TimeSpan ILr2SongDbSyncRequestHost.CurrentChartInfoParseTimeout =>
            library.chartInfoBuildService.CurrentParseTimeout;

        void ILr2SongDbSyncRequestHost.ReportStartupBackgroundTask(
            string name,
            string status,
            long elapsedMs,
            bool failed,
            string detail) =>
            library.ReportStartupBackgroundTask(name, status, elapsedMs, failed, detail);

        void ILr2SongDbSyncRequestHost.PublishLr2SongDbSyncPreflightStage(string stage, string reason, string runId) =>
            library.PublishLr2SongDbSyncPreflightStage(stage, reason, runId);

        void ILr2SongDbSyncRequestHost.LogLr2SongDbSyncPreflightStageDone(string stage, string reason, string runId, long elapsedMs) =>
            library.LogLr2SongDbSyncPreflightStageDone(stage, reason, runId, elapsedMs);

        void ILr2SongDbSyncRequestHost.EnsureLr2SongDbSyncChartInfoIndexHydrated(string reason) =>
            library.EnsureLr2SongDbSyncChartInfoIndexHydrated(reason);

        Dictionary<string, BMSFile> ILr2SongDbSyncRequestHost.CreateLr2SongDbSyncCompatibilityProjectionIndex() =>
            library.CreateLr2SongDbSyncCompatibilityProjectionIndex();

        Func<BMSFile, LR2SongDBExtended.chart_info> ILr2SongDbSyncRequestHost.CreateLr2SongDbSyncChartInfoResolverSnapshot() =>
            library.CreateLr2SongDbSyncChartInfoResolverSnapshot();

        HashSet<string> ILr2SongDbSyncRequestHost.CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason) =>
            library.CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(reason);

        void ILr2SongDbSyncRequestHost.UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<LR2SongDBExtended.chart_info> rows) =>
            library.UpsertChartInfoIndexRows(rows, "lr2_song_db_sync_inline_chart_info", dispatchPresentation: false);

        CatalogChartInfoWriteReceipt ILr2SongDbSyncRequestHost.ApplyLr2SongDbSyncChartInfoWrite(
            LR2SongDBExtended songDb,
            CatalogChartInfoWriteRequest request) =>
            library.catalogMutationOwner.ApplyChartInfoWriteInTransaction(songDb, request);

        bool ILr2SongDbSyncRequestHost.IsLr2SongDbSyncInputCurrent(Lr2SongDbSyncInput input) =>
            library.IsLr2SongDbSyncInputCurrent(input);

        void ILr2SongDbSyncRequestHost.UpdateLr2SongDbSyncProgress(Lr2SongDbSyncProgress progress) =>
            UpdateProgress(progress);

        int ILr2SongDbSyncRequestHost.ApplyLr2SongDbSyncCompatibilityProjection(
            IReadOnlyList<BMSFileMaintenanceInfo> maintenanceInfos,
            string reason,
            IReadOnlyDictionary<string, BMSFile> bmsByPath,
            bool logSummary,
            bool dispatchPresentation) =>
            library.ApplyLr2SongDbSyncCompatibilityProjection(
                maintenanceInfos,
                reason,
                bmsByPath,
                logSummary,
                dispatchPresentation);

        ISet<string> ILr2SongDbSyncRequestHost.GetLr2SongDbSyncTransientSongRowsSkipPaths(
            Lr2SongDbSyncInput input,
            string reason) =>
            library.GetLr2SongDbSyncTransientSongRowsSkipPaths(input, reason);

        Lr2SongDbSyncSongRowsSkipVerificationResult ILr2SongDbSyncRequestHost.VerifyLr2SongDbSyncSongRowsFreshFromFileDiff(
            LR2SongDBExtended songDb,
            IReadOnlyList<BMSFile> songRows,
            Lr2SongDbSyncInput input,
            string reason) =>
            library.VerifyLr2SongDbSyncSongRowsFreshFromFileDiff(songDb, songRows, input, reason);

        void ILr2SongDbSyncRequestHost.DispatchWarningPresentationChanged(string reason) =>
            library.DispatchWarningPresentationChanged(reason);

        void ILr2SongDbSyncRequestHost.MarkLr2SongDbSyncPreflightCancelled(string signature, string runId, string stage) =>
            library.MarkLr2SongDbSyncPreflightCancelled(signature, runId, stage);

        void ILr2SongDbSyncRequestHost.MarkLr2SongDbSyncFailedStatus(string signature, string runId, Exception ex)
        {
            using LR2SongDBExtended songDb = library.dbGateway.OpenSongDb();
            Lr2SongDbSyncStatusService.MarkFailed(
                songDb,
                signature,
                runId,
                processedCursor: null,
                totalCount: null,
                stage: "failed",
                error: ex.Message,
                nowUtc: DateTime.UtcNow);
        }

        void ILr2SongDbSyncRequestHost.LogInstallPerformance(string message) =>
            LogInstallPerformance(message);

        internal Lr2SongDbSyncStatusSnapshot GetStatusSnapshot()
        {
            lock (StatusGate)
            {
                return Status?.Clone() ?? new Lr2SongDbSyncStatusSnapshot
                {
                    Status = Lr2SongDbSyncStatusKind.NotNeeded
                };
            }
        }

        internal void PublishStatus(Lr2SongDbSyncStatusSnapshot status)
        {
            lock (StatusGate)
            {
                Status = status?.Clone() ?? new Lr2SongDbSyncStatusSnapshot
                {
                    Status = Lr2SongDbSyncStatusKind.NotNeeded
                };
            }
            library.Lr2SongDbSyncStatusVersion++;
        }

        internal void ClearPreparation()
        {
            lock (RequestGate)
            {
                PreparationInProgress = false;
            }
        }

        internal void ClearPreparedDataSurface(string reason)
        {
            bool hadSurface;
            lock (ScanSurfaceGate)
            {
                hadSurface = PreparedDataSurface?.HasPreparedDataSurface == true;
                PreparedDataSurface = Lr2SongDbSyncPreparedDataSurface.Empty;
                PreparedDataSurfaceAppliedScanGeneration = 0;
            }
            if (hadSurface)
            {
                LogInstallPerformance("lr2_song_db_sync_prepared_surface cleared reason=" + (reason ?? "unknown"));
            }
        }

        internal bool TryBeginRequest(out int requestVersion) =>
            ((ILr2SongDbSyncRequestHost)this).TryBeginLr2SongDbSyncRequest(out requestVersion);

        internal void CompleteRequest(int requestVersion, string stage)
        {
            lock (RequestGate)
            {
                CompletedVersion = Math.Max(CompletedVersion, requestVersion);
                library.Lr2SongDbSyncCompletedVersion = CompletedVersion;
                library.Lr2SongDbSyncStage = stage ?? string.Empty;
                library.Lr2SongDbSyncStageProcessedCount = library.Lr2SongDbSyncStageTotalCount > 0
                    ? library.Lr2SongDbSyncStageTotalCount
                    : library.Lr2SongDbSyncProcessedCount;
                library.Lr2SongDbSyncStageTotalCount = library.Lr2SongDbSyncStageTotalCount > 0
                    ? library.Lr2SongDbSyncStageTotalCount
                    : library.Lr2SongDbSyncTotalCount;
                Running = false;
                library.Lr2SongDbSyncRunning = false;
                DisposeCancellationUnsafe();
            }
            PublishStatus(BMSLibrary.CreateRuntimeLr2SongDbSyncStatus(
                Lr2SongDbSyncStatusKind.Completed,
                GetStatusSnapshot().Signature,
                library.Lr2SongDbSyncStage,
                library.Lr2SongDbSyncProcessedCount,
                library.Lr2SongDbSyncTotalCount,
                lastError: null,
                library.Lr2SongDbSyncStageProcessedCount,
                library.Lr2SongDbSyncStageTotalCount));
        }

        internal void FailRequest(
            int requestVersion,
            Lr2SongDbSyncStatusKind status,
            string stage,
            string message)
        {
            lock (RequestGate)
            {
                FailedVersion = Math.Max(FailedVersion, requestVersion);
                library.Lr2SongDbSyncFailedVersion = FailedVersion;
                library.Lr2SongDbSyncFailureMessage = message ?? string.Empty;
                library.Lr2SongDbSyncStage = stage ?? string.Empty;
                Running = false;
                library.Lr2SongDbSyncRunning = false;
                DisposeCancellationUnsafe();
            }
            PublishStatus(BMSLibrary.CreateRuntimeLr2SongDbSyncStatus(
                status,
                GetStatusSnapshot().Signature,
                library.Lr2SongDbSyncStage,
                library.Lr2SongDbSyncProcessedCount,
                library.Lr2SongDbSyncTotalCount,
                library.Lr2SongDbSyncFailureMessage,
                library.Lr2SongDbSyncStageProcessedCount,
                library.Lr2SongDbSyncStageTotalCount));
        }

        internal void UpdateProgress(Lr2SongDbSyncProgress progress)
        {
            if (progress == null)
            {
                return;
            }
            library.Lr2SongDbSyncTotalCount = Math.Max(0, progress.TotalCount);
            library.Lr2SongDbSyncProcessedCount = Math.Max(0, progress.ProcessedCursor);
            library.Lr2SongDbSyncStage = progress.Stage ?? string.Empty;
            library.Lr2SongDbSyncStageProcessedCount = Math.Max(0, progress.StageProcessedCount);
            library.Lr2SongDbSyncStageTotalCount = Math.Max(0, progress.StageTotalCount);
            PublishStatus(BMSLibrary.CreateRuntimeLr2SongDbSyncStatus(
                Lr2SongDbSyncStatusKind.Running,
                GetStatusSnapshot().Signature,
                library.Lr2SongDbSyncStage,
                library.Lr2SongDbSyncProcessedCount,
                library.Lr2SongDbSyncTotalCount,
                lastError: null,
                library.Lr2SongDbSyncStageProcessedCount,
                library.Lr2SongDbSyncStageTotalCount));
        }

        internal void DisposeCancellation()
        {
            lock (RequestGate)
            {
                DisposeCancellationUnsafe();
            }
        }

        private void DisposeCancellationUnsafe()
        {
            Cancellation?.Dispose();
            Cancellation = null;
        }

        internal bool Cancel(string reason)
        {
            lock (RequestGate)
            {
                if (!Running || Cancellation == null)
                {
                    return false;
                }
                LogInstallPerformance("lr2_song_db_sync cancel_requested"
                    + " reason=" + (reason ?? "unknown")
                    + " stage=" + (library.Lr2SongDbSyncStage ?? string.Empty)
                    + " processed=" + library.Lr2SongDbSyncProcessedCount
                    + " total=" + library.Lr2SongDbSyncTotalCount);
                Cancellation.Cancel();
                return true;
            }
        }

        internal bool TryBlockMutation(string operation, bool showMessage = true)
        {
            bool blocked;
            string stage;
            int processed;
            int total;
            lock (RequestGate)
            {
                blocked = Running;
                stage = library.Lr2SongDbSyncStage ?? string.Empty;
                processed = library.Lr2SongDbSyncProcessedCount;
                total = library.Lr2SongDbSyncTotalCount;
            }
            if (!blocked)
            {
                return false;
            }
            LogInstallPerformance("lr2_song_db_sync_mutation_blocked operation=" + (operation ?? "(unknown)")
                + " stage=" + stage
                + " processed=" + processed
                + " total=" + total);
            if (showMessage)
            {
                library.ShowOperationDialog(
                    Resources.Warn_Lr2SongDbSyncRunning,
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
            return true;
        }

        internal IDisposable TryBeginMutation(string operation, bool showMessage = true)
        {
            string stage;
            int processed;
            int total;
            bool preparing;
            lock (RequestGate)
            {
                if (!Running && !PreparationInProgress)
                {
                    MutationInProgress++;
                    return new MutationScope(this);
                }
                stage = library.Lr2SongDbSyncStage ?? string.Empty;
                processed = library.Lr2SongDbSyncProcessedCount;
                total = library.Lr2SongDbSyncTotalCount;
                preparing = PreparationInProgress;
            }
            LogInstallPerformance("lr2_song_db_sync_mutation_blocked operation=" + (operation ?? "(unknown)")
                + " stage=" + stage
                + " processed=" + processed
                + " total=" + total
                + " preparing=" + preparing.ToString().ToLowerInvariant());
            if (showMessage)
            {
                library.ShowOperationDialog(
                    Resources.Warn_Lr2SongDbSyncRunning,
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
            return null;
        }

        private void EndMutation()
        {
            lock (RequestGate)
            {
                MutationInProgress = Math.Max(0, MutationInProgress - 1);
            }
        }

        private sealed class MutationScope(Lr2SynchronizationOwner owner) : IDisposable
        {
            private Lr2SynchronizationOwner owner = owner;

            public void Dispose()
            {
                Lr2SynchronizationOwner current = Interlocked.Exchange(ref owner, null);
                current?.EndMutation();
            }
        }

        private Lr2SongDbSyncRuntimeSnapshot CreateRuntimeSnapshotUnsafe()
        {
            return new Lr2SongDbSyncRuntimeSnapshot
            {
                Running = Running,
                Preparing = PreparationInProgress,
                MutationInProgress = MutationInProgress,
                RequestedVersion = library.Lr2SongDbSyncRequestedVersion,
                Stage = library.Lr2SongDbSyncStage ?? string.Empty,
                ProcessedCount = library.Lr2SongDbSyncProcessedCount,
                TotalCount = library.Lr2SongDbSyncTotalCount,
                StageProcessedCount = library.Lr2SongDbSyncStageProcessedCount,
                StageTotalCount = library.Lr2SongDbSyncStageTotalCount
            };
        }
    }
}
