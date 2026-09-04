using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using static BeMusicSeeker.Models.BmsLibraryInternal.Lr2SongDbSyncInputSurfaceHelper;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    /// <summary>
    /// Owns the LR2 synchronization request lifecycle and its cross-route state.
    /// The facade exposes only the application-facing observable projection.
    /// </summary>
    internal sealed class Lr2SynchronizationOwner :
        ILr2PlaylistFolderSynchronizationPort,
        ICatalogWriteFailureSink
    {
        private readonly ILr2SynchronizationDataPort data;

        private readonly ILr2SynchronizationRuntimePort runtime;

        private readonly ILr2SynchronizationProjectionPort projection;

        private readonly Action<string> queueObservablePropertyChange;

        private readonly object mutationSequenceGate = new();

        private long nextMutationLeaseId;

        private long activeMutationLeaseId;

        internal Lr2SynchronizationOwner(
            ILr2SynchronizationDataPort data,
            ILr2SynchronizationRuntimePort runtime,
            ILr2SynchronizationProjectionPort projection,
            Action<string> queueObservablePropertyChange)
        {
            this.data = data ?? throw new ArgumentNullException(nameof(data));
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            this.projection = projection ?? throw new ArgumentNullException(nameof(projection));
            this.queueObservablePropertyChange = queueObservablePropertyChange
                ?? throw new ArgumentNullException(nameof(queueObservablePropertyChange));
        }

        internal object RequestGate { get; } = new();

        internal object StatusGate { get; } = new();

        internal object ScanSurfaceGate { get; } = new();

        internal object CommittedPathReceiptGate { get; } = new();

        internal int RequestedVersion { get; set; }

        internal int CompletedVersion { get; set; }

        internal int FailedVersion { get; set; }

        internal bool Running { get; set; }

        internal bool PreparationInProgress { get; set; }

        internal bool StatusPublicationInProgress { get; set; }

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

        internal Lr2SongDbSyncCommittedPathReceipt CommittedPathReceipt { get; set; }

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

        internal BmsLibraryOptionsSnapshot CurrentOptionsSnapshot =>
            data.CurrentOptionsSnapshot;

        CustomFolderOutputPhysicalSurface ILr2PlaylistFolderSynchronizationPort.GetCurrentAppManagedCustomFolderOutputPhysicalSurface() =>
            GetCurrentAppManagedCustomFolderOutputPhysicalSurface();

        Lr2FolderFileDbSyncResult ILr2PlaylistFolderSynchronizationPort.SyncPlaylistLr2FolderFileRows(
            string operation,
            Lr2FolderFileDbSyncRequest request,
            LibraryFileMutationCapability mutationCapability) =>
            SyncPlaylistLr2FolderFileRows(operation, request, mutationCapability);

        internal Lr2SongDbSyncAppManagedOutputScope CreateLr2SongDbSyncAppManagedOutputScope()
        {
            Lr2SongDbSyncAppManagedOutputScope scope =
                data.CaptureLr2SongDbSyncAppManagedOutputScope(data.CurrentOptionsSnapshot);
            if (!scope.IsComplete)
            {
                BMSLibrary.LogInstallPerformanceWarn(
                    "lr2folder_app_managed_output_scope failed");
            }
            return scope;
        }

        internal Lr2SongDbSyncPreparedDataSurface SyncLr2BuiltinCustomFolderRows(
            string reason,
            LibraryFileMutationCapability mutationCapability)
        {
            RequireMutationCapability(mutationCapability);
            BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
            if (options?.OperationModeLR2DB != true)
            {
                return Lr2SongDbSyncPreparedDataSurface.Empty;
            }

            List<string> builtinSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options);
            Lr2FolderFileCandidateSnapshot candidates = builtinSourceDirectories.Count > 0
                ? CreateLr2SongDbSyncLr2FolderFileCandidates(
                    builtinSourceDirectories,
                    options.LR2RootPath,
                    CreateCurrentLr2BuiltinCustomFolderSettings(DateTime.UtcNow))
                : new Lr2FolderFileCandidateSnapshot(
                    [],
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    discoveryComplete: true);
            var request = new Lr2SongDbSyncRequest
            {
                RootDirectories = [],
                Lr2FolderDiscoveryDirectories = builtinSourceDirectories,
                Lr2FolderPruneDirectories = CreateLr2BuiltinCustomFolderPruneDirectories(),
                Lr2FolderFilePaths = candidates.Paths,
                Lr2FolderFileEntries = candidates.EntriesByPath,
                Lr2FolderFileDiscoveryComplete = candidates.DiscoveryComplete,
                Lr2RootPath = options.LR2RootPath,
                Lr2NormalCustomFolderOutputBaseDir = options.LR2CustomFolderOutputBaseDir,
                Lr2AdditionalNormalCustomFolderOutputBaseDirs = options.LR2CustomFolderAdditionalOutputBaseDirs,
                Lr2RootCustomFolderOutputBaseDir = options.LR2CustomFolderOutputBaseDirRootType,
                Lr2BuiltinFolderSourceDirectories = builtinSourceDirectories
            };
            PrepareLr2FolderParentDirectoryEntrySurface(request);
            Lr2TextMetadataCandidateSnapshot textMetadataSnapshot = CreateLr2PreparedTextMetadataCandidates(
                builtinSourceDirectories,
                request.DirectoryEntries.Keys);
            ApplyLr2TextMetadataCandidatesToRequest(request, textMetadataSnapshot, builtinSourceDirectories);
            return new Lr2SongDbSyncPreparedDataSurface(
                builtinSourceDirectories,
                candidates.Paths,
                candidates.EntriesByPath,
                request.DirectoryEntries,
                textMetadataSnapshot.FolderInfoCandidates.Paths,
                textMetadataSnapshot.FolderInfoCandidates.EntriesByPath,
                textMetadataSnapshot.TextFileDirectories,
                candidates.DiscoveryComplete);
        }

        internal void SyncExternalLr2FolderRowsForCustomFolderOutputBaseChange(string reason)
        {
            if (CurrentOptionsSnapshot?.OperationModeLR2DB != true)
            {
                return;
            }

            bool queuePreparedSync;
            using (LibraryFileMutationLease mutationLease = BeginMutationWhenAvailable(
                "lr2folder_settings_output_base_sync"))
            using (LibraryFileMutationCapability mutationCapability = mutationLease.CreateMutationCapability())
            {
                BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
                if (options?.OperationModeLR2DB != true)
                {
                    return;
                }

                queuePreparedSync = HasLr2SongDbSyncPreparedDataSurface();
                if (!queuePreparedSync)
                {
                    var stopwatch = Stopwatch.StartNew();
                    Lr2SearchRootSnapshot rootSnapshot = data.CaptureBmsDirectories();
                    List<string> roots = [.. rootSnapshot.Roots];
                    List<string> builtinSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options);
                    List<string> discoveryDirectories = NormalizeDistinctDirectories(CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(roots, options));
                    Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();
                    if (!appManagedOutputScope.IsComplete)
                    {
                        BMSLibrary.LogInstallPerformance("lr2folder_settings_output_base_sync skipped"
                            + " reason=" + (reason ?? "unknown")
                            + " detail=app_managed_scope_incomplete"
                            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                        return;
                    }

                    Lr2FolderFileCandidateSnapshot candidates = discoveryDirectories.Count > 0
                        ? CreateLr2SongDbSyncLr2FolderFileCandidates(
                            discoveryDirectories,
                            options.LR2RootPath,
                            CreateCurrentLr2BuiltinCustomFolderSettings(DateTime.UtcNow),
                            appManagedOutputScope.Directories)
                        : new Lr2FolderFileCandidateSnapshot(
                            [],
                            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                            discoveryComplete: true);
                    candidates = Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                        candidates.Paths,
                        candidates.EntriesByPath,
                        appManagedOutputScope.FilePaths,
                        candidates.DiscoveryComplete,
                        out int appManagedCandidateCount,
                        appManagedOutputScope.Directories);

                    List<string> pruneDirectories = NormalizeDistinctDirectories(
                        CreateLr2SongDbSyncLr2FolderPruneDirectories(roots, builtinSourceDirectories, options: options));
                    var request = new Lr2SongDbSyncRequest
                    {
                        RootDirectories = roots,
                        Lr2FolderDiscoveryDirectories = discoveryDirectories,
                        Lr2FolderPruneDirectories = pruneDirectories,
                        Lr2FolderFilePaths = candidates.Paths,
                        Lr2FolderFileEntries = candidates.EntriesByPath,
                        Lr2FolderFileDiscoveryComplete = candidates.DiscoveryComplete,
                        Lr2RootPath = options.LR2RootPath,
                        Lr2NormalCustomFolderOutputBaseDir = options.LR2CustomFolderOutputBaseDir,
                        Lr2AdditionalNormalCustomFolderOutputBaseDirs = options.LR2CustomFolderAdditionalOutputBaseDirs,
                        Lr2RootCustomFolderOutputBaseDir = options.LR2CustomFolderOutputBaseDirRootType,
                        Lr2BuiltinFolderSourceDirectories = builtinSourceDirectories
                    };
                    PrepareLr2FolderParentDirectoryEntrySurface(request);
                    Lr2TextMetadataCandidateSnapshot textMetadataSnapshot = CreateLr2PreparedTextMetadataCandidates(
                        request.Lr2FolderDiscoveryDirectories,
                        request.DirectoryEntries.Keys);
                    ApplyLr2TextMetadataCandidatesToRequest(request, textMetadataSnapshot, request.Lr2FolderDiscoveryDirectories);
                    Lr2FolderFileDbSyncResult syncResult = SyncLr2FolderFileRows(
                        options,
                        request,
                        reason,
                        "lr2folder_settings_output_base_sync",
                        allowPrune: true,
                        pruneExcludedDirectories: appManagedOutputScope.Directories,
                        pruneExcludedPaths: appManagedOutputScope.PruneExcludedPaths,
                        scopeReadLr2FolderRowsOnly: true,
                        mutationCapability: mutationCapability);
                    BMSLibrary.LogInstallPerformance("lr2folder_settings_output_base_sync summary"
                        + " reason=" + (reason ?? "unknown")
                        + " roots=" + roots.Count
                        + " discoveryDirs=" + discoveryDirectories.Count
                        + " candidates=" + candidates.Paths.Count
                        + " appManagedFiltered=" + appManagedCandidateCount
                        + " pruneDirs=" + pruneDirectories.Count
                        + " existingRows=" + (syncResult?.ExistingReadCount ?? 0)
                        + " upserted=" + (syncResult?.UpsertedCount ?? 0)
                        + " deleted=" + (syncResult?.DeletedCount ?? 0)
                        + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                }
            }

            if (queuePreparedSync)
            {
                Lr2SongDbSyncRequestCoordinator.Queue(
                    this,
                    reason,
                    force: true,
                    prepareGeneratedData: null,
                    allowIncompleteToQueue: true,
                    allowCommittedPathReceipt: false);
            }
        }

        internal Lr2BuiltinCustomFolderSettings CreateCurrentLr2BuiltinCustomFolderSettings(DateTime nowUtc)
        {
            LR2Config config = data.CreateCurrentLr2ConfigOrNull();
            return Lr2BuiltinCustomFolderSettings.CreateFromAddDates(
                config,
                data.CaptureBmsFilesSnapshot()
                    .Where(file => !string.IsNullOrWhiteSpace(file.path))
                    .Select(file => file.adddate),
                nowUtc);
        }

        internal List<string> CreateLr2SongDbSyncBuiltinFolderSourceDirectories(BmsLibraryOptionsSnapshot options = null)
        {
            options ??= data.CurrentOptionsSnapshot;
            return Lr2FolderFileDiscoveryService.CreateBuiltinFolderSourceDirectories(options.LR2RootPath);
        }

        internal List<string> CreateLr2SongDbSyncLr2FolderPruneDirectories(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> builtinSourceDirectories,
            bool includeAppManagedOutputDirectories = true,
            BmsLibraryOptionsSnapshot options = null)
        {
            options ??= data.CurrentOptionsSnapshot;
            return Lr2FolderFileDiscoveryService.CreatePruneDirectories(
                rootDirectories,
                CreateNormalCustomFolderOutputBaseDirectories(options),
                options.LR2CustomFolderOutputBaseDirRootType,
                builtinSourceDirectories,
                includeAppManagedOutputDirectories);
        }

        private List<string> CreateNormalCustomFolderOutputBaseDirectories(BmsLibraryOptionsSnapshot options = null)
        {
            options ??= CurrentOptionsSnapshot;
            return [.. new[] { options.LR2CustomFolderOutputBaseDir }
                .Concat(options.LR2CustomFolderAdditionalOutputBaseDirs)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        private static List<string> CreateLr2BuiltinCustomFolderPruneDirectories()
        {
            return Lr2FolderFileDiscoveryService.CreateBuiltinCustomFolderPruneDirectories();
        }

        private Lr2FolderFileCandidateSnapshot CreateLr2SongDbSyncLr2FolderFileCandidates(
            IEnumerable<string> rootDirectories,
            string lr2RootPath,
            Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings,
            IEnumerable<string> excludedDirectories = null)
        {
            return Lr2FolderFileDiscoveryService.CreateFileCandidates(
                rootDirectories,
                lr2RootPath,
                builtinCustomFolderSettings,
                BMSLibrary.LogEverythingScan,
                data.EverythingNative,
                excludedDirectories);
        }

        private static List<string> NormalizeDistinctDirectories(IEnumerable<string> directories)
        {
            return [.. (directories ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(BMSLibrary.SafeFullPathOrOriginal)
                .Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        }

        private static long RestartElapsed(Stopwatch stopwatch)
        {
            long elapsedMs = stopwatch.ElapsedMilliseconds;
            stopwatch.Restart();
            return elapsedMs;
        }

        private void PrepareLr2FolderParentDirectoryEntrySurface(Lr2SongDbSyncRequest request)
        {
            if (request == null)
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var stopwatchStage = Stopwatch.StartNew();
            IReadOnlyCollection<string> parentDirectoryTargets = Lr2FolderPhysicalParentDirectoryTargetHelper.CreateTargets(
                (request.Lr2FolderFilePaths ?? []).Concat(request.Lr2FolderFileEntries?.Keys ?? []),
                request.RootDirectories,
                request.Lr2NormalCustomFolderOutputBaseDir,
                request.Lr2AdditionalNormalCustomFolderOutputBaseDirs,
                request.Lr2RootCustomFolderOutputBaseDir,
                request.Lr2BuiltinFolderSourceDirectories);
            long targetMs = RestartElapsed(stopwatchStage);
            if (parentDirectoryTargets.Count == 0)
            {
                BMSLibrary.LogInstallPerformance("lr2folder_parent_directory_surface targets=0 targetMs=" + targetMs + " totalMs=" + stopwatch.ElapsedMilliseconds);
                return;
            }

            IReadOnlyDictionary<string, RootFileEnumerationEntry> parentDirectoryEntries = CreateLr2DirectoryEntriesFromSurfaceOrGroupedScan(
                request.DirectoryEntries,
                request.Lr2FolderDiscoveryDirectories,
                parentDirectoryTargets,
                data.EverythingNative);
            long entryMs = RestartElapsed(stopwatchStage);
            request.DirectoryEntries = MergeMissingLr2DirectoryEntrySurface(
                request.DirectoryEntries,
                parentDirectoryEntries);
            long overlayMs = RestartElapsed(stopwatchStage);
            BMSLibrary.LogInstallPerformance("lr2folder_parent_directory_surface"
                + " targets=" + parentDirectoryTargets.Count
                + " entries=" + (parentDirectoryEntries?.Count ?? 0)
                + " targetMs=" + targetMs
                + " entryMs=" + entryMs
                + " overlayMs=" + overlayMs
                + " totalMs=" + stopwatch.ElapsedMilliseconds);
        }

        private Lr2TextMetadataCandidateSnapshot CreateLr2PreparedTextMetadataCandidates(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> targetDirectories)
        {
            if (!(rootDirectories ?? []).Any(path => !string.IsNullOrWhiteSpace(path))
                || !(targetDirectories ?? []).Any(path => !string.IsNullOrWhiteSpace(path)))
            {
                return new Lr2TextMetadataCandidateSnapshot(
                    new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true),
                    []);
            }

            return CreateLr2SongDbSyncTextMetadataCandidates(rootDirectories, targetDirectories, data.EverythingNative);
        }

        private static void ApplyLr2TextMetadataCandidatesToRequest(
            Lr2SongDbSyncRequest request,
            Lr2TextMetadataCandidateSnapshot textMetadataSnapshot,
            IEnumerable<string> metadataScopeDirectories)
        {
            if (request == null || textMetadataSnapshot == null)
            {
                return;
            }

            IReadOnlyList<string> folderInfoPaths = MergePreparedFileSurface(
                request.FolderInfoFilePaths,
                request.FolderInfoFileEntries,
                textMetadataSnapshot.FolderInfoCandidates.Paths,
                textMetadataSnapshot.FolderInfoCandidates.EntriesByPath,
                metadataScopeDirectories,
                out IReadOnlyDictionary<string, RootFileEnumerationEntry> folderInfoEntries);
            request.FolderInfoFilePaths = folderInfoPaths;
            request.FolderInfoFileEntries = folderInfoEntries;
            request.TextFileDirectories = MergePreparedDirectoryList(
                request.TextFileDirectories,
                textMetadataSnapshot.TextFileDirectories,
                metadataScopeDirectories);
        }

        internal Lr2FolderFileDbSyncResult SyncLr2FolderFileRows(
            BmsLibraryOptionsSnapshot options,
            Lr2SongDbSyncRequest request,
            string reason,
            string logName,
            LibraryFileMutationCapability mutationCapability,
            bool allowPrune = true,
            IReadOnlyCollection<string> pruneExcludedDirectories = null,
            IReadOnlyCollection<string> pruneExcludedPaths = null,
            bool scopeReadLr2FolderRowsOnly = false,
            bool updateParentDirectoryRowsForPreservedItems = true)
        {
            RequireMutationCapability(mutationCapability);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                PrepareLr2FolderParentDirectoryEntrySurface(request);
                Lr2FolderExistingRowsSnapshot existingRows =
                    data.CaptureLr2FolderExistingRows(request);
                Lr2SongDbSyncService.Lr2FolderFileSyncItemsResult syncItems =
                    Lr2SongDbSyncService.CreateLr2FolderFileSyncItems(
                        request.Lr2FolderFilePaths,
                        request,
                        request.Lr2FolderFileEntries,
                        path => existingRows.RowsByPath.TryGetValue(path, out LR2SongDB.folder row) ? row : null);
                IReadOnlyCollection<Lr2FolderFileSyncItem> parentDirectorySyncItems = updateParentDirectoryRowsForPreservedItems
                    ? syncItems.Items
                    : [.. syncItems.Items.Where(item => item != null && !item.PreserveExistingRowOnly)];
                Lr2FolderDirectoryMetadataSnapshot parentDirectoryMetadata =
                    Lr2SongDbSyncService.CreateLr2FolderParentDirectoryMetadataSnapshot(parentDirectorySyncItems, request);
                bool effectiveAllowPrune = allowPrune
                    && request.Lr2FolderFileDiscoveryComplete
                    && !syncItems.HasReadFailures;
                Lr2FolderFileDbSyncResult syncResult = data.ApplyLr2FolderFileSync(
                    new Lr2FolderFileDbSyncRequest
                    {
                        Items = syncItems.Items,
                        ScopeDirectories = request.Lr2FolderPruneDirectories,
                        DirectoryRowScopeDirectories = Lr2SongDbSyncService.CreateLr2FolderDirectoryRowScopeDirectories(request),
                        DirectoryRowGenerationScopeDirectories = Lr2SongDbSyncService.CreateLr2FolderDirectoryRowGenerationScopeDirectories(request),
                        ScopePaths = request.Lr2FolderFilePaths,
                        PruneExcludedDirectories = pruneExcludedDirectories ?? [],
                        PruneExcludedPaths = pruneExcludedPaths ?? [],
                        DirectoryMetadataResolver = parentDirectoryMetadata.Resolve,
                        GeneratedAtUtc = DateTime.UtcNow,
                        AllowPrune = effectiveAllowPrune,
                        ScopeReadLr2FolderRowsOnly = scopeReadLr2FolderRowsOnly,
                        UpdateParentDirectoryRowsForPreservedItems = updateParentDirectoryRowsForPreservedItems
                    });

                stopwatch.Stop();
                BMSLibrary.LogInstallPerformance(logName + " done"
                    + " reason=" + (reason ?? "unknown")
                    + " roots=" + request.Lr2FolderDiscoveryDirectories.Count
                    + " candidates=" + request.Lr2FolderFilePaths.Count
                    + " discoveryComplete=" + request.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant()
                    + " readFailures=" + syncItems.HasReadFailures.ToString().ToLowerInvariant()
                    + " allowPrune=" + effectiveAllowPrune.ToString().ToLowerInvariant()
                    + " pruneDeferred=" + (!effectiveAllowPrune && allowPrune == false).ToString().ToLowerInvariant()
                    + " pruneExcludedDirs=" + (pruneExcludedDirectories?.Count ?? 0)
                    + " pruneExcludedPaths=" + (pruneExcludedPaths?.Count ?? 0)
                    + " existingRows=" + syncResult.ExistingReadCount
                    + " existingExactRows=" + syncResult.ExistingExactReadCount
                    + " existingScopeRows=" + syncResult.ExistingScopeReadCount
                    + " generated=" + syncResult.GeneratedCount
                    + " preserved=" + syncResult.PreservedCount
                    + " upserted=" + syncResult.UpsertedCount
                    + " deleted=" + syncResult.DeletedCount
                    + " skippedUnsupported=" + syncResult.SkippedUnsupportedPathCount
                    + " skippedMissingMetadata=" + syncResult.SkippedMissingMetadataCount
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                return syncResult;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                try
                {
                    MarkLr2SongDbSyncIncomplete(
                        options,
                        runId: logName,
                        stage: logName + "_failed",
                        detail: logName + "_failed: " + BMSLibrary.GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "),
                        logReason: logName + "_failed");
                    BMSLibrary.LogInstallPerformanceWarn(logName + " failed"
                        + " reason=" + (reason ?? "unknown")
                        + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                        + " exception=" + ex.GetType().Name
                        + " message=" + BMSLibrary.GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
                }
                catch
                {
                    // Diagnostics must never replace the authoritative DB exception.
                }
                throw;
            }
        }

        internal bool ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(BmsLibraryOptionsSnapshot options)
        {
            if (options?.OperationModeLR2DB != true)
            {
                return false;
            }

            string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
            Lr2SongDbSyncStatusSnapshot status = data.EvaluateLr2SongDbSyncStatus(
                enabled: true,
                signature,
                DateTime.UtcNow);
            return status == null || status.Status != Lr2SongDbSyncStatusKind.Completed;
        }

        internal void MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult result)
        {
            if (result?.Lr2NormalFolderSyncFailed != true)
            {
                return;
            }

            string failureReason = string.IsNullOrWhiteSpace(result.Lr2NormalFolderSyncFailureReason)
                ? "unknown"
                : result.Lr2NormalFolderSyncFailureReason;
            MarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
                options,
                stage: "lr2_normal_folder_file_diff_sync_failed",
                detail: "lr2_normal_folder_file_diff_sync_failed: " + failureReason,
                logReason: "lr2_normal_folder_file_diff_sync_failed");
        }

        internal void MarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
            BmsLibraryOptionsSnapshot options,
            string stage,
            string detail,
            string logReason)
        {
            MarkLr2SongDbSyncIncomplete(
                options,
                runId: "normal_folder_sync",
                stage: stage,
                detail: detail,
                logReason: logReason);
        }

        internal void MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
            BmsLibraryOptionsSnapshot options,
            string stage,
            string detail,
            string logReason)
        {
            MarkLr2SongDbSyncIncomplete(
                options,
                runId: "song_db_write",
                stage: stage,
                detail: detail,
                logReason: logReason);
        }

        internal void MarkLr2SongDbSyncIncompleteAfterPlaylistLr2FolderSyncFailure(
            Exception exception,
            string reason)
        {
            try
            {
                string displayedMessage = BMSLibrary.GetDisplayedExceptionMessage(exception)
                    .Replace(Environment.NewLine, " | ");
                MarkLr2SongDbSyncIncomplete(
                    data.CurrentOptionsSnapshot,
                    runId: "playlist_lr2folder_sync",
                    stage: "lr2_playlist_lr2folder_sync_failed",
                    detail: "lr2_playlist_lr2folder_sync_failed: " + displayedMessage,
                    logReason: string.IsNullOrWhiteSpace(reason) ? "playlist_lr2folder_sync" : reason);
            }
            catch (Exception publishException)
            {
                try
                {
                    BMSLibrary.LogInstallPerformanceWarn(
                        "lr2_playlist_lr2folder_sync_failure_publish_failed"
                        + " exception=" + publishException.GetType().Name
                        + " message=" + BMSLibrary.GetDisplayedExceptionMessage(publishException).Replace(Environment.NewLine, " | "));
                }
                catch
                {
                    // Failure publication must never replace the original playlist exception.
                }
            }
        }

        internal void MarkLr2SongDbSyncIncomplete(
            BmsLibraryOptionsSnapshot options,
            string runId,
            string stage,
            string detail,
            string logReason)
        {
            if (options?.OperationModeLR2DB != true)
            {
                return;
            }

            try
            {
                string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
                Lr2SongDbSyncStatusSnapshot status = data.ApplyLr2SongDbSyncStatusMutation(
                    new Lr2SongDbSyncStatusMutationRequest(
                        Lr2SongDbSyncStatusMutationKind.MarkIncomplete,
                        signature,
                        string.IsNullOrWhiteSpace(runId) ? "runtime_write" : runId,
                        processedCursor: null,
                        totalCount: null,
                        stage,
                        detail,
                        DateTime.UtcNow));
                PublishStatus(status);
            }
            catch (Exception ex)
            {
                BMSLibrary.LogInstallPerformanceWarn("lr2_song_db_sync_status mark_incomplete_failed"
                    + " reason=" + (logReason ?? "unknown")
                    + " exception=" + ex.GetType().Name
                    + " message=" + BMSLibrary.GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
            }
        }

        void ICatalogWriteFailureSink.PublishCatalogWriteFailureFact(CatalogWriteFailureFact failureFact)
        {
            if (failureFact == null)
            {
                return;
            }

            try
            {
                BMSLibrary.LogInstallPerformanceWarn(
                    "lr2_song_db_write failed"
                    + " reason=" + (failureFact.LogReason ?? "unknown")
                    + " stage=" + (failureFact.Stage ?? "lr2_song_db_write_failed")
                    + " exception=" + (failureFact.ExceptionTypeName ?? "unknown")
                    + " message=" + (failureFact.DisplayedMessage ?? string.Empty));
            }
            catch
            {
                // Failure logging must never replace the original catalog exception.
            }

            try
            {
                MarkLr2SongDbSyncIncomplete(
                    data.CurrentOptionsSnapshot,
                    failureFact.RunId,
                    failureFact.Stage,
                    failureFact.Detail,
                    failureFact.LogReason);
            }
            catch (Exception publishException)
            {
                try
                {
                    BMSLibrary.LogInstallPerformanceWarn(
                        "lr2_catalog_write_failure_fact_publish_failed"
                        + " reason=" + (failureFact.LogReason ?? "unknown")
                        + " exception=" + publishException.GetType().Name
                        + " message=" + BMSLibrary.GetDisplayedExceptionMessage(publishException).Replace(Environment.NewLine, " | "));
                }
                catch
                {
                    // Failure publication must never replace the original catalog exception.
                }
            }
        }

        internal Lr2FolderFileDbSyncResult SyncPlaylistLr2FolderFileRows(
            string operation,
            Lr2FolderFileDbSyncRequest request,
            LibraryFileMutationCapability mutationCapability)
        {
            RequireMutationCapability(mutationCapability);
            if (request == null)
            {
                return null;
            }

            string resolvedOperation = string.IsNullOrWhiteSpace(operation)
                ? "playlist_lr2folder_sync"
                : operation;

            try
            {
                return data.ApplyLr2FolderFileSync(request);
            }
            catch (Exception ex)
            {
                MarkLr2SongDbSyncIncompleteAfterPlaylistLr2FolderSyncFailure(ex, resolvedOperation);
                try
                {
                    BMSLibrary.LogInstallPerformanceWarn(
                        resolvedOperation + " failed"
                        + " exception=" + ex.GetType().Name
                        + " message=" + BMSLibrary.GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
                }
                catch
                {
                    // Failure diagnostics must never replace the original playlist exception.
                }
                throw;
            }
        }

        internal void SyncLr2NormalFoldersForCatalogMutation(
            Lr2NormalFolderCatalogMutationReceipt receipt,
            string reason,
            LibraryFileMutationCapability mutationCapability)
        {
            if (receipt?.HasBmsMutation != true)
            {
                return;
            }

            BmsLibraryOptionsSnapshot options = data.CurrentOptionsSnapshot;
            if (options?.OperationModeLR2DB != true)
            {
                return;
            }

            RequireMutationCapability(mutationCapability);
            Exception failure = null;
            var stopwatch = Stopwatch.StartNew();
            List<string> roots = [];
            Lr2NormalFolderSyncScope syncInput = Lr2NormalFolderSyncScope.Empty;
            try
            {
                if (failure == null)
                {
                    if (receipt.RequiresCurrentBmsChartSnapshot && !receipt.SnapshotAvailable)
                    {
                        throw new InvalidOperationException("LR2 normal-folder catalog snapshot is unavailable.");
                    }

                    roots = [.. data.CaptureBmsDirectories().Roots];
                    if (roots.Count > 0)
                    {
                        syncInput = Lr2NormalFolderSyncScopeBuilder.CreateForCatalogMutation(roots, receipt);
                        if (syncInput.ChartPaths.Count > 0
                            || syncInput.PruneScopeDirectories.Count > 0
                            || syncInput.PruneExactDirectories.Count > 0)
                        {
                            IReadOnlyCollection<string> directoryMetadataTargets =
                                Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(roots, syncInput.ChartPaths);
                            Lr2OwnedMutationDirectoryMetadataSurface metadataSurface =
                                CreateLr2OwnedMutationDirectoryMetadataSurface(directoryMetadataTargets);
                            Lr2NormalFolderDbSyncResult syncResult = data.ApplyLr2NormalFolderSync(
                                new Lr2NormalFolderDbSyncRequest
                                {
                                    RootDirectories = roots,
                                    ChartPaths = syncInput.ChartPaths,
                                    DirectoryPaths = directoryMetadataTargets,
                                    FolderInfoFilePaths = metadataSurface.FolderInfoCandidates.Paths,
                                    FolderInfoFileEntries = metadataSurface.FolderInfoCandidates.EntriesByPath,
                                    DirectoryLastWriteTimeUtcResolver = CreateLastWriteTimeResolver(metadataSurface.DirectoryEntries),
                                    PruneScopeDirectories = syncInput.PruneScopeDirectories,
                                    PruneExactDirectories = syncInput.PruneExactDirectories,
                                    GeneratedAtUtc = DateTime.UtcNow,
                                    AllowPrune = syncInput.PruneScopeDirectories.Count > 0
                                        || syncInput.PruneExactDirectories.Count > 0,
                                    UseScopedExistingRows = true
                                });
                            stopwatch.Stop();
                            string successLog = "lr2_normal_folder_catalog_sync done"
                                + " reason=" + (reason ?? "unknown")
                                + " ownedVersion=" + receipt.OwnedCollectionVersion
                                + " paths=" + syncInput.ChartPaths.Count
                                + " directoryPaths=" + directoryMetadataTargets.Count
                                + " pruneScopes=" + syncInput.PruneScopeDirectories.Count
                                + " exactPrunes=" + syncInput.PruneExactDirectories.Count
                                + " roots=" + roots.Count
                                + " generated=" + syncResult.GeneratedCount
                                + " upserted=" + syncResult.UpsertedCount
                                + " deleted=" + syncResult.DeletedCount
                                + " elapsedMs=" + stopwatch.ElapsedMilliseconds;
                            BMSLibrary.LogInstallPerformance(successLog);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure == null)
            {
                return;
            }

            stopwatch.Stop();
            string failureDetail = BMSLibrary.GetDisplayedExceptionMessage(failure);
            try
            {
                MarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
                    options,
                    stage: "lr2_normal_folder_mutation_sync_failed",
                    detail: "lr2_normal_folder_mutation_sync_failed: " + failureDetail,
                    logReason: "lr2_normal_folder_mutation_sync_failed");
                BMSLibrary.LogInstallPerformanceWarn("lr2_normal_folder_catalog_sync failed"
                    + " reason=" + (reason ?? "unknown")
                    + " paths=" + syncInput.ChartPaths.Count
                    + " pruneScopes=" + syncInput.PruneScopeDirectories.Count
                    + " exactPrunes=" + syncInput.PruneExactDirectories.Count
                    + " roots=" + roots.Count
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " exception=" + failure.GetType().Name
                    + " message=" + failureDetail.Replace(Environment.NewLine, " | "));
            }
            catch
            {
                // Failure diagnostics must never replace the authoritative catalog exception.
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static Lr2FolderInfoCandidateSnapshot CreateLr2OwnedMutationFolderInfoCandidates(
            IEnumerable<string> targetDirectories)
        {
            IReadOnlyList<string> targets = NormalizeLr2DirectoryMetadataTargets(targetDirectories);
            var entries = new List<RootFileEnumerationEntry>();
            foreach (string targetDirectory in targets)
            {
                RootFileEnumerationEntry entry = CreateLr2OwnedMutationFolderInfoEntry(targetDirectory);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }

            return Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromEntries(entries, targets);
        }

        private static Lr2OwnedMutationDirectoryMetadataSurface CreateLr2OwnedMutationDirectoryMetadataSurface(
            IEnumerable<string> targetDirectories)
        {
            return new Lr2OwnedMutationDirectoryMetadataSurface(
                CreateLr2OwnedMutationFolderInfoCandidates(targetDirectories),
                CreateLr2OwnedMutationDirectoryEntries(targetDirectories));
        }

        private static RootFileEnumerationEntry CreateLr2OwnedMutationFolderInfoEntry(string directoryPath)
        {
            try
            {
                string folderInfoPath = Path.Combine(directoryPath, "folderinfo.txt");
                if (!LongPathFileSystem.FileExists(folderInfoPath))
                {
                    return null;
                }
                string normalizedPath = LongPathFileSystem.NormalizePathForStorage(folderInfoPath);
                LongPathFileSystem.FileMetadata metadata = LongPathFileSystem.GetFileMetadata(normalizedPath);
                return LongPathFileSystem.FileExists(normalizedPath)
                    ? new RootFileEnumerationEntry(normalizedPath, metadata.LastWriteTimeUtc, metadata.Length)
                    : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SecurityException)
            {
                return null;
            }
        }

        private static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateLr2OwnedMutationDirectoryEntries(
            IEnumerable<string> targetDirectories)
        {
            IReadOnlyList<string> targets = NormalizeLr2DirectoryMetadataTargets(targetDirectories);
            var targetSet = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            var entries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (string targetDirectory in targets)
            {
                RootFileEnumerationEntry entry = RootFileEnumerationEntry.FromDirectoryInfo(targetDirectory);
                string key = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path);
                if (!string.IsNullOrWhiteSpace(key) && targetSet.Contains(key))
                {
                    entries[key] = new RootFileEnumerationEntry(key, entry.LastWriteTimeUtc, entry.FileSize);
                }
            }

            return entries;
        }

        private sealed class Lr2OwnedMutationDirectoryMetadataSurface(
            Lr2FolderInfoCandidateSnapshot folderInfoCandidates,
            IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries)
        {
            public Lr2FolderInfoCandidateSnapshot FolderInfoCandidates { get; } =
                folderInfoCandidates ?? new Lr2FolderInfoCandidateSnapshot([], new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase), discoveryComplete: true);

            public IReadOnlyDictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; } =
                directoryEntries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        }

        private static Func<string, DateTime?> CreateLastWriteTimeResolver(
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
        {
            if (entriesByPath == null || entriesByPath.Count == 0)
            {
                return null;
            }

            return path =>
            {
                string key = Lr2FolderPath.NormalizeDirectoryPath(path);
                return !string.IsNullOrWhiteSpace(key)
                    && entriesByPath.TryGetValue(key, out RootFileEnumerationEntry entry)
                        ? entry.LastWriteTimeUtc
                        : null;
            };
        }

        internal void CaptureLr2SongDbSyncScanSurface(
            BmsLibraryOptionsSnapshot options,
            IEnumerable<string> rootDirectories,
            SongTableFileCheckResult fileCheckResult)
        {
            if (options?.OperationModeLR2DB != true
                || fileCheckResult?.Lr2ScanSurfaceAvailable != true)
            {
                return;
            }

            List<string> roots = [.. (rootDirectories ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(BMSLibrary.SafeFullPathOrOriginal)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            if (roots.Count == 0)
            {
                return;
            }
            if (fileCheckResult.Lr2NormalFolderSyncFailed
                || fileCheckResult.Lr2NormalFolderSkippedMissingMetadataCount > 0
                || fileCheckResult.Lr2NormalFolderInfoReadFailureCount > 0)
            {
                int preservedGeneration;
                lock (ScanSurfaceGate)
                {
                    preservedGeneration = ScanSurfaceSnapshot?.Generation ?? 0;
                    AppManagedCustomFolderOutputPhysicalSurface = new CustomFolderOutputPhysicalSurface(
                        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                        discoveryComplete: false);
                }
                BMSLibrary.LogInstallPerformance("lr2_song_db_sync_scan_surface skipped"
                    + " reason=normal_folder_sync_unapplied"
                    + " failed=" + fileCheckResult.Lr2NormalFolderSyncFailed.ToString().ToLowerInvariant()
                    + " skippedMissingMetadata=" + fileCheckResult.Lr2NormalFolderSkippedMissingMetadataCount
                    + " folderInfoReadFailures=" + fileCheckResult.Lr2NormalFolderInfoReadFailureCount
                    + " preservedGeneration=" + preservedGeneration);
                return;
            }

            IReadOnlyList<string> lr2FolderDiscoveryDirectories = fileCheckResult.Lr2ScanLr2FolderDiscoveryDirectories ?? [];
            IReadOnlyList<string> appManagedOutputDirectories = [];
            IReadOnlyList<string> appManagedOutputFilePaths = [];
            int appManagedCandidateCount = 0;
            Lr2FolderFileCandidateSnapshot lr2FolderCandidates;
            bool reusedFilteredLr2FolderCandidates = fileCheckResult.Lr2ScanLr2FolderCandidatesAlreadyFiltered;
            if (reusedFilteredLr2FolderCandidates)
            {
                lr2FolderCandidates = new Lr2FolderFileCandidateSnapshot(
                    fileCheckResult.Lr2ScanLr2FolderFilePaths,
                    fileCheckResult.Lr2ScanLr2FolderFileEntries,
                    fileCheckResult.Lr2ScanLr2FolderFileDiscoveryComplete);
                appManagedCandidateCount = fileCheckResult.Lr2ScanLr2FolderAppManagedFilteredCount;
            }
            else
            {
                Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();
                appManagedOutputDirectories = appManagedOutputScope.Directories;
                appManagedOutputFilePaths = appManagedOutputScope.FilePaths;
                if (!appManagedOutputScope.IsComplete)
                {
                    lr2FolderCandidates = BMSLibrary.CreateIncompleteLr2FolderCandidateSnapshot();
                }
                else
                {
                    lr2FolderCandidates = Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                        fileCheckResult.Lr2ScanLr2FolderFilePaths,
                        fileCheckResult.Lr2ScanLr2FolderFileEntries,
                        appManagedOutputFilePaths,
                        fileCheckResult.Lr2ScanLr2FolderFileDiscoveryComplete,
                        out appManagedCandidateCount,
                        appManagedOutputScope.Directories);
                }
            }
            IReadOnlyList<string> lr2FolderFilePaths = lr2FolderCandidates.Paths;
            IReadOnlyDictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries = lr2FolderCandidates.EntriesByPath;
            bool lr2FolderFileDiscoveryComplete = lr2FolderCandidates.DiscoveryComplete;
            if (lr2FolderDiscoveryDirectories.Count == 0)
            {
                int previousGeneration = 0;
                lock (ScanSurfaceGate)
                {
                    previousGeneration = ScanSurfaceSnapshot?.Generation ?? 0;
                    ScanSurfaceSnapshot = null;
                    AppManagedCustomFolderOutputPhysicalSurface = new CustomFolderOutputPhysicalSurface(
                        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                        discoveryComplete: false);
                    PreparedDataSurfaceAppliedScanGeneration = 0;
                }
                BMSLibrary.LogInstallPerformance("lr2_song_db_sync_scan_surface skipped"
                    + " reason=missing_lr2folder_surface"
                    + " roots=" + roots.Count
                    + " invalidatedGeneration=" + previousGeneration);
                return;
            }

            Lr2SongDbSyncScanSurfaceSnapshot snapshot;
            StorageRowsVersionSnapshot currentStorageRowsVersion = data.CaptureStorageRowsVersionSnapshot();
            lock (ScanSurfaceGate)
            {
                int generation = ScanSurfaceGeneration == int.MaxValue
                    ? 1
                    : ScanSurfaceGeneration + 1;
                ScanSurfaceGeneration = generation;
                snapshot = new Lr2SongDbSyncScanSurfaceSnapshot(
                    generation,
                    roots,
                    fileCheckResult.Lr2ScanNormalFolderDirectoryPaths,
                    fileCheckResult.Lr2ScanDirectoryEntries,
                    fileCheckResult.Lr2ScanNormalFolderDirectoryEntries,
                    fileCheckResult.Lr2ScanFolderInfoFilePaths,
                    fileCheckResult.Lr2ScanFolderInfoFileEntries,
                    fileCheckResult.Lr2ScanTextFileDirectories,
                    lr2FolderDiscoveryDirectories,
                    lr2FolderFilePaths,
                    lr2FolderFileEntries,
                    lr2FolderFileDiscoveryComplete,
                    data.OwnedChartCollectionVersion,
                    currentStorageRowsVersion.BmsRowsVersion,
                    currentStorageRowsVersion.BmsonRowsVersion);
                ScanSurfaceSnapshot = snapshot;
                AppManagedCustomFolderOutputPhysicalSurface = new CustomFolderOutputPhysicalSurface(
                    fileCheckResult.Lr2ScanAppManagedCustomFolderOutputFileEntries,
                    fileCheckResult.Lr2ScanAppManagedCustomFolderOutputDiscoveryComplete);
                PreparedDataSurfaceAppliedScanGeneration = 0;
            }
            BMSLibrary.LogInstallPerformance("lr2_song_db_sync_scan_surface captured"
                + " generation=" + snapshot.Generation
                + " roots=" + snapshot.RootDirectories.Count
                + " normalFolderDirs=" + snapshot.NormalFolderDirectoryPaths.Count
                + " directorySurfaceEntries=" + snapshot.DirectoryEntries.Count
                + " folderInfoCandidates=" + snapshot.FolderInfoFilePaths.Count
                + " lr2FolderDiscoveryRoots=" + snapshot.Lr2FolderDiscoveryDirectories.Count
                + " lr2FolderCandidates=" + snapshot.Lr2FolderFilePaths.Count
                + " reusedFilteredLr2FolderCandidates=" + reusedFilteredLr2FolderCandidates.ToString().ToLowerInvariant()
                + " appManagedFiltered=" + appManagedCandidateCount
                + " appManagedScopeDirs=" + (reusedFilteredLr2FolderCandidates
                    ? fileCheckResult.Lr2ScanLr2FolderAppManagedScopeDirectoryCount
                    : appManagedOutputDirectories.Count)
                + " appManagedExactFiles=" + (reusedFilteredLr2FolderCandidates
                    ? fileCheckResult.Lr2ScanLr2FolderAppManagedExactFileCount
                    : appManagedOutputFilePaths.Count)
                + " appManagedPhysicalCandidates=" + (fileCheckResult.Lr2ScanAppManagedCustomFolderOutputFilePaths?.Count ?? 0)
                + " appManagedPhysicalDiscoveryComplete=" + fileCheckResult.Lr2ScanAppManagedCustomFolderOutputDiscoveryComplete.ToString().ToLowerInvariant()
                + " lr2FolderDiscoveryComplete=" + snapshot.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant()
                + " textFileDirs=" + snapshot.TextFileDirectories.Count
                + " ownedCollectionVersion=" + snapshot.OwnedCollectionVersion
                + " bmsRowsVersion=" + snapshot.BmsRowsVersion
                + " bmsonRowsVersion=" + snapshot.BmsonRowsVersion);
        }

        internal bool IsShutdownRequested => runtime.IsShutdownRequested;

        internal Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler =>
            runtime.StartupBackgroundTaskScheduler;

        internal Lr2SongDbSyncRuntimeSnapshot GetLr2SongDbSyncRuntimeSnapshot()
        {
            lock (RequestGate)
            {
                return CreateRuntimeSnapshotUnsafe();
            }
        }

        /// <summary>
        /// Reserves the LR2 preparation route and, when generated output is
        /// requested, returns the same exclusive logical lease used by other
        /// file mutations.  The lease is the only authorization that may be
        /// passed to nested playlist/catalog apply work.
        /// </summary>
        internal bool TryReserveLr2SongDbSyncPreparation(
            bool requiresPreparation,
            out Lr2SongDbSyncRuntimeSnapshot blockingSnapshot,
            out LibraryFileMutationLease preparationLease)
        {
            preparationLease = null;
            lock (mutationSequenceGate)
            {
                lock (RequestGate)
                {
                    if (Running || StatusPublicationInProgress || PreparationInProgress || MutationInProgress > 0)
                    {
                        blockingSnapshot = CreateRuntimeSnapshotUnsafe();
                        return false;
                    }
                    if (requiresPreparation)
                    {
                        long leaseId = ++nextMutationLeaseId;
                        MutationInProgress = 1;
                        activeMutationLeaseId = leaseId;
                        PreparationInProgress = true;
                        preparationLease = new LibraryFileMutationLease(
                            this,
                            () => IsMutationLeaseActive(leaseId),
                            () => EndPreparation(leaseId));
                    }
                    blockingSnapshot = CreateRuntimeSnapshotUnsafe();
                    return true;
                }
            }
        }

        internal Lr2SongDbSyncStatusSnapshot GetLr2SongDbSyncStatusSnapshot()
        {
            return GetStatusSnapshot();
        }

        internal Lr2SearchRootSnapshot CaptureBmsDirectories() =>
            data.CaptureBmsDirectories();

        internal Lr2SongDbSyncStatusSnapshot EvaluateLr2SongDbSyncStatus(
            bool enabled,
            string signature,
            DateTime nowUtc) =>
            data.EvaluateLr2SongDbSyncStatus(enabled, signature, nowUtc);

        internal Lr2SongDbSyncResult ApplyLr2SongDbSync(Lr2SongDbSyncRequest request) =>
            data.ApplyLr2SongDbSync(request);

        internal void PublishLr2SongDbSyncCommittedPathReceipt(
            SongTableFileCheckResult fileCheckResult,
            string reason)
        {
            if (CurrentOptionsSnapshot?.OperationModeLR2DB != true
                || fileCheckResult == null
                || fileCheckResult.CommittedLr2SongDbSyncBmsPaths.Count == 0)
            {
                DiscardLr2SongDbSyncCommittedPathReceipt("file_diff_not_eligible_" + (reason ?? "unknown"));
                return;
            }

            StorageRowsVersionSnapshot storageRowsVersion = data.CaptureStorageRowsVersionSnapshot();
            var receipt = new Lr2SongDbSyncCommittedPathReceipt(
                storageRowsVersion.BmsRowsVersion,
                fileCheckResult.CommittedLr2SongDbSyncBmsPaths);
            lock (CommittedPathReceiptGate)
            {
                CommittedPathReceipt = receipt;
            }
            LogInstallPerformance("lr2_song_db_sync committed_path_receipt published"
                + " reason=" + (reason ?? "unknown")
                + " paths=" + receipt.CommittedBmsPaths.Count
                + " bmsRowsVersion=" + receipt.BmsRowsVersion);
        }

        internal Lr2SongDbSyncCommittedPathReceipt TakeLr2SongDbSyncCommittedPathReceipt(
            Lr2SongDbSyncInput input,
            string reason)
        {
            Lr2SongDbSyncCommittedPathReceipt receipt;
            lock (CommittedPathReceiptGate)
            {
                receipt = CommittedPathReceipt;
                CommittedPathReceipt = null;
            }
            if (receipt == null)
            {
                return null;
            }
            if (!receipt.Matches(input))
            {
                LogInstallPerformance("lr2_song_db_sync committed_path_receipt discarded"
                    + " reason=version_mismatch_" + (reason ?? "unknown")
                    + " receiptBmsRowsVersion=" + receipt.BmsRowsVersion
                    + " inputBmsRowsVersion=" + (input?.BmsRowsVersion ?? 0));
                return null;
            }
            LogInstallPerformance("lr2_song_db_sync committed_path_receipt taken"
                + " reason=" + (reason ?? "unknown")
                + " paths=" + receipt.CommittedBmsPaths.Count
                + " bmsRowsVersion=" + receipt.BmsRowsVersion);
            return receipt;
        }

        internal void DiscardLr2SongDbSyncCommittedPathReceipt(string reason)
        {
            bool discarded;
            lock (CommittedPathReceiptGate)
            {
                discarded = CommittedPathReceipt != null;
                CommittedPathReceipt = null;
            }
            if (discarded)
            {
                LogInstallPerformance("lr2_song_db_sync committed_path_receipt discarded"
                    + " reason=" + (reason ?? "unknown"));
            }
        }

        internal void PublishLr2SongDbSyncStatus(Lr2SongDbSyncStatusSnapshot status)
        {
            PublishStatus(status);
        }

        internal bool TrySkipForShutdown(string operation, string reason) =>
            runtime.TrySkipForShutdown(operation, reason);

        internal void ClearLr2SongDbSyncPreparedDataSurface(string reason) =>
            ClearPreparedDataSurface(reason);

        internal bool TryBeginLr2SongDbSyncRequest(out int requestVersion)
        {
            lock (RequestGate)
            {
                if (Running || StatusPublicationInProgress || MutationInProgress > 0)
                {
                    requestVersion = RequestedVersion;
                    return false;
                }
                RequestedVersion++;
                requestVersion = RequestedVersion;
                Cancellation?.Dispose();
                Cancellation = new CancellationTokenSource();
                SetObservableRequestedVersion(requestVersion);
                SetObservableTotalCount(0);
                SetObservableProcessedCount(0);
                SetObservableStage("queued");
                SetObservableStageProcessedCount(0);
                SetObservableStageTotalCount(0);
                SetObservableFailureMessage(string.Empty);
                Running = true;
                SetObservableRunning(true);
                return true;
            }
        }

        internal void CompleteLr2SongDbSyncRequest(int requestVersion, string stage)
        {
            CompleteRequest(requestVersion, stage);
        }

        internal void FailLr2SongDbSyncRequest(
            int requestVersion,
            Lr2SongDbSyncStatusKind status,
            string stage,
            string message)
        {
            FailRequest(requestVersion, status, stage, message);
        }

        internal void RunLr2SongDbSync(
            string reason,
            string signature,
            int requestVersion,
            bool allowCommittedPathReceipt) =>
            Lr2SongDbSyncRequestCoordinator.Run(
                this,
                reason,
                signature,
                requestVersion,
                allowCommittedPathReceipt);

        internal CancellationToken GetLr2SongDbSyncCancellationToken()
        {
            lock (RequestGate)
            {
                return Cancellation?.Token ?? CancellationToken.None;
            }
        }

        internal Lr2SongDbSyncInput CreateLr2SongDbSyncInput()
        {
            var inputStopwatch = Stopwatch.StartNew();
            var rowSnapshotStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncInputRowSnapshot rowSnapshot = CreateLr2SongDbSyncInputRowSnapshot();
            rowSnapshotStopwatch.Stop();

            var rootsStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncInputRootSnapshot rootSnapshot = CreateLr2SongDbSyncInputRootSnapshot();
            rootsStopwatch.Stop();

            var builtinSettingsStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot = CreateLr2SongDbSyncInputSettingsSnapshot(
                rowSnapshot.SongRows,
                rootSnapshot.CapturedAtUtc,
                rootSnapshot.RootDirectories);
            builtinSettingsStopwatch.Stop();

            var scanSurfaceStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncScanSurfaceSelection scanSurfaceSelection =
                CreateLr2SongDbSyncScanSurfaceSelection(rootSnapshot, rowSnapshot);
            scanSurfaceStopwatch.Stop();

            Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection =
                CreateLr2SongDbSyncPreparedSurfaceSelection(scanSurfaceSelection.Surface);
            var inputBuilder = new Lr2SongDbSyncInputBuilder(
                BMSLibrary.LogEverythingScan,
                BMSLibrary.LogInstallPerformance,
                data.EverythingNative);

            var lr2FolderCandidatesStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();

            return inputBuilder.Create(
                rowSnapshot,
                rootSnapshot,
                settingsSnapshot,
                scanSurfaceSelection,
                preparedSurfaceSelection,
                appManagedOutputScope,
                inputStopwatch,
                rowSnapshotStopwatch,
                rootsStopwatch,
                builtinSettingsStopwatch,
                scanSurfaceStopwatch,
                lr2FolderCandidatesStopwatch);
        }

        private Lr2SongDbSyncScanSurfaceSelection CreateLr2SongDbSyncScanSurfaceSelection(
            Lr2SongDbSyncInputRootSnapshot rootSnapshot,
            Lr2SongDbSyncInputRowSnapshot rowSnapshot)
        {
            Lr2SongDbSyncScanSurfaceSnapshot scanSurface = GetCurrentLr2SongDbSyncScanSurface(
                rootSnapshot.RootDirectories,
                rootSnapshot.Lr2FolderDiscoveryDirectories,
                rowSnapshot,
                out string scanSurfaceMissReason);

            return new Lr2SongDbSyncScanSurfaceSelection(scanSurface, scanSurfaceMissReason);
        }

        private Lr2SongDbSyncInputRowSnapshot CreateLr2SongDbSyncInputRowSnapshot()
        {
            return data.CaptureLr2SynchronizationInputRowSnapshot();
        }

        private Lr2SongDbSyncInputRootSnapshot CreateLr2SongDbSyncInputRootSnapshot()
        {
            DateTime capturedAtUtc = DateTime.UtcNow;
            List<string> rootDirectories = [.. data.CaptureBmsDirectories().Roots];
            BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
            return new Lr2SongDbSyncInputRootSnapshot(
                capturedAtUtc,
                rootDirectories,
                CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(rootDirectories, options),
                options.LR2RootPath);
        }

        private Lr2SongDbSyncInputSettingsSnapshot CreateLr2SongDbSyncInputSettingsSnapshot(
            IEnumerable<BMSFile> songRows,
            DateTime nowUtc,
            IEnumerable<string> rootDirectories)
        {
            BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
            List<string> lr2BuiltinFolderSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options);
            return new Lr2SongDbSyncInputSettingsSnapshot(
                Lr2BuiltinCustomFolderSettings.Create(
                    data.CreateCurrentLr2ConfigOrNull(),
                    songRows,
                    nowUtc),
                lr2BuiltinFolderSourceDirectories,
                options.LR2CustomFolderOutputBaseDir,
                options.LR2CustomFolderAdditionalOutputBaseDirs,
                options.LR2CustomFolderOutputBaseDirRootType,
                CreateLr2SongDbSyncLr2FolderPruneDirectories(
                    rootDirectories,
                    lr2BuiltinFolderSourceDirectories,
                    options: options));
        }

        private Lr2SongDbSyncPreparedSurfaceSelection CreateLr2SongDbSyncPreparedSurfaceSelection(
            Lr2SongDbSyncScanSurfaceSnapshot scanSurface)
        {
            Lr2SongDbSyncPreparedDataSurface pendingPreparedSurface =
                TakeLr2SongDbSyncPreparedDataSurface(out int appliedScanGeneration);
            bool alreadyAppliedToScanSurface = scanSurface != null
                && pendingPreparedSurface?.HasPreparedDataSurface == true
                && appliedScanGeneration == scanSurface.Generation;
            return new Lr2SongDbSyncPreparedSurfaceSelection(
                pendingPreparedSurface,
                alreadyAppliedToScanSurface ? Lr2SongDbSyncPreparedDataSurface.Empty : pendingPreparedSurface,
                appliedScanGeneration,
                alreadyAppliedToScanSurface);
        }

        internal TimeSpan CurrentChartInfoParseTimeout =>
            data.CurrentChartInfoParseTimeout;

        internal void ReportStartupBackgroundTask(
            string name,
            string status,
            long elapsedMs,
            bool failed,
            string detail) =>
            runtime.ReportStartupBackgroundTask(name, status, elapsedMs, failed, detail);

        internal void PublishLr2SongDbSyncPreflightStage(string stage, string reason, string runId)
        {
            UpdateProgress(new Lr2SongDbSyncProgress
            {
                Stage = stage ?? string.Empty,
                StageProcessedCount = 0,
                StageTotalCount = 0
            });
        }

        internal void LogLr2SongDbSyncPreflightStageDone(string stage, string reason, string runId, long elapsedMs)
        {
            BMSLibrary.LogInstallPerformance("lr2_song_db_sync preflight_stage_done"
                + " stage=" + (stage ?? string.Empty)
                + " reason=" + (reason ?? string.Empty)
                + " runId=" + (runId ?? string.Empty)
                + " elapsedMs=" + elapsedMs);
        }

        internal void EnsureLr2SongDbSyncChartInfoIndexHydrated(string reason) =>
            projection.EnsureLr2SongDbSyncChartInfoIndexHydrated(reason);

        internal Dictionary<string, BMSFile> CreateLr2SongDbSyncCompatibilityProjectionIndex() =>
            projection.CreateLr2SongDbSyncCompatibilityProjectionIndex();

        internal Func<BMSFile, LR2SongDBExtended.chart_info> CreateLr2SongDbSyncChartInfoResolverSnapshot() =>
            projection.CreateLr2SongDbSyncChartInfoResolverSnapshot();

        internal HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason) =>
            projection.CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(reason);

        internal void UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<LR2SongDBExtended.chart_info> rows) =>
            projection.UpsertLr2SongDbSyncChartInfoIndexRows(rows);

        internal void UpdateLr2SongDbSyncProgress(Lr2SongDbSyncProgress progress) =>
            UpdateProgress(progress);

        internal int ApplyLr2SongDbSyncCompatibilityProjection(
            IReadOnlyList<BMSFileMaintenanceInfo> maintenanceInfos,
            string reason,
            IReadOnlyDictionary<string, BMSFile> bmsByPath,
            bool logSummary,
            bool dispatchPresentation) =>
            projection.ApplyLr2SongDbSyncCompatibilityProjection(
                maintenanceInfos,
                reason,
                bmsByPath,
                logSummary,
                dispatchPresentation);

        internal void DispatchWarningPresentationChanged(string reason) =>
            projection.DispatchWarningPresentationChanged(reason);

        internal void MarkLr2SongDbSyncFailedStatus(string signature, string runId, Exception ex)
        {
            data.ApplyLr2SongDbSyncStatusMutation(
                new Lr2SongDbSyncStatusMutationRequest(
                    Lr2SongDbSyncStatusMutationKind.MarkFailed,
                    signature,
                    runId,
                    processedCursor: null,
                    totalCount: null,
                    stage: "failed",
                    detail: ex?.Message,
                    DateTime.UtcNow));
        }

        internal void LogInstallPerformance(string message) =>
            BMSLibrary.LogInstallPerformance(message);

        internal void MarkLr2SongDbSyncInterruptedStatus(
            string signature,
            string runId,
            int processedCursor,
            int totalCount,
            string stage)
        {
            try
            {
                data.ApplyLr2SongDbSyncStatusMutation(
                    new Lr2SongDbSyncStatusMutationRequest(
                        Lr2SongDbSyncStatusMutationKind.MarkIncomplete,
                        signature,
                        runId,
                        processedCursor: Math.Max(0, processedCursor),
                        totalCount: Math.Max(0, totalCount),
                        stage,
                        detail: "shutdown_interrupted",
                        DateTime.UtcNow));
            }
            catch
            {
                // A remaining Running row is acceptable when status persistence
                // cannot safely complete during shutdown.
            }
        }

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
            SetObservableStatusVersion(ObservableStatusVersion + 1);
        }

        private void OnPropertyChanged(string propertyName)
        {
            try
            {
                queueObservablePropertyChange(propertyName);
            }
            catch (Exception ex)
            {
                BMSLibrary.LogInstallPerformanceWarn(
                    "lr2_sync_property_publication_queue_failed property="
                    + (propertyName ?? string.Empty)
                    + " exception="
                    + ex.GetType().Name
                    + " message="
                    + BMSLibrary.GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
            }
        }

        private void SetObservableRunning(bool value)
        {
            if (ObservableRunning != value)
            {
                ObservableRunning = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncRunning));
            }
        }

        private void SetObservableRequestedVersion(int value)
        {
            if (ObservableRequestedVersion != value)
            {
                ObservableRequestedVersion = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncRequestedVersion));
            }
        }

        private void SetObservableCompletedVersion(int value)
        {
            if (ObservableCompletedVersion != value)
            {
                ObservableCompletedVersion = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncCompletedVersion));
            }
        }

        private void SetObservableFailedVersion(int value)
        {
            if (ObservableFailedVersion != value)
            {
                ObservableFailedVersion = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncFailedVersion));
            }
        }

        private void SetObservableTotalCount(int value)
        {
            if (ObservableTotalCount != value)
            {
                ObservableTotalCount = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncTotalCount));
            }
        }

        private void SetObservableProcessedCount(int value)
        {
            if (ObservableProcessedCount != value)
            {
                ObservableProcessedCount = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncProcessedCount));
            }
        }

        private void SetObservableStage(string value)
        {
            value ??= string.Empty;
            if (!string.Equals(ObservableStage, value, StringComparison.Ordinal))
            {
                ObservableStage = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncStage));
            }
        }

        private void SetObservableStageProcessedCount(int value)
        {
            if (ObservableStageProcessedCount != value)
            {
                ObservableStageProcessedCount = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncStageProcessedCount));
            }
        }

        private void SetObservableStageTotalCount(int value)
        {
            if (ObservableStageTotalCount != value)
            {
                ObservableStageTotalCount = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncStageTotalCount));
            }
        }

        private void SetObservableFailureMessage(string value)
        {
            value ??= string.Empty;
            if (!string.Equals(ObservableFailureMessage, value, StringComparison.Ordinal))
            {
                ObservableFailureMessage = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncFailureMessage));
            }
        }

        private void SetObservableStatusVersion(int value)
        {
            if (ObservableStatusVersion != value)
            {
                ObservableStatusVersion = value;
                OnPropertyChanged(nameof(BMSLibrary.Lr2SongDbSyncStatusVersion));
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

        internal void ApplyLr2SongDbSyncPreparedDataSurface(
            string reason,
            Lr2SongDbSyncPreparedDataSurface preparedSurface)
        {
            var applyStopwatch = Stopwatch.StartNew();
            var lockWaitStopwatch = Stopwatch.StartNew();
            long lockWaitMs = 0;
            long discoveryRootsMs = 0;
            long lr2FolderCandidatesMs = 0;
            long folderInfoCandidatesMs = 0;
            long directoryOverlayMs = 0;
            long textFileDirsMs = 0;
            string mergeResult = "unknown";
            preparedSurface ??= Lr2SongDbSyncPreparedDataSurface.Empty;
            BmsLibraryOptionsSnapshot currentSettings = data.CurrentOptionsSnapshot;
            Lr2SongDbSyncScanSurfaceSnapshot snapshot = null;
            lock (ScanSurfaceGate)
            {
                lockWaitStopwatch.Stop();
                lockWaitMs = lockWaitStopwatch.ElapsedMilliseconds;
                PreparedDataSurface = preparedSurface;
                PreparedDataSurfaceAppliedScanGeneration = 0;
                snapshot = ScanSurfaceSnapshot;
                if (snapshot == null)
                {
                    mergeResult = "no_scan_surface";
                    snapshot = null;
                }
                else if (!preparedSurface.HasPreparedDataSurface)
                {
                    mergeResult = "no_prepared_surface";
                    snapshot = null;
                }
                else
                {
                    var discoveryRootsStopwatch = Stopwatch.StartNew();
                    bool discoveryRootsCurrent = BMSLibrary.ArePathSetsEqual(
                        snapshot.Lr2FolderDiscoveryDirectories,
                        CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(snapshot.RootDirectories, currentSettings));
                    discoveryRootsStopwatch.Stop();
                    discoveryRootsMs = discoveryRootsStopwatch.ElapsedMilliseconds;
                    if (!discoveryRootsCurrent)
                    {
                        mergeResult = "lr2folder_roots_changed";
                        snapshot = null;
                    }
                    else
                    {
                        mergeResult = "merged";
                        int generation = ScanSurfaceGeneration == int.MaxValue
                            ? 1
                            : ScanSurfaceGeneration + 1;
                        ScanSurfaceGeneration = generation;
                        var lr2FolderCandidatesStopwatch = Stopwatch.StartNew();
                        Lr2FolderFileCandidateSnapshot candidates = Lr2FolderFileDiscoveryService.MergeCandidateSurface(
                            new Lr2FolderFileCandidateSnapshot(
                                snapshot.Lr2FolderFilePaths,
                                snapshot.Lr2FolderFileEntries,
                                snapshot.Lr2FolderFileDiscoveryComplete),
                            preparedSurface);
                        lr2FolderCandidatesStopwatch.Stop();
                        lr2FolderCandidatesMs = lr2FolderCandidatesStopwatch.ElapsedMilliseconds;
                        var folderInfoCandidatesStopwatch = Stopwatch.StartNew();
                        IReadOnlyList<string> mergedFolderInfoPaths = MergePreparedFileSurface(
                            snapshot.FolderInfoFilePaths,
                            snapshot.FolderInfoFileEntries,
                            preparedSurface.FolderInfoFilePaths,
                            preparedSurface.FolderInfoFileEntries,
                            preparedSurface.Lr2FolderScopeDirectories,
                            out IReadOnlyDictionary<string, RootFileEnumerationEntry> mergedFolderInfoEntries);
                        folderInfoCandidatesStopwatch.Stop();
                        folderInfoCandidatesMs = folderInfoCandidatesStopwatch.ElapsedMilliseconds;
                        var textFileDirsStopwatch = Stopwatch.StartNew();
                        IReadOnlyList<string> mergedTextFileDirectories = MergePreparedDirectoryList(
                            snapshot.TextFileDirectories,
                            preparedSurface.TextFileDirectories,
                            preparedSurface.Lr2FolderScopeDirectories);
                        textFileDirsStopwatch.Stop();
                        textFileDirsMs = textFileDirsStopwatch.ElapsedMilliseconds;
                        var directoryOverlayStopwatch = Stopwatch.StartNew();
                        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries =
                            OverlayLr2DirectoryEntrySurface(snapshot.DirectoryEntries, preparedSurface.DirectoryEntries);
                        directoryOverlayStopwatch.Stop();
                        directoryOverlayMs = directoryOverlayStopwatch.ElapsedMilliseconds;
                        snapshot = new Lr2SongDbSyncScanSurfaceSnapshot(
                            generation,
                            snapshot.RootDirectories,
                            snapshot.NormalFolderDirectoryPaths,
                            directoryEntries,
                            snapshot.NormalFolderDirectoryEntries,
                            mergedFolderInfoPaths,
                            mergedFolderInfoEntries,
                            mergedTextFileDirectories,
                            snapshot.Lr2FolderDiscoveryDirectories,
                            candidates.Paths,
                            candidates.EntriesByPath,
                            candidates.DiscoveryComplete,
                            snapshot.OwnedCollectionVersion,
                            snapshot.BmsRowsVersion,
                            snapshot.BmsonRowsVersion);
                        ScanSurfaceSnapshot = snapshot;
                        PreparedDataSurfaceAppliedScanGeneration = generation;
                    }
                }
            }
            applyStopwatch.Stop();

            BMSLibrary.LogInstallPerformance("lr2_song_db_sync_prepared_surface applied"
                + " reason=" + (reason ?? "unknown")
                + " mergeResult=" + mergeResult
                + " scopeDirs=" + preparedSurface.Lr2FolderScopeDirectories.Count
                + " lr2FolderCandidates=" + preparedSurface.Lr2FolderFilePaths.Count
                + " directoryEntries=" + preparedSurface.DirectoryEntries.Count
                + " folderInfoCandidates=" + preparedSurface.FolderInfoFilePaths.Count
                + " textFileDirs=" + preparedSurface.TextFileDirectories.Count
                + " discoveryComplete=" + preparedSurface.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant()
                + " mergedScanSurfaceGeneration=" + (snapshot?.Generation ?? 0)
                + " lockWaitMs=" + lockWaitMs
                + " discoveryRootsMs=" + discoveryRootsMs
                + " lr2FolderCandidatesMs=" + lr2FolderCandidatesMs
                + " folderInfoCandidatesMs=" + folderInfoCandidatesMs
                + " textFileDirsMs=" + textFileDirsMs
                + " directoryOverlayMs=" + directoryOverlayMs
                + " elapsedMs=" + applyStopwatch.ElapsedMilliseconds);
        }

        internal bool HasLr2SongDbSyncPreparedDataSurface()
        {
            lock (ScanSurfaceGate)
            {
                return PreparedDataSurface?.HasPreparedDataSurface == true;
            }
        }

        internal Lr2SongDbSyncPreparedDataSurface TakeLr2SongDbSyncPreparedDataSurface(
            out int appliedScanSurfaceGeneration)
        {
            lock (ScanSurfaceGate)
            {
                Lr2SongDbSyncPreparedDataSurface surface = PreparedDataSurface
                    ?? Lr2SongDbSyncPreparedDataSurface.Empty;
                appliedScanSurfaceGeneration = PreparedDataSurfaceAppliedScanGeneration;
                PreparedDataSurface = Lr2SongDbSyncPreparedDataSurface.Empty;
                PreparedDataSurfaceAppliedScanGeneration = 0;
                return surface;
            }
        }

        internal CustomFolderOutputPhysicalSurface GetCurrentAppManagedCustomFolderOutputPhysicalSurface()
        {
            lock (ScanSurfaceGate)
            {
                return AppManagedCustomFolderOutputPhysicalSurface ?? CustomFolderOutputPhysicalSurface.Empty;
            }
        }

        internal Lr2SongDbSyncScanSurfaceSnapshot GetCurrentLr2SongDbSyncScanSurface(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> lr2FolderDiscoveryDirectories,
            Lr2SongDbSyncInputRowSnapshot rowSnapshot,
            out string missReason)
        {
            missReason = string.Empty;
            Lr2SongDbSyncScanSurfaceSnapshot snapshot;
            lock (ScanSurfaceGate)
            {
                snapshot = ScanSurfaceSnapshot;
            }
            if (snapshot == null)
            {
                missReason = "none";
                return null;
            }
            if (rowSnapshot == null)
            {
                missReason = "row_snapshot";
                return null;
            }
            if (snapshot.BmsRowsVersion != rowSnapshot.BmsRowsVersion)
            {
                missReason = "bms_rows_version";
                return null;
            }
            if (snapshot.BmsonRowsVersion != rowSnapshot.BmsonRowsVersion)
            {
                missReason = "bmson_rows_version";
                return null;
            }
            if (!BMSLibrary.ArePathSetsEqual(snapshot.RootDirectories, rootDirectories))
            {
                missReason = "roots";
                return null;
            }
            if (!BMSLibrary.ArePathSetsEqual(snapshot.Lr2FolderDiscoveryDirectories, lr2FolderDiscoveryDirectories))
            {
                missReason = "lr2folder_roots";
                return null;
            }
            return snapshot;
        }

        internal bool IsCurrentLr2SongDbSyncScanSurface(Lr2SongDbSyncInput input)
        {
            if (input == null || input.ScanSurfaceGeneration <= 0)
            {
                return true;
            }

            Lr2SongDbSyncScanSurfaceSnapshot snapshot;
            lock (ScanSurfaceGate)
            {
                snapshot = ScanSurfaceSnapshot;
            }
            return snapshot != null
                && snapshot.Generation == input.ScanSurfaceGeneration
                && snapshot.BmsRowsVersion == input.BmsRowsVersion
                && snapshot.BmsonRowsVersion == input.BmsonRowsVersion
                && BMSLibrary.ArePathSetsEqual(snapshot.RootDirectories, input.RootDirectories)
                && BMSLibrary.ArePathSetsEqual(snapshot.Lr2FolderDiscoveryDirectories, input.Lr2FolderDiscoveryDirectories);
        }

        private List<string> CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(
            IEnumerable<string> rootDirectories,
            BmsLibraryOptionsSnapshot options = null)
        {
            options ??= data.CurrentOptionsSnapshot;
            return Lr2FolderFileDiscoveryService.CreateDiscoveryDirectories(
                rootDirectories,
                CreateNormalCustomFolderOutputBaseDirectories(options),
                options.LR2CustomFolderOutputBaseDirRootType,
                CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options));
        }

        internal bool IsLr2SongDbSyncInputCurrent(Lr2SongDbSyncInput input)
        {
            StorageRowsVersionSnapshot currentStorageRowsVersion = data.CaptureStorageRowsVersionSnapshot();
            if (input == null
                || data.OwnedChartCollectionVersion != input.OwnedChartCollectionVersion
                || currentStorageRowsVersion.BmsRowsVersion != input.BmsRowsVersion
                || currentStorageRowsVersion.BmsonRowsVersion != input.BmsonRowsVersion)
            {
                return false;
            }

            List<string> roots = [.. data.CaptureBmsDirectories().Roots];
            if (!BMSLibrary.ArePathSetsEqual(input.RootDirectories, roots))
            {
                return false;
            }
            if (!IsCurrentLr2SongDbSyncScanSurface(input))
            {
                return false;
            }

            BmsLibraryOptionsSnapshot options = data.CurrentOptionsSnapshot;
            List<string> lr2FolderDiscoveryDirectories = CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(roots, options);
            if (!BMSLibrary.ArePathSetsEqual(input.Lr2FolderDiscoveryDirectories, lr2FolderDiscoveryDirectories))
            {
                return false;
            }
            Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();
            if (!appManagedOutputScope.IsComplete)
            {
                return false;
            }
            Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings = Lr2BuiltinCustomFolderSettings.Create(
                data.CreateCurrentLr2ConfigOrNull(),
                [],
                DateTime.UtcNow);
            if (!BMSLibrary.AreLr2BuiltinCustomFolderConfigurationEqual(input.Lr2BuiltinCustomFolderSettings, builtinCustomFolderSettings))
            {
                return false;
            }
            if (!string.Equals(BMSLibrary.SafeFullPathOrOriginal(input.Lr2RootPath), BMSLibrary.SafeFullPathOrOriginal(options.LR2RootPath), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(BMSLibrary.SafeFullPathOrOriginal(input.Lr2NormalCustomFolderOutputBaseDir), BMSLibrary.SafeFullPathOrOriginal(options.LR2CustomFolderOutputBaseDir), StringComparison.OrdinalIgnoreCase)
                || !BMSLibrary.ArePathSetsEqual(input.Lr2AdditionalNormalCustomFolderOutputBaseDirs, options.LR2CustomFolderAdditionalOutputBaseDirs)
                || !string.Equals(BMSLibrary.SafeFullPathOrOriginal(input.Lr2RootCustomFolderOutputBaseDir), BMSLibrary.SafeFullPathOrOriginal(options.LR2CustomFolderOutputBaseDirRootType), StringComparison.OrdinalIgnoreCase)
                || !BMSLibrary.ArePathSetsEqual(input.Lr2BuiltinFolderSourceDirectories, CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options)))
            {
                return false;
            }

            return true;
        }

        internal bool TryBeginRequest(out int requestVersion) =>
            TryBeginLr2SongDbSyncRequest(out requestVersion);

        internal void CompleteRequest(int requestVersion, string stage)
        {
            lock (RequestGate)
            {
                CompletedVersion = Math.Max(CompletedVersion, requestVersion);
                SetObservableCompletedVersion(CompletedVersion);
                SetObservableStage(stage ?? string.Empty);
                SetObservableStageProcessedCount(ObservableStageTotalCount > 0
                    ? ObservableStageTotalCount
                    : ObservableProcessedCount);
                SetObservableStageTotalCount(ObservableStageTotalCount > 0
                    ? ObservableStageTotalCount
                    : ObservableTotalCount);
                Running = false;
                SetObservableRunning(false);
                DisposeCancellationUnsafe();
                StatusPublicationInProgress = true;
            }
            try
            {
                PublishStatus(BMSLibrary.CreateRuntimeLr2SongDbSyncStatus(
                    Lr2SongDbSyncStatusKind.Completed,
                    GetStatusSnapshot().Signature,
                    ObservableStage,
                    ObservableProcessedCount,
                    ObservableTotalCount,
                    lastError: null,
                    ObservableStageProcessedCount,
                    ObservableStageTotalCount));
            }
            finally
            {
                lock (RequestGate)
                {
                    StatusPublicationInProgress = false;
                    Monitor.PulseAll(RequestGate);
                }
            }
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
                SetObservableFailedVersion(FailedVersion);
                SetObservableFailureMessage(message ?? string.Empty);
                SetObservableStage(stage ?? string.Empty);
                Running = false;
                SetObservableRunning(false);
                DisposeCancellationUnsafe();
                StatusPublicationInProgress = true;
            }
            try
            {
                PublishStatus(BMSLibrary.CreateRuntimeLr2SongDbSyncStatus(
                    status,
                    GetStatusSnapshot().Signature,
                    ObservableStage,
                    ObservableProcessedCount,
                    ObservableTotalCount,
                    ObservableFailureMessage,
                    ObservableStageProcessedCount,
                    ObservableStageTotalCount));
            }
            finally
            {
                lock (RequestGate)
                {
                    StatusPublicationInProgress = false;
                    Monitor.PulseAll(RequestGate);
                }
            }
        }

        internal void UpdateProgress(Lr2SongDbSyncProgress progress)
        {
            if (progress == null)
            {
                return;
            }
            SetObservableTotalCount(Math.Max(0, progress.TotalCount));
            SetObservableProcessedCount(Math.Max(0, progress.ProcessedCursor));
            SetObservableStage(progress.Stage ?? string.Empty);
            SetObservableStageProcessedCount(Math.Max(0, progress.StageProcessedCount));
            SetObservableStageTotalCount(Math.Max(0, progress.StageTotalCount));
            PublishStatus(BMSLibrary.CreateRuntimeLr2SongDbSyncStatus(
                Lr2SongDbSyncStatusKind.Running,
                GetStatusSnapshot().Signature,
                ObservableStage,
                ObservableProcessedCount,
                ObservableTotalCount,
                lastError: null,
                ObservableStageProcessedCount,
                ObservableStageTotalCount));
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
            DiscardLr2SongDbSyncCommittedPathReceipt("request_disposed");
        }

        internal bool RequestShutdownCancellation(string reason)
        {
            lock (RequestGate)
            {
                if (!Running || Cancellation == null)
                {
                    return false;
                }
                LogInstallPerformance("lr2_song_db_sync shutdown_cancellation_requested"
                    + " reason=" + (reason ?? "unknown")
                    + " stage=" + (ObservableStage ?? string.Empty)
                    + " processed=" + ObservableProcessedCount
                    + " total=" + ObservableTotalCount);
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
                blocked = Running
                    || StatusPublicationInProgress
                    || MutationInProgress > 0
                    || PreparationInProgress;
                stage = ObservableStage ?? string.Empty;
                processed = ObservableProcessedCount;
                total = ObservableTotalCount;
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
                runtime.ShowOperationDialog(
                    Resources.Warn_Lr2SongDbSyncRunning,
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
            return true;
        }

        internal LibraryFileMutationLease TryBeginMutation(
            string operation,
            bool showMessage = true)
        {
            string stage;
            int processed;
            int total;
            bool preparing;
            lock (mutationSequenceGate)
            {
                lock (RequestGate)
                {
                    if (!Running
                        && !StatusPublicationInProgress
                        && MutationInProgress == 0
                        && !PreparationInProgress)
                    {
                        long leaseId = ++nextMutationLeaseId;
                        MutationInProgress = 1;
                        activeMutationLeaseId = leaseId;
                        return new LibraryFileMutationLease(
                            this,
                            () => IsMutationLeaseActive(leaseId),
                            () => EndMutation(leaseId));
                    }
                    stage = ObservableStage ?? string.Empty;
                    processed = ObservableProcessedCount;
                    total = ObservableTotalCount;
                    preparing = PreparationInProgress;
                }
            }
            LogInstallPerformance("lr2_song_db_sync_mutation_blocked operation=" + (operation ?? "(unknown)")
                + " stage=" + stage
                + " processed=" + processed
                + " total=" + total
                + " preparing=" + preparing.ToString().ToLowerInvariant());
            if (showMessage)
            {
                runtime.ShowOperationDialog(
                    Resources.Warn_Lr2SongDbSyncRunning,
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
            return null;
        }

        /// <summary>
        /// Waits for an in-flight LR2 synchronization/preparation to finish, then reserves
        /// the catalog-mutation slot. Settings-driven background work uses this so a valid
        /// request is retried instead of being silently discarded when synchronization is busy.
        /// </summary>
        internal LibraryFileMutationLease BeginMutationWhenAvailable(string operation)
        {
            while (true)
            {
                lock (RequestGate)
                {
                    if (Running
                        || StatusPublicationInProgress
                        || PreparationInProgress
                        || MutationInProgress > 0)
                    {
                        if (runtime.IsShutdownRequested)
                        {
                            throw new InvalidOperationException("LR2 synchronization is unavailable during shutdown.");
                        }

                        Monitor.Wait(RequestGate, TimeSpan.FromMilliseconds(250));
                        continue;
                    }
                }

                LibraryFileMutationLease mutationReservation = TryBeginMutation(operation, showMessage: false);
                if (mutationReservation != null)
                {
                    return mutationReservation;
                }
            }
        }

        internal void WaitForLr2SongDbSyncPreparationAvailability(string operation)
        {
            lock (RequestGate)
            {
                while (Running
                    || StatusPublicationInProgress
                    || PreparationInProgress
                    || MutationInProgress > 0)
                {
                    if (runtime.IsShutdownRequested)
                    {
                        throw new InvalidOperationException("LR2 synchronization is unavailable during shutdown.");
                    }

                    Monitor.Wait(RequestGate, TimeSpan.FromMilliseconds(250));
                }
            }
        }

        internal void ThrowIfLr2SongDbSyncMutationBlocked(
            string operation,
            LibraryFileMutationCapability mutationCapability)
        {
            RequireMutationCapability(mutationCapability);
        }

        private bool IsMutationLeaseActive(long leaseId)
        {
            lock (RequestGate)
            {
                return MutationInProgress == 1 && activeMutationLeaseId == leaseId;
            }
        }

        private void EndMutation(long leaseId)
        {
            lock (RequestGate)
            {
                if (MutationInProgress == 1 && activeMutationLeaseId == leaseId)
                {
                    MutationInProgress = 0;
                    activeMutationLeaseId = 0;
                }
                Monitor.PulseAll(RequestGate);
            }
        }

        private void EndPreparation(long leaseId)
        {
            lock (RequestGate)
            {
                if (MutationInProgress == 1 && activeMutationLeaseId == leaseId)
                {
                    MutationInProgress = 0;
                    activeMutationLeaseId = 0;
                    PreparationInProgress = false;
                }
                Monitor.PulseAll(RequestGate);
            }
        }

        private void RequireMutationCapability(LibraryFileMutationCapability mutationCapability)
        {
            if (mutationCapability == null)
            {
                throw new ArgumentNullException(nameof(mutationCapability));
            }
            mutationCapability.Validate(this);
        }


        private Lr2SongDbSyncRuntimeSnapshot CreateRuntimeSnapshotUnsafe()
        {
            return new Lr2SongDbSyncRuntimeSnapshot
            {
                Running = Running,
                Preparing = PreparationInProgress,
                MutationInProgress = MutationInProgress,
                RequestedVersion = ObservableRequestedVersion,
                Stage = ObservableStage ?? string.Empty,
                ProcessedCount = ObservableProcessedCount,
                TotalCount = ObservableTotalCount,
                StageProcessedCount = ObservableStageProcessedCount,
                StageTotalCount = ObservableStageTotalCount
            };
        }
    }
}
