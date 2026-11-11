using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Wpf;

internal static class MainWindowViewModelTestFactory
{
    /// <summary>fixtureの配置を明示し、process環境を再読しない固定の実行条件を作ります。</summary>
    /// <param name="paths">配置。省略時は現在の配置です。</param>
    /// <param name="updateManifestUrl">更新確認が使うURL。省略時は既定のURLです。通信せずに更新確認を失敗させるには、相対URLを渡します。</param>
    internal static ApplicationRuntimeOptions CreateRuntimeOptions(ApplicationPathSnapshot? paths = null, string? updateManifestUrl = null)
        => new(paths ?? ApplicationPathPolicy.Current,
            new BmsDatabaseLoadOptions(true, true, true), null,
            updateManifestUrl ?? BeMusicSeeker.Models.Update.UpdateCheckService.DefaultManifestUrl, []);

    /// <summary>
    /// 明示された設定、または他の testhost と共有しない既定値で画面 owner を構成する。
    /// 項目保存で生成した専用ファイルは、当該testhostの全処理・画面終端後に回収します。
    /// </summary>
    /// <param name="settings">明示する設定。省略時は専用の隔離設定です。</param>
    /// <param name="fileDbMutationDialogs">ライブラリ変更の結果通知の窓口。省略時は構成の共通の窓口（<see cref="UndeclaredUiDialogService"/>）です。</param>
    /// <param name="settingsDialogs">設定画面の確認と通知の窓口。省略時は構成の共通の窓口（<see cref="UndeclaredUiDialogService"/>）です。</param>
    /// <param name="playlistWorkspaceDialogs">プレイリスト作業領域の確認と通知の窓口。省略時は構成の共通の窓口（<see cref="UndeclaredUiDialogService"/>）です。</param>
    /// <remarks>
    /// 構成の共通のダイアログ窓口は常に<see cref="UndeclaredUiDialogService"/>です。専用の引数を渡さずに通知・確認が出ると、共有ホストの観測記録の項目<c>D</c>で
    /// ケースが失敗になります。通知を期待するケースは、記録用の窓口を専用の引数へ明示的に渡して表明します。
    /// 専用の引数で差し替えられない経路（ライブラリ、再起動失敗、保守の再検査など構成の共通の窓口を使う経路）で通知を宣言するときは、<c>ApplicationComposition</c> を直接構成し、<c>dialogService</c> か該当する専用引数で渡します。
    /// </remarks>
    internal static MainWindowViewModel Create(
        Settings? settings = null,
        BeMusicSeeker.Views.Dialogs.IUiDialogService? fileDbMutationDialogs = null,
        BeMusicSeeker.Views.Dialogs.IUiDialogService? settingsDialogs = null,
        BeMusicSeeker.Views.Dialogs.IUiDialogService? playlistWorkspaceDialogs = null)
    {
        settings ??= TestSettingsFactory.CreateIsolatedSettings();
        return new ApplicationComposition(
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            dialogService: new UndeclaredUiDialogService(),
            fileDbMutationDialogService: fileDbMutationDialogs,
            settingsDialogService: settingsDialogs,
            playlistWorkspaceDialogService: playlistWorkspaceDialogs)
            .CreateMainWindowViewModelForTest();
    }

    /// <summary>独立fixture、または既存実画面と同じcompositionのL/Pでライブラリを作成します。</summary>
    /// <param name="databaseScope">実Attach先のcomposition、または独立fixtureが共有するDB所有者。</param>
    /// <param name="owner">実Attach先の画面。省略時は独立fixtureです。</param>
    internal static TestBmsLibrary CreateLibrary(ApplicationDatabaseScope databaseScope, string songDbPath, Settings settings, MainWindowViewModel? owner = null)
    {
        ApplicationComposition? composition = owner == null ? null : GetComposition(owner);
        return new TestBmsLibrary(
            databaseScope,
            songDbPath,
            getLR2Config: settings.OperationModeLR2DB && !string.IsNullOrWhiteSpace(settings.LR2ConfigXmlPath)
                ? composition?.ResolveLr2Config(settings.LR2ConfigXmlPath) ?? new Lr2ConfigOwner(new LR2Config(settings.LR2ConfigXmlPath)) : null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
            operationAdmission: composition?.OperationAdmission,
            playlistOperationAdmission: composition?.PlaylistOperationAdmission,
            settings: settings,
            bmsRootRegistrations: composition?.BmsRootRegistrations);
    }

