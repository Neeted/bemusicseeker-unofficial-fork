#nullable disable

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Provides process-local composition values shared by production-shaped test fixtures.
/// </summary>
internal static class TestBmsFactory
{
    /// <summary>
    /// Gets an application snapshot whose native bridge path is intentionally absent,
    /// so tests cannot depend on the local Everything service or index.
    /// </summary>
    internal static ApplicationPathSnapshot MissingEverythingBridge { get; } =
        ApplicationPathSnapshot.FromExecutablePath(
            Path.Combine(Path.GetTempPath(), "BeMusicSeeker.Tests", "MissingEverythingBridge", "BeMusicSeeker.exe"));

    /// <summary>
    /// Uses the repository's shipped bridge for production-shaped scan-route tests.
    /// The scan still falls back to the managed enumerator when the Everything
    /// service is unavailable.
    /// </summary>
    internal static ApplicationPathSnapshot AvailableEverythingBridge { get; } =
        ApplicationPathSnapshot.FromExecutablePath(
            Path.Combine(AppContext.BaseDirectory, "BeMusicSeeker.Tests.exe"));
}

internal sealed class TestBmsLibrary : BMSLibrary
{
    /// <summary>専用設定または固定の既定入力を捕捉し、利用者設定を読みません。</summary>
    private static Func<BmsLibraryOptionsSnapshot> CreateOptions(Settings settings)
    {
        Settings values = settings ?? MainWindowViewModelTestFactory.CreateIsolatedSettings();
        return () => BmsLibraryOptionsSnapshot.CreateCurrent(values);
    }

    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config = null,
        string _lr2ScoreDB = null,
        string startupRequiredFileScanReason = null,
        Settings settings = null)
        : base(songDbPath, getLR2Config, _lr2ScoreDB, startupRequiredFileScanReason, CreateOptions(settings), new TestUiScheduler(() => Dispatcher.CurrentDispatcher), TestBmsFactory.MissingEverythingBridge)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        Settings settings = null)
        : base(songDbPath, getLR2Config, _lr2ScoreDB, fileMutationService, null, null, CreateOptions(settings), new TestUiScheduler(() => Dispatcher.CurrentDispatcher), TestBmsFactory.MissingEverythingBridge)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        Settings settings = null)
        : base(songDbPath, getLR2Config, _lr2ScoreDB, fileMutationService, dialogService, null, CreateOptions(settings), new TestUiScheduler(() => Dispatcher.CurrentDispatcher), TestBmsFactory.MissingEverythingBridge)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    /// <summary>
    /// Creates a test library with the narrow install-estimation diagnostic boundary enabled.
    /// </summary>
    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        IInstallEstimationExecutionObserver installEstimationExecutionObserver,
        Settings settings = null)
        : base(
            songDbPath,
            getLR2Config,
            _lr2ScoreDB,
            fileMutationService,
            dialogService,
            null,
            CreateOptions(settings),
            new TestUiScheduler(() => Dispatcher.CurrentDispatcher),
            TestBmsFactory.MissingEverythingBridge,
            installEstimationExecutionObserver)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        IUiScheduler uiScheduler,
        Settings settings = null)
        : base(songDbPath, getLR2Config, _lr2ScoreDB, fileMutationService, dialogService, null, CreateOptions(settings), uiScheduler, TestBmsFactory.MissingEverythingBridge)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    /// <summary>
    /// 明示設定とUI境界で実ライブラリを作り、後片付け・公開・推定を共有設定の変更なしで確認します。
    /// </summary>
    /// <param name="chartFileScanner">実差分読込みの入口へ渡す固定走査境界。</param>
    /// <param name="installEstimationExecutionObserver">起動復元から明示推定までの評価開始を観測します。</param>
    /// <param name="operationAdmission">実ownerと共有する受付。省略時は独立受付。</param>
    /// <param name="playlistOperationAdmission">実起動構成と共有するプレイリスト局所受付。</param>
    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        IUiScheduler uiScheduler,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider,
        IChartFileScanner chartFileScanner = null,
        IInstallEstimationExecutionObserver installEstimationExecutionObserver = null,
        ChartFileOperationSynchronizer operationAdmission = null,
        ChartFileOperationSynchronizer playlistOperationAdmission = null,
        Settings settings = null)
        : base(
            songDbPath,
            getLR2Config,
            _lr2ScoreDB,
            fileMutationService,
            dialogService,
            null,
            optionsSnapshotProvider ?? CreateOptions(settings),
            uiScheduler,
            TestBmsFactory.MissingEverythingBridge,
            installEstimationExecutionObserver: installEstimationExecutionObserver,
            chartFileScanner: chartFileScanner,
            operationAdmission: operationAdmission,
            playlistOperationAdmission: playlistOperationAdmission)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    /// <summary>実起動fixtureの設定入力と同compositionのL/P受付を明示接続します。</summary>
    /// <param name="operationAdmission">起動を接続する共通受付。独立fixtureでは省略できます。</param>
    /// <param name="playlistOperationAdmission">同compositionのプレイリスト局所受付。</param>
    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        string startupRequiredFileScanReason,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider,
        ChartFileOperationSynchronizer operationAdmission = null,
        ChartFileOperationSynchronizer playlistOperationAdmission = null,
        Settings settings = null)
        : base(songDbPath, getLR2Config, _lr2ScoreDB, startupRequiredFileScanReason, optionsSnapshotProvider,
            new TestUiScheduler(() => Dispatcher.CurrentDispatcher), TestBmsFactory.MissingEverythingBridge,
            operationAdmission: operationAdmission, playlistOperationAdmission: playlistOperationAdmission)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    /// <summary>
    /// 実走査経路に使うライブラリを、明示したnative bridge・固定scanner・UI境界で構成します。
    /// 実起動へ接続するfixtureは同compositionのL/Pを転送し、独立fixtureは専用受付を持ちます。
    /// </summary>
    /// <param name="chartFileScanner">固定走査入力。nullでは選択したbridge経由の実走査を使います。</param>
    /// <param name="uiScheduler">実進捗公開へ接続するUI境界。</param>
    /// <param name="rootFileEnumerator">LR2候補を捕捉する既存の一括列挙境界。</param>
    /// <param name="irClient">startup から終了までの IR 通信を所有する明示的な境界。</param>
    /// <param name="dialogService">本番入口の元警告を観測する通知境界。</param>
    /// <param name="operationAdmission">本番ownerと共有する論理受付。省略時は独立受付です。</param>
    /// <param name="playlistOperationAdmission">同compositionのプレイリスト局所受付。</param>
    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        string startupRequiredFileScanReason,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider,
        ApplicationPathSnapshot applicationPathSnapshot,
        IChartFileScanner chartFileScanner = null,
        IUiScheduler uiScheduler = null,
        IRootFileEnumerator rootFileEnumerator = null,
        IBmsLibraryIrClient irClient = null,
        IBmsLibraryDialogService dialogService = null,
        ChartFileOperationSynchronizer operationAdmission = null,
        ChartFileOperationSynchronizer playlistOperationAdmission = null,
        Settings settings = null)
        : base(
            _lr2SongDB: songDbPath,
            getLR2Config: getLR2Config,
            _lr2ScoreDB: _lr2ScoreDB,
            fileMutationService: null,
            dialogService: dialogService,
            startupRequiredFileScanReason: startupRequiredFileScanReason,
            optionsSnapshotProvider: optionsSnapshotProvider,
            uiScheduler: uiScheduler ?? new TestUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationPathSnapshot: applicationPathSnapshot,
            installEstimationExecutionObserver: null,
            chartFileScanner: chartFileScanner,
            rootFileEnumerator: rootFileEnumerator,
            irClient: irClient,
            operationAdmission: operationAdmission,
            playlistOperationAdmission: playlistOperationAdmission)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    /// <summary>
    /// Creates a production-shaped library with the shipped Everything bridge.
    /// </summary>
    internal TestBmsLibrary(
        string songDbPath,
        ApplicationPathSnapshot applicationPathSnapshot,
        Settings settings = null)
        : base(songDbPath, null, null, null, CreateOptions(settings), new TestUiScheduler(() => Dispatcher.CurrentDispatcher), applicationPathSnapshot)
    {
        MarkCatalogPathConvergenceCompleted();
    }
}

