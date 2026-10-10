using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using static BeMusicSeeker.Models.BmsLibraryInternal.Lr2SongDbSyncInputSurfaceHelper;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

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

        private readonly ChartFileOperationSynchronizer operationAdmission;

        private readonly IRootFileEnumerator rootFileEnumerator;




        /// <summary>LR2準備・同期を生存する共通受付の権限で実行し、独立した同期版と状態を管理します。</summary>
        /// <param name="operationAdmission">準備から状態保存・後片付けまで保持する共通受付。</param>
        /// <param name="rootFileEnumerator">入力更新とLR2が共用するグループ列挙境界。nullは通常の列挙経路です。</param>
        /// <param name="playlistOperationAdmission">構成の寿命で共有するP受付。省略時は独立構成の専用受付を作ります。同期状態や終了状態はこのownerだけが所有します。</param>
        internal Lr2SynchronizationOwner(
            ILr2SynchronizationDataPort data,
            ILr2SynchronizationRuntimePort runtime,
            ILr2SynchronizationProjectionPort projection,
            Action<string> queueObservablePropertyChange,
            ChartFileOperationSynchronizer operationAdmission,
            IRootFileEnumerator rootFileEnumerator = null,
            ChartFileOperationSynchronizer playlistOperationAdmission = null)
        {
            PlaylistOperationAdmission = playlistOperationAdmission ?? new ChartFileOperationSynchronizer();
            this.rootFileEnumerator = rootFileEnumerator;
            this.operationAdmission = operationAdmission ?? throw new ArgumentNullException(nameof(operationAdmission));
            this.data = data ?? throw new ArgumentNullException(nameof(data));
            this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            this.projection = projection ?? throw new ArgumentNullException(nameof(projection));
            this.queueObservablePropertyChange = queueObservablePropertyChange
                ?? throw new ArgumentNullException(nameof(queueObservablePropertyChange));
        }

        /// <summary>モデルの変更権限を発行する共通受付。</summary>
        internal ChartFileOperationSynchronizer OperationAdmission => operationAdmission;

        /// <summary>構成から接続されたP受付。再構築前後で生存権限を借用できますが、個別のstore・対象・終了状態の有効性は変えません。</summary>
        internal ChartFileOperationSynchronizer PlaylistOperationAdmission { get; }

        /// <summary>実変更scopeが現行管理領域へ交差する場合だけPを非待機取得します。非交差ではleaseはnullです。</summary>
        /// <param name="paths">最初の副作用前に固定した実変更先と変更元。</param>
        /// <param name="recursive">祖先フォルダの移動・再帰削除も交差として扱うか。</param>
        /// <param name="lease">交差時に後片付けまで保持するP。競合時は取得せずfalseを返します。</param>
        internal bool TryEnterManagedOutputMutation(IEnumerable<string> paths, bool recursive, out LibraryFileMutationLease lease, LibraryFileMutationCapability capability = null, IEnumerable<string> recursivePaths = null)
        {
            lease = null;
            BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
            if (!options.OperationModeLR2DB) { return true; }
            if (capability?.PlaylistCapability != null)
            {
                lease = PlaylistOperationAdmission.Borrow(capability.PlaylistCapability);
                return true;
            }
            var regions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string directory in new[] { options.LR2CustomFolderOutputBaseDir, options.LR2CustomFolderOutputBaseDirRootType }
                .Concat(options.LR2CustomFolderAdditionalOutputBaseDirs))
            {
                if (!string.IsNullOrWhiteSpace(directory)) { regions.Add(directory); }
            }
            // 正本の対象数だけを読み、全所持譜面を列挙しません。読取不能は元例外で止めます。
            regions.UnionWith(data.CaptureLr2SongDbSyncAppManagedOutputScope(options, throwOnFailure: true).Directories);
            var recursiveTargets = new HashSet<string>(recursivePaths ?? [], StringComparer.OrdinalIgnoreCase);
            bool intersects = (paths ?? []).Concat(recursiveTargets).Where(path => !string.IsNullOrWhiteSpace(path)).Any(path => regions.Any(region =>
                CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(path, region)
                || ((recursive || recursiveTargets.Contains(path)) && CustomFolderOutputBaseSearchRootSyncService.IsSameOrNestedDirectory(region, path))));
            if (!intersects) { return true; }
            if (!PlaylistOperationAdmission.TryEnter(out IDisposable accepted)) { return false; }
            lease = (LibraryFileMutationLease)accepted;
            return true;
        }

        internal object RequestGate { get; } = new();

        internal object StatusGate { get; } = new();

        internal object ScanSurfaceGate { get; } = new();


        internal int RequestedVersion { get; set; }

        internal int CompletedVersion { get; set; }

        internal int FailedVersion { get; set; }


        internal bool Running { get; set; }




        internal CancellationTokenSource Cancellation { get; set; }

        internal Lr2SongDbSyncStatusSnapshot Status { get; set; } = new()
        {
            Status = Lr2SongDbSyncStatusKind.NotNeeded
        };


        internal CustomFolderOutputPhysicalSurface AppManagedCustomFolderOutputPhysicalSurface { get; set; } = new(
            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
            discoveryComplete: false);





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

        /// <summary>同じ操作で捕捉した設定から生成管理領域を構成します。最新性確認での省略時は現設定を使います。</summary>
        /// <param name="options">受理済み操作の変更不能設定。nullは現設定を捕捉します。</param>
        internal Lr2SongDbSyncAppManagedOutputScope CreateLr2SongDbSyncAppManagedOutputScope(BmsLibraryOptionsSnapshot options = null)
        {
            Lr2SongDbSyncAppManagedOutputScope scope =
                data.CaptureLr2SongDbSyncAppManagedOutputScope(options ?? data.CurrentOptionsSnapshot);
            if (!scope.IsComplete)
            {
                BMSLibrary.LogInstallPerformanceWarn(
                    "lr2folder_app_managed_output_scope failed");
            }
            return scope;
        }

        /// <summary>生存する共通権限と捕捉した設定入力で組込folder行を同期し、後続全体同期へ準備入力を返します。</summary>
        internal Lr2SongDbSyncPreparedDataSurface SyncLr2BuiltinCustomFolderRows(
            string reason,
            LibraryFileMutationCapability mutationCapability,
            BmsLibraryOptionsSnapshot options = null)
        {
            RequireMutationCapability(mutationCapability);
            options ??= CurrentOptionsSnapshot;
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

        /// <summary>受理済み設定後更新を、共通受付と物理変更権限を揃えて実終端まで実行します。</summary>
        /// <param name="reason">設定変更の既存受付理由。</param>
        /// <param name="capability">設定側が保持する共通権限。nullは新しい非待機受付です。</param>
        /// <param name="playlistCapability">同じ設定保存の局所権限。管理外探索も両受付を保持し、開始競合を元Busy失敗として返します。</param>
        /// <returns>実行した外部LR2行更新・cleanupの終端Task。元失敗・取消を伝播します。</returns>
        internal Task SyncExternalLr2FolderRowsForCustomFolderOutputBaseChangeAsync(string reason, LibraryFileMutationCapability capability = null, LibraryFileMutationCapability playlistCapability = null)
        {
            if (CurrentOptionsSnapshot?.OperationModeLR2DB != true)
            {
                return Task.CompletedTask;
            }

            using LibraryFileMutationLease mutationLease = capability == null
                ? TryBeginMutation("lr2folder_settings_output_base_sync", showMessage: false)
                : operationAdmission.Borrow(capability);
            if (mutationLease == null) { throw new InvalidOperationException(Resources.Warn_LibraryOperationBusy); }
            playlistCapability ??= capability?.PlaylistCapability;
            LibraryFileMutationLease playlistLease;
            if (playlistCapability != null) { playlistLease = PlaylistOperationAdmission.Borrow(playlistCapability); }
            else if (PlaylistOperationAdmission.TryEnter(out IDisposable acquired)) { playlistLease = (LibraryFileMutationLease)acquired; }
            else { throw new InvalidOperationException(Resources.Warn_LibraryOperationBusy); }
            using LibraryFileMutationLease playlistOperation = playlistLease;
            using LibraryFileMutationCapability playlistAuthority = playlistLease.CreateMutationCapability();
            playlistCapability = playlistAuthority;
            BmsLibraryOptionsSnapshot options = CurrentOptionsSnapshot;
            using (LibraryFileMutationCapability mutationCapability = mutationLease.CreateMutationCapability())
            {
                if (options?.OperationModeLR2DB != true)
                {
                    return Task.CompletedTask;
                }

                var stopwatch = Stopwatch.StartNew();
                Lr2SearchRootSnapshot rootSnapshot = data.CaptureBmsDirectories();
                List<string> roots = [.. rootSnapshot.Roots];
                List<string> builtinSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options);
                List<string> discoveryDirectories = NormalizeDistinctDirectories(CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(roots, options));
                Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope(options);
                if (!appManagedOutputScope.IsComplete)
                {
                    BMSLibrary.LogInstallPerformance("lr2folder_settings_output_base_sync skipped"
                        + " reason=" + (reason ?? "unknown")
                        + " detail=app_managed_scope_incomplete"
                        + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                    return Task.CompletedTask;
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

            return Task.CompletedTask;
        }

        internal Lr2BuiltinCustomFolderSettings CreateCurrentLr2BuiltinCustomFolderSettings(DateTime nowUtc)
        {
            LR2Config config = data.CreateCurrentLr2ConfigOrNull();
            return Lr2BuiltinCustomFolderSettings.CreateFromAddDates(
                config,
                data.CaptureBmsFilesSnapshot()
                    .Where(file => !string.IsNullOrWhiteSpace(file.Path))
                    .Select(file => file.AddDate),
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
                excludedDirectories,
                rootFileEnumerator);
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
                data.EverythingNative,
                rootFileEnumerator);
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

            return CreateLr2SongDbSyncTextMetadataCandidates(rootDirectories, targetDirectories, data.EverythingNative, rootFileEnumerator);
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

        /// <summary>
        /// Synchronizes the prepared LR2 folder-file rows through the caller-owned mutation lease.
        /// </summary>
        /// <param name="progressReporter">Optional best-effort per-file preparation progress reporter.</param>
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
            bool updateParentDirectoryRowsForPreservedItems = true,
            Action<int, int, string> progressReporter = null)
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
                        path => existingRows.RowsByPath.TryGetValue(path, out LR2SongDB.folder row) ? row : null,
                        progressReporter);
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
                BMSLibrary.LogInstallPerformance(logName + " result=applied"
                    + " checkedFiles=" + syncItems.Items.Count
                    + " parseTargets=" + syncItems.ParseTargetCount
                    + " parsedFiles=" + syncItems.ParsedCount
                    + " unchangedFiles=" + syncItems.UnchangedCount
                    + " rowScope=folder_file_and_parent_directory"
                    + " changedFolderRows=" + (syncResult.UpsertedCount + syncResult.DeletedCount)
                    + " upsertedFolderRows=" + syncResult.UpsertedCount
                    + " deletedFolderRows=" + syncResult.DeletedCount
                    + " readFailures=" + syncItems.HasReadFailures.ToString().ToLowerInvariant()
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
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

        /// <summary>同じ共通ownerに属する生存権限でplaylistのfolder行を保存し、元の部分成功・失敗結果を返します。</summary>
        internal Lr2FolderFileDbSyncResult SyncPlaylistLr2FolderFileRows(
            string operation,
            Lr2FolderFileDbSyncRequest request,
            LibraryFileMutationCapability mutationCapability)
        {
            mutationCapability.Validate(PlaylistOperationAdmission);
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

        /// <summary>確定済みの譜面変更receiptを同じ生存権限でLR2へ反映します。確定済み変更を巻き戻さず必須反映失敗を伝播します。</summary>
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
                            if (!TryEnterManagedOutputMutation(directoryMetadataTargets.Concat(syncInput.PruneExactDirectories),
                                recursive: false, out LibraryFileMutationLease playlistLease, capability: mutationCapability,
                                recursivePaths: syncInput.PruneScopeDirectories))
                            { throw new InvalidOperationException(Resources.Warn_LibraryOperationBusy); }
                            using LibraryFileMutationLease playlistOperation = playlistLease;
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
                                + " bmsCountQueries=" + (receipt.CurrentBmsFacts?.QueryDiagnostics?.SubtreeCountQueryCount ?? 0)
                                + " bmsRangeQueries=" + (receipt.CurrentBmsFacts?.QueryDiagnostics?.RangeQueryCount ?? 0)
                                + " bmsRangeVisitedRefs=" + (receipt.CurrentBmsFacts?.QueryDiagnostics?.RangeVisitedReferenceCount ?? 0)
                                + " bmsRangeReturnedPaths=" + (receipt.CurrentBmsFacts?.QueryDiagnostics?.RangeReturnedPathCount ?? 0)
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
                    + " bmsCountQueries=" + (receipt.CurrentBmsFacts?.QueryDiagnostics?.SubtreeCountQueryCount ?? 0)
                    + " bmsRangeQueries=" + (receipt.CurrentBmsFacts?.QueryDiagnostics?.RangeQueryCount ?? 0)
                    + " bmsRangeVisitedRefs=" + (receipt.CurrentBmsFacts?.QueryDiagnostics?.RangeVisitedReferenceCount ?? 0)
                    + " bmsRangeReturnedPaths=" + (receipt.CurrentBmsFacts?.QueryDiagnostics?.RangeReturnedPathCount ?? 0)
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
                var entry = RootFileEnumerationEntry.FromDirectoryInfo(targetDirectory);
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

        /// <summary>今回の走査面を型付き結果として返します。管理出力の読取りキャッシュだけを更新します。</summary>
        internal Lr2SongDbSyncScanSurfaceSnapshot CreateLr2SongDbSyncScanSurface(
            BmsLibraryOptionsSnapshot options, IEnumerable<string> rootDirectories, SongTableFileCheckResult result)
        {
            if (options?.OperationModeLR2DB != true || result?.Lr2ScanSurfaceAvailable != true) { return null; }
            IReadOnlyList<string> roots = [.. (rootDirectories ?? []).Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(BMSLibrary.SafeFullPathOrOriginal).Distinct(StringComparer.OrdinalIgnoreCase)];
            Lr2FolderFileCandidateSnapshot candidates;
            if (result.Lr2ScanLr2FolderCandidatesAlreadyFiltered)
            {
                candidates = new(result.Lr2ScanLr2FolderFilePaths, result.Lr2ScanLr2FolderFileEntries, result.Lr2ScanLr2FolderFileDiscoveryComplete);
            }
            else
            {
                Lr2SongDbSyncAppManagedOutputScope scope = CreateLr2SongDbSyncAppManagedOutputScope(options);
                candidates = scope.IsComplete ? Lr2FolderFileDiscoveryService.ExcludeAppManagedOutputCandidates(
                    result.Lr2ScanLr2FolderFilePaths, result.Lr2ScanLr2FolderFileEntries, scope.FilePaths,
                    result.Lr2ScanLr2FolderFileDiscoveryComplete, out _, scope.Directories)
                    : BMSLibrary.CreateIncompleteLr2FolderCandidateSnapshot();
            }
            lock (ScanSurfaceGate)
            {
                AppManagedCustomFolderOutputPhysicalSurface = new(result.Lr2ScanAppManagedCustomFolderOutputFileEntries,
                    result.Lr2ScanAppManagedCustomFolderOutputDiscoveryComplete);
            }
            return new(roots, result.Lr2ScanNormalFolderDirectoryPaths, result.Lr2ScanDirectoryEntries,
                result.Lr2ScanNormalFolderDirectoryEntries, result.Lr2ScanFolderInfoFilePaths, result.Lr2ScanFolderInfoFileEntries,
                result.Lr2ScanTextFileDirectories, result.Lr2ScanLr2FolderDiscoveryDirectories, candidates.Paths,
                candidates.EntriesByPath, candidates.DiscoveryComplete);
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







        internal void PublishLr2SongDbSyncStatus(Lr2SongDbSyncStatusSnapshot status)
        {
            PublishStatus(status);
        }

        internal bool TrySkipForShutdown(string operation, string reason) =>
            runtime.TrySkipForShutdown(operation, reason);


        /// <summary>専用進捗へ渡す同一実行の識別。因果判定や結果再利用には使用しません。</summary>
        internal OperationProgressRequest ProgressRequest { get; private set; }

        /// <summary>取得済み共通権限で開始します。同じ要求識別とRunning状態を要求版通知より先に公開し、準備中の競合要求にも実状態を返します。</summary>
        internal int BeginRequest(LibraryFileMutationCapability capability, OperationProgressRequest originatingRequest = null, Lr2SongDbSyncStatusSnapshot initialStatus = null)
        {
            capability.Validate(operationAdmission);
            lock (RequestGate)
            {
                int version = RequestedVersion + 1;
                ProgressRequest = originatingRequest == null ? new(0, 0, "lr2_song_db_sync", version)
                    : new(originatingRequest.Generation, originatingRequest.OperationToken, "lr2_song_db_sync", version);
                if (initialStatus != null) { PublishStatus(initialStatus); }
                return BeginLr2SongDbSyncRequestUnsafe();
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

        /// <summary>生存共通権限で受理済み同期の実処理と後片付けを実行します。自己受付を取り直しません。</summary>
        /// <param name="capability">同じ受付の生存中の所有者に属する権限。</param>
        /// <param name="options">署名と生成準備から継続して渡す受理済み操作の変更不能設定。</param>
        /// <returns>実処理と後片付けの終端。失敗と取消は呼出し元へ伝播します。</returns>
        internal Task RunLr2SongDbSyncAsync(string reason, string signature, int requestVersion,
            LibraryFileInitializationResult initializationResult, Lr2SongDbSyncPreparedDataSurface preparedSurface, LibraryFileMutationCapability capability, BmsLibraryOptionsSnapshot options)
        {
            capability.Validate(operationAdmission);
            Lr2SongDbSyncRequestCoordinator.Run(this, reason, signature, requestVersion, initializationResult, preparedSurface, options);
            return Task.CompletedTask;
        }

        internal CancellationToken GetLr2SongDbSyncCancellationToken()
        {
            lock (RequestGate)
            {
                return Cancellation?.Token ?? CancellationToken.None;
            }
        }

        /// <summary>親受付内で捕捉した設定・走査・生成結果から最終入力を構成します。過去の共有結果や全体版で因果を推測しません。</summary>
        /// <param name="options">受付後に捕捉した変更不能設定。省略時は呼出し時の設定を捕捉します。</param>
        /// <returns>所持集合、探索面、生成準備と設定が一致した同期入力。</returns>
        internal Lr2SongDbSyncInput CreateLr2SongDbSyncInput(BmsLibraryOptionsSnapshot options = null,
            LibraryFileInitializationResult initializationResult = null, Lr2SongDbSyncPreparedDataSurface preparedSurface = null)
        {
            options ??= CurrentOptionsSnapshot;
            var inputStopwatch = Stopwatch.StartNew();
            var rowSnapshotStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncInputRowSnapshot rowSnapshot = CreateLr2SongDbSyncInputRowSnapshot();
            rowSnapshotStopwatch.Stop();

            var rootsStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncInputRootSnapshot rootSnapshot = CreateLr2SongDbSyncInputRootSnapshot(options);
            rootsStopwatch.Stop();

            var builtinSettingsStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot = CreateLr2SongDbSyncInputSettingsSnapshot(
                rowSnapshot.SongRows,
                rootSnapshot.CapturedAtUtc,
                rootSnapshot.RootDirectories, options);
            builtinSettingsStopwatch.Stop();

            var scanSurfaceStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncScanSurfaceSelection scanSurfaceSelection =
                new(initializationResult?.ScanSurface, initializationResult?.ScanSurface == null ? "new_request" : string.Empty);
            scanSurfaceStopwatch.Stop();

            Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection =
                new(preparedSurface ?? Lr2SongDbSyncPreparedDataSurface.Empty);
            var inputBuilder = new Lr2SongDbSyncInputBuilder(
                BMSLibrary.LogEverythingScan,
                BMSLibrary.LogInstallPerformance,
                data.EverythingNative,
                rootFileEnumerator);

            var lr2FolderCandidatesStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope = CreateLr2SongDbSyncAppManagedOutputScope(options);

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



        private Lr2SongDbSyncInputRowSnapshot CreateLr2SongDbSyncInputRowSnapshot()
        {
            return data.CaptureLr2SynchronizationInputRowSnapshot();
        }

        private Lr2SongDbSyncInputRootSnapshot CreateLr2SongDbSyncInputRootSnapshot(BmsLibraryOptionsSnapshot options)
        {
            DateTime capturedAtUtc = DateTime.UtcNow;
            List<string> rootDirectories = [.. data.CaptureBmsDirectories().Roots];
            return new Lr2SongDbSyncInputRootSnapshot(
                capturedAtUtc,
                rootDirectories,
                CreateLr2SongDbSyncLr2FolderDiscoveryDirectories(rootDirectories, options),
                options.LR2RootPath);
        }

        private Lr2SongDbSyncInputSettingsSnapshot CreateLr2SongDbSyncInputSettingsSnapshot(
            IEnumerable<ChartFile> songRows,
            DateTime nowUtc,
            IEnumerable<string> rootDirectories, BmsLibraryOptionsSnapshot options)
        {
            List<string> lr2BuiltinFolderSourceDirectories = CreateLr2SongDbSyncBuiltinFolderSourceDirectories(options);
            return new Lr2SongDbSyncInputSettingsSnapshot(
                Lr2BuiltinCustomFolderSettings.CreateFromAddDates(
                    data.CreateCurrentLr2ConfigOrNull(),
                    (songRows ?? []).Select(chart => chart?.AddDate),
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

        internal Dictionary<string, ChartFile> CreateLr2SongDbSyncCompatibilityProjectionIndex() =>
            projection.CreateLr2SongDbSyncCompatibilityProjectionIndex();

        internal Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateLr2SongDbSyncChartInfoResolverSnapshot() =>
            projection.CreateLr2SongDbSyncChartInfoResolverSnapshot();

        internal HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason) =>
            projection.CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(reason);

        internal void UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<BeMusicSeeker.Models.ChartDetails> rows) =>
            projection.UpsertLr2SongDbSyncChartInfoIndexRows(rows);

        internal void UpdateLr2SongDbSyncProgress(Lr2SongDbSyncProgress progress) =>
            UpdateProgress(progress);

        internal int ApplyLr2SongDbSyncCompatibilityProjection(
            IReadOnlyList<ResourceHealthMaintenanceSnapshot> maintenanceInfos,
            string reason,
            IReadOnlyDictionary<string, ChartFile> bmsByPath,
            bool logSummary,
            bool dispatchPresentation) =>
            projection.ApplyLr2SongDbSyncCompatibilityProjection(
                maintenanceInfos,
                reason,
                bmsByPath,
                logSummary,
                dispatchPresentation);

        internal void DispatchWarningPresentationChanged(string reason, IReadOnlyList<string> md5s = null) =>
            projection.DispatchWarningPresentationChanged(reason, md5s);

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

        /// <summary>LR2失敗の保存と公開を一箇所で終結します。同じ要求で既に確定したFailed/Incompleteを再通知・上書きしません。</summary>
        /// <param name="signature">今回の要求が捕捉した設定署名。</param>
        /// <param name="runId">下位実行の識別。実行開始前の失敗では空です。</param>
        /// <param name="requestVersion">今回の実要求版。開始前なら現在の要求版です。</param>
        /// <param name="failure">元の同期失敗または終了取消。</param>
        /// <param name="stage">失敗を観測した実段階。</param>
        /// <param name="interruptedByShutdown">終了による中断は既存Incompleteとして保存します。</param>
        internal void RecordLr2SongDbSyncFailure(string signature, string runId, int requestVersion,
            Exception failure, string stage, bool interruptedByShutdown = false)
        {
            Lr2SongDbSyncStatusSnapshot current = GetStatusSnapshot();
            if (current.Status is Lr2SongDbSyncStatusKind.Failed or Lr2SongDbSyncStatusKind.Incomplete) { return; }
            if (interruptedByShutdown)
            {
                MarkLr2SongDbSyncInterruptedStatus(signature, runId, ObservableProcessedCount, ObservableTotalCount, stage);
                FailRequest(requestVersion, Lr2SongDbSyncStatusKind.Incomplete, stage, "shutdown_interrupted");
                return;
            }
            try { MarkLr2SongDbSyncFailedStatus(signature, runId, failure); }
            catch
            {
                // 状態保存自体が失敗しても、元の同期失敗と実行中でない公開状態を保持する。
            }
            FailRequest(requestVersion, Lr2SongDbSyncStatusKind.Failed, stage, failure.Message);
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
                Lr2SongDbSyncStatusSnapshot next = status?.Clone() ?? new Lr2SongDbSyncStatusSnapshot
                {
                    Status = Lr2SongDbSyncStatusKind.NotNeeded
                };
                Status = next;
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



        /// <summary>同じ受付中に生成準備結果を探索面へ反映し、受理済み設定と対応させます。</summary>
        /// <param name="options">生成準備とworkerへ共通に渡す変更不能設定。省略時は現設定を捕捉します。</param>






        internal CustomFolderOutputPhysicalSurface GetCurrentAppManagedCustomFolderOutputPhysicalSurface()
        {
            lock (ScanSurfaceGate)
            {
                return AppManagedCustomFolderOutputPhysicalSurface ?? CustomFolderOutputPhysicalSurface.Empty;
            }
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
            }
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
            }
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

        /// <summary>
        /// 一回の進捗通知の値から公開状態を構成し、既存の観測プロパティも更新します。
        /// 楽曲処理と保存の通知が並行しても、公開状態の段階名と件数を別通知の値から読み戻しません。
        /// </summary>
        internal void UpdateProgress(Lr2SongDbSyncProgress progress)
        {
            if (progress == null)
            {
                return;
            }
            Lr2SongDbSyncStatusSnapshot status = BMSLibrary.CreateRuntimeLr2SongDbSyncStatus(
                Lr2SongDbSyncStatusKind.Running,
                GetStatusSnapshot().Signature,
                progress.Stage ?? string.Empty,
                Math.Max(0, progress.ProcessedCursor),
                Math.Max(0, progress.TotalCount),
                lastError: null,
                Math.Max(0, progress.StageProcessedCount),
                Math.Max(0, progress.StageTotalCount));
            SetObservableTotalCount(status.TotalCount.GetValueOrDefault());
            SetObservableProcessedCount(status.ProcessedCursor.GetValueOrDefault());
            SetObservableStage(status.Stage);
            SetObservableStageProcessedCount(status.StageProcessedCount.GetValueOrDefault());
            SetObservableStageTotalCount(status.StageTotalCount.GetValueOrDefault());
            PublishStatus(status);
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

        /// <summary>共通受付の保持だけで新しい変更のBusyを判定します。</summary>
        internal bool TryBlockMutation(string operation, bool showMessage = true, LibraryFileMutationCapability capability = null)
        {
            if (capability != null) { capability.Validate(operationAdmission); return false; }
            if (!operationAdmission.IsActive) { return false; }
            if (showMessage)
            {
                runtime.ShowOperationDialog(Resources.Warn_LibraryOperationBusy, Resources.MessageBoxTitle_Warning,
                MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            }
            return true;
        }

        /// <summary>新要求は非待機で共通受付を取得し、受理済み継続は明示権限を検査して借用します。</summary>
        internal LibraryFileMutationLease TryBeginMutation(string operation, bool showMessage = true,
            LibraryFileMutationCapability capability = null)
        {
            if (capability != null) { return operationAdmission.Borrow(capability); }
            if (operationAdmission.TryEnter(out IDisposable lease)) { return (LibraryFileMutationLease)lease; }
            TryBlockMutation(operation, showMessage);
            return null;
        }

        /// <summary>受理済み背景処理が先行操作の実終端後に共通受付を取得します。</summary>
        internal async Task<LibraryFileMutationLease> AcquireAcceptedBackgroundMutationAsync(string operation)
            => (LibraryFileMutationLease)await operationAdmission.EnterAcceptedBackgroundAsync().ConfigureAwait(false);

        /// <summary>本モデルの共通受付から発行した生存権限だけを検査します。</summary>
        internal void ThrowIfLr2SongDbSyncMutationBlocked(string operation, LibraryFileMutationCapability mutationCapability)
            => RequireMutationCapability(mutationCapability);

        private int BeginLr2SongDbSyncRequestUnsafe()
        {
            RequestedVersion++;
            int requestVersion = RequestedVersion;
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
            return requestVersion;
        }

        private void RequireMutationCapability(LibraryFileMutationCapability mutationCapability)
        {
            if (mutationCapability == null)
            {
                throw new ArgumentNullException(nameof(mutationCapability));
            }
            mutationCapability.Validate(operationAdmission);
        }


        private Lr2SongDbSyncRuntimeSnapshot CreateRuntimeSnapshotUnsafe()
        {
            return new Lr2SongDbSyncRuntimeSnapshot
            {
                Running = Running,
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
