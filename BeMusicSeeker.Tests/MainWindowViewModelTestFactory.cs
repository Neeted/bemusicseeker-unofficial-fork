using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class MainWindowViewModelTestFactory
{
    internal static MainWindowViewModel Create()
    {
        return new ApplicationComposition(
            uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog())
            .CreateMainWindowViewModelForTest();
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
