using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
internal sealed class ApplicationComposition : ISettingsDialogPlayerFactoryPort, IStartupLibraryFactory
{
    /// <summary>Shares optional FS/DB terminal reporting across feature and view consumers.</summary>
    internal IUiDialogService FileDbMutationDialogs { get; }

    /// <summary>Gets the App-owned terminal save warning callback, usable after ordinary dialog shutdown.</summary>
    internal Action<Exception> ReportTerminalSettingsSaveFailure { get; }

    /// <summary>終了時に再起動失敗を通知するダイアログサービスを取得します。</summary>
    internal IUiDialogService RestartFailureDialogs { get; }

    /// <summary>再起動失敗を通知できなかった場合の既存の失敗報告処理を取得します。</summary>
    internal Action<Exception> ReportRestartFailure { get; }

    /// <summary>設定画面が確認と通知に使用するダイアログサービスを取得します。</summary>
    internal IUiDialogService SettingsDialogService { get; }

    private readonly Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider;

    private readonly Func<StartupSettingsSnapshot> startupSettingsProvider;

    private readonly Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider;

    private readonly Func<PlaylistUrlAcquisitionOptionsSnapshot> playlistUrlAcquisitionOptionsProvider;

    private readonly Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly IMainChartColumnSettingsStore mainChartColumnSettingsStore;

    private readonly IMainWindowViewSettingsStore mainWindowViewSettingsStore;

    private readonly IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore;

    private readonly IKeywordSearchFavoritesSettingsStore keywordSearchFavoritesSettingsStore;

    private readonly IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore;

    private readonly ISettingsEditSession settingsEditSession;

    private readonly IPlaybackSettingsStore playbackSettingsStore;

    private readonly IPlayerSettingsGateway playerSettingsGateway;

    private readonly IAudioSettingsGateway audioSettingsGateway;

    private readonly IAudioDeviceCatalog audioDeviceCatalog;

    private readonly Func<InstallDestinationWorkflowSettingsSnapshot> installDestinationSettingsProvider;

    private readonly Func<IBMSPlayer> defaultBmsPlayerFactory;

    private readonly Action<Exception> reportSettingsApplyFailure;

    private readonly IUiDialogService playlistWorkspaceDialogService;

    private readonly IUiScheduler uiScheduler;

    private readonly IApplicationLifetimePort applicationLifetime;

    private readonly ICultureCatalog cultureCatalog;

    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    private readonly IExternalShellGateway externalShellGateway;

    private readonly IExternalProgramLaunchGateway externalProgramLaunchGateway;

    private readonly IExternalPlayerProcessGateway externalPlayerProcessGateway;

    private readonly IUpdaterProcessGateway updaterProcessGateway;

    private readonly IScoreViewerRegistrationGateway scoreViewerRegistrationGateway;

    private readonly IPackageInstallMutationPort packageInstallMutationPort;

