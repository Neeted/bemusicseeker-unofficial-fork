using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Markup;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// アプリケーション起動時に ViewModel へ渡す production composition を構築します。
/// </summary>
internal sealed class ApplicationComposition
{
    private readonly Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider;

    private readonly Func<StartupSettingsSnapshot> startupSettingsProvider;

    private readonly Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider;

    private readonly Func<PlaylistUrlAcquisitionOptionsSnapshot> playlistUrlAcquisitionOptionsProvider;

    private readonly Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly IMainChartColumnSettingsStore mainChartColumnSettingsStore;

    private readonly Func<bool> firstStartupProvider;

    private readonly Action completeFirstStartup;

    private readonly Action reloadSettings;

    private readonly Action saveSettings;

    private readonly IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore;

    private readonly IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore;

    private readonly ISettingsEditSession settingsEditSession;

    private readonly IPlaybackSettingsStore playbackSettingsStore;

    private readonly Func<IBMSPlayer> defaultBmsPlayerFactory;

    internal ApplicationComposition(
        Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider = null,
        Func<StartupSettingsSnapshot> startupSettingsProvider = null,
        Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider = null,
        Func<PlaylistUrlAcquisitionOptionsSnapshot> playlistUrlAcquisitionOptionsProvider = null,
        Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider = null,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider = null,
        IMainChartColumnSettingsStore mainChartColumnSettingsStore = null,
        Func<bool> firstStartupProvider = null,
        Action completeFirstStartup = null,
        Action reloadSettings = null,
        Action saveSettings = null,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore = null,
        IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore = null,
        ISettingsEditSession settingsEditSession = null,
        Func<IBMSPlayer> defaultBmsPlayerFactory = null)
    {
        this.settingsEditSession = settingsEditSession
            ?? BeMusicSeeker.Models.SettingsEditSession.CreateDefault();
        playbackSettingsStore = new SettingsPlaybackSettingsStore(() => this.settingsEditSession.Values);
        this.defaultBmsPlayerFactory = defaultBmsPlayerFactory
            ?? (() => new InternalBMSAutoPlayerSoundOnly());
        this.bmsLibraryOptionsProvider = bmsLibraryOptionsProvider
            ?? (() => BmsLibraryOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.startupSettingsProvider = startupSettingsProvider
            ?? (() => StartupSettingsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.playlistUrlCompletionOptionsProvider = playlistUrlCompletionOptionsProvider
            ?? (() => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.playlistUrlAcquisitionOptionsProvider = playlistUrlAcquisitionOptionsProvider
            ?? (() => PlaylistUrlAcquisitionOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.beatorajaBmtOptionsProvider = beatorajaBmtOptionsProvider
            ?? (() => BeatorajaBmtOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider
            ?? (() => CustomFolderOutputSettingsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.mainChartColumnSettingsStore = mainChartColumnSettingsStore
            ?? new SettingsMainChartColumnSettingsStore(() => this.settingsEditSession.Values);
        this.firstStartupProvider = firstStartupProvider
            ?? (() => GetApplication().firstStartup);
        this.completeFirstStartup = completeFirstStartup
            ?? (() => GetApplication().firstStartup = false);
        this.reloadSettings = reloadSettings
            ?? this.settingsEditSession.Reload;
        this.saveSettings = saveSettings
            ?? this.settingsEditSession.Save;
        this.keywordSearchHistorySettingsStore = keywordSearchHistorySettingsStore
            ?? new SettingsKeywordSearchHistorySettingsStore(() => this.settingsEditSession.Values);
        this.playHistoryDisplaySettingsStore = playHistoryDisplaySettingsStore
            ?? new SettingsPlayHistoryDisplaySettingsStore(() => this.settingsEditSession.Values);
    }

    internal Func<BmsLibraryOptionsSnapshot> BmsLibraryOptionsProvider => bmsLibraryOptionsProvider;

    internal Func<StartupSettingsSnapshot> StartupSettingsProvider => startupSettingsProvider;

    internal Func<PlaylistUrlCompletionOptionsSnapshot> PlaylistUrlCompletionOptionsProvider => playlistUrlCompletionOptionsProvider;

    internal Func<PlaylistUrlAcquisitionOptionsSnapshot> PlaylistUrlAcquisitionOptionsProvider => playlistUrlAcquisitionOptionsProvider;

    internal Func<BeatorajaBmtOptionsSnapshot> BeatorajaBmtOptionsProvider => beatorajaBmtOptionsProvider;

    internal Func<CustomFolderOutputSettingsSnapshot> CustomFolderOutputSettingsProvider => customFolderOutputSettingsProvider;

    internal IMainChartColumnSettingsStore MainChartColumnSettingsStore => mainChartColumnSettingsStore;

    internal Func<bool> FirstStartupProvider => firstStartupProvider;

    internal Action CompleteFirstStartup => completeFirstStartup;

    internal Action ReloadSettings => reloadSettings;

    internal Action SaveSettings => saveSettings;

    internal IKeywordSearchHistorySettingsStore KeywordSearchHistorySettingsStore => keywordSearchHistorySettingsStore;

    internal IPlayHistoryDisplaySettingsStore PlayHistoryDisplaySettingsStore => playHistoryDisplaySettingsStore;

    internal ISettingsEditSession SettingsEditSession => settingsEditSession;

    private static App GetApplication()
    {
        return (App)System.Windows.Application.Current;
    }

    internal MainChartListViewModel CreateMainChartListViewModel(
        Action<Action> dispatchPresentationAction,
        Action<string> log)
    {
        return new MainChartListViewModel(
            dispatchPresentationAction,
            log,
            mainChartColumnSettingsStore);
    }

    internal PlaylistWorkspaceViewModel CreatePlaylistWorkspaceViewModel(
        Action<Action> dispatchPresentationAction,
        MainChartListViewModel mainChartList,
        Func<BMSPlaylist> tablesProvider,
        Func<IEnumerable<BMSTable>> tableSnapshotProvider,
        Action<string> detailViewLog,
        Action<string> detailRetentionLog,
        Func<bool> playlistUrlInstallQueueActiveProvider,
        Action<Exception, string> externalPlaylistImportWarningLog,
        Action<string> externalPlaylistImportInfoLog,
        Action<Exception, string> beatorajaTableUrlImportWarningLog,
        Action<string> beatorajaTableUrlImportInfoLog,
        Func<BMSLibrary> libraryProvider,
        Func<LR2Config> lr2ConfigProvider,
        Action<BMSPlaylist.OperationNotificationScope, string> presentPlaylistOperationNotifications,
        Action<string> summaryBulkWarningLog,
        DispatcherCollection<BMSTable> emptyPlaylistTreeSource,
        Func<string, Func<Task>, bool> playlistLibraryIndexPrewarmScheduler,
        Func<bool> playlistReloadCleanupStartupOperableProvider,
        Func<MainViewUpdateMode> playlistReloadCleanupCurrentTreeModeProvider,
        Func<Task> playlistReloadCleanupDispatcherIdleWaiter,
        Func<bool> playlistReloadCleanupShutdownRequestedProvider,
        Action playlistReloadCleanupGarbageCollector,
        Action<string> playlistReloadLog,
        Action<Exception, string> playlistSyncFailureLog,
        Action<Action<bool>> playlistSummaryPresentationRefreshGate = null)
    {
        var playlistPropertySaveService = new PlaylistPropertySaveService(
            tablesProvider,
            libraryProvider,
            lr2ConfigProvider,
            customFolderOutputSettingsProvider);
        var playlistWorkspace = new PlaylistWorkspaceViewModel(
            dispatchPresentationAction,
            mainChartList,
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            detailViewLog,
            detailRetentionLog,
            customFolderOutputSettingsProvider,
            new PlaylistUrlAcquisitionWorkflow(
                new AppPlaylistUrlDownloadGateway(),
                detailViewLog),
            PlaylistExternalPackageLookupService.CreateDefault(),
            playlistUrlAcquisitionOptionsProvider,
            playlistUrlInstallQueueActiveProvider,
            externalPlaylistImportWarningLog,
            externalPlaylistImportInfoLog,
            beatorajaTableUrlImportWarningLog,
            beatorajaTableUrlImportInfoLog,
            mainChartColumnSettingsStore,
            new PlaylistSummaryBmtSortCoordinator(tablesProvider, tableSnapshotProvider),
            keywordSearchHistorySettingsStore,
            tablesProvider,
            playlistPropertySaveService,
            libraryProvider,
            presentPlaylistOperationNotifications,
            lr2ConfigProvider,
            summaryBulkWarningLog,
            emptyPlaylistTreeSource,
            playlistLibraryIndexPrewarmScheduler,
            playlistReloadCleanupStartupOperableProvider,
            playlistReloadCleanupCurrentTreeModeProvider,
            playlistReloadCleanupDispatcherIdleWaiter,
            playlistReloadCleanupShutdownRequestedProvider,
            playlistReloadCleanupGarbageCollector,
            playlistReloadLog,
            playlistSyncFailureLog,
            playlistSummaryPresentationRefreshGate);
        return playlistWorkspace;
    }

    internal IPlaylistDetailDataSource CreatePlaylistDetailDataSource(
        BMSLibrary library,
        BMSPlaylist playlists,
        MainChartListViewModel mainChartList)
    {
        if (mainChartList == null)
        {
            throw new ArgumentNullException(nameof(mainChartList));
        }
        return new PlaylistDetailDataSource(library, playlists, mainChartList.RowProjection);
    }

    internal MainWindowViewModel.SettingDialogViewModel CreateSettingDialogViewModel(MainWindowViewModel owner)
    {
        if (owner == null)
        {
            throw new ArgumentNullException(nameof(owner));
        }
        return new MainWindowViewModel.SettingDialogViewModel(
            owner,
            reloadSettings,
            saveSettings,
            settingsEditSession);
    }

    internal MainWindowChildComposition CreateMainWindowChildComposition(
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        Func<IBMSPlayer> bmsPlayerFactory,
        Func<Dispatcher> uiDispatcherProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        Action<string> mainViewLog,
        Action<Action> dispatchMainChartListAction,
        Action<string> mainViewLogWarning,
        Action<DroppedInstallBatchRequest, System.Threading.CancellationToken> processDroppedInstallBatch,
        Action<DropInstallQueueStatusSnapshot> updateDropInstallQueueStatus,
        Action<Exception> handleDroppedInstallBatchException)
    {
        return new MainWindowChildComposition(
            mainChartList,
            playlistWorkspace,
            bmsPlayerFactory,
            uiDispatcherProvider,
            playbackSettingsStore,
            chartFileOperations,
            mainViewLog,
            dispatchMainChartListAction,
            mainViewLogWarning,
            processDroppedInstallBatch,
            updateDropInstallQueueStatus,
            handleDroppedInstallBatchException);
    }

    internal IBMSPlayer CreateBmsPlayer(
        StartupSettingsSnapshot startupSettings,
        Func<LR2Config> createLr2PlayerConfig)
    {
        if (startupSettings == null)
        {
            throw new ArgumentNullException(nameof(startupSettings));
        }
        if (startupSettings.UsePlayeruBMplay)
        {
            return new uBMplay(startupSettings.uBMplayPath);
        }
        if (startupSettings.UsePlayerBMIIDXView)
        {
            return new BMIIDXView2015(startupSettings.BMIIDXViewPath);
        }
        if (startupSettings.UsePlayerLR2body && File.Exists(startupSettings.LR2bodyPath))
        {
            if (createLr2PlayerConfig == null)
            {
                throw new ArgumentNullException(nameof(createLr2PlayerConfig));
            }
            return new LR2body(startupSettings.LR2bodyPath, createLr2PlayerConfig());
        }
        return null;
    }

    internal IBMSPlayer CreateDefaultBmsPlayer()
    {
        return defaultBmsPlayerFactory()
            ?? throw new InvalidOperationException("Default playback player factory returned null.");
    }

    internal IBMSPlayer CreateBmsPlayerForSettings(BeMusicSeeker.Properties.Settings settingsValues)
    {
        if (settingsValues == null)
        {
            throw new ArgumentNullException(nameof(settingsValues));
        }
        StartupSettingsSnapshot settings = StartupSettingsSnapshot.CreateCurrent(settingsValues);
        IBMSPlayer player = CreateBmsPlayer(
            settings,
            () => new LR2Config(settings.LR2ConfigXmlPath));
        if (player != null)
        {
            return player;
        }
        if (settings.UsePlayerLR2body)
        {
            throw new InvalidOperationException("Configured LR2 playback player could not be created.");
        }
        return CreateDefaultBmsPlayer();
    }

    internal BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile)
    {
        if (libraryProfile == null)
        {
            throw new ArgumentNullException(nameof(libraryProfile));
        }
        return new BMSLibrary(
            libraryProfile.SongDbPath,
            libraryProfile.Lr2ConfigProvider,
            libraryProfile.Lr2ScoreDbPath,
            libraryProfile.StartupRequiredFileScanReason,
            bmsLibraryOptionsProvider);
    }

    internal BMSPlaylist CreateBmsPlaylist(
        LibraryProfile libraryProfile,
        Func<List<BMSScore>> getBMSScores,
        Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> getBeatorajaBmtSongHashResolver)
    {
        if (libraryProfile == null)
        {
            throw new ArgumentNullException(nameof(libraryProfile));
        }
        return new BMSPlaylist(
            libraryProfile.SongDbPath,
            libraryProfile.Lr2ConfigProvider,
            libraryProfile.Lr2ScoreDbPath,
            getBMSScores,
            getBeatorajaBmtSongHashResolver,
            playlistUrlCompletionOptionsProvider,
            beatorajaBmtOptionsProvider,
            customFolderOutputSettingsProvider);
    }

    internal static ApplicationComposition CreateDefault()
    {
        return new ApplicationComposition();
    }

    internal MainWindowViewModel CreateMainWindowViewModel()
    {
        return new MainWindowViewModel(this);
    }
}

/// <summary>
/// Constructs the child graph owned by one main-window ViewModel.
/// </summary>
internal sealed class MainWindowChildComposition
{
    internal MainWindowChildComposition(
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        Func<IBMSPlayer> bmsPlayerFactory,
        Func<Dispatcher> uiDispatcherProvider,
        IPlaybackSettingsStore playbackSettingsStore,
        ChartFileOperationSynchronizer chartFileOperations,
        Action<string> mainViewLog,
        Action<Action> dispatchMainChartListAction,
        Action<string> mainViewLogWarning,
        Action<DroppedInstallBatchRequest, System.Threading.CancellationToken> processDroppedInstallBatch,
        Action<DropInstallQueueStatusSnapshot> updateDropInstallQueueStatus,
        Action<Exception> handleDroppedInstallBatchException)
    {
        MainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        PlaylistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        ProgressHub = new OperationProgressHubViewModel();
        if (bmsPlayerFactory == null)
        {
            throw new ArgumentNullException(nameof(bmsPlayerFactory));
        }
        PlaybackPanel = new PlaybackPanelViewModel(
            bmsPlayerFactory() ?? throw new InvalidOperationException("Playback player factory returned null."),
            new WpfPlaybackUiDispatcher(uiDispatcherProvider),
            new MainChartListPlaybackQueue(MainChartList),
            playbackSettingsStore,
            new WpfPlaybackDialogService(new UiDialogCoordinator()),
            exception => NLogWrapper.TraceLogger?.Warn(exception),
            chartFileOperations);
        ChartFilters = new ChartListFilterViewModel();
        PlayHistory = new PlayHistoryWorkflowOwner();
        RegularChartListOwner = new RegularChartListOwner(
            MainChartList,
            PlaylistWorkspace,
            mainViewLog,
            dispatchMainChartListAction,
            mainViewLogWarning);
        DropInstallQueueProcessor = new DropInstallQueueProcessor(
            processDroppedInstallBatch,
            updateDropInstallQueueStatus,
            handleDroppedInstallBatchException);
    }

    internal MainChartListViewModel MainChartList { get; }

    internal PlaylistWorkspaceViewModel PlaylistWorkspace { get; }

    internal OperationProgressHubViewModel ProgressHub { get; }

    internal PlaybackPanelViewModel PlaybackPanel { get; }

    internal ChartListFilterViewModel ChartFilters { get; }

    internal PlayHistoryWorkflowOwner PlayHistory { get; }

    internal RegularChartListOwner RegularChartListOwner { get; }

    internal DropInstallQueueProcessor DropInstallQueueProcessor { get; }
}

/// <summary>
/// XAML resource から MainWindowViewModel を application composition 経由で生成します。
/// </summary>
public sealed class MainWindowViewModelResourceExtension : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return ApplicationComposition.CreateDefault().CreateMainWindowViewModel();
    }
}
