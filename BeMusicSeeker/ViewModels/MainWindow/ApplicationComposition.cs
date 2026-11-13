using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;
using Ribbit.Media.Audio;
using Ribbit.Net;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// アプリケーション起動時に ViewModel へ渡す production composition を構築します。
/// </summary>
internal sealed class ApplicationComposition : ISettingsDialogPlayerFactoryPort, IStartupLibraryFactory
{
    /// <summary>同じ構成の機能へ渡す、起動時に確定した実行条件を取得します。</summary>
    internal ApplicationRuntimeOptions RuntimeOptions { get; }

    /// <summary>構成が作るすべての所有者へ渡す、アプリ共通のダイアログ窓口を取得します。専用引数で差し替えられた経路を除き、通知と確認はこの窓口へ届きます。</summary>
    internal IUiDialogService DialogService { get; }

    /// <summary>プレイリスト作業領域とそのダイアログが使う通知・確認の窓口を取得します。専用の指定がなければ共通の窓口と同じです。</summary>
    internal IUiDialogService PlaylistWorkspaceDialogService { get; }

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

    private CustomFolderOutputSettingsSnapshot appliedCustomFolderOutputPlacement;

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

    private readonly IBmsLibraryDialogService bmsLibraryDialogService;

    private readonly IUiScheduler uiScheduler;

    private readonly IApplicationLifetimePort applicationLifetime;

    private readonly ICultureCatalog cultureCatalog;

    private readonly ApplicationPathSnapshot applicationPathSnapshot;

    private readonly IRootFileEnumerator rootFileEnumerator;

    private readonly IChartFileScanner chartFileScanner;

    private readonly IExternalShellGateway externalShellGateway;

    private readonly IExternalProgramLaunchGateway externalProgramLaunchGateway;

    private readonly IExternalPlayerProcessGateway externalPlayerProcessGateway;

    private readonly IUpdaterProcessGateway updaterProcessGateway;

    private readonly IScoreViewerRegistrationGateway scoreViewerRegistrationGateway;

    /// <summary>全機能と必須背景入力更新へ同じ論理受付を渡します。</summary>
    internal ChartFileOperationSynchronizer OperationAdmission { get; } = new();

    /// <summary>再構築前後のplaylistへ接続するP受付だけを構成の寿命で所有します。L・DB・同期処理・終了状態とは分離します。</summary>
    internal ChartFileOperationSynchronizer PlaylistOperationAdmission { get; } = new();

    /// <summary>新旧モデル・読取り・バックアップ・終了処理が共有する構成寿命のDB所有者です。</summary>
    internal ApplicationDatabaseScope DatabaseScope { get; } = new();

    private readonly IPackageInstallMutationPort packageInstallMutationPort;