internal sealed class TestBmsPlaylist : BMSPlaylist
{
    private static Func<PlaylistUrlCompletionOptionsSnapshot> CreatePlaylistUrlOptions(Settings settings)
    {
        Settings values = settings ?? MainWindowViewModelTestFactory.CreateIsolatedSettings();
        return () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(values);
    }

    private static Func<BeatorajaBmtOptionsSnapshot> CreateBeatorajaOptions(Settings settings)
    {
        Settings values = settings ?? MainWindowViewModelTestFactory.CreateIsolatedSettings();
        return () => BeatorajaBmtOptionsSnapshot.CreateCurrent(values);
    }

    private static Func<CustomFolderOutputSettingsSnapshot> CreateCustomFolderOptions(Settings settings)
    {
        Settings values = settings ?? MainWindowViewModelTestFactory.CreateIsolatedSettings();
        return () => CustomFolderOutputSettingsSnapshot.CreateCurrent(values);
    }

    /// <summary>所有ライブラリの共通受付と実LR2同期を接続し、注入した出力設定でプレイリストを構成します。</summary>
    /// <param name="libraryBindings">同じライブラリが所有する生存権限と同期能力。</param>
    /// <param name="songDbPath">テストが所有する曲DB。</param>
    /// <param name="customFolderOutputSettingsProvider">操作開始時に捕捉する出力設定。</param>
    /// <param name="getLr2Config">出力先登録・保存を行うテスト所有のLR2設定。</param>
    /// <param name="playlistUrlCompletionOptionsProvider">専用設定からURL補完入力を捕捉する既存依存。</param>
    /// <param name="beatorajaBmtOptionsProvider">専用設定からBMT入力を捕捉する既存依存。</param>
    /// <param name="recommendationScoreReader">推薦だけの固定原観測読取り。省略時は実ライブラリを使います。</param>
    internal TestBmsPlaylist(
        BmsPlaylistLibraryBindings libraryBindings,
        string songDbPath,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider,
        Func<LR2Config> getLr2Config = null,
        Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider = null,
        Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider = null,
        Func<CancellationToken, Task<WalkureScoreInput>> recommendationScoreReader = null,
        Settings settings = null)
        : base(
            libraryBindings,
            songDbPath,
            getLr2Config,
            null,
            playlistUrlCompletionOptionsProvider ?? CreatePlaylistUrlOptions(settings),
            beatorajaBmtOptionsProvider ?? CreateBeatorajaOptions(settings),
            customFolderOutputSettingsProvider,
            TestBmsFactory.MissingEverythingBridge,
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            recommendationScoreReader: recommendationScoreReader)
    {
    }

