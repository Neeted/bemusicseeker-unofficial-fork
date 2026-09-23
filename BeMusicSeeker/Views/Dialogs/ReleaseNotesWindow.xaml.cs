using System;
using System.Windows.Navigation;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>Displays the release-history source in an owned modal window without participating in settings editing.</summary>
public partial class ReleaseNotesWindow : ThemedWindow
{
    /// <summary>Initializes the release-notes window with the application title-bar theme.</summary>
    public ReleaseNotesWindow()
        : this(new DwmNativeWindowTitleBarGateway(), new AppNativeWindowTitleBarThemeSource())
    {
    }

    /// <summary>Initializes the window with testable title-bar boundaries.</summary>
    internal ReleaseNotesWindow(
        INativeWindowTitleBarGateway titleBarGateway,
        INativeWindowTitleBarThemeSource titleBarThemeSource)
        : base(titleBarGateway, titleBarThemeSource)
    {
        InitializeComponent();
    }

    private void hyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        SettingsDialogViewModel viewModel = DataContext as SettingsDialogViewModel
            ?? throw new InvalidOperationException("Setting dialog view model is unavailable.");
        viewModel.ExternalShellGateway.Open(ExternalShellRequest.OpenUrl(e.Uri.ToString()));
        e.Handled = true;
    }
}
