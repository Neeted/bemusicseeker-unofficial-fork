using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

internal static class PlaylistWorkspaceTestPorts
{
    internal static PlaylistWorkspaceViewModel CreateProgressWorkspace(
        Action<Action> dispatch,
        PlaylistUrlAcquisitionWorkflow? acquisitionWorkflow = null,
        IUiDialogService? dialogService = null,
        Func<IReadOnlyList<string>, bool>? installSink = null,
        Action<Uri>? browserSink = null,
        Func<bool>? installQueueActiveProvider = null,
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
            installQueueActiveProvider ?? InactiveInstallQueueProvider,
            installSink ?? PlaylistUrlInstallSink,
            browserSink ?? PlaylistUrlBrowserOpenSink,
            ExternalPlaylistImportWarningLog,
            ExternalPlaylistImportInfoLog,
            BeatorajaTableUrlImportWarningLog,
            BeatorajaTableUrlImportInfoLog,
            PlaylistSummaryColumnSettingsStore,
            PlaylistSummaryBmtSortCoordinator,
            KeywordSearchHistorySettingsStore,
            KeywordSearchFavoritesSettingsStore,
            playlistStoreProvider ?? PlaylistStoreProvider,
            PlaylistPropertySaveService,
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

    internal static PlaylistUrlAcquisitionWorkflow CreateUrlAcquisitionWorkflow(Action<string>? log = null)
    {
        return new PlaylistUrlAcquisitionWorkflow(
            new AppPlaylistUrlDownloadGateway(),
            log ?? (_ => { }));
    }

    internal static PlaylistExternalPackageLookupService CreateExternalPackageLookupService()
    {
        return PlaylistExternalPackageLookupService.CreateDefault();
    }

    internal static Func<PlaylistUrlAcquisitionOptionsSnapshot> UrlAcquisitionOptionsProvider =>
        () => new PlaylistUrlAcquisitionOptionsSnapshot();

    internal static Func<bool> InactiveInstallQueueProvider => () => false;

    internal static Func<IReadOnlyList<string>, bool> PlaylistUrlInstallSink => _ => true;

    internal static Action<Uri> PlaylistUrlBrowserOpenSink => _ => { };

    internal static Action PlaylistUrlInstallTreeExpansionSink => () => { };

    internal static Action<PlaylistSummarySelectionRestoreRequest> PlaylistSummarySelectionRestoreSink => _ => { };

    internal static Action<Exception, string> ExternalPlaylistImportWarningLog => (_, _) => { };

    internal static Action<string> ExternalPlaylistImportInfoLog => _ => { };

    internal static Action<Exception, string> BeatorajaTableUrlImportWarningLog => (_, _) => { };

    internal static Action<string> BeatorajaTableUrlImportInfoLog => _ => { };

    internal static IMainChartColumnSettingsStore PlaylistSummaryColumnSettingsStore =>
        new SettingsMainChartColumnSettingsStore(() => BeMusicSeeker.Properties.Settings.Default);

    internal static PlaylistSummaryBmtSortCoordinator PlaylistSummaryBmtSortCoordinator =>
        new PlaylistSummaryBmtSortCoordinator(() => null, () => []);

    internal static IKeywordSearchHistorySettingsStore KeywordSearchHistorySettingsStore =>
        new InMemoryKeywordSearchHistorySettingsStore();

    /// <summary>
    /// Creates an isolated Favorites settings boundary for workspace fixtures.
    /// </summary>
    internal static IKeywordSearchFavoritesSettingsStore KeywordSearchFavoritesSettingsStore =>
        new InMemoryKeywordSearchFavoritesSettingsStore();

    /// <summary>
    /// Tests which do not exercise playlist persistence intentionally keep the
    /// provider unavailable.  A test that reaches URL admission or playlist
    /// mutation must pass an owned store explicitly.
    /// </summary>
    internal static Func<BMSPlaylist> PlaylistStoreProvider => () => null!;

    /// <summary>
    /// Creates a schema-backed store owned by one test case.
    /// </summary>
    internal static OwnedPlaylistStore CreateOwnedPlaylistStore()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            nameof(PlaylistWorkspaceTestPorts),
            "PlaylistStore-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        string songDbPath = BmsPlaylistTestSupport.CreateTempSongDbPath(directory);
        BeMusicSeeker.Models.BmsLibraryInternal.PlaylistPersistenceRepository.EnsureSchema(songDbPath);
        BMSPlaylist playlist = new TestBmsPlaylist(
            songDbPath,
            null,
            null,
            null,
            null,
            () => new PlaylistUrlCompletionOptionsSnapshot(),
            () => new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = false },
            () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false },
            new TestLr2PlaylistFolderSynchronizationPort(songDbPath));
        return new OwnedPlaylistStore(playlist, songDbPath, directory);
    }

    internal sealed class OwnedPlaylistStore : IDisposable
    {
        private readonly string directory;
        private int disposed;

        internal OwnedPlaylistStore(BMSPlaylist playlist, string songDbPath, string directory)
        {
            Store = playlist ?? throw new ArgumentNullException(nameof(playlist));
            SongDbPath = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));
            this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

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

    internal static Func<Action, Task> PlaylistRestoreUiApplyScheduler => action =>
    {
        action();
        return Task.CompletedTask;
    };

    internal static Func<bool> PlaylistRestoreUiThreadCheck => () => true;

    internal static PlaylistPropertySaveService PlaylistPropertySaveService =>
        new PlaylistPropertySaveService(
            () => null!,
            () => null!,
            () => null!,
            () => new CustomFolderOutputSettingsSnapshot());

    internal sealed class PlaylistWorkspaceDialogService : IUiDialogService
    {
        internal UiDialogResult ConfirmationResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel);

        internal Func<UiConfirmationRequest, UiDialogResult>? ConfirmationFactory { get; set; }

        internal UiDialogResult MessageResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal UiMessageRequest LastMessageRequest { get; private set; } = null!;

        internal UiConfirmationRequest LastConfirmationRequest { get; private set; } = null!;

        internal List<UiSaveFilePickerRequest> SaveFilePickerRequests { get; } = [];

        internal Queue<UiSaveFilePickerResult> SaveFilePickerResults { get; } = [];

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            LastMessageRequest = request;
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastConfirmationRequest = request;
            return Task.FromResult(ConfirmationFactory?.Invoke(request) ?? ConfirmationResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default)
        {
            SaveFilePickerRequests.Add(request);
            if (SaveFilePickerResults.Count == 0)
            {
                throw new InvalidOperationException("No save file picker result was configured.");
            }
            return Task.FromResult(SaveFilePickerResults.Dequeue());
        }

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class InMemoryKeywordSearchHistorySettingsStore : IKeywordSearchHistorySettingsStore
    {
        public string KeywordSearchHistory { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchHistory { get; set; } = string.Empty;
    }

    private sealed class InMemoryKeywordSearchFavoritesSettingsStore : IKeywordSearchFavoritesSettingsStore
    {
        public string KeywordSearchFavorites { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchFavorites { get; set; } = string.Empty;
    }
}
