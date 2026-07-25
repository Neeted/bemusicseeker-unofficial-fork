using System;
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
using static BeMusicSeeker.Models.BmsLibraryInternal.Lr2SongDbSyncInputSurfaceHelper;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    /// <summary>
    /// Owns the LR2 synchronization request lifecycle and its cross-route state.
    /// The facade exposes only the application-facing observable projection.
    /// </summary>
    internal sealed class Lr2SynchronizationOwner : ILr2ChartInfoTrustPort, ILr2PlaylistFolderSynchronizationPort
    {
        private readonly BMSLibrary library;

        private readonly object chartInfoTrustGate = new();

        private readonly object mutationSequenceGate = new();

        private ChartInfoCompletedLr2SongDbSyncTrustSnapshot chartInfoTrustSnapshot;

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

        internal bool StatusPublicationInProgress { get; set; }

        private object preparationMutationReservationToken;

        private readonly AsyncLocal<object> preparationMutationScopeToken = new();

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

        internal BmsLibraryOptionsSnapshot CurrentOptionsSnapshot =>
            library.CurrentOptionsSnapshot;

        CustomFolderOutputPhysicalSurface ILr2PlaylistFolderSynchronizationPort.GetCurrentAppManagedCustomFolderOutputPhysicalSurface() =>
            GetCurrentAppManagedCustomFolderOutputPhysicalSurface();

        Lr2FolderFileDbSyncResult ILr2PlaylistFolderSynchronizationPort.SyncPlaylistLr2FolderFileRows(
            string operation,
            Lr2FolderFileDbSyncRequest request) =>
            SyncPlaylistLr2FolderFileRows(operation, request);

        internal Lr2SongDbSyncAppManagedOutputScope CreateLr2SongDbSyncAppManagedOutputScope()
        {
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool isComplete = true;
            try
            {
                using LR2SongDBExtended songDb = library.dbGateway.OpenSongDbReadOnly();
                string playlistTableName = SQLiteTable<LR2SongDBExtended.playlist>.GetTableName();
                long playlistTableExists = songDb.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = ?;",
                    playlistTableName);
                if (playlistTableExists == 0)
                {
                    return new Lr2SongDbSyncAppManagedOutputScope([], [], [], isComplete: true);
                }

                BmsLibraryOptionsSnapshot options = library.CurrentOptionsSnapshot;
                foreach (BMSTable table in songDb.Table<BMSTable>())
                {
                    string outputDirectory = ResolveManagedPlaylistOutputDirectory(table, options);
                    if (!string.IsNullOrWhiteSpace(outputDirectory))
                    {
                        directories.Add(outputDirectory);
                    }
                }
            }
            catch (Exception ex)
            {
                isComplete = false;
                BMSLibrary.LogInstallPerformanceWarn("lr2folder_app_managed_output_scope failed"
                    + " exception=" + ex.GetType().Name
                    + " message=" + BMSLibrary.GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
            }

            return new Lr2SongDbSyncAppManagedOutputScope(
                [.. directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
                [],
                [],
                isComplete);
        }

        internal Lr2SongDbSyncPreparedDataSurface SyncLr2BuiltinCustomFolderRows(string reason)
        {
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
            SyncLr2FolderFileRows(options, request, reason, "lr2_builtin_folder_scoped_sync");
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
            using (IDisposable mutationScope = BeginMutationWhenAvailable(
                "lr2folder_settings_output_base_sync"))
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
                    List<string> roots = library.getBMSDirectories();
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
                        scopeReadLr2FolderRowsOnly: true);
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
                using IDisposable mutationSequence = EnterLr2MutationSequence();
                Lr2SongDbSyncRequestCoordinator.Queue(
                    this,
                    reason,
                    force: true,
                    prepareGeneratedData: null,
                    allowIncompleteToQueue: true);
            }
        }

        private static string ResolveManagedPlaylistOutputDirectory(BMSTable table, BmsLibraryOptionsSnapshot options)
        {
            if (table == null)
            {
                return null;
            }

            string outputDir;
            try
            {
                outputDir = table.Output_dir;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NullReferenceException)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(outputDir))
            {
                return null;
            }

            try
            {
                return BMSLibrary.SafeFullPathOrOriginal(BMSPlaylist.GetCustomFolderOutputDirectory(
                    table,
                    options.LR2CustomFolderOutputBaseDir,
                    options.LR2CustomFolderOutputBaseDirRootType,
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories(options.LR2CustomFolderAdditionalOutputBaseDirs)));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }
        }

        internal Lr2BuiltinCustomFolderSettings CreateCurrentLr2BuiltinCustomFolderSettings(DateTime nowUtc)
        {
            LR2Config config = library.CreateCurrentLr2ConfigOrNull();
            using (library.rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                return Lr2BuiltinCustomFolderSettings.CreateFromAddDates(
                    config,
                    (library._BMSFiles ?? [])
                        .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path))
                        .Select(file => file.adddate),
                    nowUtc);
            }
        }

        internal List<string> CreateLr2SongDbSyncBuiltinFolderSourceDirectories(BmsLibraryOptionsSnapshot options = null)
        {
            options ??= library.CurrentOptionsSnapshot;
            return Lr2FolderFileDiscoveryService.CreateBuiltinFolderSourceDirectories(options.LR2RootPath);
        }

        internal List<string> CreateLr2SongDbSyncLr2FolderPruneDirectories(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> builtinSourceDirectories,
            bool includeAppManagedOutputDirectories = true,
            BmsLibraryOptionsSnapshot options = null)
        {
            options ??= library.CurrentOptionsSnapshot;
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
                library.everythingNative,
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
                library.everythingNative);
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

            return CreateLr2SongDbSyncTextMetadataCandidates(rootDirectories, targetDirectories, library.everythingNative);
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
            bool allowPrune = true,
            IReadOnlyCollection<string> pruneExcludedDirectories = null,
            IReadOnlyCollection<string> pruneExcludedPaths = null,
            bool scopeReadLr2FolderRowsOnly = false,
            bool updateParentDirectoryRowsForPreservedItems = true)
        {
            using IDisposable mutationSequence = EnterLr2MutationSequence();
            using IDisposable mutationReservation = TryBeginMutation(
                string.IsNullOrWhiteSpace(logName) ? "lr2_folder_file_sync" : logName,
                showMessage: false);
            if (mutationReservation == null)
            {
                throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
            }
            var stopwatch = Stopwatch.StartNew();
            try
            {
                PrepareLr2FolderParentDirectoryEntrySurface(request);
                using LR2SongDBExtended songDb = library.dbGateway.OpenSongDb();
                IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath =
                    Lr2SongDbSyncService.CreateExistingLr2FolderRowMap(songDb, request);
                Lr2SongDbSyncService.Lr2FolderFileSyncItemsResult syncItems =
                    Lr2SongDbSyncService.CreateLr2FolderFileSyncItems(
                        request.Lr2FolderFilePaths,
                        request,
                        request.Lr2FolderFileEntries,
                        path => existingRowsByPath.TryGetValue(path, out LR2SongDB.folder row) ? row : null);
                IReadOnlyCollection<Lr2FolderFileSyncItem> parentDirectorySyncItems = updateParentDirectoryRowsForPreservedItems
                    ? syncItems.Items
                    : [.. syncItems.Items.Where(item => item != null && !item.PreserveExistingRowOnly)];
                Lr2FolderDirectoryMetadataSnapshot parentDirectoryMetadata =
                    Lr2SongDbSyncService.CreateLr2FolderParentDirectoryMetadataSnapshot(parentDirectorySyncItems, request);
                bool effectiveAllowPrune = allowPrune
                    && request.Lr2FolderFileDiscoveryComplete
                    && !syncItems.HasReadFailures;
                string savepoint = songDb.SaveTransactionPoint();
                Lr2FolderFileDbSyncResult syncResult;
                try
                {
                    syncResult = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
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
                    },
                    commitTransaction: false);
                    songDb.Commit();
                }
                catch
                {
                    try
                    {
                        songDb.RollbackTo(savepoint);
                    }
                    catch (Exception rollbackException)
                    {
                        try
                        {
                            BMSLibrary.LogInstallPerformanceWarn(
                                logName + " rollback_failed"
                                + " exception=" + rollbackException.GetType().Name
                                + " message=" + BMSLibrary.GetDisplayedExceptionMessage(rollbackException).Replace(Environment.NewLine, " | "));
                        }
                        catch
                        {
                            // Rollback diagnostics must never replace the original LR2 folder exception.
                        }
                    }
                    throw;
                }

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
                return null;
            }
        }

        internal bool ShouldProtectExistingBmsRowsFromLr2SongDbSyncMigration(BmsLibraryOptionsSnapshot options)
        {
            if (options?.OperationModeLR2DB != true)
            {
                return false;
            }

            string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
            using LR2SongDBExtended songDb = library.dbGateway.OpenSongDb();
            Lr2SongDbSyncStatusSnapshot status = Lr2SongDbSyncStatusService.Evaluate(
                songDb,
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
                    library.CurrentOptionsSnapshot,
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
                using LR2SongDBExtended songDb = library.dbGateway.OpenSongDb();
                Lr2SongDbSyncStatusSnapshot status = Lr2SongDbSyncStatusService.MarkIncomplete(
                    songDb,
                    signature,
                    runId: string.IsNullOrWhiteSpace(runId) ? "runtime_write" : runId,
                    processedCursor: null,
                    totalCount: null,
                    stage,
                    detail,
                    nowUtc: DateTime.UtcNow);
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

        internal void PublishCatalogWriteFailureFact(CatalogWriteFailureFact failureFact)
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
                    library.CurrentOptionsSnapshot,
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
            Lr2FolderFileDbSyncRequest request)
        {
            if (request == null)
            {
                return null;
            }

            string resolvedOperation = string.IsNullOrWhiteSpace(operation)
                ? "playlist_lr2folder_sync"
                : operation;
            using IDisposable mutationSequence = EnterLr2MutationSequence();
            using IDisposable mutationReservation = TryBeginMutation(
                resolvedOperation,
                showMessage: false);
            if (mutationReservation == null)
            {
                throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
            }

            try
            {
                using (library.catalogMutationOwner.EnterMaintenanceWriteGuard())
                using (LR2SongDBExtended songDb = library.dbGateway.OpenSongDb())
                {
                    string savepoint = songDb.SaveTransactionPoint();
                    try
                    {
                        Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(
                            songDb,
                            request,
                            commitTransaction: false);
                        songDb.Commit();
                        return result;
                    }
                    catch
                    {
                        try
                        {
                            songDb.RollbackTo(savepoint);
                        }
                        catch (Exception rollbackException)
                        {
                            try
                            {
                                BMSLibrary.LogInstallPerformanceWarn(
                                    resolvedOperation + " rollback_failed"
                                    + " exception=" + rollbackException.GetType().Name
                                    + " message=" + BMSLibrary.GetDisplayedExceptionMessage(rollbackException).Replace(Environment.NewLine, " | "));
                            }
                            catch
                            {
                                // Rollback diagnostics must never replace the original playlist exception.
                            }
                        }
                        throw;
                    }
                }
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
            string reason)
        {
            if (receipt?.HasBmsMutation != true)
            {
                return;
            }

            BmsLibraryOptionsSnapshot options = library.CurrentOptionsSnapshot;
            if (options?.OperationModeLR2DB != true)
            {
                return;
            }

            using IDisposable mutationSequence = EnterLr2MutationSequence();
            using IDisposable mutationReservation = TryBeginMutation(
                "lr2_normal_folder_catalog_mutation",
                showMessage: false);
            Exception failure = mutationReservation == null
                ? new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning)
                : null;
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

                    roots = library.getBMSDirectories();
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
                            using (library.catalogMutationOwner.EnterMaintenanceWriteGuard())
                            using (LR2SongDBExtended songDb = library.dbGateway.OpenSongDb())
                            {
                                string savepoint = songDb.SaveTransactionPoint();
                                try
                                {
                                    Lr2NormalFolderDbSyncResult syncResult = Lr2NormalFolderDbSyncService.Sync(
                                        songDb,
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
                                        },
                                        commitTransaction: false);
                                    songDb.Commit();
                                    stopwatch.Stop();
                                    BMSLibrary.LogInstallPerformance("lr2_normal_folder_catalog_sync done"
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
                                        + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                                }
                                catch
                                {
                                    try
                                    {
                                        songDb.RollbackTo(savepoint);
                                    }
                                    catch (Exception rollbackException)
                                    {
                                        try
                                        {
                                            BMSLibrary.LogInstallPerformanceWarn(
                                                "lr2_normal_folder_catalog_sync rollback_failed"
                                                + " exception=" + rollbackException.GetType().Name
                                                + " message=" + BMSLibrary.GetDisplayedExceptionMessage(rollbackException).Replace(Environment.NewLine, " | "));
                                        }
                                        catch
                                        {
                                            // Rollback diagnostics must never replace the original folder write exception.
                                        }
                                    }
                                    throw;
                                }
                            }
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
            MarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
                options,
                stage: "lr2_normal_folder_mutation_sync_failed",
                detail: "lr2_normal_folder_mutation_sync_failed: " + BMSLibrary.GetDisplayedExceptionMessage(failure),
                logReason: "lr2_normal_folder_mutation_sync_failed");
            try
            {
                BMSLibrary.LogInstallPerformanceWarn("lr2_normal_folder_catalog_sync failed"
                    + " reason=" + (reason ?? "unknown")
                    + " paths=" + syncInput.ChartPaths.Count
                    + " pruneScopes=" + syncInput.PruneScopeDirectories.Count
                    + " exactPrunes=" + syncInput.PruneExactDirectories.Count
                    + " roots=" + roots.Count
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " exception=" + failure.GetType().Name
                    + " message=" + BMSLibrary.GetDisplayedExceptionMessage(failure).Replace(Environment.NewLine, " | "));
            }
            catch
            {
                // Failure diagnostics must never replace the catalog mutation result.
            }
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

        ChartInfoCompletedLr2SongDbSyncTrustSnapshot ILr2ChartInfoTrustPort.GetCurrent()
        {
            ChartInfoCompletedLr2SongDbSyncTrustSnapshot snapshot;
            lock (chartInfoTrustGate)
            {
                snapshot = chartInfoTrustSnapshot;
            }
            ChartInfoOwnerVersionSnapshot currentVersion = library.catalogChartInfoOwner.CaptureOwnerVersionSnapshot();
            return snapshot?.IsCurrent(currentVersion) == true ? snapshot : null;
        }

        void ILr2ChartInfoTrustPort.Clear(string reason) => ClearChartInfoTrust(reason);

        internal void CaptureChartInfoCompletedLr2SongDbSyncTrustFromFileDiff(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult fileCheckResult,
            string reason)
        {
            ChartInfoLr2TrustInput trustInput = ChartInfoLr2TrustInput.Create(options, fileCheckResult);
            if (trustInput?.CanTrust != true)
            {
                ClearChartInfoTrust("file_diff_changed_" + (reason ?? "unknown"));
                return;
            }
            ChartInfoOwnerVersionSnapshot version = library.catalogChartInfoOwner.CaptureOwnerVersionSnapshot();
            var snapshot = new ChartInfoCompletedLr2SongDbSyncTrustSnapshot
            {
                OwnedCollectionVersion = version.OwnedCollectionVersion,
                BmsRowsVersion = version.BmsRowsVersion,
                BmsonRowsVersion = version.BmsonRowsVersion,
                BmsOwnerCount = version.BmsOwnerCount,
                BmsonOwnerCount = version.BmsonOwnerCount,
                Reason = reason ?? "unknown"
            };
            lock (chartInfoTrustGate)
            {
                chartInfoTrustSnapshot = snapshot;
            }
            BMSLibrary.LogInstallPerformance("chart_info_song_db_sync_trust captured"
                + " reason=" + (reason ?? "unknown")
                + " ownerCount=" + snapshot.OwnerCount
                + " bmsOwners=" + snapshot.BmsOwnerCount
                + " bmsonOwners=" + snapshot.BmsonOwnerCount
                + " ownedCollectionVersion=" + snapshot.OwnedCollectionVersion
                + " bmsRowsVersion=" + snapshot.BmsRowsVersion
                + " bmsonRowsVersion=" + snapshot.BmsonRowsVersion);
        }

        private void ClearChartInfoTrust(string reason)
        {
            bool cleared = false;
            lock (chartInfoTrustGate)
            {
                if (chartInfoTrustSnapshot != null)
                {
                    chartInfoTrustSnapshot = null;
                    cleared = true;
                }
            }
            if (cleared)
            {
                BMSLibrary.LogInstallPerformance("chart_info_song_db_sync_trust cleared reason=" + (reason ?? "unknown"));
            }
        }

        internal void CaptureLr2SongDbSyncFileDiffFreshnessSnapshot(
            BmsLibraryOptionsSnapshot options,
            SongTableFileCheckResult fileCheckResult,
            string reason)
        {
            if (options?.OperationModeLR2DB != true
                || fileCheckResult == null)
            {
                ClearLr2SongDbSyncFileDiffFreshnessSnapshot("file_diff_unavailable_" + (reason ?? "unknown"));
                return;
            }

            ChartInfoOwnerVersionSnapshot version = library.catalogChartInfoOwner.CaptureOwnerVersionSnapshot();
            Lr2SongDbSyncScanSurfaceSnapshot scanSurface;
            lock (ScanSurfaceGate)
            {
                scanSurface = ScanSurfaceSnapshot;
            }
            string missReason = null;
            if (scanSurface == null
                || scanSurface.OwnedCollectionVersion != version.OwnedCollectionVersion
                || scanSurface.BmsRowsVersion != version.BmsRowsVersion
                || scanSurface.BmsonRowsVersion != version.BmsonRowsVersion)
            {
                missReason = "scan_surface_not_current";
            }
            else if (!fileCheckResult.HasDbDiff)
            {
                missReason = "no_db_diff";
            }
            else if (version.BmsOwnerCount <= 0)
            {
                missReason = "no_bms_rows";
            }

            bool canVerifyFullSongRows = fileCheckResult.BmsAddedTargetCount >= version.BmsOwnerCount
                && fileCheckResult.InlineMaintenanceBmsCount >= version.BmsOwnerCount;
            bool hasTransientNewInsertSkipRows = fileCheckResult.NewlyInsertedBmsPaths.Count > 0;
            if (missReason == null && !canVerifyFullSongRows && !hasTransientNewInsertSkipRows)
            {
                missReason = "no_fresh_song_rows";
            }
            else if (missReason == null && fileCheckResult.InlineMaintenanceFailedCount > 0)
            {
                missReason = "inline_maintenance_failed";
            }
            else if (missReason == null && fileCheckResult.BmsMovedHashRelinkAmbiguousCount > 0)
            {
                missReason = "ambiguous_hash_relink";
            }
            else if (missReason == null
                && (fileCheckResult.Lr2NormalFolderSyncFailed
                || fileCheckResult.Lr2NormalFolderSkippedMissingMetadataCount > 0
                || fileCheckResult.Lr2NormalFolderInfoReadFailureCount > 0))
            {
                missReason = "normal_folder_sync_unapplied";
            }

            if (missReason != null)
            {
                ClearLr2SongDbSyncFileDiffFreshnessSnapshot(missReason + "_" + (reason ?? "unknown"));
                BMSLibrary.LogInstallPerformance("lr2_song_db_sync_file_diff_freshness skipped"
                    + " reason=" + (reason ?? "unknown")
                    + " skipReason=" + missReason
                    + " bmsOwners=" + version.BmsOwnerCount
                    + " bmsonOwners=" + version.BmsonOwnerCount
                    + " bmsTargets=" + fileCheckResult.BmsAddedTargetCount
                    + " newInsertSkipRows=" + fileCheckResult.NewlyInsertedBmsPaths.Count
                    + " inlineMaintenanceBms=" + fileCheckResult.InlineMaintenanceBmsCount
                    + " inlineMaintenanceFailed=" + fileCheckResult.InlineMaintenanceFailedCount
                    + " scanSurfaceGeneration=" + (scanSurface?.Generation ?? 0));
                return;
            }

            var snapshot = new Lr2SongDbSyncFileDiffFreshnessSnapshot(
                reason ?? "unknown",
                scanSurface.Generation,
                version.OwnedCollectionVersion,
                version.BmsRowsVersion,
                version.BmsonRowsVersion,
                version.BmsOwnerCount,
                version.BmsonOwnerCount,
                fileCheckResult.BmsAddedTargetCount,
                fileCheckResult.BmsDeletedTargetCount,
                fileCheckResult.BmsDateOnlyUpdateCount,
                fileCheckResult.BmsTextOnlyUpdateCount,
                fileCheckResult.BmsMovedHashRelinkCount,
                fileCheckResult.BmsMovedHashRelinkAmbiguousCount,
                fileCheckResult.InlineChartInfoTargetCount,
                fileCheckResult.InlineChartInfoSuccessCount,
                fileCheckResult.InlineChartInfoCurrentSkippedCount,
                fileCheckResult.InlineChartInfoParseFailedCount,
                fileCheckResult.InlineMaintenanceTargetCount,
                fileCheckResult.InlineMaintenanceBmsCount,
                fileCheckResult.InlineMaintenanceBmsonCount,
                fileCheckResult.InlineMaintenanceFailedCount,
                fileCheckResult.NewlyInsertedBmsPaths);
            lock (FileDiffFreshnessGate)
            {
                FileDiffFreshnessSnapshot = snapshot;
            }
            BMSLibrary.LogInstallPerformance("lr2_song_db_sync_file_diff_freshness captured"
                + " reason=" + snapshot.Reason
                + " scanSurfaceGeneration=" + snapshot.ScanSurfaceGeneration
                + " bmsOwners=" + snapshot.BmsOwnerCount
                + " bmsonOwners=" + snapshot.BmsonOwnerCount
                + " bmsTargets=" + snapshot.BmsTargetCount
                + " newInsertSkipRows=" + snapshot.TransientSongRowSkipPaths.Count
                + " bmsDeleted=" + snapshot.BmsDeletedCount
                + " inlineChartInfoTargets=" + snapshot.InlineChartInfoTargetCount
                + " inlineMaintenanceBms=" + snapshot.InlineMaintenanceBmsCount
                + " inlineMaintenanceBmson=" + snapshot.InlineMaintenanceBmsonCount
                + " ownedCollectionVersion=" + snapshot.OwnedCollectionVersion
                + " bmsRowsVersion=" + snapshot.BmsRowsVersion
                + " bmsonRowsVersion=" + snapshot.BmsonRowsVersion);
        }

        private void ClearLr2SongDbSyncFileDiffFreshnessSnapshot(string reason)
        {
            bool cleared;
            lock (FileDiffFreshnessGate)
            {
                cleared = FileDiffFreshnessSnapshot != null;
                FileDiffFreshnessSnapshot = null;
            }
            if (cleared)
            {
                BMSLibrary.LogInstallPerformance("lr2_song_db_sync_file_diff_freshness cleared reason=" + (reason ?? "unknown"));
            }
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
                    library.OwnedChartCollectionVersion,
                    library.catalogStorageRowsOwner.BmsRowsVersion,
                    library.catalogStorageRowsOwner.BmsonRowsVersion);
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

        internal bool IsShutdownRequested => library.IsShutdownRequested;

        internal Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler =>
            library.StartupBackgroundTaskScheduler;

        internal int GetLr2SongDbSyncMutationInProgress()
        {
            lock (RequestGate)
            {
                return MutationInProgress;
            }
        }

        internal Lr2SongDbSyncRuntimeSnapshot GetLr2SongDbSyncRuntimeSnapshot()
        {
            lock (RequestGate)
            {
                return CreateRuntimeSnapshotUnsafe();
            }
        }

        internal bool TryReserveLr2SongDbSyncPreparation(
            bool requiresPreparation,
            out Lr2SongDbSyncRuntimeSnapshot blockingSnapshot)
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
                    PreparationInProgress = true;
                    preparationMutationReservationToken = new object();
                }
                blockingSnapshot = CreateRuntimeSnapshotUnsafe();
                return true;
            }
        }

        internal IDisposable EnterLr2SongDbSyncPreparationMutationScope() =>
            EnterPreparationMutationScope();

        internal Lr2SongDbSyncStatusSnapshot GetLr2SongDbSyncStatusSnapshot()
        {
            return GetStatusSnapshot();
        }

        internal LR2SongDBExtended OpenSongDb() => library.dbGateway.OpenSongDb();

        internal IDisposable EnterLr2SongDbSyncCatalogMutationLease() =>
            library.catalogMutationOwner.EnterMaintenanceWriteGuard();

        internal void PublishLr2SongDbSyncStatus(Lr2SongDbSyncStatusSnapshot status)
        {
            PublishStatus(status);
        }

        internal bool TrySkipForShutdown(string operation, string reason) =>
            library.TrySkipForShutdown(operation, reason);

        internal void ClearLr2SongDbSyncPreparedDataSurface(string reason) =>
            ClearPreparedDataSurface(reason);

        internal void ClearLr2SongDbSyncPrepareReservation()
        {
            ClearPreparation();
        }

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

        internal void RunLr2SongDbSync(string reason, string signature, int requestVersion) =>
            Lr2SongDbSyncRequestCoordinator.Run(this, reason, signature, requestVersion);

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
                library.everythingNative);

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
            List<string> chartPaths;
            List<BMSFile> songRows;
            int ownedCollectionVersion;
            StorageRowsVersionSnapshot storageRowsVersion;
            using (library.rwlockBMSFilesInitializedAll.GetReaderGuard())
            {
                var chartPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                chartPaths = [];
                songRows = [];
                foreach (BMSFile file in library._BMSFiles ?? [])
                {
                    if (file == null || string.IsNullOrWhiteSpace(file.path))
                    {
                        continue;
                    }
                    songRows.Add(file);
                    if (chartPathSet.Add(file.path))
                    {
                        chartPaths.Add(file.path);
                    }
                }
                ownedCollectionVersion = library.OwnedChartCollectionVersion;
                storageRowsVersion = library.CreateCurrentStorageRowsVersionSnapshotUnsafe();
            }
            return new Lr2SongDbSyncInputRowSnapshot(
                chartPaths,
                songRows,
                ownedCollectionVersion,
                storageRowsVersion.BmsRowsVersion,
                storageRowsVersion.BmsonRowsVersion);
        }

        private Lr2SongDbSyncInputRootSnapshot CreateLr2SongDbSyncInputRootSnapshot()
        {
            DateTime capturedAtUtc = DateTime.UtcNow;
            List<string> rootDirectories = library.getBMSDirectories();
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
                    library.CreateCurrentLr2ConfigOrNull(),
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
            library.chartInfoBuildService.CurrentParseTimeout;

        internal void ReportStartupBackgroundTask(
            string name,
            string status,
            long elapsedMs,
            bool failed,
            string detail) =>
            library.ReportStartupBackgroundTask(name, status, elapsedMs, failed, detail);

        internal void PublishLr2SongDbSyncPreflightStage(string stage, string reason, string runId) =>
            library.PublishLr2SongDbSyncPreflightStage(stage, reason, runId);

        internal void LogLr2SongDbSyncPreflightStageDone(string stage, string reason, string runId, long elapsedMs) =>
            library.LogLr2SongDbSyncPreflightStageDone(stage, reason, runId, elapsedMs);

        internal void EnsureLr2SongDbSyncChartInfoIndexHydrated(string reason) =>
            library.EnsureLr2SongDbSyncChartInfoIndexHydrated(reason);

        internal Dictionary<string, BMSFile> CreateLr2SongDbSyncCompatibilityProjectionIndex() =>
            library.CreateLr2SongDbSyncCompatibilityProjectionIndex();

        internal Func<BMSFile, LR2SongDBExtended.chart_info> CreateLr2SongDbSyncChartInfoResolverSnapshot() =>
            library.CreateLr2SongDbSyncChartInfoResolverSnapshot();

        internal HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason) =>
            library.CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(reason);

        internal void UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<LR2SongDBExtended.chart_info> rows) =>
            library.UpsertChartInfoIndexRows(rows, "lr2_song_db_sync_inline_chart_info", dispatchPresentation: false);

        internal CatalogChartInfoWriteReceipt ApplyLr2SongDbSyncChartInfoWrite(
            LR2SongDBExtended songDb,
            CatalogChartInfoWriteRequest request) =>
            library.catalogMutationOwner.ApplyChartInfoWriteInTransaction(songDb, request);

        internal void UpdateLr2SongDbSyncProgress(Lr2SongDbSyncProgress progress) =>
            UpdateProgress(progress);

        internal int ApplyLr2SongDbSyncCompatibilityProjection(
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

        internal void DispatchWarningPresentationChanged(string reason) =>
            library.DispatchWarningPresentationChanged(reason);

        internal void MarkLr2SongDbSyncFailedStatus(string signature, string runId, Exception ex)
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

        internal void LogInstallPerformance(string message) =>
            BMSLibrary.LogInstallPerformance(message);

        internal void MarkLr2SongDbSyncPreflightCancelled(
            string signature,
            string runId,
            string stage)
        {
            try
            {
                using LR2SongDBExtended songDb = library.dbGateway.OpenSongDb();
                Lr2SongDbSyncStatusService.MarkCancelled(
                    songDb,
                    signature,
                    runId,
                    processedCursor: 0,
                    totalCount: 0,
                    stage,
                    DateTime.UtcNow);
            }
            catch
            {
                // Runtime cancellation state is still reported by the caller's catch block.
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

        private void SetObservableRunning(bool value)
        {
            if (ObservableRunning != value)
            {
                ObservableRunning = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncRunning);
            }
        }

        private void SetObservableRequestedVersion(int value)
        {
            if (ObservableRequestedVersion != value)
            {
                ObservableRequestedVersion = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncRequestedVersion);
            }
        }

        private void SetObservableCompletedVersion(int value)
        {
            if (ObservableCompletedVersion != value)
            {
                ObservableCompletedVersion = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncCompletedVersion);
            }
        }

        private void SetObservableFailedVersion(int value)
        {
            if (ObservableFailedVersion != value)
            {
                ObservableFailedVersion = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncFailedVersion);
            }
        }

        private void SetObservableTotalCount(int value)
        {
            if (ObservableTotalCount != value)
            {
                ObservableTotalCount = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncTotalCount);
            }
        }

        private void SetObservableProcessedCount(int value)
        {
            if (ObservableProcessedCount != value)
            {
                ObservableProcessedCount = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncProcessedCount);
            }
        }

        private void SetObservableStage(string value)
        {
            value ??= string.Empty;
            if (!string.Equals(ObservableStage, value, StringComparison.Ordinal))
            {
                ObservableStage = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncStage);
            }
        }

        private void SetObservableStageProcessedCount(int value)
        {
            if (ObservableStageProcessedCount != value)
            {
                ObservableStageProcessedCount = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncStageProcessedCount);
            }
        }

        private void SetObservableStageTotalCount(int value)
        {
            if (ObservableStageTotalCount != value)
            {
                ObservableStageTotalCount = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncStageTotalCount);
            }
        }

        private void SetObservableFailureMessage(string value)
        {
            value ??= string.Empty;
            if (!string.Equals(ObservableFailureMessage, value, StringComparison.Ordinal))
            {
                ObservableFailureMessage = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncFailureMessage);
            }
        }

        private void SetObservableStatusVersion(int value)
        {
            if (ObservableStatusVersion != value)
            {
                ObservableStatusVersion = value;
                library.RaisePropertyChanged(() => library.Lr2SongDbSyncStatusVersion);
            }
        }

        internal void ClearPreparation()
        {
            lock (RequestGate)
            {
                PreparationInProgress = false;
                preparationMutationReservationToken = null;
                Monitor.PulseAll(RequestGate);
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
            BmsLibraryOptionsSnapshot currentSettings = library.CurrentOptionsSnapshot;
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
            if (snapshot.OwnedCollectionVersion != rowSnapshot.OwnedCollectionVersion)
            {
                missReason = "owned_collection_version";
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
                && snapshot.OwnedCollectionVersion == input.OwnedChartCollectionVersion
                && snapshot.BmsRowsVersion == input.BmsRowsVersion
                && snapshot.BmsonRowsVersion == input.BmsonRowsVersion
                && BMSLibrary.ArePathSetsEqual(snapshot.RootDirectories, input.RootDirectories)
                && BMSLibrary.ArePathSetsEqual(snapshot.Lr2FolderDiscoveryDirectories, input.Lr2FolderDiscoveryDirectories);
        }

        private List<string> CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(
            IEnumerable<string> rootDirectories,
            BmsLibraryOptionsSnapshot options = null)
        {
            options ??= library.CurrentOptionsSnapshot;
            return Lr2FolderFileDiscoveryService.CreateDiscoveryDirectories(
                rootDirectories,
                CreateNormalCustomFolderOutputBaseDirectories(options),
                options.LR2CustomFolderOutputBaseDirRootType,
                CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options));
        }

        internal bool IsLr2SongDbSyncInputCurrent(Lr2SongDbSyncInput input)
        {
            if (input == null
                || library.OwnedChartCollectionVersion != input.OwnedChartCollectionVersion
                || library.catalogStorageRowsOwner.BmsRowsVersion != input.BmsRowsVersion
                || library.catalogStorageRowsOwner.BmsonRowsVersion != input.BmsonRowsVersion)
            {
                return false;
            }

            List<string> roots = library.getBMSDirectories();
            if (!BMSLibrary.ArePathSetsEqual(input.RootDirectories, roots))
            {
                return false;
            }
            if (!IsCurrentLr2SongDbSyncScanSurface(input))
            {
                return false;
            }

            BmsLibraryOptionsSnapshot options = library.CurrentOptionsSnapshot;
            List<string> lr2FolderDiscoveryDirectories = CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(roots, options);
            if (!BMSLibrary.ArePathSetsEqual(input.Lr2FolderDiscoveryDirectories, lr2FolderDiscoveryDirectories))
            {
                return false;
            }
            Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope();
            if (!appManagedOutputScope.IsComplete
                || !BMSLibrary.ArePathSetsEqual(input.Lr2FolderPruneExcludedDirectories, appManagedOutputScope.Directories))
            {
                return false;
            }
            Lr2BuiltinCustomFolderSettings builtinCustomFolderSettings = Lr2BuiltinCustomFolderSettings.Create(
                library.CreateCurrentLr2ConfigOrNull(),
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

        internal ISet<string> GetLr2SongDbSyncTransientSongRowsSkipPaths(
            Lr2SongDbSyncInput input,
            string reason)
        {
            if (!IsAutomaticLr2SongDbSyncFileDiffFollowupReason(reason)
                || !IsLr2SongDbSyncInputCurrent(input))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            Lr2SongDbSyncFileDiffFreshnessSnapshot snapshot;
            lock (FileDiffFreshnessGate)
            {
                snapshot = FileDiffFreshnessSnapshot;
            }
            if (snapshot == null
                || input == null
                || snapshot.ScanSurfaceGeneration != input.ScanSurfaceGeneration
                || snapshot.OwnedCollectionVersion != input.OwnedChartCollectionVersion
                || snapshot.BmsRowsVersion != input.BmsRowsVersion
                || snapshot.BmsonRowsVersion != input.BmsonRowsVersion
                || snapshot.InlineMaintenanceFailedCount > 0)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(snapshot.TransientSongRowSkipPaths, StringComparer.OrdinalIgnoreCase);
        }

        internal Lr2SongDbSyncSongRowsSkipVerificationResult VerifyLr2SongDbSyncSongRowsFreshFromFileDiff(
            LR2SongDBExtended songDb,
            IReadOnlyList<BMSFile> songRows,
            Lr2SongDbSyncInput input,
            string reason)
        {
            int targetRows = songRows?.Count ?? 0;
            if (!IsAutomaticLr2SongDbSyncFileDiffFollowupReason(reason))
            {
                return CreateLr2SongDbSyncSongRowsSkipResult(false, "not_automatic_file_diff_followup", targetRows);
            }
            if (songDb == null)
            {
                return CreateLr2SongDbSyncSongRowsSkipResult(false, "song_db_unavailable", targetRows);
            }
            if (!IsLr2SongDbSyncInputCurrent(input))
            {
                return CreateLr2SongDbSyncSongRowsSkipResult(false, "input_not_current", targetRows);
            }

            Lr2SongDbSyncFileDiffFreshnessSnapshot snapshot;
            lock (FileDiffFreshnessGate)
            {
                snapshot = FileDiffFreshnessSnapshot;
            }
            if (snapshot == null)
            {
                return CreateLr2SongDbSyncSongRowsSkipResult(false, "missing_file_diff_freshness_snapshot", targetRows);
            }
            if (input == null
                || snapshot.ScanSurfaceGeneration != input.ScanSurfaceGeneration
                || snapshot.OwnedCollectionVersion != input.OwnedChartCollectionVersion
                || snapshot.BmsRowsVersion != input.BmsRowsVersion
                || snapshot.BmsonRowsVersion != input.BmsonRowsVersion)
            {
                return CreateLr2SongDbSyncSongRowsSkipResult(false, "file_diff_freshness_not_current", targetRows);
            }
            if (snapshot.BmsOwnerCount != targetRows)
            {
                return CreateLr2SongDbSyncSongRowsSkipResult(false, "song_row_count_mismatch", targetRows);
            }
            if (snapshot.BmsTargetCount < snapshot.BmsOwnerCount)
            {
                return CreateLr2SongDbSyncSongRowsSkipResult(false, "insufficient_bms_target_coverage", targetRows);
            }
            if (snapshot.InlineMaintenanceBmsCount < snapshot.BmsOwnerCount || snapshot.InlineMaintenanceFailedCount > 0)
            {
                return CreateLr2SongDbSyncSongRowsSkipResult(false, "insufficient_maintenance_coverage", targetRows);
            }
            if (snapshot.BmsMovedHashRelinkAmbiguousCount > 0)
            {
                return CreateLr2SongDbSyncSongRowsSkipResult(false, "ambiguous_hash_relink", targetRows);
            }
            if (AreAllLr2SongDbSyncSongRowsFreshNewInserts(snapshot, songRows))
            {
                return new Lr2SongDbSyncSongRowsSkipVerificationResult
                {
                    CanSkip = true,
                    Reason = "file_diff_new_insert_projection_current",
                    TargetRows = targetRows,
                    VerifiedRows = targetRows,
                    MissingRows = 0,
                    MismatchedRows = 0,
                    DuplicatePathRows = 0,
                    DigestCheckedRows = 0,
                    DigestMissingRows = 0,
                    DigestMismatchedRows = 0,
                    ProjectionMs = 0,
                    ExistingReadMs = 0,
                    DigestReadMs = 0,
                    ElapsedMs = 0,
                    DiagnosticSamples = []
                };
            }

            return CreateLr2SongDbSyncSongRowsSkipResult(false, "file_diff_transient_coverage_incomplete", targetRows);
        }

        private static bool AreAllLr2SongDbSyncSongRowsFreshNewInserts(
            Lr2SongDbSyncFileDiffFreshnessSnapshot snapshot,
            IReadOnlyList<BMSFile> songRows)
        {
            int targetRows = songRows?.Count ?? 0;
            if (targetRows <= 0
                || snapshot?.TransientSongRowSkipPaths == null
                || snapshot.TransientSongRowSkipPaths.Count < targetRows)
            {
                return false;
            }

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BMSFile row in songRows)
            {
                if (row == null
                    || string.IsNullOrWhiteSpace(row.path)
                    || !snapshot.TransientSongRowSkipPaths.Contains(row.path)
                    || !seenPaths.Add(row.path))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsAutomaticLr2SongDbSyncFileDiffFollowupReason(string reason)
        {
            return !string.IsNullOrWhiteSpace(reason)
                && reason.StartsWith("post_startup_", StringComparison.OrdinalIgnoreCase);
        }

        private static Lr2SongDbSyncSongRowsSkipVerificationResult CreateLr2SongDbSyncSongRowsSkipResult(
            bool canSkip,
            string reason,
            int targetRows)
        {
            return new Lr2SongDbSyncSongRowsSkipVerificationResult
            {
                CanSkip = canSkip,
                Reason = reason ?? "unknown",
                TargetRows = Math.Max(0, targetRows)
            };
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
                bool preparationScopeOwned = PreparationInProgress
                    && preparationMutationReservationToken != null
                    && ReferenceEquals(preparationMutationScopeToken.Value, preparationMutationReservationToken);
                blocked = Running
                    || StatusPublicationInProgress
                    || (PreparationInProgress && !preparationScopeOwned);
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
                library.ShowOperationDialog(
                    Resources.Warn_Lr2SongDbSyncRunning,
                    Resources.MessageBoxTitle_Warning,
                    MessageBoxButton.OK,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.OK);
            }
            return true;
        }

        internal IDisposable TryBeginMutation(
            string operation,
            bool showMessage = true)
        {
            string stage;
            int processed;
            int total;
            bool preparing;
            lock (RequestGate)
            {
                bool preparationScopeOwned = PreparationInProgress
                    && preparationMutationReservationToken != null
                    && ReferenceEquals(preparationMutationScopeToken.Value, preparationMutationReservationToken);
                if (!Running
                    && !StatusPublicationInProgress
                    && (!PreparationInProgress || preparationScopeOwned))
                {
                    MutationInProgress++;
                    return new MutationScope(this);
                }
                stage = ObservableStage ?? string.Empty;
                processed = ObservableProcessedCount;
                total = ObservableTotalCount;
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

        /// <summary>
        /// Waits for an in-flight LR2 synchronization/preparation to finish, then reserves
        /// the catalog-mutation slot. Settings-driven background work uses this so a valid
        /// request is retried instead of being silently discarded when synchronization is busy.
        /// </summary>
        internal IDisposable BeginMutationWhenAvailable(string operation)
        {
            while (true)
            {
                lock (RequestGate)
                {
                    bool preparationScopeOwned = PreparationInProgress
                        && preparationMutationReservationToken != null
                        && ReferenceEquals(preparationMutationScopeToken.Value, preparationMutationReservationToken);
                    if (Running
                        || StatusPublicationInProgress
                        || (PreparationInProgress && !preparationScopeOwned))
                    {
                        if (library.IsShutdownRequested)
                        {
                            throw new InvalidOperationException("LR2 synchronization is unavailable during shutdown.");
                        }

                        Monitor.Wait(RequestGate, TimeSpan.FromMilliseconds(250));
                        continue;
                    }
                }

                IDisposable mutationSequence = EnterLr2MutationSequence();
                IDisposable mutationReservation = TryBeginMutation(operation, showMessage: false);
                if (mutationReservation != null)
                {
                    return new MutationSequenceAndReservationScope(mutationSequence, mutationReservation);
                }

                mutationSequence.Dispose();
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
                    if (library.IsShutdownRequested)
                    {
                        throw new InvalidOperationException("LR2 synchronization is unavailable during shutdown.");
                    }

                    Monitor.Wait(RequestGate, TimeSpan.FromMilliseconds(250));
                }
            }
        }

        internal IDisposable EnterLr2MutationSequence()
        {
            Monitor.Enter(mutationSequenceGate);
            return new MutationSequenceScope(mutationSequenceGate);
        }

        private IDisposable EnterPreparationMutationScope()
        {
            object reservationToken;
            lock (RequestGate)
            {
                if (!PreparationInProgress || preparationMutationReservationToken == null)
                {
                    throw new InvalidOperationException("LR2 preparation mutation scope is not reserved.");
                }
                reservationToken = preparationMutationReservationToken;
            }

            object previousToken = preparationMutationScopeToken.Value;
            preparationMutationScopeToken.Value = reservationToken;
            return new PreparationMutationScope(this, reservationToken, previousToken);
        }

        private void ExitPreparationMutationScope(object reservationToken, object previousToken)
        {
            if (ReferenceEquals(preparationMutationScopeToken.Value, reservationToken))
            {
                preparationMutationScopeToken.Value = previousToken;
            }
        }

        internal void ThrowIfLr2SongDbSyncMutationBlocked(string operation)
        {
            if (TryBlockMutation(operation, showMessage: false))
            {
                throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
            }
        }

        private void EndMutation()
        {
            lock (RequestGate)
            {
                MutationInProgress = Math.Max(0, MutationInProgress - 1);
                Monitor.PulseAll(RequestGate);
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

        private sealed class PreparationMutationScope(
            Lr2SynchronizationOwner owner,
            object reservationToken,
            object previousToken) : IDisposable
        {
            private Lr2SynchronizationOwner owner = owner;

            public void Dispose()
            {
                Lr2SynchronizationOwner current = Interlocked.Exchange(ref owner, null);
                current?.ExitPreparationMutationScope(reservationToken, previousToken);
            }
        }

        private sealed class MutationSequenceScope(object gate) : IDisposable
        {
            private object gate = gate;

            public void Dispose()
            {
                object current = Interlocked.Exchange(ref gate, null);
                if (current != null)
                {
                    Monitor.Exit(current);
                }
            }
        }

        private sealed class MutationSequenceAndReservationScope(
            IDisposable mutationSequence,
            IDisposable mutationReservation) : IDisposable
        {
            private IDisposable mutationSequence = mutationSequence;
            private IDisposable mutationReservation = mutationReservation;

            public void Dispose()
            {
                IDisposable reservation = Interlocked.Exchange(ref mutationReservation, null);
                IDisposable sequence = Interlocked.Exchange(ref mutationSequence, null);
                reservation?.Dispose();
                sequence?.Dispose();
            }
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
