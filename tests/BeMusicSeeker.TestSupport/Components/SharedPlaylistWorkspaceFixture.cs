using System.Collections.ObjectModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>設定とプレイリストの実媒体・workspace接続で共用する既存fixtureを保持します。</summary>
internal static class SharedPlaylistWorkspaceFixture
{

    /// <summary>専用workspaceへ既存の配送/適用Taskと集約診断を接続し、各ケースが実終端と局所仕事量を観測できるようにします。</summary>
    /// <param name="playlistStoreProvider">未接続時に null を返す通常の store 取得契約も受け入れます。</param>
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
        Func<BMSPlaylist?>? playlistStoreProvider = null,
        Func<PlaylistPresentationRefreshRequestedEventArgs, bool>? presentationRefreshDeferredProvider = null,
        Func<string, Func<Task>, bool>? externalSyncScheduler = null,
        Func<string, Func<Task>, bool>? referenceApplyScheduler = null,
        Func<BMSLibrary>? playlistLibraryProvider = null,
        Action<Action>? dispatchPresentation = null,
        Action<Action>? catalogNotificationQueue = null,
        PlaylistSummaryBmtSortCoordinator? playlistSummaryBmtSort = null,
        Func<Action, Task>? restoreUiApplyScheduler = null,
        Func<bool>? restoreUiThreadCheck = null,
        Action<Uri>? browserOpenSink = null,
        PlaylistPropertySaveService? propertySaveService = null,
        IUiDialogService? playlistWorkspaceDialogService = null,
        Action<string>? detailRetentionLog = null)
    {
        var workspace = new PlaylistWorkspaceViewModel(
            dispatchPresentation ?? (action => action()),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            detailRetentionLog ?? (_ => { }),
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceFixturePorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceFixturePorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceFixturePorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceFixturePorts.PlaylistUrlInstallSink,
            browserOpenSink ?? PlaylistWorkspaceFixturePorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceFixturePorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceFixturePorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceFixturePorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceFixturePorts.PlaylistSummaryColumnSettingsStore,
                playlistSummaryBmtSort ?? PlaylistWorkspaceFixturePorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceFixturePorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceFixturePorts.KeywordSearchFavoritesSettingsStore,
            playlistStoreProvider ?? PlaylistWorkspaceFixturePorts.PlaylistStoreProvider,
            propertySaveService ?? PlaylistWorkspaceFixturePorts.PlaylistPropertySaveService,
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
             restoreUiApplyScheduler ?? PlaylistWorkspaceFixturePorts.PlaylistRestoreUiApplyScheduler,
             restoreUiThreadCheck ?? PlaylistWorkspaceFixturePorts.PlaylistRestoreUiThreadCheck,
             playlistWorkspaceDialogService ?? UndeclaredDialogs.Create());
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
        dataSource = new FakePlaylistDetailDataSource();
        workspace.SetDetailDataSource(dataSource);
        return workspace;
    }

    /// <summary>実DBのバックアップ・復元と必要UIを接続します。受付を観測する場合は同じfixture libraryを共有します。</summary>
    /// <param name="databaseScope">復元する接続・ライブラリ・プレイリストが共有するDB所有者。</param>
    /// <param name="libraryOverride">復元のL/Pとモデルのbindingへ共通に渡す、テストが所有するlibrary。</param>
    /// <param name="reloadFailureLog">通知表示などの二次失敗を観測する診断先。省略時は記録しません。</param>
    /// <param name="modelUiScheduler">モデルと復元wrapperの内側だけに渡す配送境界。省略時は共有Dispatcherを維持します。</param>
    internal static PlaylistWorkspaceViewModel CreateBackupWorkspace(
        ApplicationDatabaseScope databaseScope,
        string songDbPath,
        IEnumerable<BMSTable> tables,
        out BMSPlaylist playlist,
        out List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications,
        Func<Action, Task>? restoreUiApplyScheduler = null,
        Func<bool>? restoreUiThreadCheck = null,
        TestBmsLibrary? libraryOverride = null,
        Action<Exception, string>? reloadFailureLog = null, IUiScheduler? modelUiScheduler = null)
    {
        PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
        Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
            });
        TestBmsLibrary library = libraryOverride ?? (modelUiScheduler == null
            ? MainWindowViewModelTestFactory.CreateLibrary(databaseScope, songDbPath, settings)
            : new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new UnexpectedBmsLibraryDialogService(), modelUiScheduler, settings: settings));
        var createdPlaylist = new BMSPlaylist(
            new BmsPlaylistLibraryBindings(library),
            songDbPath, null, null,
            () => new PlaylistUrlCompletionOptionsSnapshot(),
            () => new BeatorajaBmtOptionsSnapshot(),
            () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings),
            TestBmsFactory.MissingEverythingBridge,
            new RestoreUiScheduler(restoreUiApplyScheduler, restoreUiThreadCheck, modelUiScheduler))
        {
            BMSTables = new ObservableCollection<BMSTable>(tables)
        };
        createdPlaylist.StartupBackgroundTaskScheduler = (_, _, _, _) => false;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            playlistStoreProvider: () => createdPlaylist,
            playlistLibraryProvider: () => library,
            reloadFailureLog: reloadFailureLog,
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
    /// <param name="modelUiScheduler">既存callbackの内側でcollectionへ適用する配送境界。省略時だけ共有Dispatcherを選びます。</param>
    internal sealed class RestoreUiScheduler(
        Func<Action, Task>? schedule,
        Func<bool>? threadCheck, IUiScheduler? modelUiScheduler = null) : IUiScheduler
    {
        private readonly IUiScheduler inner = modelUiScheduler ?? new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher);

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
                    // 注入 callback は受付/完了を制御し、collection event は選択したモデル配送境界へ渡す。
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

    internal sealed class RestoreUiOperation(Task completion) : IUiScheduledOperation
    {
        public bool IsAccepted => true;
        public bool IsCompleted => completion.IsCompleted;
        public bool IsAborted => completion.IsCanceled;
        public string RejectionReason => string.Empty;
        public Task Completion => completion;
        public void Abort() => throw new NotSupportedException();
    }
}