    /// <summary>実画面が既に所有する構成をfixture入力の接続にだけ使い、別ownerを作りません。</summary>
    internal static ApplicationComposition GetComposition(MainWindowViewModel owner)
        => (ApplicationComposition)(typeof(MainWindowViewModel)
            .GetField("applicationComposition", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(owner) ?? throw new InvalidOperationException("Actual main-window composition is unavailable."));

    /// <summary>専用設定と任意のスコア読取りを接続し、実起動では同libraryの型付き能力とPを共有します。</summary>
    /// <param name="databaseScope">独立fixtureが共有するDB所有者。library指定時はlibrary自身の所有者を使います。</param>
    /// <param name="library">実起動factoryの所有ライブラリ。純粋な独立port fixtureでは省略します。</param>
    internal static TestBmsPlaylist CreatePlaylist(
        ApplicationDatabaseScope databaseScope,
        string songDbPath,
        Settings settings,
        Lr2ConfigOwner? getLr2Config = null,
        Func<CancellationToken, Task<WalkureScoreInput>>? recommendationScoreReader = null,
        BMSLibrary? library = null)
    {
        if (library != null)
        {
            return new TestBmsPlaylist(
                new BmsPlaylistLibraryBindings(library),
                songDbPath,
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings),
                getLr2Config,
                () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings),
                () => BeatorajaBmtOptionsSnapshot.CreateCurrent(settings),
                recommendationScoreReader);
        }
        return new TestBmsPlaylist(
            databaseScope,
            songDbPath,
            getLr2Config: getLr2Config,
            scoreDbPath: null,
            getBeatorajaBmtSongHashResolver: null,
            playlistUrlCompletionOptionsProvider: () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings),
            beatorajaBmtOptionsProvider: () => BeatorajaBmtOptionsSnapshot.CreateCurrent(settings),
            customFolderOutputSettingsProvider: () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings),
            recommendationScoreReader: recommendationScoreReader);
    }

    internal static MainWindowViewModel CreateMainWindowViewModelForTest(
        this ApplicationComposition composition)
    {
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        return viewModel;
    }

}

internal sealed class NoOpSettingsEditSession : ISettingsEditSession
{
    public void SaveEnvironmentForRestart(EnvironmentSettingsChange environment, string historyIdentity)
    {
        environment.Commit(Values);
        Values.PlayHistorySelectedDisplayTargetIdentity = historyIdentity;
        Save();
        Reload();
    }

    internal NoOpSettingsEditSession(Settings values)
    {
        Values = values ?? throw new ArgumentNullException(nameof(values));
    }

    public Settings Values { get; }

    public void Reload()
    {
    }

    public void Save()
    {
    }
}

internal sealed class TestSettingsDialogStatePort : ISettingsDialogStatePort
{
    private readonly MainWindowViewModel owner;
    private readonly Func<Task<StartupInitializationResult>> initializeLibrary;
    private readonly Func<LibraryFileMutationCapability?, LeapYearFolderRepairApproval?, Action<LeapYearFolderRepairNotification>?, bool, Task<StartupInitializationResult>>? initializeAcceptedLibrary;
    private readonly Func<LibraryFileMutationCapability?, Task> reloadScoresOnly;
    private readonly Func<LibraryFileMutationCapability?, Task> reloadFileDiff;
    private readonly Action? initializationFailed;

    /// <summary>純fake継続と実ownerの受理済み継続を分け、実継続には同じ生存権限を転送します。</summary>
    /// <param name="reloadFileDiff">実差分継続には生存権限を転送し、純fake継続は局所境界で終端を返します。</param>
    internal TestSettingsDialogStatePort(
        MainWindowViewModel owner,
        Func<Task<StartupInitializationResult>> initializeLibrary,
        Action? initializationFailed = null,
        Func<LibraryFileMutationCapability?, Task>? reloadScoresOnly = null,
        Func<LibraryFileMutationCapability?, Task>? reloadFileDiff = null,
        Func<LibraryFileMutationCapability?, LeapYearFolderRepairApproval?, Action<LeapYearFolderRepairNotification>?, bool, Task<StartupInitializationResult>>? initializeAcceptedLibrary = null)
    {
        this.initializeAcceptedLibrary = initializeAcceptedLibrary;
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.initializeLibrary = initializeLibrary
            ?? throw new ArgumentNullException(nameof(initializeLibrary));
        this.initializationFailed = initializationFailed;
        this.reloadScoresOnly = reloadScoresOnly ?? (_ => Task.CompletedTask);
        this.reloadFileDiff = reloadFileDiff ?? (_ => Task.CompletedTask);
    }