    /// <summary>同じ共通受付と明示されたプロセス・音声・入力列挙境界で本番の機能を構成します。</summary>
    /// <param name="runtimeOptions">起動境界またはfixtureが明示的に所有する配置と実行条件。</param>
    /// <param name="rootFileEnumerator">モデルとLR2入力に共通の任意グループ列挙境界。nullは通常経路です。</param>
    /// <param name="chartFileScanner">モデルの譜面入力を捕捉する任意の走査境界。省略時は通常の走査サービスを使います。</param>
    /// <param name="scoreViewerRegistrationGateway">Score Viewer 登録の network boundary。未指定時は production gateway を使います。</param>
    /// <param name="keywordSearchFavoritesSettingsStore">Keyword search Favorites persistence boundary。</param>
    /// <param name="fileDbMutationDialogService">ファイル/DB変更の結果通知。省略時は共通のダイアログ窓口を使います。</param>
    /// <param name="playlistWorkspaceDialogService">プレイリスト作業領域の確認と通知。省略時は共通のダイアログ窓口を使います。</param>
    /// <param name="reportTerminalSettingsSaveFailure">Appが所有する、終了時の設定保存失敗の警告。終了時のUIを持たない構成では省略します。</param>
    /// <param name="restartFailureDialogs">終了時の再起動失敗通知。省略時は共通のダイアログ窓口を使います。</param>
    /// <param name="reportRestartFailure">再起動失敗を通知できない場合の報告処理。</param>
    /// <param name="settingsDialogService">設定画面の確認と通知。省略時は共通のダイアログ窓口を使います。</param>
    /// <param name="packageInstallMutationPort">パッケージの変更処理。省略時は既存のライブラリ変更処理を使います。</param>
    /// <param name="libraryDialogService">ライブラリ操作の通知と確認。省略時は共通のダイアログ窓口を使い、モデルへ注入する窓口の実装を作ります。</param>
    /// <param name="dialogService">アプリ共通のダイアログ窓口（必須）。本番は構成の根（App）が作る一つを渡し、専用引数を省略した経路と構成が作る他の所有者へ同じ窓口を届けます。省略時に実際のダイアログ調停処理を暗黙に作らないのは、通知・確認の宣言を持たないテストが実際のダイアログへ到達することを避けるためで、テストは未宣言の要求を失敗にする窓口か、宣言した記録用の窓口を明示して渡します。null は <see cref="ArgumentNullException"/> です。</param>
    internal ApplicationComposition(
        ApplicationRuntimeOptions runtimeOptions,
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
        IRootFileEnumerator rootFileEnumerator = null,
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
        IPackageInstallMutationPort packageInstallMutationPort = null,
        IChartFileScanner chartFileScanner = null,
        IUiDialogService libraryDialogService = null,
        IUiDialogService dialogService = null)
    {
        RuntimeOptions = runtimeOptions ?? throw new ArgumentNullException(nameof(runtimeOptions));
        this.settingsEditSession = settingsEditSession
            ?? BeMusicSeeker.Models.SettingsEditSession.CreateDefault();
        this.applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
        this.cultureCatalog = cultureCatalog
            ?? throw new ArgumentNullException(nameof(cultureCatalog));
        this.rootFileEnumerator = rootFileEnumerator;
        this.chartFileScanner = chartFileScanner;
        applicationPathSnapshot = runtimeOptions.Paths;
        this.externalShellGateway = externalShellGateway ?? ExternalShellGatewayPolicy.Current;
        this.externalProgramLaunchGateway = externalProgramLaunchGateway ?? ExternalProgramLaunchGatewayPolicy.Current;
        this.externalPlayerProcessGateway = externalPlayerProcessGateway ?? ExternalPlayerProcessGatewayPolicy.Current;
        this.updaterProcessGateway = updaterProcessGateway ?? UpdaterProcessGatewayPolicy.Current;
        this.scoreViewerRegistrationGateway = scoreViewerRegistrationGateway ?? new AppScoreViewerRegistrationGateway();
        Settings registrationSettings = this.settingsEditSession.Values;
        Lr2ConfigSession registrationSession = Lr2ConfigSession;
        BmsRootRegistrations = new BmsRootRegistrationStore(registrationSettings,
            () => registrationSession.TryResolve(registrationSettings.LR2ConfigXmlPath, out Lr2ConfigOwner owner) ? owner : null,
            () => Volatile.Read(ref appliedCustomFolderOutputPlacement),
            placement => Volatile.Write(ref appliedCustomFolderOutputPlacement, placement));
        playbackSettingsStore = new SettingsPlaybackSettingsStore(() => this.settingsEditSession.Values);
        playerSettingsGateway = new SettingsPlayerSettingsGateway(() => this.settingsEditSession.Values);
        audioSettingsGateway = new SettingsAudioGateway(() => this.settingsEditSession.Values);
        this.audioDeviceCatalog = audioDeviceCatalog ?? new BassAudioDeviceCatalog();
        this.defaultBmsPlayerFactory = defaultBmsPlayerFactory
            ?? (() => new InternalBMSAutoPlayerSoundOnly(playerSettingsGateway, runtimeOptions.TestSession?.NullOutputPlayback == true
                ? new NullOutputPlaybackRuntime() : new BassAudioPlaybackRuntime()));
        this.uiScheduler = uiScheduler
            ?? throw new ArgumentNullException(nameof(uiScheduler));
        this.reportSettingsApplyFailure = reportSettingsApplyFailure;
        ReportTerminalSettingsSaveFailure = reportTerminalSettingsSaveFailure;
        DialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        RestartFailureDialogs = restartFailureDialogs ?? DialogService;
        ReportRestartFailure = reportRestartFailure ?? reportSettingsApplyFailure;
        SettingsDialogService = settingsDialogService ?? DialogService;
        this.packageInstallMutationPort = packageInstallMutationPort;
        PlaylistWorkspaceDialogService = playlistWorkspaceDialogService ?? DialogService;
        FileDbMutationDialogs = fileDbMutationDialogService ?? DialogService;
        bmsLibraryDialogService = new BmsLibraryDialogService(libraryDialogService ?? DialogService, this.uiScheduler);
        appliedCustomFolderOutputPlacement = CustomFolderOutputSettingsSnapshot.CreateCurrent(this.settingsEditSession.Values);
        this.bmsLibraryOptionsProvider = bmsLibraryOptionsProvider
            ?? (() => BmsLibraryOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values, Volatile.Read(ref appliedCustomFolderOutputPlacement)));
        this.startupSettingsProvider = startupSettingsProvider
            ?? (() => StartupSettingsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.playlistUrlCompletionOptionsProvider = playlistUrlCompletionOptionsProvider
            ?? (() => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.playlistUrlAcquisitionOptionsProvider = playlistUrlAcquisitionOptionsProvider
            ?? (() => PlaylistUrlAcquisitionOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.beatorajaBmtOptionsProvider = beatorajaBmtOptionsProvider
            ?? (() => BeatorajaBmtOptionsSnapshot.CreateCurrent(this.settingsEditSession.Values));
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider
            ?? (() => CustomFolderOutputSettingsSnapshot.CreateCurrent(this.settingsEditSession.Values, Volatile.Read(ref appliedCustomFolderOutputPlacement)));
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

    /// <summary>初回設定・ライブラリ・プレイヤー再生成をまたいで同じLR2正本を保持します。</summary>
    internal Lr2ConfigSession Lr2ConfigSession { get; } = new();

    /// <summary>構成で共有する確定登録の保存と読取りです。</summary>
    internal BmsRootRegistrationStore BmsRootRegistrations { get; }

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

    /// <summary>プレイリストの通信・表示と、所有入力付き要求の寿命を受け取る共通導入受付を接続します。</summary>
    internal PlaylistWorkspaceViewModel CreatePlaylistWorkspaceViewModel(
        Action<Action> dispatchPresentationAction,
        MainChartListViewModel mainChartList,
        Func<BMSPlaylist> tablesProvider,
        Func<IEnumerable<BMSTable>> tableSnapshotProvider,
        Action<string> detailViewLog,
        Action<string> detailRetentionLog,
        Func<DroppedInstallBatchRequest, bool> playlistUrlInstallSink,
        Action<Uri> playlistUrlBrowserOpenSink,
        Action<Exception, string> externalPlaylistImportWarningLog,
        Action<string> externalPlaylistImportInfoLog,
        Action<Exception, string> beatorajaTableUrlImportWarningLog,
        Action<string> beatorajaTableUrlImportInfoLog,
        Func<BMSLibrary> libraryProvider,
        Func<Lr2ConfigOwner> lr2ConfigProvider,
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
        Func<bool> playlistRestoreUiThreadCheck,
        Func<Task> playlistUrlInstallCompletionProvider)
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
            PlaylistWorkspaceDialogService,
            PlaylistOperationAdmission,
            playlistUrlInstallCompletionProvider);
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
            DatabaseScope,
            statePort,
            workspacePort,
            customFolderOutputPort,
            playHistoryPort,
            searchRootRuntimePort,
            playerFactoryPort,
            playbackRuntimePort,
            lr2SongDbSyncWorkflow,
            settingsEditSession,
            new ApplicationDataUninstallWorkflowOwner(schemaDialogs, new Lr2ApplicationDataUninstallStore(DatabaseScope)),
            schemaDialogs,
            playHistoryDisplaySettingsStore: playHistoryDisplaySettingsStore,
            reportApplyFailure: reportSettingsApplyFailure,
            requestOperationModeRestart: requestOperationModeRestart,
            applicationLifetime: applicationLifetime,
            cultureCatalog: cultureCatalog,
            externalShellGateway: externalShellGateway,
            applicationPathSnapshot: applicationPathSnapshot,
            schemaWindowDialogs: schemaWindowDialogs,
            audioDeviceTestWorkflow: new AudioDeviceTestWorkflowOwner(
                playbackRuntimePort,
                new BassAudioDeviceTestRuntime(applicationPathSnapshot)),
            audioDeviceCatalog: audioDeviceCatalog,
            audioSettingsGateway: audioSettingsGateway,
            operationAdmission: OperationAdmission,
            lr2ConfigSession: Lr2ConfigSession,
            bmsRootRegistrations: BmsRootRegistrations,
            publishOutputPlacement: placement => Volatile.Write(ref appliedCustomFolderOutputPlacement, placement));
    }

