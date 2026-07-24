using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class MainWindowViewModelTestFactory
{
    internal static MainWindowViewModel Create()
    {
        return new ApplicationComposition().CreateMainWindowViewModelForTest();
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
