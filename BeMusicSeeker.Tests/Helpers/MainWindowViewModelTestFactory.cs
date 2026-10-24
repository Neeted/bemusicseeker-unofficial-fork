using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class MainWindowViewModelTestFactory
{
    private static readonly ConcurrentBag<string> isolatedSettingsDirectories = [];

    /// <summary>
    /// 明示された設定、または他の testhost と共有しない既定値で画面 owner を構成する。
    /// 項目保存で生成した専用ファイルは、当該testhostの全処理・画面終端後に回収します。
    /// </summary>
    internal static MainWindowViewModel Create(Settings? settings = null, BeMusicSeeker.Views.Dialogs.IUiDialogService? fileDbMutationDialogs = null)
    {
        settings ??= CreateIsolatedSettings();
        return new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            fileDbMutationDialogService: fileDbMutationDialogs)
            .CreateMainWindowViewModelForTest();
    }

    /// <summary>テストホスト間で共有しないポータブル設定インスタンスを作成します。</summary>
    internal static Settings CreateIsolatedSettings(Action<Settings>? configure = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BmsViewModelSettings-" + Guid.NewGuid().ToString("N"));
        isolatedSettingsDirectories.Add(directory);
        Settings settings = PortableSettingsPersistenceTests.OpenSettings(Path.Combine(directory, "user.config"));
        configure?.Invoke(settings);
        return settings;
    }

    /// <summary>このtesthostが生成した設定専用ディレクトリだけを、全テストと共有画面の終端後に回収します。</summary>
    internal static void CleanupIsolatedSettingsDirectories()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        while (isolatedSettingsDirectories.TryTake(out string? ownedDirectory))
        {
            string fullPath = Path.GetFullPath(ownedDirectory);
            if (!string.Equals(Path.GetDirectoryName(fullPath), temporaryRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullPath).StartsWith("BmsViewModelSettings-", StringComparison.Ordinal))
            { throw new InvalidOperationException("設定fixture所有外の削除を拒否しました: " + fullPath); }
            if (Directory.Exists(fullPath)) { Directory.Delete(fullPath, recursive: true); }
        }
    }

    /// <summary>独立fixture、または既存実画面と同じcompositionのL/Pでライブラリを作成します。</summary>
    /// <param name="owner">実Attach先の画面。省略時は独立fixtureです。</param>
    internal static TestBmsLibrary CreateLibrary(string songDbPath, Settings settings, MainWindowViewModel? owner = null)
    {
        ApplicationComposition? composition = owner == null ? null : GetComposition(owner);
        return new TestBmsLibrary(
            songDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings),
            operationAdmission: composition?.OperationAdmission,
            playlistOperationAdmission: composition?.PlaylistOperationAdmission);
    }

    /// <summary>実画面が既に所有する構成をfixture入力の接続にだけ使い、別ownerを作りません。</summary>
    internal static ApplicationComposition GetComposition(MainWindowViewModel owner)
        => (ApplicationComposition)(typeof(MainWindowViewModel)
            .GetField("applicationComposition", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(owner) ?? throw new InvalidOperationException("Actual main-window composition is unavailable."));

    /// <summary>専用設定と任意のスコア読取りを接続し、実起動では同libraryの型付き能力とPを共有します。</summary>
    /// <param name="library">実起動factoryの所有ライブラリ。純粋な独立port fixtureでは省略します。</param>
    internal static TestBmsPlaylist CreatePlaylist(
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
        viewModel.PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested +=
            (_, _) => { };
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

    public bool IsInitializationCompletionCurrent(long operationToken) => ((ISettingsDialogStatePort)owner).IsInitializationCompletionCurrent(operationToken);

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
    public async Task<StartupInitializationResult> ReloadFileDiffAsync(LibraryFileMutationCapability? capability = null)
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
