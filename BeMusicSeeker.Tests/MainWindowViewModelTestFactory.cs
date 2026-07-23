using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class MainWindowViewModelTestFactory
{
    internal static MainWindowViewModel Create()
    {
        return new ApplicationComposition().CreateMainWindowViewModel();
    }
}
