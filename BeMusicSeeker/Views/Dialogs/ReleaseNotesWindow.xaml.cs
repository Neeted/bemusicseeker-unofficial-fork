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
    private readonly IUiDialogService dialogService;

    /// <summary>Initializes the release-notes window with the application title-bar theme.</summary>
    /// <param name="dialogService">リンク起動失敗の通知に使う窓口。設定画面が使う窓口を渡し、null は許されません。</param>
    internal ReleaseNotesWindow(IUiDialogService dialogService)
        : this(new DwmNativeWindowTitleBarGateway(), new AppNativeWindowTitleBarThemeSource(), dialogService)
    {
    }

    /// <summary>Initializes the window with testable title-bar boundaries.</summary>
    /// <param name="titleBarGateway">標準タイトルバー属性を適用する境界。</param>
    /// <param name="titleBarThemeSource">標準タイトルバーのsemantic paletteを供給する境界。</param>
    /// <param name="dialogService">リンク起動失敗の通知に使う窓口。null は許されません。</param>
    internal ReleaseNotesWindow(
        INativeWindowTitleBarGateway titleBarGateway,
        INativeWindowTitleBarThemeSource titleBarThemeSource,
        IUiDialogService dialogService)
        : base(titleBarGateway, titleBarThemeSource)
    {
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
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
                UiDialogResult notification = await dialogService.ShowMessageAsync(new UiMessageRequest(
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