    /// <summary>Creates the application composition with replaceable process and audio catalog boundaries.</summary>
    /// <param name="scoreViewerRegistrationGateway">Score Viewer 登録の network boundary。未指定時は production gateway を使います。</param>
    /// <param name="keywordSearchFavoritesSettingsStore">Keyword search Favorites persistence boundary。</param>
    /// <param name="fileDbMutationDialogService">Shared optional mutation-report presentation boundary.</param>
    /// <param name="reportTerminalSettingsSaveFailure">App-owned terminal settings warning; omitted by compositions without a terminal UI.</param>
    /// <param name="restartFailureDialogs">終了時の再起動失敗通知。省略時は既存のダイアログ調停処理を使います。</param>
    /// <param name="reportRestartFailure">再起動失敗を通知できない場合の報告処理。</param>
    /// <param name="settingsDialogService">設定画面の確認と通知。省略時は既存のダイアログ調停処理を使います。</param>
    /// <param name="packageInstallMutationPort">パッケージの変更処理。省略時は既存のライブラリ変更処理を使います。</param>
    internal ApplicationComposition(
        Func<BmsLibraryOptionsSnapshot> bmsLibraryOptionsProvider = null,
        Func<StartupSettingsSnapshot> startupSettingsProvider = null,
        Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider = null,
        Func<PlaylistUrlAcquisitionOptionsSnapshot> playlistUrlAcquisitionOptionsProvider = null,
        Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider = null,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider = null,
        IMainChartColumnSettingsStore mainChartColumnSettingsStore = null,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore = null,
        IPlayHistoryDisplaySettingsStore playHistoryDisplaySettingsStore = null,
        ISettingsEditSession settingsEditSession = null,
        Func<IBMSPlayer> defaultBmsPlayerFactory = null,
        Func<InstallDestinationWorkflowSettingsSnapshot> installDestinationSettingsProvider = null,
        Action<Exception> reportSettingsApplyFailure = null,
        IUiDialogService playlistWorkspaceDialogService = null,
        IUiScheduler uiScheduler = null,
        IApplicationLifetimePort applicationLifetime = null,
        ICultureCatalog cultureCatalog = null,
        ApplicationPathSnapshot applicationPathSnapshot = null,
        IExternalShellGateway externalShellGateway = null,
        IExternalPlayerProcessGateway externalPlayerProcessGateway = null,
        IUpdaterProcessGateway updaterProcessGateway = null,
        IAudioDeviceCatalog audioDeviceCatalog = null,
        IScoreViewerRegistrationGateway scoreViewerRegistrationGateway = null,
        IExternalProgramLaunchGateway externalProgramLaunchGateway = null,
        IKeywordSearchFavoritesSettingsStore keywordSearchFavoritesSettingsStore = null,
        IUiDialogService fileDbMutationDialogService = null,
        Action<Exception> reportTerminalSettingsSaveFailure = null,
        IUiDialogService restartFailureDialogs = null,
        Action<Exception> reportRestartFailure = null,
        IUiDialogService settingsDialogService = null,
        IPackageInstallMutationPort packageInstallMutationPort = null)
    {
        this.settingsEditSession = settingsEditSession
            ?? BeMusicSeeker.Models.SettingsEditSession.CreateDefault();
        this.applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
        this.cultureCatalog = cultureCatalog
            ?? throw new ArgumentNullException(nameof(cultureCatalog));
        this.applicationPathSnapshot = applicationPathSnapshot ?? ApplicationPathPolicy.Current;
        this.externalShellGateway = externalShellGateway ?? ExternalShellGatewayPolicy.Current;
        this.externalProgramLaunchGateway = externalProgramLaunchGateway ?? ExternalProgramLaunchGatewayPolicy.Current;
        this.externalPlayerProcessGateway = externalPlayerProcessGateway ?? ExternalPlayerProcessGatewayPolicy.Current;
        this.updaterProcessGateway = updaterProcessGateway ?? UpdaterProcessGatewayPolicy.Current;
        this.scoreViewerRegistrationGateway = scoreViewerRegistrationGateway ?? new AppScoreViewerRegistrationGateway();
        playbackSettingsStore = new SettingsPlaybackSettingsStore(() => this.settingsEditSession.Values);
        playerSettingsGateway = new SettingsPlayerSettingsGateway(() => this.settingsEditSession.Values);
        audioSettingsGateway = new SettingsAudioGateway(() => this.settingsEditSession.Values);
        this.audioDeviceCatalog = audioDeviceCatalog ?? new BassAudioDeviceCatalog();
        this.defaultBmsPlayerFactory = defaultBmsPlayerFactory
            ?? (() => new InternalBMSAutoPlayerSoundOnly(playerSettingsGateway, new BassAudioPlaybackRuntime()));
        this.uiScheduler = uiScheduler
            ?? throw new ArgumentNullException(nameof(uiScheduler));
        this.reportSettingsApplyFailure = reportSettingsApplyFailure;
        ReportTerminalSettingsSaveFailure = reportTerminalSettingsSaveFailure;
        RestartFailureDialogs = restartFailureDialogs ?? new UiDialogCoordinator();
        ReportRestartFailure = reportRestartFailure ?? reportSettingsApplyFailure;
        SettingsDialogService = settingsDialogService ?? new UiDialogCoordinator();
        this.packageInstallMutationPort = packageInstallMutationPort;
        this.playlistWorkspaceDialogService = playlistWorkspaceDialogService ?? new UiDialogCoordinator();
        FileDbMutationDialogs = fileDbMutationDialogService ?? new UiDialogCoordinator();
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
        mainWindowViewSettingsStore = new SettingsMainWindowViewSettingsStore(() => this.settingsEditSession.Values);
        this.keywordSearchHistorySettingsStore = keywordSearchHistorySettingsStore
            ?? new SettingsKeywordSearchHistorySettingsStore(() => this.settingsEditSession.Values);
        this.keywordSearchFavoritesSettingsStore = keywordSearchFavoritesSettingsStore
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

    internal IMainWindowViewSettingsStore MainWindowViewSettingsStore => mainWindowViewSettingsStore;

    internal IKeywordSearchHistorySettingsStore KeywordSearchHistorySettingsStore => keywordSearchHistorySettingsStore;

    /// <summary>
    /// Gets the settings boundary used to persist normal and summary Favorites.
    /// </summary>
    internal IKeywordSearchFavoritesSettingsStore KeywordSearchFavoritesSettingsStore => keywordSearchFavoritesSettingsStore;

    internal IPlayHistoryDisplaySettingsStore PlayHistoryDisplaySettingsStore => playHistoryDisplaySettingsStore;

    internal Func<InstallDestinationWorkflowSettingsSnapshot> InstallDestinationSettingsProvider =>
        installDestinationSettingsProvider;

    internal ISettingsEditSession SettingsEditSession => settingsEditSession;

    internal IUiScheduler UiScheduler => uiScheduler;

    internal IApplicationLifetimePort ApplicationLifetime => applicationLifetime;

    internal ICultureCatalog CultureCatalog => cultureCatalog;

    internal ApplicationPathSnapshot ApplicationPathSnapshot => applicationPathSnapshot;

    internal IExternalShellGateway ExternalShellGateway => externalShellGateway;

    internal IExternalProgramLaunchGateway ExternalProgramLaunchGateway => externalProgramLaunchGateway;


    internal MainChartListViewModel CreateMainChartListViewModel(
        Action<Action> dispatchPresentationAction,
        Action<string> log)
    {
        return new MainChartListViewModel(
            dispatchPresentationAction,
            log,
            mainChartColumnSettingsStore);
    }

    /// <summary>プレイリストの通信・表示と、受理結果を返す共通導入受付を接続します。</summary>
    internal PlaylistWorkspaceViewModel CreatePlaylistWorkspaceViewModel(
        Action<Action> dispatchPresentationAction,
        MainChartListViewModel mainChartList,
        Func<BMSPlaylist> tablesProvider,
        Func<IEnumerable<BMSTable>> tableSnapshotProvider,
        Action<string> detailViewLog,
        Action<string> detailRetentionLog,
        Func<bool> playlistUrlInstallQueueActiveProvider,
        Func<IReadOnlyList<string>, bool> playlistUrlInstallSink,
        Action<Uri> playlistUrlBrowserOpenSink,
        Action<Exception, string> externalPlaylistImportWarningLog,
        Action<string> externalPlaylistImportInfoLog,
        Action<Exception, string> beatorajaTableUrlImportWarningLog,
        Action<string> beatorajaTableUrlImportInfoLog,
        Func<BMSLibrary> libraryProvider,
        Func<LR2Config> lr2ConfigProvider,
        Action<string> summaryBulkWarningLog,
        ObservableCollection<BMSTable> emptyPlaylistTreeSource,
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
            keywordSearchFavoritesSettingsStore,
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
        ISettingsDialogCustomFolderOutputPort customFolderOutputPort,
        ISettingsDialogPlayHistoryPort playHistoryPort,
        ISettingsDialogSearchRootRuntimePort searchRootRuntimePort,
        ISettingsDialogPlayerFactoryPort playerFactoryPort,
        ISettingsDialogPlaybackRuntimePort playbackRuntimePort,
        Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow,
        Func<OperationModeRestartRequest, Task<bool>> requestOperationModeRestart = null)
    {
        IUiDialogService schemaDialogs = SettingsDialogService;
        ILr2PlayHistorySchemaUninstallDialogPort schemaWindowDialogs = new Lr2PlayHistorySchemaUninstallDialogPort(schemaDialogs);
        return new SettingsDialogViewModel(
            statePort,
            workspacePort,
            customFolderOutputPort,
            playHistoryPort,
            searchRootRuntimePort,
            playerFactoryPort,
            playbackRuntimePort,
            lr2SongDbSyncWorkflow,
            settingsEditSession,
            playHistoryDisplaySettingsStore,
            reportSettingsApplyFailure,
            requestOperationModeRestart: requestOperationModeRestart,
            applicationLifetime: applicationLifetime,
            cultureCatalog: cultureCatalog,
            externalShellGateway: externalShellGateway,
            applicationPathSnapshot: applicationPathSnapshot,
            schemaDialogs: schemaDialogs,
            schemaWindowDialogs: schemaWindowDialogs,
            applicationDataUninstallWorkflow: new ApplicationDataUninstallWorkflowOwner(
                schemaDialogs,
                new Lr2ApplicationDataUninstallStore()),
            audioDeviceTestWorkflow: new AudioDeviceTestWorkflowOwner(
                playbackRuntimePort,
                new BassAudioDeviceTestRuntime(applicationPathSnapshot)),
            audioDeviceCatalog: audioDeviceCatalog,
            audioSettingsGateway: audioSettingsGateway);
    }

    internal MainWindowChildComposition CreateMainWindowChildComposition(
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        Func<IBMSPlayer> bmsPlayerFactory,
        ChartFileOperationSynchronizer chartFileOperations,
        Action<string> mainViewLog,
        Action<Action> dispatchMainChartListAction,
        Action<string> mainViewLogWarning,
        Func<Action, bool> dispatchPackageInstallUi,
        Func<BMSLibrary> installDestinationLibraryProvider,
        IUiDialogService installDestinationDialogService,
        StartupProgressWorkflowOwner startupProgressWorkflowOwner,
        Action<Exception> reportPackageInstallWorkflowNotificationFailure = null,
        Func<BMSLibrary, Action<MaintenanceWorkflowProgress>, CancellationToken, MaintenanceWorkflowResult> maintenanceRescanExecutor = null,
        Func<Action, Task> maintenanceRescanScheduler = null,
        Action<string> maintenanceRescanLog = null,
        Action<Exception> reportMaintenanceRescanWorkflowNotificationFailure = null,
        Action<Exception> reportMaintenanceRescanWorkflowFailure = null,
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
        Action<string> libraryFolderTreeLog = null,
        Action<string> libraryFolderTreeLogWarning = null,
        IExternalShellGateway externalShellGateway = null,
        IPackageInstallMutationPort packageInstallMutationPort = null)
    {
        externalShellGateway ??= this.externalShellGateway;
        return new MainWindowChildComposition(
            selectedChartAudioConversionExecutor ?? new BassSelectedChartAudioConversionExecutor(),
            mainChartList,
            playlistWorkspace,
            bmsPlayerFactory,
            this.uiScheduler,
            playbackSettingsStore,
            keywordSearchHistorySettingsStore,
            chartFileOperations,
            mainViewLog,
            dispatchMainChartListAction,
            mainViewLogWarning,
            dispatchPackageInstallUi,
            installDestinationLibraryProvider,
            installDestinationDialogService,
            installDestinationSettingsProvider,
            LongPathFileSystem.DirectoryExists,
            externalShellGateway.OpenDirectory,
            startupProgressWorkflowOwner,
            reportPackageInstallWorkflowNotificationFailure,
            maintenanceRescanExecutor,
            maintenanceRescanScheduler,
            maintenanceRescanLog,
            reportMaintenanceRescanWorkflowNotificationFailure,
            reportMaintenanceRescanWorkflowFailure,
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
            selectedChartMutationDialogService == null
                ? null
                : new PendingDeleteConfirmationDialogPort(selectedChartMutationDialogService),
            selectedChartMutationLibraryProvider,
            selectedChartResourceHealthDialogService,
            selectedChartResourceHealthLibraryProvider,
            maintenanceRescanDialogService,
            chartInfoParseFailureRemovalLibraryProvider,
            chartInfoParseFailureRemovalDialogService,
            chartInfoParseFailureRemovalScheduler,
            selectedChartAudioConversionSettingsProvider
                ?? (() => SelectedChartAudioConversionSettingsSnapshot.CreateCurrent(audioSettingsGateway.CaptureEncodingSettings())),
            selectedChartAudioConversionEncoderFallback
                ?? audioSettingsGateway.ApplyEncoderFallback,
            selectedChartAudioConversionDialogService,
            lr2SongDbSyncWorkflow,
            rankingCacheDownloadWorkflow,
            selectedChartExternalActionFileExists,
            libraryFolderTreeLog,
            libraryFolderTreeLogWarning,
            externalShellGateway,
            this.applicationPathSnapshot,
            this.updaterProcessGateway,
            settingsProvider: () => this.settingsEditSession.Values,
            externalProgramLaunchGateway: this.externalProgramLaunchGateway,
            keywordSearchFavoritesSettingsStore: this.keywordSearchFavoritesSettingsStore,
            packageInstallMutationPort: packageInstallMutationPort ?? this.packageInstallMutationPort);
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
            scoreViewerRegistrationGateway,
            new WpfScoreViewerRegistrationInteraction(
                warningLog,
                externalShellGateway,
                playlistWorkspaceDialogService),
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
            return new uBMplay(startupSettings.uBMplayPath, playerSettingsGateway, externalPlayerProcessGateway);
        }
        if (startupSettings.UsePlayerBMIIDXView)
        {
            return new BMIIDXView2015(startupSettings.BMIIDXViewPath, playerSettingsGateway, externalPlayerProcessGateway);
        }
        if (startupSettings.UsePlayerLR2body && File.Exists(startupSettings.LR2bodyPath))
        {
            if (createLr2PlayerConfig == null)
            {
                throw new ArgumentNullException(nameof(createLr2PlayerConfig));
            }
            return new LR2body(startupSettings.LR2bodyPath, createLr2PlayerConfig(), playerSettingsGateway, externalPlayerProcessGateway);
        }
        return null;
    }

