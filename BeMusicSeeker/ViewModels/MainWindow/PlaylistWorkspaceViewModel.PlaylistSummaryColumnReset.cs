using System;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal event EventHandler<PlaylistSummaryColumnResetConfirmationRequestedEventArgs> PlaylistSummaryColumnResetConfirmationRequested;

    /// <summary>
    /// Requests shell confirmation and resets the playlist summary columns when approved.
    /// </summary>
    /// <returns><see langword="true"/> when the settings were reset.</returns>
    internal bool TryResetPlaylistSummaryColumnsToDefault()
    {
        EventHandler<PlaylistSummaryColumnResetConfirmationRequestedEventArgs> handler =
            PlaylistSummaryColumnResetConfirmationRequested
            ?? throw new InvalidOperationException("Playlist summary column reset confirmation is not configured.");
        var request = new PlaylistSummaryColumnResetConfirmationRequestedEventArgs();
        handler(this, request);
        if (!request.Confirmed)
        {
            return false;
        }

        ResetPlaylistSummaryColumnsToDefault();
        return true;
    }
}

internal sealed class PlaylistSummaryColumnResetConfirmationRequestedEventArgs : EventArgs
{
    internal bool Confirmed { get; set; }
}