    public bool HasActiveLibraryProfile => owner.HasActiveLibraryProfile;

    public bool IsLibraryOperationInProgress => owner.IsLibraryOperationInProgress;


    public Task<StartupInitializationResult> CompleteRequiredInitializationAfterAdmissionAsync(StartupInitializationResult result)
        => result.Settings == null ? Task.FromResult(result with { CompletionPublished = result.Outcome == StartupInitializationOutcome.Succeeded })
            : ((ISettingsDialogStatePort)owner).CompleteRequiredInitializationAfterAdmissionAsync(result);


    public Task<LeapYearFolderRepairApproval?> PrepareLibraryInitializationAsync()
        => ((ISettingsDialogStatePort)owner).PrepareLibraryInitializationAsync();

    public Task PresentLeapYearFolderRepairAsync(LeapYearFolderRepairNotification? notification)
        => ((ISettingsDialogStatePort)owner).PresentLeapYearFolderRepairAsync(notification);

    public async Task<StartupInitializationResult> InitializeLibraryAsync(LibraryFileMutationCapability? capability = null,
        LeapYearFolderRepairApproval? leapYearRepairApproval = null, Action<LeapYearFolderRepairNotification>? repairNotificationObserver = null, bool forceBeatorajaBmtProjection = false)
    {
        StartupInitializationResult result = await (initializeAcceptedLibrary == null
            ? initializeLibrary() : initializeAcceptedLibrary(capability, leapYearRepairApproval, repairNotificationObserver, forceBeatorajaBmtProjection));
        if (result.Outcome == StartupInitializationOutcome.SettingsRequired)
        {
            initializationFailed?.Invoke();
        }
        return result;
    }

    public async Task<StartupInitializationResult> ReloadScoresOnlyAsync(LibraryFileMutationCapability capability)
    {
        Task operation = reloadScoresOnly(capability);
        await operation;
        return operation is Task<StartupInitializationResult> result ? await result : new(StartupInitializationOutcome.Succeeded);
    }

    public Task PresentLibraryDirectoryWarningAsync(LibraryDirectoryPreflightException failure)
        => ((ISettingsDialogStatePort)owner).PresentLibraryDirectoryWarningAsync(failure);

    /// <summary>受理済みの実差分継続だけ同じ生存権限を渡し、純fakeは局所依存のまま実終端を返します。</summary>
    public void ReevaluatePendingPackagesForSearchRoots(LibraryFileMutationCapability capability, out Action publication)
        => ((ISettingsDialogStatePort)owner).ReevaluatePendingPackagesForSearchRoots(capability, out publication);

    public async Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability? capability = null, Lr2SearchRootSnapshot? searchRoots = null)
    {
        Task operation = reloadFileDiff(capability);
        await operation;
        return operation is Task<StartupInitializationResult> result ? await result : new(StartupInitializationOutcome.Succeeded);
    }

    public event EventHandler? LibraryOperationAvailabilityChanged;

    public event Action<Lr2PlayHistorySchemaStatusSnapshot>? Lr2PlayHistorySchemaStatusChanged;

    internal void NotifyLibraryOperationAvailabilityChanged()
        => LibraryOperationAvailabilityChanged?.Invoke(this, EventArgs.Empty);

    internal void NotifyLr2PlayHistorySchemaStatusChanged(Lr2PlayHistorySchemaStatusSnapshot snapshot)
        => Lr2PlayHistorySchemaStatusChanged?.Invoke(snapshot);
}

internal sealed class RecordingSettingsDialogPresentationPort : ISettingDialogPresentationPort
{
    private readonly Action<string>? observer;

    internal RecordingSettingsDialogPresentationPort(Action<string>? observer = null)
    {
        this.observer = observer;
    }

    internal List<string> Requests { get; } = new();

    public void OpenSettingsDialog(bool deferPresentation = false) => Record("open");

    public void OpenInitialSetupLanguageDialog() => Record("initial-setup");

    public void CloseSettingsDialog() => Record("close");

    internal Func<Task>? WaitForClose { get; set; }

    public async Task CloseSettingsDialogAsync()
    {
        CloseSettingsDialog();
        if (WaitForClose != null)
        {
            await WaitForClose();
        }
    }

    public void RefreshAppearanceSelection() => Record("refresh");

    private void Record(string request)
    {
        Requests.Add(request);
        observer?.Invoke(request);
    }
}