    internal IBMSPlayer CreateDefaultBmsPlayer()
    {
        return defaultBmsPlayerFactory()
            ?? throw new InvalidOperationException("Default playback player factory returned null.");
    }

    internal IBMSPlayer CreateBmsPlayerForSettings(StartupSettingsSnapshot settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
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

    IBMSPlayer ISettingsDialogPlayerFactoryPort.CreateDefaultBmsPlayer()
        => CreateDefaultBmsPlayer();

    IBMSPlayer ISettingsDialogPlayerFactoryPort.CreateBmsPlayerForSettings(StartupSettingsSnapshot settings)
        => CreateBmsPlayerForSettings(settings);

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
            bmsLibraryOptionsProvider,
            uiScheduler,
            applicationPathSnapshot);
    }

    /// <summary>
    /// Creates the startup playlist from the library created for the same profile.
    /// </summary>
    /// <param name="libraryProfile">The immutable profile captured for startup.</param>
    /// <param name="library">The library created from <paramref name="libraryProfile"/>.</param>
    /// <returns>The constructed startup playlist.</returns>
    internal BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library)
    {
        if (libraryProfile == null)
        {
            throw new ArgumentNullException(nameof(libraryProfile));
        }
        if (library == null)
        {
            throw new ArgumentNullException(nameof(library));
        }

        return new BMSPlaylist(
            new BmsPlaylistLibraryBindings(library),
            libraryProfile.SongDbPath,
            libraryProfile.Lr2ConfigProvider,
            libraryProfile.Lr2ScoreDbPath,
            playlistUrlCompletionOptionsProvider,
            beatorajaBmtOptionsProvider,
            customFolderOutputSettingsProvider,
            applicationPathSnapshot,
            uiScheduler);
    }

    BMSLibrary IStartupLibraryFactory.CreateBmsLibrary(LibraryProfile libraryProfile)
        => CreateBmsLibrary(libraryProfile);

    BMSPlaylist IStartupLibraryFactory.CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library)
        => CreateBmsPlaylist(libraryProfile, library);

    internal MainWindowViewModel CreateMainWindowViewModel()
    {
        return new MainWindowViewModel(this, this);
    }
}