    internal new ObservableCollection<BMSTable> BMSTables
    {
        get => base.BMSTables;
        set => base.BMSTables = value;
    }

    internal TestBmsPlaylist(
        string songDbPath,
        Func<LR2Config> getLr2Config = null,
        string scoreDbPath = null,
        Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> getBeatorajaBmtSongHashResolver = null,
        Func<CancellationToken, Task<WalkureScoreInput>> recommendationScoreReader = null,
        Settings settings = null)
        : base(
            songDbPath,
            getLr2Config,
            scoreDbPath,
            getBeatorajaBmtSongHashResolver,
            CreatePlaylistUrlOptions(settings),
            CreateBeatorajaOptions(settings),
            CreateCustomFolderOptions(settings),
            TestBmsFactory.MissingEverythingBridge,
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            new TestLr2PlaylistFolderSynchronizationPort(songDbPath),
            new ChartFileOperationSynchronizer(),
            recommendationScoreReader: recommendationScoreReader)
    {
    }

    internal TestBmsPlaylist(
        string songDbPath,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization,
        Settings settings = null)
        : base(
            songDbPath,
            null,
            null,
            null,
            CreatePlaylistUrlOptions(settings),
            CreateBeatorajaOptions(settings),
            CreateCustomFolderOptions(settings),
            TestBmsFactory.MissingEverythingBridge,
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            lr2PlaylistFolderSynchronization,
            new ChartFileOperationSynchronizer())
    {
    }