    /// <summary>メイン画面の譜面操作・閲覧と起動処理の担当を、構成寿命で共有する依存へ接続します。</summary>
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
        StartupProgressWorkflowOwner startupProgressWorkflowOwner,
        Action<Exception> reportPackageInstallWorkflowNotificationFailure = null,
        Func<BMSLibrary, Action<MaintenanceWorkflowProgress>, CancellationToken, LibraryFileMutationCapability, MaintenanceReadFailureCollector, MaintenanceWorkflowResult> maintenanceRescanExecutor = null,
        Func<Action, Task> maintenanceRescanScheduler = null,
        Action<string> maintenanceRescanLog = null,
        Action<Exception> reportMaintenanceRescanWorkflowNotificationFailure = null,
        Action<Exception> reportMaintenanceRescanWorkflowFailure = null,
        Func<Func<Task>, Task> folderAutoRenameScheduler = null,
        Action<string> folderAutoRenameLog = null,
        Action<Exception> reportFolderAutoRenameNotificationFailure = null,
        Action<Exception> reportFolderAutoRenameFailure = null,
        ScoreViewerRegistrationWorkflowOwner scoreViewerRegistrationWorkflow = null,
        Func<BMSLibrary> zeroNoteLibraryProvider = null,
        Func<BMSLibrary> packageCatalogLibraryProvider = null,
        Func<bool> showDuplicateFileCheckConfirmProvider = null,
        Func<BMSLibrary> duplicateMaintenanceLibraryProvider = null,
        Func<BMSLibrary> selectedChartMutationLibraryProvider = null,
        Func<BMSLibrary> selectedChartResourceHealthLibraryProvider = null,
        Func<BMSLibrary> chartInfoParseFailureRemovalLibraryProvider = null,
        Func<Action, Task> chartInfoParseFailureRemovalScheduler = null,
        Func<SelectedChartAudioConversionSettingsSnapshot> selectedChartAudioConversionSettingsProvider = null,
        Action<EncoderType> selectedChartAudioConversionEncoderFallback = null,
        ISelectedChartAudioConversionExecutor selectedChartAudioConversionExecutor = null,
        Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow = null,
        Func<string, bool> selectedChartExternalActionFileExists = null,
        Action<string> libraryFolderTreeLog = null,
        Action<string> libraryFolderTreeLogWarning = null,
        IExternalShellGateway externalShellGateway = null,
        IPackageInstallMutationPort packageInstallMutationPort = null)
    {
        externalShellGateway ??= this.externalShellGateway;
        return new MainWindowChildComposition(
            RuntimeOptions.UpdateManifestUrl,
            RuntimeOptions.RestartArguments,
            selectedChartAudioConversionExecutor ?? new BassSelectedChartAudioConversionExecutor(),
            mainChartList,
            playlistWorkspace,
            bmsPlayerFactory,
            uiScheduler,
            DialogService,
            playbackSettingsStore,
            keywordSearchHistorySettingsStore,
            chartFileOperations,
            mainViewLog,
            dispatchMainChartListAction,
            mainViewLogWarning,
            dispatchPackageInstallUi,
            installDestinationLibraryProvider,
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
            FileDbMutationDialogs,
            scoreViewerRegistrationWorkflow ?? CreateScoreViewerRegistrationWorkflowOwner(),
            zeroNoteLibraryProvider,
            packageCatalogLibraryProvider,
            showDuplicateFileCheckConfirmProvider,
            duplicateMaintenanceLibraryProvider,
            selectedChartMutationLibraryProvider,
            selectedChartResourceHealthLibraryProvider,
            chartInfoParseFailureRemovalLibraryProvider,
            chartInfoParseFailureRemovalScheduler,
            selectedChartAudioConversionSettingsProvider
                ?? (() => SelectedChartAudioConversionSettingsSnapshot.CreateCurrent(audioSettingsGateway.CaptureEncodingSettings())),
            selectedChartAudioConversionEncoderFallback
                ?? audioSettingsGateway.ApplyEncoderFallback,
            lr2SongDbSyncWorkflow,
            selectedChartExternalActionFileExists,
            libraryFolderTreeLog,
            libraryFolderTreeLogWarning,
            externalShellGateway,
            applicationPathSnapshot,
            updaterProcessGateway,
            settingsProvider: () => settingsEditSession.Values,
            externalProgramLaunchGateway: externalProgramLaunchGateway,
            keywordSearchFavoritesSettingsStore: keywordSearchFavoritesSettingsStore,
            packageInstallMutationPort: packageInstallMutationPort ?? this.packageInstallMutationPort,
            exitForUnrestoredDestination: applicationLifetime.ExitForUnrestoredDestination,
            playlistOperationAdmission: PlaylistOperationAdmission,
            reportPlaybackSettingsSaveFailure: reportSettingsApplyFailure);
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
                PlaylistWorkspaceDialogService),
            () => settingsEditSession.Values.ShowScoreViewerRegisterConfirmMsg,
            warningLog);
    }

    /// <summary>プレイヤーも構成が保持する採用済み正本を使います。</summary>
    internal Lr2ConfigOwner ResolveLr2Config(string path) => Lr2ConfigSession.Resolve(path);

    internal IBMSPlayer CreateBmsPlayer(
        StartupSettingsSnapshot startupSettings,
        Func<Lr2ConfigOwner> createLr2PlayerConfig)
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
            () => ResolveLr2Config(settings.LR2ConfigXmlPath));
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

    /// <summary>個別のモデル・DB・終了状態を持つライブラリへ、構成の寿命のLとPを明示接続します。</summary>
    internal BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile)
    {
        if (libraryProfile == null)
        {
            throw new ArgumentNullException(nameof(libraryProfile));
        }
        return new BMSLibrary(
            DatabaseScope,
            RuntimeOptions.DatabaseLoadOptions,
            libraryProfile.SongDbPath,
            libraryProfile.Lr2ConfigOwner,
            libraryProfile.Lr2ScoreDbPath,
            fileMutationService: null,
            dialogService: bmsLibraryDialogService,
            libraryProfile.StartupRequiredFileScanReason,
            bmsLibraryOptionsProvider,
            uiScheduler,
            applicationPathSnapshot,
            BmsRootRegistrations,
            chartFileScanner: chartFileScanner,
            rootFileEnumerator: rootFileEnumerator,
            operationAdmission: OperationAdmission,
            playlistOperationAdmission: PlaylistOperationAdmission);
    }

    /// <summary>
    /// 同じprofileで作ったライブラリへplaylistを接続し、そのライブラリからP受付を借用します。
    /// </summary>
    /// <param name="libraryProfile">起動要求で捕捉した変更不能なprofile。</param>
    /// <param name="library">同じprofileから構築したライブラリ。対象・DB・終了状態はこの接続に属します。</param>
    /// <returns>個別storeのplaylist。再構築前後のP共有だけで旧対象を現行として扱いません。</returns>
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
            libraryProfile.Lr2ConfigOwner,
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
    /// 検索設定の保存境界と譜面操作・閲覧・起動処理の依存を明示して、画面寿命の担当を構成します。
    /// </summary>
    /// <param name="keywordSearchFavoritesSettingsStore">通常一覧・概要で共有するお気に入り設定の保存境界。</param>
    /// <param name="dialogService">再生・導入先・メンテナンス・選択譜面の操作など、画面寿命の担当が使う構成共通のダイアログ窓口。担当ごとの専用引数は持たず、すべてこの窓口へ接続します。例外として、フォルダ自動リネームは構成のファイル/DB変更の窓口（<paramref name="folderAutoRenameDialogService"/>）を使います。</param>
    /// <param name="exitForUnrestoredDestination">操作終端後に通常Closeを通さず最終終了する境界。</param>
    /// <param name="playlistOperationAdmission">構成寿命で共有するPの新規受付を閉じるための既存owner。</param>
    /// <param name="updateManifestUrl">起動時に捕捉済みの更新URL。妥当性の検査は更新確認処理へ委ねます。</param>
    /// <param name="restartArguments">起動時に捕捉済みの再起動引数。動作モード変更の再起動と同じ値を更新後の再起動へ引き継ぎます。</param>
    internal MainWindowChildComposition(
        string updateManifestUrl,
        IReadOnlyList<string> restartArguments,
        ISelectedChartAudioConversionExecutor selectedChartAudioConversionExecutor,
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        Func<IBMSPlayer> bmsPlayerFactory,
        IUiScheduler uiScheduler,
        IUiDialogService dialogService,
        IPlaybackSettingsStore playbackSettingsStore,
        IKeywordSearchHistorySettingsStore keywordSearchHistorySettingsStore,
        ChartFileOperationSynchronizer chartFileOperations,
        Action<string> mainViewLog,
        Action<Action> dispatchMainChartListAction,
        Action<string> mainViewLogWarning,
        Func<Action, bool> dispatchPackageInstallUi,
        Func<BMSLibrary> installDestinationLibraryProvider,
        Func<InstallDestinationWorkflowSettingsSnapshot> installDestinationSettingsProvider,
        Func<string, bool> libraryFolderTreeDirectoryExists,
        Func<string, ExplorerOpenResult> libraryFolderTreeExplorerOpen,
        StartupProgressWorkflowOwner startupProgressWorkflowOwner,
        Action<Exception> reportPackageInstallWorkflowNotificationFailure = null,
        Func<BMSLibrary, Action<MaintenanceWorkflowProgress>, CancellationToken, LibraryFileMutationCapability, MaintenanceReadFailureCollector, MaintenanceWorkflowResult> maintenanceRescanExecutor = null,
        Func<Action, Task> maintenanceRescanScheduler = null,
        Action<string> maintenanceRescanLog = null,
        Action<Exception> reportMaintenanceRescanWorkflowNotificationFailure = null,
        Action<Exception> reportMaintenanceRescanWorkflowFailure = null,
        Func<Func<Task>, Task> folderAutoRenameScheduler = null,
        Action<string> folderAutoRenameLog = null,
        Action<Exception> reportFolderAutoRenameNotificationFailure = null,
        Action<Exception> reportFolderAutoRenameFailure = null,
        IUiDialogService folderAutoRenameDialogService = null,
        ScoreViewerRegistrationWorkflowOwner scoreViewerRegistrationWorkflow = null,
        Func<BMSLibrary> zeroNoteLibraryProvider = null,
        Func<BMSLibrary> packageCatalogLibraryProvider = null,
        Func<bool> showDuplicateFileCheckConfirmProvider = null,
        Func<BMSLibrary> duplicateMaintenanceLibraryProvider = null,
        Func<BMSLibrary> selectedChartMutationLibraryProvider = null,
        Func<BMSLibrary> selectedChartResourceHealthLibraryProvider = null,
        Func<BMSLibrary> chartInfoParseFailureRemovalLibraryProvider = null,
        Func<Action, Task> chartInfoParseFailureRemovalScheduler = null,
        Func<SelectedChartAudioConversionSettingsSnapshot> selectedChartAudioConversionSettingsProvider = null,
        Action<EncoderType> selectedChartAudioConversionEncoderFallback = null,
        Lr2SongDbSyncWorkflowOwner lr2SongDbSyncWorkflow = null,
        Func<string, bool> selectedChartExternalActionFileExists = null,
        Action<string> libraryFolderTreeLog = null,
        Action<string> libraryFolderTreeLogWarning = null,
        IExternalShellGateway externalShellGateway = null,
        ApplicationPathSnapshot applicationPathSnapshot = null,
        IUpdaterProcessGateway updaterProcessGateway = null,
        Func<BeMusicSeeker.Properties.Settings> settingsProvider = null,
        IExternalProgramLaunchGateway externalProgramLaunchGateway = null,
        IKeywordSearchFavoritesSettingsStore keywordSearchFavoritesSettingsStore = null,
        IPackageInstallMutationPort packageInstallMutationPort = null,
        Action<Exception> exitForUnrestoredDestination = null,
        ChartFileOperationSynchronizer playlistOperationAdmission = null,
        Action<Exception> reportPlaybackSettingsSaveFailure = null)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
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
            new WpfPlaybackDialogService(dialogService),
            exception => NLogWrapper.TraceLogger?.Warn(exception),
            chartFileOperations,
            reportPlaybackSettingsSaveFailure);
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
        PlayHistory = new PlayHistoryWorkflowOwner();
        // 当該操作の所有権解放後にだけ呼び、通常Shell終端の保存・一時領域回収へは進めません。
        Task ExitForUnrestoredDestinationAsync(Exception failure) => uiScheduler.InvokeAsync(() =>
        {
            chartFileOperations.CloseAdmission();
            playlistOperationAdmission?.CloseAdmission();
            PlaybackPanel.BeginShutdown();
            (exitForUnrestoredDestination
                ?? throw new InvalidOperationException("Unrestored destination exit is not connected."))(failure);
        });
        PendingPackageWorkflow = new PendingPackageWorkflowOwner(
            installDestinationLibraryProvider ?? throw new ArgumentNullException(nameof(installDestinationLibraryProvider)),
            chartFileOperations,
            ChartMutationActivity,
            PlaybackPanel,
            dialogService,
            installDestinationSettingsProvider ?? throw new ArgumentNullException(nameof(installDestinationSettingsProvider)),
            externalShellGateway: externalShellGateway,
            exitForUnrestoredDestination: ExitForUnrestoredDestinationAsync);
        RegularChartListOwner = new RegularChartListOwner(
            MainChartList,
            PlaylistWorkspace,
            mainViewLog,
            dispatchMainChartListAction,
            mainViewLogWarning,
            PendingPackageWorkflow,
            chartFileOperations,
            ChartMutationActivity,
            (IChartMutationPlaybackPort)PlaybackPanel,
            uiScheduler,
            dialogService);
        PackageInstallWorkflow = new PackageInstallWorkflowOwner(
            dialogService,
            chartFileOperations,
            ChartMutationActivity,
            packageInstallMutationPort ?? new BmsLibraryPackageInstallMutationPort(),
            PlaybackPanel,
            dispatchPackageInstallUi,
            reportPackageInstallWorkflowNotificationFailure);
        MaintenanceRescanWorkflow = new MaintenanceRescanWorkflowOwner(
            maintenanceRescanExecutor ?? MissingMaintenanceRescanExecutor,
            maintenanceRescanScheduler ?? (action => Task.Run(action)),
            dispatchMainChartListAction,
            maintenanceRescanLog,
            reportMaintenanceRescanWorkflowNotificationFailure,
            reportMaintenanceRescanWorkflowFailure,
            dialogs: dialogService,
            dispatchTerminalToUi: action => uiScheduler.InvokeAsync(action));
        FolderAutoRenameWorkflow = new FolderAutoRenameWorkflowOwner(
            chartFileOperations,
            ChartMutationActivity,
            new BmsLibraryFolderAutoRenameMutationPort(),
            (IChartMutationPlaybackPort)PlaybackPanel,
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
            updaterProcessGateway ?? throw new ArgumentNullException(nameof(updaterProcessGateway)),
            restartArguments ?? throw new ArgumentNullException(nameof(restartArguments)));
        UpdateCheckService updateCheckService = new(AppHttpClient.Create(5000));
        StartupUpdateWorkflow = new StartupUpdateWorkflowOwner(
            () => updateCheckService.CheckAsync(updateManifestUrl),
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
            chartFileOperations,
            dialogService);
        PackageCatalogWorkflow = new PackageCatalogWorkflowOwner(
            packageCatalogLibraryProvider ?? (() => null),
            chartFileOperations,
            ChartMutationActivity,
            dialogService,
            PlaybackPanel,
            mutation => Task.Run(mutation));
        DuplicateMaintenanceWorkflow = new DuplicateMaintenanceWorkflowOwner(
            duplicateMaintenanceLibraryProvider ?? throw new ArgumentNullException(nameof(duplicateMaintenanceLibraryProvider)),
            chartFileOperations,
            ChartMutationActivity,
            PlaybackPanel,
            dialogService,
            showDuplicateFileCheckConfirmProvider ?? throw new ArgumentNullException(nameof(showDuplicateFileCheckConfirmProvider)),
            LongPathFileSystem.DirectoryExists,
            externalShellGateway.OpenDirectory,
            MaintenanceTree.CaptureNextDuplicateGroupSelection,
            exitForUnrestoredDestination: ExitForUnrestoredDestinationAsync);
        SelectedChartMutations = new SelectedChartMutationWorkflowOwner(
            selectedChartMutationLibraryProvider ?? throw new ArgumentNullException(nameof(selectedChartMutationLibraryProvider)),
            chartFileOperations,
            ChartMutationActivity,
            PlaybackPanel,
            dialogService,
            new PendingDeleteConfirmationDialogPort(dialogService));
        Func<string, bool> externalActionFileExists = selectedChartExternalActionFileExists ?? LongPathFileSystem.FileExists;
        SelectedChartExternalActions = new SelectedChartExternalActionWorkflowOwner(
            externalActionFileExists,
            externalShellGateway,
            settingsProvider: settingsProvider,
            externalProgramLaunchGateway: externalProgramLaunchGateway
                ?? new WindowsExternalProgramLaunchGateway(externalActionFileExists));
        SelectedChartResourceHealth = new SelectedChartResourceHealthWorkflowOwner(
            selectedChartResourceHealthLibraryProvider ?? throw new ArgumentNullException(nameof(selectedChartResourceHealthLibraryProvider)),
            dialogService);
        ChartInfoParseFailureRemoval = new ChartInfoParseFailureRemovalWorkflowOwner(
            chartInfoParseFailureRemovalLibraryProvider ?? throw new ArgumentNullException(nameof(chartInfoParseFailureRemovalLibraryProvider)),
            dialogService,
            chartInfoParseFailureRemovalScheduler ?? (action => Task.Run(action)));
        SelectedChartAudioConversion = new SelectedChartAudioConversionWorkflowOwner(
            selectedChartAudioConversionSettingsProvider ?? throw new ArgumentNullException(nameof(selectedChartAudioConversionSettingsProvider)),
            selectedChartAudioConversionEncoderFallback ?? throw new ArgumentNullException(nameof(selectedChartAudioConversionEncoderFallback)),
            PlaybackPanel,
            dialogService,
            selectedChartAudioConversionExecutor,
            chartFileOperations);
        Lr2SongDbSyncWorkflow = lr2SongDbSyncWorkflow
            ?? throw new ArgumentNullException(nameof(lr2SongDbSyncWorkflow));
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

    private static MaintenanceWorkflowResult MissingMaintenanceRescanExecutor(
        BMSLibrary library,
        Action<MaintenanceWorkflowProgress> progress,
        CancellationToken cancellationToken, LibraryFileMutationCapability capability,
        MaintenanceReadFailureCollector readFailures)
    {
        throw new InvalidOperationException("Maintenance rescan executor is not configured.");
    }

}
