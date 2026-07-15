using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal event EventHandler<PlaylistTableRemovalConfirmationRequestedEventArgs> PlaylistTableRemovalConfirmationRequested;

    /// <summary>
    /// Requests confirmation before a playlist table is removed from the tree.
    /// The caller keeps view-host selection work before starting the existing asynchronous mutation.
    /// </summary>
    /// <param name="table">The playlist table selected for removal.</param>
    /// <returns><see langword="true"/> when the shell confirms the removal.</returns>
    internal bool ConfirmPlaylistTableRemoval(BMSTable table)
    {
        if (table == null)
        {
            return false;
        }

        EventHandler<PlaylistTableRemovalConfirmationRequestedEventArgs> handler =
            PlaylistTableRemovalConfirmationRequested
            ?? throw new InvalidOperationException("Playlist table removal confirmation is not configured.");
        var request = new PlaylistTableRemovalConfirmationRequestedEventArgs();
        handler(this, request);
        return request.Confirmed;
    }
}

internal sealed class PlaylistTableRemovalConfirmationRequestedEventArgs : EventArgs
{
    internal bool Confirmed { get; set; }
}
