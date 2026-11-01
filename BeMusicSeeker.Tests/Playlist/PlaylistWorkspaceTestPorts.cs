using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

using static BeMusicSeeker.TestSupport.Components.PlaylistWorkspaceFixturePorts;

namespace BeMusicSeeker.Tests;

internal static class PlaylistWorkspaceTestPorts
{
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

    internal static Action PlaylistUrlInstallTreeExpansionSink => () => { };

    /// <summary>
    /// 呼出し側のDB所有者を共有し、一つのテストケースがschemaを所有するプレイリストを作ります。返却前の生成失敗は、その場で作成directoryを回収します。
    /// </summary>
    /// <param name="databaseScope">同じappの接続とモデルが共有するDB所有者。</param>
    internal static OwnedPlaylistStore CreateOwnedPlaylistStore(ApplicationDatabaseScope databaseScope)
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
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
                new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath));
            return new OwnedPlaylistStore(databaseScope, playlist, songDbPath, directory);
        }
        catch (Exception primaryFailure)
        {
            try { if (System.IO.Directory.Exists(directory)) { System.IO.Directory.Delete(directory, recursive: true); } }
            catch (Exception cleanupFailure) { throw new AggregateException(primaryFailure, cleanupFailure); }
            throw;
        }
    }

    internal sealed class OwnedPlaylistStore : IDisposable
    {
        private readonly string directory;
        private int disposed;

        /// <summary>生成したDB所有者と実storeの寿命を一つのfixtureへ集めます。</summary>
        internal OwnedPlaylistStore(ApplicationDatabaseScope databaseScope, BMSPlaylist playlist, string songDbPath, string directory)
        {
            DatabaseScope = databaseScope;
            Store = playlist ?? throw new ArgumentNullException(nameof(playlist));
            SongDbPath = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));
            this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        /// <summary>このstoreと、後から接続する同じappのモデルが共有するDB所有者です。</summary>
        internal ApplicationDatabaseScope DatabaseScope { get; }

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
        }
    }
}