/// <summary>
/// Constructs the child graph owned by one main-window ViewModel.
/// </summary>
internal sealed class MainWindowChildComposition
{
    /// <summary>
    /// Creates the child owner graph with explicit search persistence boundaries.
    /// </summary>
    /// <param name="keywordSearchFavoritesSettingsStore">The explicit normal/summary Favorites settings boundary.</param>
    internal MainWindowChildComposition(
        ISelectedChartAudioConversionExecutor selectedChartAudioConversionExecutor,
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        Func<IBMSPlayer> bmsPlayerFactory,
        IUiScheduler uiScheduler,
        IPlaybackSettingsStore playbackSettingsStore,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore,
        ChartFileOperationSynchronizer chartFileOperations,
        Action<string> mainViewLog,
        Action<Action> dispatchMainChartListAction,
        Action<string> mainViewLogWarning,
        Func<Action, bool> dispatchPackageInstallUi,
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
        IPendingDeleteConfirmationDialogPort selectedChartMutationPendingDeleteDialogPort = null,
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
        Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow = null,
        RankingCacheDownloadWorkflowOwner rankingCacheDownloadWorkflow = null,
        Func<string, bool> selectedChartExternalActionFileExists = null,
        Action<string> libraryFolderTreeLog = null,
        Action<string> libraryFolderTreeLogWarning = null,
        IExternalShellGateway externalShellGateway = null,
        ApplicationPathSnapshot applicationPathSnapshot = null,
        IUpdaterProcessGateway updaterProcessGateway = null,
        Func<BeMusicSeeker.Properties.Settings> settingsProvider = null,
        IExternalProgramLaunchGateway externalProgramLaunchGateway = null,
        IKeywordSearchFavoritesSettingsStore keywordSearchFavoritesSettingsStore = null,
        IPackageInstallMutationPort packageInstallMutationPort = null)
    {
        MainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        PlaylistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        externalShellGateway ??= ExternalShellGatewayPolicy.Current;
        ChartMutationActivity = new ChartMutationActivityOwner();
        ProgressHub = new OperationProgressHubViewModel(startupProgressWorkflowOwner);
        if (bmsPlayerFactory == null)
        {
            throw new ArgumentNullException(nameof(bmsPlayerFactory));
        }
        PlaybackPanel = new PlaybackPanelViewModel(
            bmsPlayerFactory() ?? throw new InvalidOperationException("Playback player factory returned null."),
            new WpfPlaybackUiDispatcher(uiScheduler),
            new MainChartListPlaybackQueue(MainChartList),
            playbackSettingsStore,
            new WpfPlaybackDialogService(new UiDialogCoordinator()),
            exception => NLogWrapper.TraceLogger?.Warn(exception),
            chartFileOperations);
        ChartFilters = new ChartListFilterViewModel(
            keywordSearchHistorySettingsStore,
            keywordSearchFavoritesSettingsStore);
        LibraryFolderTree = new LibraryFolderTreeViewModel(
            libraryFolderTreeDirectoryExists,
            libraryFolderTreeExplorerOpen,
            uiScheduler,
            libraryFolderTreeLog,
            libraryFolderTreeLogWarning);
        InstallTree = new InstallTreeViewModel();
        MaintenanceTree = new MaintenanceTreeViewModel();
        PlayHistory = new PlayHistoryWorkflowOwner(mainViewLog);
        PendingPackageWorkflow = new PendingPackageWorkflowOwner(
            installDestinationLibraryProvider ?? throw new ArgumentNullException(nameof(installDestinationLibraryProvider)),
            chartFileOperations,
            ChartMutationActivity,
            PlaybackPanel,
            installDestinationDialogService ?? throw new ArgumentNullException(nameof(installDestinationDialogService)),
            installDestinationSettingsProvider ?? throw new ArgumentNullException(nameof(installDestinationSettingsProvider)),
            externalShellGateway: externalShellGateway);
        RegularChartListOwner = new RegularChartListOwner(
            MainChartList,
            PlaylistWorkspace,
            mainViewLog,
            dispatchMainChartListAction,
            mainViewLogWarning,
            PendingPackageWorkflow,
            chartFileOperations,
            ChartMutationActivity,
            (IFolderAutoRenamePlaybackPort)PlaybackPanel,
            uiScheduler,
            installDestinationDialogService);
        PackageInstallWorkflow = new PackageInstallWorkflowOwner(
            installDestinationDialogService,
            chartFileOperations,
            ChartMutationActivity,
            packageInstallMutationPort ?? new BmsLibraryPackageInstallMutationPort(),
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
            chartFileOperations,
            ChartMutationActivity,
            new BmsLibraryFolderAutoRenameMutationPort(),
            (IFolderAutoRenamePlaybackPort)PlaybackPanel,
            folderAutoRenameScheduler ?? (action => Task.Run(action)),
            dispatchMainChartListAction,
            folderAutoRenameDialogService ?? throw new ArgumentNullException(nameof(folderAutoRenameDialogService)),
            folderAutoRenameLog,
            reportFolderAutoRenameNotificationFailure,
            reportFolderAutoRenameFailure);
        ProgressHub.AttachWorkflowProgressSources(
            PackageInstallWorkflow,
            MaintenanceRescanWorkflow,
            FolderAutoRenameWorkflow);
        UpdateDownloadService updateDownloadService = new(
            applicationPathSnapshot ?? throw new ArgumentNullException(nameof(applicationPathSnapshot)),
            updaterProcessGateway ?? throw new ArgumentNullException(nameof(updaterProcessGateway)));
        UpdateCheckService updateCheckService = new(AppHttpClient.Create(5000));
        StartupUpdateWorkflow = new StartupUpdateWorkflowOwner(
            () => updateCheckService.CheckAsync(CommandLineSwitches.UpdateManifestUrl),
            updateDownloadService.DownloadAndVerifyAsync,
            updateDownloadService.PrepareUpdaterLaunch,
            updateDownloadService.CleanupPreviousWorkDirectory,
            packagePath => updateDownloadService.TryDeleteDownloadedPackage(
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
            ChartMutationActivity,
            installDestinationDialogService,
            mutation => Task.Run(mutation));
        DuplicateMaintenanceWorkflow = new DuplicateMaintenanceWorkflowOwner(
            duplicateMaintenanceLibraryProvider ?? throw new ArgumentNullException(nameof(duplicateMaintenanceLibraryProvider)),
            chartFileOperations,
            ChartMutationActivity,
            PlaybackPanel,
            duplicateMaintenanceDialogService ?? throw new ArgumentNullException(nameof(duplicateMaintenanceDialogService)),
            showDuplicateFileCheckConfirmProvider ?? throw new ArgumentNullException(nameof(showDuplicateFileCheckConfirmProvider)),
            LongPathFileSystem.DirectoryExists,
            externalShellGateway.OpenDirectory,
            MaintenanceTree.CaptureNextDuplicateGroupHeader);
        SelectedChartMutations = new SelectedChartMutationWorkflowOwner(
            selectedChartMutationLibraryProvider ?? throw new ArgumentNullException(nameof(selectedChartMutationLibraryProvider)),
            chartFileOperations,
            ChartMutationActivity,
            PlaybackPanel,
            selectedChartMutationDialogService ?? throw new ArgumentNullException(nameof(selectedChartMutationDialogService)),
            selectedChartMutationPendingDeleteDialogPort ?? throw new ArgumentNullException(nameof(selectedChartMutationPendingDeleteDialogPort)));
        Func<string, bool> externalActionFileExists = selectedChartExternalActionFileExists ?? LongPathFileSystem.FileExists;
        SelectedChartExternalActions = new SelectedChartExternalActionWorkflowOwner(
            externalActionFileExists,
            externalShellGateway,
            settingsProvider: settingsProvider,
            externalProgramLaunchGateway: externalProgramLaunchGateway
                ?? new WindowsExternalProgramLaunchGateway(externalActionFileExists));
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

    internal ChartMutationActivityOwner ChartMutationActivity { get; }

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

    private static MaintenanceWorkflowResult MissingMaintenanceRescanExecutor(
        BMSLibrary library,
        Action<MaintenanceWorkflowProgress> progress,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Maintenance rescan executor is not configured.");
    }

}
