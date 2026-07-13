using System;
using System.Windows;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>
/// Adapts the application dialog route to playback-specific interactions.
/// </summary>
internal sealed class WpfPlaybackDialogService : IPlaybackDialogService
{
    private readonly IUiDialogService dialogs;

    internal WpfPlaybackDialogService(IUiDialogService dialogs)
    {
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    public bool ConfirmTemporaryInstallPlayback()
    {
        UiDialogResult result = dialogs
            .ConfirmAsync(new UiConfirmationRequest(
                Resources.Msg_warn_play_temp_install,
                Resources.Warning,
                MessageBoxButton.YesNo,
                MessageBoxImage.Exclamation,
                MessageBoxResult.None))
            .GetAwaiter()
            .GetResult();
        if (result.Status is UiDialogStatus.Accepted)
        {
            return true;
        }
        if (result.Status is UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return false;
        }
        throw result.Exception ?? new InvalidOperationException("Temporary install playback confirmation was not shown: " + result.Status);
    }

    public void NotifyPlaybackFailure(Exception exception)
    {
        if (exception == null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        UiDialogResult result = dialogs
            .ShowMessageAsync(new UiMessageRequest(
                Resources.Msg_failed_play + Environment.NewLine + exception.Message,
                Resources.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK))
            .GetAwaiter()
            .GetResult();
        if (result.Status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser or UiDialogStatus.ClosedByUser)
        {
            return;
        }
        throw result.Exception ?? new InvalidOperationException("Playback failure notification was not shown: " + result.Status);
    }
}
