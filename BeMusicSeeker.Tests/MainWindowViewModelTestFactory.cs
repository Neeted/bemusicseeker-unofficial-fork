using System;
using System.Collections.Generic;
using System.Windows.Threading;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class MainWindowViewModelTestFactory
{
    internal static MainWindowViewModel Create()
    {
        return new ApplicationComposition(
            uiDispatcherProvider: () => Dispatcher.CurrentDispatcher)
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

internal sealed class TestFirstStartupStatePort : ISettingsDialogFirstStartupStatePort
{
    internal TestFirstStartupStatePort(bool isFirstStartup = false)
    {
        IsFirstStartup = isFirstStartup;
    }

    public bool IsFirstStartup { get; }
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
