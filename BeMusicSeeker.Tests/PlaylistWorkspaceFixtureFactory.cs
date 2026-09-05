using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

internal static class PlaylistWorkspaceFixtureFactory
{
    internal static PlaylistWorkspaceViewModel CreateBareHydrationWorkspace(
        Func<BMSPlaylist> playlistStoreProvider,
        Func<BMSLibrary> playlistLibraryProvider)
    {
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            playlistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            playlistLibraryProvider,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (_, _) => { },
            (_, _) => false,
            (_, _) => false,
            PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler,
            PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck,
            null);
        workspace.ConfigureCatalogNotificationQueue(action => action());
        return workspace;
    }

    internal static PlaylistWorkspaceViewModel CreateDetailWorkspace(
        out FakePlaylistDetailDataSource dataSource,
        Func<string, Func<Task>, bool>? prewarmScheduler = null,
        Func<bool>? reloadCleanupStartupOperableProvider = null,
        Func<MainViewUpdateMode>? reloadCleanupCurrentTreeModeProvider = null,
        Func<Task>? reloadCleanupDispatcherIdleWaiter = null,
        Func<bool>? reloadCleanupShutdownRequestedProvider = null,
        Action? reloadCleanupGarbageCollector = null,
        Action<string>? reloadCleanupLog = null,
        Action<Exception, string>? reloadFailureLog = null,
        Func<BMSPlaylist>? playlistStoreProvider = null,
        Func<PlaylistPresentationRefreshRequestedEventArgs, bool>? presentationRefreshDeferredProvider = null,
        Func<string, Func<Task>, bool>? externalSyncScheduler = null,
        Func<string, Func<Task>, bool>? referenceApplyScheduler = null,
        Func<BMSLibrary>? playlistLibraryProvider = null,
        Action<Action>? dispatchPresentation = null,
        Action<Action>? catalogNotificationQueue = null,
        PlaylistSummaryBmtSortCoordinator? playlistSummaryBmtSort = null,
        Action<PlaylistSummarySelectionRestoreRequest>? selectionRestoreSink = null,
        Func<Action, Task>? restoreUiApplyScheduler = null,
        Func<bool>? restoreUiThreadCheck = null,
        Action<Uri>? browserOpenSink = null,
        PlaylistPropertySaveService? propertySaveService = null,
        IUiDialogService? playlistWorkspaceDialogService = null)
    {
        var workspace = new PlaylistWorkspaceViewModel(
            dispatchPresentation ?? (action => action()),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            browserOpenSink ?? PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                playlistSummaryBmtSort ?? PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            playlistStoreProvider ?? PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            propertySaveService ?? PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            playlistLibraryProvider ?? (() => null!),
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            prewarmScheduler ?? ((_, _) => false),
            reloadCleanupStartupOperableProvider ?? (() => true),
            reloadCleanupCurrentTreeModeProvider ?? (() => MainViewUpdateMode.FolderFilterSelected),
            reloadCleanupDispatcherIdleWaiter ?? (() => Task.CompletedTask),
            reloadCleanupShutdownRequestedProvider ?? (() => false),
            reloadCleanupGarbageCollector ?? (() => { }),
            reloadCleanupLog ?? (_ => { }),
            reloadFailureLog ?? ((_, _) => { }),
            externalSyncScheduler ?? ((_, _) => false),
            referenceApplyScheduler ?? ((_, _) => false),
             restoreUiApplyScheduler ?? PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler,
             restoreUiThreadCheck ?? PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck,
             playlistWorkspaceDialogService);
        workspace.ConfigureCatalogNotificationQueue(
            catalogNotificationQueue ?? dispatchPresentation ?? (action => action()));
        workspace.PlaylistPresentationRefreshRequested += (_, request) =>
        {
            switch (request.Kind)
            {
                case PlaylistPresentationRefreshKind.SummaryData:
                    bool dataDeferred = presentationRefreshDeferredProvider?.Invoke(request) == true;
                    workspace.ApplyPlaylistSummaryDataRefresh(dataDeferred, request.RebuildAsync);
                    break;
                case PlaylistPresentationRefreshKind.SummaryPresentation:
                    bool presentationDeferred = presentationRefreshDeferredProvider?.Invoke(request) == true;
                    workspace.ApplyPlaylistSummaryPresentationRefresh(presentationDeferred);
                    break;
                case PlaylistPresentationRefreshKind.Tree:
                    bool treeDeferred = presentationRefreshDeferredProvider?.Invoke(request) == true;
                    workspace.ApplyPlaylistTreePresentationRefresh(request.Reason, treeDeferred);
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
                    bool hydrationDeferred = presentationRefreshDeferredProvider?.Invoke(request) == true;
                    workspace.ApplyPlaylistEntriesHydrationCompleted(
                        request.HydrationVersion,
                        hydrationDeferred,
                        request.HydrationSourceStore,
                        request.HydrationSourceTables,
                        request.HydrationNotificationGeneration,
                        request.HydrationCompletionReceipt);
                    break;
                default:
                    throw new InvalidOperationException("Unknown test refresh request kind.");
            }
        };
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
        workspace.PlaylistSummarySelectionRestoreRequested +=
            selectionRestoreSink ?? PlaylistWorkspaceTestPorts.PlaylistSummarySelectionRestoreSink;
        dataSource = new FakePlaylistDetailDataSource();
        workspace.SetDetailDataSource(dataSource);
        return workspace;
    }

    internal static PlaylistWorkspaceViewModel CreateBackupWorkspace(
        string songDbPath,
        IEnumerable<BMSTable> tables,
        out BMSPlaylist playlist,
        out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications,
        Func<Action, Task>? restoreUiApplyScheduler = null,
        Func<bool>? restoreUiThreadCheck = null)
    {
        PlaylistPersistenceRepository.EnsureSchema(songDbPath);
        BMSPlaylist createdPlaylist = new BMSPlaylist(
            songDbPath,
            null,
            null,
            null,
            null,
            () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(Settings.Default),
            () => new BeatorajaBmtOptionsSnapshot(),
            () => new CustomFolderOutputSettingsSnapshot(),
            TestBmsFactory.MissingEverythingBridge,
            new RestoreUiScheduler(restoreUiApplyScheduler, restoreUiThreadCheck),
            new TestLr2PlaylistFolderSynchronizationPort(songDbPath),
            (_, _) => new LibraryFileMutationLease(new object(), static () => true, static () => { }))
        {
            BMSTables = new ObservableCollection<BMSTable>(tables)
        };
        createdPlaylist.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistStoreProvider: () => createdPlaylist,
            restoreUiApplyScheduler: restoreUiApplyScheduler,
            restoreUiThreadCheck: restoreUiThreadCheck);
        workspace.RefreshPlaylistTreeTables(createdPlaylist);
        List<PlaylistOperationNotificationPresentationRequestedEventArgs> capturedNotifications = [];
        workspace.PlaylistOperationNotificationPresentationRequested +=
            (_, request) => capturedNotifications.Add(request);
        playlist = createdPlaylist;
        notifications = capturedNotifications;
        return workspace;
    }

    /// <summary>復元テストの既存 callback を、本番 owner と同じ UI operation の完了境界へ接続します。</summary>
    private sealed class RestoreUiScheduler(
        Func<Action, Task>? schedule,
        Func<bool>? threadCheck) : IUiScheduler
    {
        private readonly TestUiScheduler inner = new(() => TestUiDispatcherHost.Dispatcher);

        public bool IsAvailable => inner.IsAvailable;
        public bool CanExecuteInline => inner.CanExecuteInline;
        public bool CheckAccess() => inner.CheckAccess();

        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            async Task ApplyAsync()
            {
                void Apply()
                {
                    if (threadCheck?.Invoke() == false)
                    {
                        throw new InvalidOperationException("Controlled restore UI thread rejection.");
                    }
                    // 注入 callback は受付/完了を制御する。実 collection event は常に共有 dispatcher に配送する。
                    inner.Invoke(action, priority);
                }
                if (schedule == null)
                {
                    await inner.InvokeAsync(Apply, priority);
                }
                else
                {
                    await schedule(Apply);
                }
            }
            return new RestoreUiOperation(ApplyAsync());
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.InvokeAsync(action, priority);
        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.InvokeAsync(action, priority);
    }

    private sealed class RestoreUiOperation(Task completion) : IUiScheduledOperation
    {
        public bool IsAccepted => true;
        public bool IsCompleted => completion.IsCompleted;
        public bool IsAborted => completion.IsCanceled;
        public string RejectionReason => string.Empty;
        public Task Completion => completion;
        public void Abort() => throw new NotSupportedException();
    }

}
