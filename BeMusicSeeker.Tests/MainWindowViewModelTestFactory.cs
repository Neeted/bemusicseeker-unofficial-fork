using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
        settings ??= PortableSettingsPersistenceTests.OpenSettings(Path.Combine(
            Path.GetTempPath(),
            "BmsViewModelSettings-" + Guid.NewGuid().ToString("N"),
            "user.config"));
        return new ApplicationComposition(
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            fileDbMutationDialogService: fileDbMutationDialogs)
            .CreateMainWindowViewModelForTest();
    }

    internal static TestBmsLibrary CreateLibrary(string songDbPath, Settings settings)
    {
        return new TestBmsLibrary(
            songDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => BmsLibraryOptionsSnapshot.CreateCurrent(settings));
    }

    internal static TestBmsPlaylist CreatePlaylist(string songDbPath, Settings settings)
    {
        return new TestBmsPlaylist(
            songDbPath,
            getLr2Config: null,
            scoreDbPath: null,
            getBmsScores: null,
            getBeatorajaBmtSongHashResolver: null,
            playlistUrlCompletionOptionsProvider: () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(settings),
            beatorajaBmtOptionsProvider: () => BeatorajaBmtOptionsSnapshot.CreateCurrent(settings),
            customFolderOutputSettingsProvider: () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings));
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
    private readonly Func<Task<bool>> initializeLibrary;
    private readonly Func<Task> reloadScoresOnly;
    private readonly Func<Task> reloadFileDiff;
    private readonly Action? initializationFailed;

    internal TestSettingsDialogStatePort(
        MainWindowViewModel owner,
        Func<Task<bool>> initializeLibrary,
        Action? initializationFailed = null,
        Func<Task>? reloadScoresOnly = null,
        Func<Task>? reloadFileDiff = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.initializeLibrary = initializeLibrary
            ?? throw new ArgumentNullException(nameof(initializeLibrary));
        this.initializationFailed = initializationFailed;
        this.reloadScoresOnly = reloadScoresOnly ?? (() => Task.CompletedTask);
        this.reloadFileDiff = reloadFileDiff ?? (() => Task.CompletedTask);
    }

    public bool HasActiveLibraryProfile => owner.HasActiveLibraryProfile;

    public bool IsLibraryOperationInProgress => owner.IsLibraryOperationInProgress;

    public async Task<bool> InitializeLibraryAsync()
    {
        bool initialized = await initializeLibrary();
        if (!initialized)
        {
            initializationFailed?.Invoke();
        }
        return initialized;
    }

    public Task ReloadScoresOnlyAsync() => reloadScoresOnly();

    public Task ReloadFileDiffAsync() => reloadFileDiff();

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

    public void OpenSettingsDialog() => Record("open");

    public void OpenInitialSetupLanguageDialog() => Record("initial-setup");

    public void CloseSettingsDialog() => Record("close");

    public void RefreshAppearanceSelection() => Record("refresh");

    private void Record(string request)
    {
        Requests.Add(request);
        observer?.Invoke(request);
    }
}
