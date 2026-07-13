using System;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Exposes the user decisions and notifications required by playback workflows.
/// </summary>
internal interface IPlaybackDialogService
{
    bool ConfirmTemporaryInstallPlayback();

    void NotifyPlaybackFailure(Exception exception);
}
