using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal event EventHandler<PlaylistFolderRemovalConfirmationRequestedEventArgs> PlaylistFolderRemovalConfirmationRequested;

    /// <summary>
    /// Confirms and removes an editable custom folder from the playlist tree.
    /// </summary>
    /// <param name="table">The playlist table that owns the folder.</param>
    /// <param name="folder">The editable folder selected in the tree.</param>
    /// <returns>A task for the existing folder removal and detail refresh operation.</returns>
    internal Task RemovePlaylistFolderAsync(BMSTable table, PlaylistFolderNode folder)
    {
        if (table == null || table.is_external_sync || folder?.IsEditable != true)
        {
            return Task.CompletedTask;
        }

        if (!ConfirmPlaylistFolderRemoval())
        {
            return Task.CompletedTask;
        }

        return RemoveFolderAsync(table, folder);
    }

    private bool ConfirmPlaylistFolderRemoval()
    {
        EventHandler<PlaylistFolderRemovalConfirmationRequestedEventArgs> handler =
            PlaylistFolderRemovalConfirmationRequested
            ?? throw new InvalidOperationException("Playlist folder removal confirmation is not configured.");
        var request = new PlaylistFolderRemovalConfirmationRequestedEventArgs();
        handler(this, request);
        return request.Confirmed;
    }
}

internal sealed class PlaylistFolderRemovalConfirmationRequestedEventArgs : EventArgs
{
    internal bool Confirmed { get; set; }
}
