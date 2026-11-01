using System.Collections.ObjectModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.PlaylistWorkspaceFixturePorts;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>進捗・URL導入・通知受信の機能別境界と、実storeの専有寿命を組み立てます。</summary>
internal static class PlaylistWorkspaceTestPorts
{
    /// <summary>進捗の配送と任意の導入・browser境界を接続します。通信とcleanupのTaskは呼出し側が同じ期限で終端させます。</summary>
    internal static PlaylistWorkspaceViewModel CreateProgressWorkspace(
        Action<Action> dispatch,
        PlaylistUrlAcquisitionWorkflow? acquisitionWorkflow = null,
        IUiDialogService? dialogService = null,
        Func<IReadOnlyList<string>, bool>? installSink = null,
        Action<Uri>? browserSink = null,
        Func<Task>? reloadCleanupDispatcherIdleWaiter = null,
        Func<bool>? reloadCleanupShutdownRequestedProvider = null,
        Action? reloadCleanupGarbageCollector = null,
        Func<BMSPlaylist>? playlistStoreProvider = null)
    {
        return new PlaylistWorkspaceViewModel(
            dispatch ?? throw new ArgumentNullException(nameof(dispatch)),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            acquisitionWorkflow ?? CreateUrlAcquisitionWorkflow(),
            CreateExternalPackageLookupService(),
            UrlAcquisitionOptionsProvider,
            installSink == null ? PlaylistUrlInstallSink : request => installSink(Array.AsReadOnly(request.Paths)),
            browserSink ?? PlaylistUrlBrowserOpenSink,
            ExternalPlaylistImportWarningLog,
            ExternalPlaylistImportInfoLog,
            BeatorajaTableUrlImportWarningLog,
            BeatorajaTableUrlImportInfoLog,
            PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceFixturePorts.PlaylistSummaryBmtSortCoordinator,
            KeywordSearchHistorySettingsStore,
            KeywordSearchFavoritesSettingsStore,
            playlistStoreProvider ?? PlaylistStoreProvider,
            PlaylistWorkspaceFixturePorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            reloadCleanupDispatcherIdleWaiter ?? (() => Task.CompletedTask),
            reloadCleanupShutdownRequestedProvider ?? (() => false),
            reloadCleanupGarbageCollector ?? (() => { }),
            _ => { },
            (exception, message) => { },
            (_, _) => false,
            (_, _) => false,
            action =>
            {
                dispatch(action);
                return Task.CompletedTask;
            },
            () => true,
            dialogService);
    }

    internal static void AttachImmediatePlaylistPresentationRouter(PlaylistWorkspaceViewModel workspace)
    {
        workspace.PlaylistEntriesHydrationRequested += (_, request) =>
        {
            if (!workspace.TryBeginPlaylistHydrationNotification(
                request.SourceStore,
                request.SourceTables,
                request.Generation,
                request.CompletionReceipt))
            {
                return;
            }
            workspace.ExecuteCurrentPlaylistHydrationNotification(
                request.SourceStore,
                request.SourceTables,
                request.Generation,
                request.CompletionReceipt,
                () => { });
        };
        workspace.PlaylistPresentationRefreshRequested += (_, request) =>
        {
            switch (request.Kind)
            {
                case PlaylistPresentationRefreshKind.SummaryData:
                    workspace.ApplyPlaylistSummaryDataRefresh(deferred: false, request.RebuildAsync);
                    break;
                case PlaylistPresentationRefreshKind.SummaryPresentation:
                    workspace.ApplyPlaylistSummaryPresentationRefresh(deferred: false);
                    break;
                case PlaylistPresentationRefreshKind.Tree:
                    workspace.ApplyPlaylistTreePresentationRefresh(request.Reason, deferred: false);
                    break;
                case PlaylistPresentationRefreshKind.HydrationCompleted:
                    if (request.HydrationSourceStore != null
                        && !workspace.IsCurrentPlaylistTreeNotificationSnapshot(
                            request.HydrationSourceStore,
                            request.HydrationSourceTables,
                            request.HydrationNotificationGeneration))
                    {
                        break;
                    }
                    if (!workspace.TryBeginPlaylistHydrationNotification(
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt))
                    {
                        break;
                    }
                    workspace.PublishPlaylistEntriesHydrationCompleted(
                        request.HydrationVersion,
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt);
                    workspace.ApplyPlaylistEntriesHydrationCompleted(
                        request.HydrationVersion,
                        deferred: false,
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt);
                    break;
                default:
                    throw new InvalidOperationException("Unknown playlist presentation refresh request kind.");
            }
        };
    }

    /// <summary>導入後のツリー展開要求だけを受理する空のpresentation境界です。</summary>
    internal static Action PlaylistUrlInstallTreeExpansionSink => () => { };

    /// <summary>
    /// 呼出し側のDB所有者を共有し、一つのテストケースがschemaを所有するプレイリストを作ります。返却前の生成失敗は、その場で作成directoryを回収します。
    /// </summary>
    /// <param name="databaseScope">同じappの接続とモデルが共有するDB所有者。</param>
    /// <param name="modelUiScheduler">モデル通知の配送境界。省略時は既存の共有Dispatcherを使います。</param>
    internal static OwnedPlaylistStore CreateOwnedPlaylistStore(ApplicationDatabaseScope databaseScope, IUiScheduler? modelUiScheduler = null)
    {
        var ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = System.IO.Path.Combine(
            ownedWorkspace.RootDirectory,
            nameof(PlaylistWorkspaceTestPorts),
            "PlaylistStore-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            string songDbPath = PlaylistTestFixture.CreateTempSongDbPath(databaseScope, directory);
            BeMusicSeeker.Models.BmsLibraryInternal.PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            BMSPlaylist playlist = new TestBmsPlaylist(
                databaseScope,
                songDbPath,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = false },
                () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
                new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath), uiScheduler: modelUiScheduler);
            return new OwnedPlaylistStore(databaseScope, playlist, songDbPath, directory, ownedWorkspace);
        }
        catch (Exception primaryFailure)
        {
            try { if (System.IO.Directory.Exists(directory)) { System.IO.Directory.Delete(directory, recursive: true); } ownedWorkspace.Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException(primaryFailure, cleanupFailure); }
            throw;
        }
    }

    /// <summary>実storeと専有媒体をまとめ、呼出し側が全使用Taskを終端した後に回収します。</summary>
    internal sealed class OwnedPlaylistStore : IDisposable
    {
        private readonly string directory;
        private readonly TestWorkspace ownedWorkspace;
        private int disposed;

        /// <summary>生成したDB所有者と実storeの寿命を一つのfixtureへ集めます。</summary>
        internal OwnedPlaylistStore(ApplicationDatabaseScope databaseScope, BMSPlaylist playlist, string songDbPath, string directory, TestWorkspace ownedWorkspace)
        {
            DatabaseScope = databaseScope;
            Store = playlist ?? throw new ArgumentNullException(nameof(playlist));
            SongDbPath = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));
            this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
            this.ownedWorkspace = ownedWorkspace;
        }

        /// <summary>このstoreと、後から接続する同じappのモデルが共有するDB所有者です。</summary>
        internal ApplicationDatabaseScope DatabaseScope { get; }

        /// <summary>同じDB所有者と専有媒体を使う実プレイリストです。</summary>
        internal BMSPlaylist Store { get; }

        /// <summary>
        /// Gets the database path owned by this store fixture for startup-service attachment.
        /// </summary>
        internal string SongDbPath { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            ownedWorkspace.Dispose();
        }
    }
}
