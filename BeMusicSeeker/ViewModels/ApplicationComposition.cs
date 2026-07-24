using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Livet;
using Ribbit.Logging;
using Ribbit.Media.Audio;
using Ribbit.Net;

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

    private readonly Func<InstallDestinationWorkflowSettingsSnapshot> installDestinationSettingsProvider;

    private readonly Func<IBMSPlayer> defaultBmsPlayerFactory;

    private readonly Func<MainWindowViewModel, Task<bool>> initializeOwner;

    private readonly Func<MainWindowViewModel, Task> reloadScoresOnly;

    private readonly Func<MainWindowViewModel, Task> reloadFileDiff;

    private readonly Action<Exception> reportSettingsApplyFailure;

    private readonly IUiDialogService playlistWorkspaceDialogService;

    private readonly Func<Dispatcher> uiDispatcherProvider;

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
        Func<IBMSPlayer> defaultBmsPlayerFactory = null,
        Func<MainWindowViewModel, Task<bool>> initializeOwner = null,
        Func<InstallDestinationWorkflowSettingsSnapshot> installDestinationSettingsProvider = null,
        Func<MainWindowViewModel, Task> reloadScoresOnly = null,
        Action<Exception> reportSettingsApplyFailure = null,
        Func<MainWindowViewModel, Task> reloadFileDiff = null,
        IUiDialogService playlistWorkspaceDialogService = null,
        Func<Dispatcher> uiDispatcherProvider = null)
    {
        this.settingsEditSession = settingsEditSession
            ?? BeMusicSeeker.Models.SettingsEditSession.CreateDefault();
        playbackSettingsStore = new SettingsPlaybackSettingsStore(() => this.settingsEditSession.Values);
        this.defaultBmsPlayerFactory = defaultBmsPlayerFactory
            ?? (() => new InternalBMSAutoPlayerSoundOnly());
        this.initializeOwner = initializeOwner
            ?? (owner => owner.InitializeAsync());
        this.reloadScoresOnly = reloadScoresOnly;
        this.reloadFileDiff = reloadFileDiff;
        this.uiDispatcherProvider = uiDispatcherProvider
            ?? throw new ArgumentNullException(nameof(uiDispatcherProvider));
        this.reportSettingsApplyFailure = reportSettingsApplyFailure;
        this.playlistWorkspaceDialogService = playlistWorkspaceDialogService ?? new UiDialogCoordinator();
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
        this.installDestinationSettingsProvider = installDestinationSettingsProvider
            ?? (() => InstallDestinationWorkflowSettingsSnapshot.CreateCurrent(this.settingsEditSession.Values));
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

    internal Func<MainWindowViewModel, Task<bool>> InitializeOwner => initializeOwner;

    internal Func<MainWindowViewModel, Task> ReloadScoresOnly =>
        reloadScoresOnly ?? (owner => owner.ReloadScoresOnlyAsync());

    internal Func<MainWindowViewModel, Task> ReloadFileDiff =>
        reloadFileDiff ?? (owner => owner.ReloadFileDiffAsync());

    internal IKeywordSearchHistorySettingsStore KeywordSearchHistorySettingsStore => keywordSearchHistorySettingsStore;

    internal IPlayHistoryDisplaySettingsStore PlayHistoryDisplaySettingsStore => playHistoryDisplaySettingsStore;

    internal Func<InstallDestinationWorkflowSettingsSnapshot> InstallDestinationSettingsProvider =>
        installDestinationSettingsProvider;

    internal ISettingsEditSession SettingsEditSession => settingsEditSession;

    internal Func<Dispatcher> UiDispatcherProvider => uiDispatcherProvider;

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
        Action<IReadOnlyList<string>> playlistUrlInstallSink,
        Action<Uri> playlistUrlBrowserOpenSink,
        Action<Exception, string> externalPlaylistImportWarningLog,
        Action<string> externalPlaylistImportInfoLog,
        Action<Exception, string> beatorajaTableUrlImportWarningLog,
        Action<string> beatorajaTableUrlImportInfoLog,
        Func<BMSLibrary> libraryProvider,
        Func<LR2Config> lr2ConfigProvider,
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
        Func<string, Func<Task>, bool> playlistExternalSyncScheduler,
        Func<string, Func<Task>, bool> playlistReferenceApplyScheduler,
        Func<Action, Task> playlistRestoreUiApplyScheduler,
        Func<bool> playlistRestoreUiThreadCheck)
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
            playlistUrlInstallSink,
            playlistUrlBrowserOpenSink,
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
            playlistExternalSyncScheduler,
            playlistReferenceApplyScheduler,
            playlistRestoreUiApplyScheduler,
            playlistRestoreUiThreadCheck,
            this.playlistWorkspaceDialogService);
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

    internal SettingsDialogViewModel CreateSettingDialogViewModel(
        ISettingsDialogStatePort statePort,
        ISettingsDialogWorkspacePort workspacePort,
        ISettingsDialogLibraryPort libraryPort,
        ISettingsDialogPlayHistoryPort playHistoryPort,
        ISettingsDialogSearchRootRuntimePort searchRootRuntimePort,
        ISettingsDialogPlaybackPort playbackPort,
        Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow,
        Func<Task<bool>> initializeOwner,
        Func<Task> reloadScoresOnly,
        Func<Task> reloadFileDiff,
        Action<string> invalidatePlayHistoryReadCache)
    {
        IUiDialogService schemaDialogs = new UiDialogCoordinator();
        return new SettingsDialogViewModel(
            statePort,
            workspacePort,
            libraryPort,
            playHistoryPort,
            searchRootRuntimePort,
            playbackPort,
            lr2SongDbSyncWorkflow,
            reloadSettings,
            saveSettings,
            settingsEditSession,
            initializeOwner,
            reloadScoresOnly,
            reloadFileDiff,
            playHistoryDisplaySettingsStore,
            invalidatePlayHistoryReadCache,
            reportSettingsApplyFailure,
            schemaDialogs: schemaDialogs,
            applicationDataUninstallWorkflow: new ApplicationDataUninstallWorkflowOwner(
                schemaDialogs,
                new Lr2ApplicationDataUninstallStore()),
            audioDeviceTestWorkflow: new AudioDeviceTestWorkflowOwner(
                playbackPort.CreateAudioDeviceTestPlaybackPort(),
                new BassAudioDeviceTestRuntime()));
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
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, IReadOnlyList<ChartPackage>> installPackageBatch,
        Action<Action> dispatchPackageInstallUi,
        Func<BMSLibrary> installDestinationLibraryProvider,
        IUiDialogService installDestinationDialogService,
        StartupProgressWorkflowOwner startupProgressWorkflowOwner,
        Action<Exception> reportPackageInstallWorkflowNotificationFailure = null,
        Func<BMSLibrary, Action<MaintenanceWorkflowProgress>, CancellationToken, MaintenanceWorkflowResult> maintenanceRescanExecutor = null,
        Func<Action, Task> maintenanceRescanScheduler = null,
        Action<string> maintenanceRescanLog = null,
        Action<Exception> reportMaintenanceRescanWorkflowNotificationFailure = null,
        Action<Exception> reportMaintenanceRescanWorkflowFailure = null,
        Func<BMSLibrary, ChartFolderAutoRenameRequest, Action<int, int, string>, FolderAutoRenameExecutionResult> folderAutoRenameSelectedExecutor = null,
        Func<BMSLibrary, string, Action<int, int, string>, FolderAutoRenameExecutionResult> folderAutoRenameAllExecutor = null,
        Func<BMSLibrary, string, bool> folderAutoRenameAllTargetChecker = null,
        Func<Action, Task> folderAutoRenameScheduler = null,
        Action<string> folderAutoRenameLog = null,
        Action<Exception> reportFolderAutoRenameNotificationFailure = null,
        Action<Exception> reportFolderAutoRenameFailure = null,
        IUiDialogService folderAutoRenameDialogService = null,
        ScoreViewerRegistrationWorkflowOwner scoreViewerRegistrationWorkflow = null,
        Func<BMSLibrary> zeroNoteLibraryProvider = null,
        Func<BMSLibrary> packageCatalogLibraryProvider = null,
        IUiDialogService duplicateMaintenanceDialogService = null,
        Func<bool> showDuplicateFileCheckConfirmProvider = null,
        Func<BMSLibrary> duplicateMaintenanceLibraryProvider = null,
        IUiDialogService selectedChartMutationDialogService = null,
        Func<BMSLibrary> selectedChartMutationLibraryProvider = null,
        IUiDialogService selectedChartResourceHealthDialogService = null,
        Func<BMSLibrary> selectedChartResourceHealthLibraryProvider = null,
        IUiDialogService maintenanceRescanDialogService = null,
        Func<BMSLibrary> chartInfoParseFailureRemovalLibraryProvider = null,
        IUiDialogService chartInfoParseFailureRemovalDialogService = null,
        Func<Action, Task> chartInfoParseFailureRemovalScheduler = null,
        Func<SelectedChartAudioConversionSettingsSnapshot> selectedChartAudioConversionSettingsProvider = null,
        Action<EncoderType> selectedChartAudioConversionEncoderFallback = null,
        IUiDialogService selectedChartAudioConversionDialogService = null,
        ISelectedChartAudioConversionExecutor selectedChartAudioConversionExecutor = null,
        Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow = null,
        RankingCacheDownloadWorkflowOwner rankingCacheDownloadWorkflow = null,
        Func<string, bool> selectedChartExternalActionFileExists = null,
        Func<string, ExplorerOpenResult> selectedChartExternalActionExplorerOpen = null,
        Action<string> selectedChartExternalActionAssociatedFileLauncher = null,
        Action<string> selectedChartExternalActionUrlLauncher = null,
        Action<string> libraryFolderTreeLog = null,
        Action<string> libraryFolderTreeLogWarning = null)
    {
        return new MainWindowChildComposition(
            mainChartList,
            playlistWorkspace,
            bmsPlayerFactory,
            uiDispatcherProvider,
            playbackSettingsStore,
            keywordSearchHistorySettingsStore,
            chartFileOperations,
            mainViewLog,
            dispatchMainChartListAction,
            mainViewLogWarning,
            installPackageBatch,
            dispatchPackageInstallUi,
            installDestinationLibraryProvider,
            installDestinationDialogService,
            installDestinationSettingsProvider,
            LongPathFileSystem.DirectoryExists,
            ExplorerOpenService.OpenDirectory,
            startupProgressWorkflowOwner,
            reportPackageInstallWorkflowNotificationFailure,
            maintenanceRescanExecutor,
            maintenanceRescanScheduler,
            maintenanceRescanLog,
            reportMaintenanceRescanWorkflowNotificationFailure,
            reportMaintenanceRescanWorkflowFailure,
            folderAutoRenameSelectedExecutor,
            folderAutoRenameAllExecutor,
            folderAutoRenameAllTargetChecker,
            folderAutoRenameScheduler,
            folderAutoRenameLog,
            reportFolderAutoRenameNotificationFailure,
            reportFolderAutoRenameFailure,
            folderAutoRenameDialogService,
            scoreViewerRegistrationWorkflow ?? CreateScoreViewerRegistrationWorkflowOwner(),
            zeroNoteLibraryProvider,
            packageCatalogLibraryProvider,
            duplicateMaintenanceDialogService,
            showDuplicateFileCheckConfirmProvider,
            duplicateMaintenanceLibraryProvider,
            selectedChartMutationDialogService,
            selectedChartMutationLibraryProvider,
            selectedChartResourceHealthDialogService,
            selectedChartResourceHealthLibraryProvider,
            maintenanceRescanDialogService,
            chartInfoParseFailureRemovalLibraryProvider,
            chartInfoParseFailureRemovalDialogService,
            chartInfoParseFailureRemovalScheduler,
            selectedChartAudioConversionSettingsProvider
                ?? (() => SelectedChartAudioConversionSettingsSnapshot.CreateCurrent(settingsEditSession.Values)),
            selectedChartAudioConversionEncoderFallback
                ?? (encoder => settingsEditSession.Values.Encoder = encoder),
            selectedChartAudioConversionDialogService,
            selectedChartAudioConversionExecutor,
            lr2SongDbSyncWorkflow,
            rankingCacheDownloadWorkflow,
            selectedChartExternalActionFileExists,
            selectedChartExternalActionExplorerOpen,
            selectedChartExternalActionAssociatedFileLauncher,
            selectedChartExternalActionUrlLauncher,
            libraryFolderTreeLog,
            libraryFolderTreeLogWarning);
    }

    private ScoreViewerRegistrationWorkflowOwner CreateScoreViewerRegistrationWorkflowOwner()
    {
        Action<Exception, string> warningLog = (exception, message) =>
        {
            if (exception == null)
            {
                NLogWrapper.FileLogger?.Warn(message);
            }
            else
            {
                NLogWrapper.FileLogger?.Warn(exception, message);
            }
        };
        return new ScoreViewerRegistrationWorkflowOwner(
            new AppScoreViewerRegistrationGateway(),
            new WpfScoreViewerRegistrationInteraction(warningLog),
            () => settingsEditSession.Values.ShowScoreViewerRegisterConfirmMsg,
            warningLog);
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
        Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> getBeatorajaBmtSongHashResolver,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization = null)
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
            customFolderOutputSettingsProvider,
            lr2PlaylistFolderSynchronization);
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
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore,
        ChartFileOperationSynchronizer chartFileOperations,
        Action<string> mainViewLog,
        Action<Action> dispatchMainChartListAction,
        Action<string> mainViewLogWarning,
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, Action, Action<string, int, int>, IReadOnlyList<ChartPackage>> installPackageBatch,
        Action<Action> dispatchPackageInstallUi,
        Func<BMSLibrary> installDestinationLibraryProvider,
        IUiDialogService installDestinationDialogService,
        Func<InstallDestinationWorkflowSettingsSnapshot> installDestinationSettingsProvider,
        Func<string, bool> libraryFolderTreeDirectoryExists,
        Func<string, ExplorerOpenResult> libraryFolderTreeExplorerOpen,
        StartupProgressWorkflowOwner startupProgressWorkflowOwner,
        Action<Exception> reportPackageInstallWorkflowNotificationFailure = null,
        Func<BMSLibrary, Action<MaintenanceWorkflowProgress>, CancellationToken, MaintenanceWorkflowResult> maintenanceRescanExecutor = null,
        Func<Action, Task> maintenanceRescanScheduler = null,
        Action<string> maintenanceRescanLog = null,
        Action<Exception> reportMaintenanceRescanWorkflowNotificationFailure = null,
        Action<Exception> reportMaintenanceRescanWorkflowFailure = null,
        Func<BMSLibrary, ChartFolderAutoRenameRequest, Action<int, int, string>, FolderAutoRenameExecutionResult> folderAutoRenameSelectedExecutor = null,
        Func<BMSLibrary, string, Action<int, int, string>, FolderAutoRenameExecutionResult> folderAutoRenameAllExecutor = null,
        Func<BMSLibrary, string, bool> folderAutoRenameAllTargetChecker = null,
        Func<Action, Task> folderAutoRenameScheduler = null,
        Action<string> folderAutoRenameLog = null,
        Action<Exception> reportFolderAutoRenameNotificationFailure = null,
        Action<Exception> reportFolderAutoRenameFailure = null,
        IUiDialogService folderAutoRenameDialogService = null,
        ScoreViewerRegistrationWorkflowOwner scoreViewerRegistrationWorkflow = null,
        Func<BMSLibrary> zeroNoteLibraryProvider = null,
        Func<BMSLibrary> packageCatalogLibraryProvider = null,
        IUiDialogService duplicateMaintenanceDialogService = null,
        Func<bool> showDuplicateFileCheckConfirmProvider = null,
        Func<BMSLibrary> duplicateMaintenanceLibraryProvider = null,
        IUiDialogService selectedChartMutationDialogService = null,
        Func<BMSLibrary> selectedChartMutationLibraryProvider = null,
        IUiDialogService selectedChartResourceHealthDialogService = null,
        Func<BMSLibrary> selectedChartResourceHealthLibraryProvider = null,
        IUiDialogService maintenanceRescanDialogService = null,
        Func<BMSLibrary> chartInfoParseFailureRemovalLibraryProvider = null,
        IUiDialogService chartInfoParseFailureRemovalDialogService = null,
        Func<Action, Task> chartInfoParseFailureRemovalScheduler = null,
        Func<SelectedChartAudioConversionSettingsSnapshot> selectedChartAudioConversionSettingsProvider = null,
        Action<EncoderType> selectedChartAudioConversionEncoderFallback = null,
        IUiDialogService selectedChartAudioConversionDialogService = null,
        ISelectedChartAudioConversionExecutor selectedChartAudioConversionExecutor = null,
        Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow = null,
        RankingCacheDownloadWorkflowOwner rankingCacheDownloadWorkflow = null,
        Func<string, bool> selectedChartExternalActionFileExists = null,
        Func<string, ExplorerOpenResult> selectedChartExternalActionExplorerOpen = null,
        Action<string> selectedChartExternalActionAssociatedFileLauncher = null,
        Action<string> selectedChartExternalActionUrlLauncher = null,
        Action<string> libraryFolderTreeLog = null,
        Action<string> libraryFolderTreeLogWarning = null)
    {
        MainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        PlaylistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        ProgressHub = new OperationProgressHubViewModel(startupProgressWorkflowOwner);
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
        ChartFilters = new ChartListFilterViewModel(keywordSearchHistorySettingsStore);
        LibraryFolderTree = new LibraryFolderTreeViewModel(
            libraryFolderTreeDirectoryExists,
            libraryFolderTreeExplorerOpen,
            uiDispatcherProvider,
            libraryFolderTreeLog,
            libraryFolderTreeLogWarning);
        InstallTree = new InstallTreeViewModel();
        MaintenanceTree = new MaintenanceTreeViewModel();
        PlayHistory = new PlayHistoryWorkflowOwner();
        PendingPackageWorkflow = new PendingPackageWorkflowOwner(
            installDestinationLibraryProvider ?? throw new ArgumentNullException(nameof(installDestinationLibraryProvider)),
            chartFileOperations,
            PlaybackPanel,
            installDestinationDialogService ?? throw new ArgumentNullException(nameof(installDestinationDialogService)),
            installDestinationSettingsProvider ?? throw new ArgumentNullException(nameof(installDestinationSettingsProvider)));
        RegularChartListOwner = new RegularChartListOwner(
            MainChartList,
            PlaylistWorkspace,
            mainViewLog,
            dispatchMainChartListAction,
            mainViewLogWarning,
            PendingPackageWorkflow);
        PackageInstallWorkflow = new PackageInstallWorkflowOwner(
            installPackageBatch,
            dispatchPackageInstallUi,
            reportPackageInstallWorkflowNotificationFailure);
        MaintenanceRescanWorkflow = new MaintenanceRescanWorkflowOwner(
            maintenanceRescanExecutor ?? MissingMaintenanceRescanExecutor,
            maintenanceRescanScheduler ?? (action => Task.Run(action)),
            dispatchMainChartListAction,
            maintenanceRescanLog,
            reportMaintenanceRescanWorkflowNotificationFailure,
            reportMaintenanceRescanWorkflowFailure,
            dialogs: maintenanceRescanDialogService ?? throw new ArgumentNullException(nameof(maintenanceRescanDialogService)));
        FolderAutoRenameWorkflow = new FolderAutoRenameWorkflowOwner(
            folderAutoRenameSelectedExecutor ?? MissingFolderAutoRenameSelectedExecutor,
            folderAutoRenameAllExecutor ?? MissingFolderAutoRenameAllExecutor,
            folderAutoRenameAllTargetChecker ?? MissingFolderAutoRenameAllTargetChecker,
            folderAutoRenameScheduler ?? (action => Task.Run(action)),
            dispatchMainChartListAction,
            folderAutoRenameDialogService ?? throw new ArgumentNullException(nameof(folderAutoRenameDialogService)),
            folderAutoRenameLog,
            reportFolderAutoRenameNotificationFailure,
            reportFolderAutoRenameFailure);
        UpdateDownloadService updateDownloadService = new();
        UpdateCheckService updateCheckService = new(AppHttpClient.Create(5000));
        StartupUpdateWorkflow = new StartupUpdateWorkflowOwner(
            () => updateCheckService.CheckAsync(CommandLineSwitches.UpdateManifestUrl),
            updateDownloadService.DownloadAndVerifyAsync,
            updateDownloadService.PrepareUpdaterLaunch,
            UpdateDownloadService.CleanupPreviousWorkDirectory,
            packagePath => UpdateDownloadService.TryDeleteDownloadedPackage(
                packagePath,
                exception => NLogWrapper.FileLogger?.Warn(exception, "startup_update package cleanup failed")),
            action => Task.Run(action),
            dispatchMainChartListAction,
            message => NLogWrapper.FileLogger?.Info(message),
            exception => NLogWrapper.FileLogger?.Warn(exception, "startup_update warning"),
            exception => NLogWrapper.FileLogger?.Error(exception, "startup_update error"));
        ElevatedProcessWarningWorkflow = new ElevatedProcessWarningWorkflowOwner(
            ProcessElevationProbe.IsCurrentProcessElevated,
            (exception, context) => NLogWrapper.FileLogger?.Warn(exception, context),
            message => NLogWrapper.FileLogger?.Warn(message));
        ScoreViewerRegistrationWorkflow = scoreViewerRegistrationWorkflow
            ?? throw new ArgumentNullException(nameof(scoreViewerRegistrationWorkflow));
        ZeroNoteMaintenanceWorkflow = new ZeroNoteMaintenanceWorkflowOwner(
            zeroNoteLibraryProvider ?? (() => null),
            chartFileOperations);
        PackageCatalogWorkflow = new PackageCatalogWorkflowOwner(
            packageCatalogLibraryProvider ?? (() => null),
            chartFileOperations,
            installDestinationDialogService);
        DuplicateMaintenanceWorkflow = new DuplicateMaintenanceWorkflowOwner(
            duplicateMaintenanceLibraryProvider ?? throw new ArgumentNullException(nameof(duplicateMaintenanceLibraryProvider)),
            chartFileOperations,
            PlaybackPanel,
            duplicateMaintenanceDialogService ?? throw new ArgumentNullException(nameof(duplicateMaintenanceDialogService)),
            showDuplicateFileCheckConfirmProvider ?? throw new ArgumentNullException(nameof(showDuplicateFileCheckConfirmProvider)),
            LongPathFileSystem.DirectoryExists,
            ExplorerOpenService.OpenDirectory,
            MaintenanceTree.CaptureNextDuplicateGroupHeader);
        SelectedChartMutations = new SelectedChartMutationWorkflowOwner(
            selectedChartMutationLibraryProvider ?? throw new ArgumentNullException(nameof(selectedChartMutationLibraryProvider)),
            chartFileOperations,
            PlaybackPanel,
            selectedChartMutationDialogService ?? throw new ArgumentNullException(nameof(selectedChartMutationDialogService)));
        SelectedChartExternalActions = new SelectedChartExternalActionWorkflowOwner(
            selectedChartExternalActionFileExists ?? LongPathFileSystem.FileExists,
            selectedChartExternalActionExplorerOpen ?? ExplorerOpenService.OpenFileAndSelect,
            selectedChartExternalActionAssociatedFileLauncher ?? LaunchAssociatedFile,
            selectedChartExternalActionUrlLauncher ?? LaunchExternalUrl);
        SelectedChartResourceHealth = new SelectedChartResourceHealthWorkflowOwner(
            selectedChartResourceHealthLibraryProvider ?? throw new ArgumentNullException(nameof(selectedChartResourceHealthLibraryProvider)),
            selectedChartResourceHealthDialogService ?? throw new ArgumentNullException(nameof(selectedChartResourceHealthDialogService)));
        ChartInfoParseFailureRemoval = new ChartInfoParseFailureRemovalWorkflowOwner(
            chartInfoParseFailureRemovalLibraryProvider ?? throw new ArgumentNullException(nameof(chartInfoParseFailureRemovalLibraryProvider)),
            chartInfoParseFailureRemovalDialogService ?? throw new ArgumentNullException(nameof(chartInfoParseFailureRemovalDialogService)),
            chartInfoParseFailureRemovalScheduler ?? (action => Task.Run(action)));
        SelectedChartAudioConversion = new SelectedChartAudioConversionWorkflowOwner(
            selectedChartAudioConversionSettingsProvider ?? throw new ArgumentNullException(nameof(selectedChartAudioConversionSettingsProvider)),
            selectedChartAudioConversionEncoderFallback ?? throw new ArgumentNullException(nameof(selectedChartAudioConversionEncoderFallback)),
            PlaybackPanel,
            selectedChartAudioConversionDialogService ?? new UiDialogCoordinator(),
            selectedChartAudioConversionExecutor);
        Lr2SongDbSyncWorkflow = lr2SongDbSyncWorkflow
            ?? throw new ArgumentNullException(nameof(lr2SongDbSyncWorkflow));
        RankingCacheDownloadWorkflow = rankingCacheDownloadWorkflow
            ?? throw new ArgumentNullException(nameof(rankingCacheDownloadWorkflow));
    }

    internal MainChartListViewModel MainChartList { get; }

    internal PlaylistWorkspaceViewModel PlaylistWorkspace { get; }

    internal OperationProgressHubViewModel ProgressHub { get; }

    internal PlaybackPanelViewModel PlaybackPanel { get; }

    internal ChartListFilterViewModel ChartFilters { get; }

    internal LibraryFolderTreeViewModel LibraryFolderTree { get; }

    internal InstallTreeViewModel InstallTree { get; }

    internal MaintenanceTreeViewModel MaintenanceTree { get; }

    internal PlayHistoryWorkflowOwner PlayHistory { get; }

    internal PendingPackageWorkflowOwner PendingPackageWorkflow { get; }

    internal RegularChartListOwner RegularChartListOwner { get; }

    internal PackageInstallWorkflowOwner PackageInstallWorkflow { get; }

    internal MaintenanceRescanWorkflowOwner MaintenanceRescanWorkflow { get; }

    internal FolderAutoRenameWorkflowOwner FolderAutoRenameWorkflow { get; }

    internal StartupUpdateWorkflowOwner StartupUpdateWorkflow { get; }

    internal ElevatedProcessWarningWorkflowOwner ElevatedProcessWarningWorkflow { get; }

    internal ScoreViewerRegistrationWorkflowOwner ScoreViewerRegistrationWorkflow { get; }

    internal ZeroNoteMaintenanceWorkflowOwner ZeroNoteMaintenanceWorkflow { get; }

    internal PackageCatalogWorkflowOwner PackageCatalogWorkflow { get; }

    internal DuplicateMaintenanceWorkflowOwner DuplicateMaintenanceWorkflow { get; }

    internal SelectedChartMutationWorkflowOwner SelectedChartMutations { get; }

    internal SelectedChartExternalActionWorkflowOwner SelectedChartExternalActions { get; }

    internal SelectedChartResourceHealthWorkflowOwner SelectedChartResourceHealth { get; }

    internal ChartInfoParseFailureRemovalWorkflowOwner ChartInfoParseFailureRemoval { get; }

    internal SelectedChartAudioConversionWorkflowOwner SelectedChartAudioConversion { get; }

    internal Lr2SongDbSyncWorkflowOwner Lr2SongDbSyncWorkflow { get; }

    internal RankingCacheDownloadWorkflowOwner RankingCacheDownloadWorkflow { get; }

    private static void LaunchAssociatedFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(path);
        }
        catch
        {
        }
    }

    private static void LaunchExternalUrl(string url)
    {
        System.Diagnostics.Process.Start(url);
    }

    private static MaintenanceWorkflowResult MissingMaintenanceRescanExecutor(
        BMSLibrary library,
        Action<MaintenanceWorkflowProgress> progress,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Maintenance rescan executor is not configured.");
    }

    private static FolderAutoRenameExecutionResult MissingFolderAutoRenameSelectedExecutor(
        BMSLibrary library,
        ChartFolderAutoRenameRequest request,
        Action<int, int, string> progressReporter)
    {
        throw new InvalidOperationException("Folder auto-rename selected executor is not configured.");
    }

    private static FolderAutoRenameExecutionResult MissingFolderAutoRenameAllExecutor(
        BMSLibrary library,
        string parentDirectory,
        Action<int, int, string> progressReporter)
    {
        throw new InvalidOperationException("Folder auto-rename all executor is not configured.");
    }

    private static bool MissingFolderAutoRenameAllTargetChecker(BMSLibrary library, string parentDirectory)
    {
        throw new InvalidOperationException("Folder auto-rename target checker is not configured.");
    }
}
