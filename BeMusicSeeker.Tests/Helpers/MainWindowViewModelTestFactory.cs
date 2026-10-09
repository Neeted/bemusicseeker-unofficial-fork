using System;
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
    /// <summary>
    /// 明示された設定、または他の testhost と共有しない既定値で画面 owner を構成する。
    /// 永続化は NoOpSettingsEditSession が抑止するため、既定の専用 path にファイルは作られない。
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
        Settings settings = PortableSettingsPersistenceTests.OpenSettings(Path.Combine(
            Path.GetTempPath(),
            "BmsViewModelSettings-" + Guid.NewGuid().ToString("N"),
            "user.config"));
        configure?.Invoke(settings);
        return settings;
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
        Func<LR2Config>? getLr2Config = null,
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
    public void SaveOperationModeForRestart(bool operationMode, string historyIdentity)
    {
        Values.OperationModeLR2DB = operationMode;
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
    private readonly Func<Task<StartupInitializationOutcome>> initializeLibrary;
    private readonly Func<LibraryFileMutationCapability?, Task<StartupInitializationOutcome>>? initializeAcceptedLibrary;
    private readonly Func<LibraryFileMutationCapability?, Task> reloadScoresOnly;
    private readonly Func<LibraryFileMutationCapability?, Task> reloadFileDiff;
    private readonly Action? initializationFailed;

    /// <summary>純fake継続と実ownerの受理済み継続を分け、実継続には同じ生存権限を転送します。</summary>
    /// <param name="reloadFileDiff">実差分継続には生存権限を転送し、純fake継続は局所境界で終端を返します。</param>
    internal TestSettingsDialogStatePort(
        MainWindowViewModel owner,
        Func<Task<StartupInitializationOutcome>> initializeLibrary,
        Action? initializationFailed = null,
        Func<LibraryFileMutationCapability?, Task>? reloadScoresOnly = null,
        Func<LibraryFileMutationCapability?, Task>? reloadFileDiff = null,
        Func<LibraryFileMutationCapability?, Task<StartupInitializationOutcome>>? initializeAcceptedLibrary = null)
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

    public async Task<StartupInitializationOutcome> InitializeLibraryAsync(LibraryFileMutationCapability? capability = null)
    {
        StartupInitializationOutcome outcome = await (initializeAcceptedLibrary == null
            ? initializeLibrary() : initializeAcceptedLibrary(capability));
        if (outcome == StartupInitializationOutcome.SettingsRequired)
        {
            initializationFailed?.Invoke();
        }
        return outcome;
    }

    public Task ReloadScoresOnlyAsync(LibraryFileMutationCapability capability) => reloadScoresOnly(capability);

    public Task PresentLibraryDirectoryWarningAsync(LibraryDirectoryPreflightException failure)
        => ((ISettingsDialogStatePort)owner).PresentLibraryDirectoryWarningAsync(failure);

    /// <summary>受理済みの実差分継続だけ同じ生存権限を渡し、純fakeは局所依存のまま実終端を返します。</summary>
    public Task ReloadFileDiffAsync(LibraryFileMutationCapability? capability = null)
        => reloadFileDiff(capability);

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