    /// <summary>
    /// Creates a playlist test double with an explicitly controlled UI-operation scheduler.
    /// </summary>
    /// <param name="songDbPath">The song database used by the playlist.</param>
    /// <param name="lr2PlaylistFolderSynchronization">The LR2 folder synchronization port.</param>
    /// <param name="uiScheduler">The scheduler whose accepted-operation lifecycle the test controls.</param>
    internal TestBmsPlaylist(
        string songDbPath,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization,
        IUiScheduler uiScheduler,
        Settings settings = null)
        : base(
            songDbPath,
            null,
            null,
            null,
            CreatePlaylistUrlOptions(settings),
            CreateBeatorajaOptions(settings),
            CreateCustomFolderOptions(settings),
            TestBmsFactory.MissingEverythingBridge,
            uiScheduler,
            lr2PlaylistFolderSynchronization,
            new ChartFileOperationSynchronizer())
    {
    }

    internal TestBmsPlaylist(
        string songDbPath,
        Func<LR2Config> getLr2Config,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization,
        Settings settings = null)
        : base(
            songDbPath,
            getLr2Config,
            null,
            null,
            CreatePlaylistUrlOptions(settings),
            CreateBeatorajaOptions(settings),
            CreateCustomFolderOptions(settings),
            TestBmsFactory.MissingEverythingBridge,
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            lr2PlaylistFolderSynchronization,
            new ChartFileOperationSynchronizer())
    {
    }

    internal TestBmsPlaylist(
        string songDbPath,
        string scoreDbPath,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization,
        Settings settings = null)
        : base(
            songDbPath,
            null,
            scoreDbPath,
            null,
            CreatePlaylistUrlOptions(settings),
            CreateBeatorajaOptions(settings),
            CreateCustomFolderOptions(settings),
            TestBmsFactory.MissingEverythingBridge,
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            lr2PlaylistFolderSynchronization,
            new ChartFileOperationSynchronizer())
    {
    }

    /// <summary>明示設定と読取り・UI反映の境界を接続し、実保存経路を持つプレイリストを作成します。</summary>
    internal TestBmsPlaylist(
        string songDbPath,
        Func<LR2Config> getLr2Config,
        string scoreDbPath,
        Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> getBeatorajaBmtSongHashResolver,
        Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider,
        Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization = null,
        Func<Uri, CancellationToken, Task<string>> playlistUrlCompletionTsvContentFetcher = null,
        Func<Uri, CancellationToken, Task<string>> playlistUrlCompletionStellaContentFetcher = null,
        ChartFileOperationSynchronizer mutationAdmission = null,
        Func<CancellationToken, Task<WalkureScoreInput>> recommendationScoreReader = null,
        IUiScheduler uiScheduler = null,
        Settings settings = null)
        : base(
            songDbPath,
            getLr2Config,
            scoreDbPath,
            getBeatorajaBmtSongHashResolver,
            playlistUrlCompletionOptionsProvider,
            beatorajaBmtOptionsProvider,
            customFolderOutputSettingsProvider,
            TestBmsFactory.MissingEverythingBridge,
            uiScheduler ?? new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            lr2PlaylistFolderSynchronization ?? new TestLr2PlaylistFolderSynchronizationPort(songDbPath),
            mutationAdmission ?? new ChartFileOperationSynchronizer(),
            playlistUrlCompletionTsvContentFetcher,
            playlistUrlCompletionStellaContentFetcher,
            recommendationScoreReader)
    {
    }
}
