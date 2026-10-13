using System;
using System.Windows;
using System.Windows.Navigation;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

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

    private async void hyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            SettingsDialogViewModel viewModel = DataContext as SettingsDialogViewModel
                ?? throw new InvalidOperationException("Setting dialog view model is unavailable.");
            viewModel.ExternalShellGateway.Open(ExternalShellRequest.OpenUrl(e.Uri.ToString()));
        }
        catch (Exception exception)
        {
            TryLogLaunchFailure(exception);
            try
            {
                UiDialogResult notification = await new UiDialogCoordinator().ShowMessageAsync(new UiMessageRequest(
                    BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + exception.Message,
                    BeMusicSeeker.Properties.Resources.Error,
                    MessageBoxButton.OK,
                    MessageBoxImage.Hand,
                    MessageBoxResult.OK,
                    owner: this));
                UiDialogRoute.ThrowIfNotShown(notification, "Release notes link failure notification");
            }
            catch (Exception notificationFailure) { TryLogLaunchFailure(notificationFailure); }
        }
    }

    private static void TryLogLaunchFailure(Exception exception)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, "release notes link launch"); }
        catch { /* 任意通知の診断失敗を再通知しません。 */ }
    }
}
