using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Markup;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// アプリケーション起動時に ViewModel へ渡す production composition を構築します。
/// </summary>
internal sealed class ApplicationComposition
{
    private readonly Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider;

    private readonly Func<StartupSettingsSnapshot> startupSettingsProvider;

    private readonly Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider;

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

    internal ApplicationComposition(
        Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider = null,
        Func<StartupSettingsSnapshot> startupSettingsProvider = null,
        Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider = null,
        Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider = null,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider = null,
        IMainChartColumnSettingsStore mainChartColumnSettingsStore = null,
        Func<bool> firstStartupProvider = null,
        Action completeFirstStartup = null,
        Action reloadSettings = null,
        Action saveSettings = null,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore = null,
        IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore = null,
        ISettingsEditSession settingsEditSession = null)
    {
        this.settingsEditSession = settingsEditSession
            ?? BeMusicSeeker.Models.SettingsEditSession.CreateDefault();
        this.bmsLibraryOptionsProvider = bmsLibraryOptionsProvider
            ?? (() => BmsLibraryOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.startupSettingsProvider = startupSettingsProvider
            ?? (() => StartupSettingsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.playlistUrlCompletionOptionsProvider = playlistUrlCompletionOptionsProvider
            ?? (() => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values));
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
        PlaylistDetailBuildState playlistDetailBuildState,
        PlaylistDetailViewState playlistViewState,
        Action<string> detailViewLog,
        Action<string> detailRetentionLog)
    {
        return new PlaylistWorkspaceViewModel(
            dispatchPresentationAction,
            mainChartList,
            playlistDetailBuildState,
            playlistViewState,
            detailViewLog,
            detailRetentionLog,
            customFolderOutputSettingsProvider);
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
        Func<BMSLibrary> filesProvider,
        Func<BMSPlaylist> tablesProvider,
        Func<LR2Config> lr2ConfigProvider,
        Func<IBMSPlayer> bmsPlayerProvider,
        Func<Dispatcher> uiDispatcherProvider,
        Action<string> mainViewLog,
        Action<Action> dispatchMainChartListAction,
        Action<string> mainViewLogWarning,
        Func<IEnumerable<BMSTable>> tableSnapshotProvider,
        Func<string, bool, bool, long> refreshPlaylistSummary,
        Action<DroppedInstallBatchRequest, System.Threading.CancellationToken> processDroppedInstallBatch,
        Action<DropInstallQueueStatusSnapshot> updateDropInstallQueueStatus,
        Action<Exception> handleDroppedInstallBatchException)
    {
        return new MainWindowChildComposition(
            mainChartList,
            playlistWorkspace,
            mainChartColumnSettingsStore,
            filesProvider,
            tablesProvider,
            lr2ConfigProvider,
            bmsPlayerProvider,
            uiDispatcherProvider,
            mainViewLog,
            dispatchMainChartListAction,
            mainViewLogWarning,
            tableSnapshotProvider,
            refreshPlaylistSummary,
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
        return new InternalBMSAutoPlayerSoundOnly();
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
        IMainChartColumnSettingsStore mainChartColumnSettingsStore,
        Func<BMSLibrary> filesProvider,
        Func<BMSPlaylist> tablesProvider,
        Func<LR2Config> lr2ConfigProvider,
        Func<IBMSPlayer> bmsPlayerProvider,
        Func<Dispatcher> uiDispatcherProvider,
        Action<string> mainViewLog,
        Action<Action> dispatchMainChartListAction,
        Action<string> mainViewLogWarning,
        Func<IEnumerable<BMSTable>> tableSnapshotProvider,
        Func<string, bool, bool, long> refreshPlaylistSummary,
        Action<DroppedInstallBatchRequest, System.Threading.CancellationToken> processDroppedInstallBatch,
        Action<DropInstallQueueStatusSnapshot> updateDropInstallQueueStatus,
        Action<Exception> handleDroppedInstallBatchException)
    {
        MainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        PlaylistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        ProgressHub = new OperationProgressHubViewModel();
        PlaybackPanel = new PlaybackPanelViewModel();
        ChartFilters = new ChartListFilterViewModel();
        RuntimeContext = new MainWindowRuntimeContext(
            filesProvider,
            tablesProvider,
            lr2ConfigProvider,
            bmsPlayerProvider,
            uiDispatcherProvider);
        PlayHistory = new PlayHistoryWorkflowOwner();
        PlaylistSummaryColumns = new PlaylistSummaryColumnSettingsCoordinator(
            PlaylistWorkspace,
            mainChartColumnSettingsStore);
        PlaylistSummaryBmtSort = new PlaylistSummaryBmtSortCoordinator(
            tablesProvider,
            tableSnapshotProvider,
            refreshPlaylistSummary);
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

    internal MainWindowRuntimeContext RuntimeContext { get; }

    internal PlayHistoryWorkflowOwner PlayHistory { get; }

    internal PlaylistSummaryColumnSettingsCoordinator PlaylistSummaryColumns { get; }

    internal PlaylistSummaryBmtSortCoordinator PlaylistSummaryBmtSort { get; }

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
