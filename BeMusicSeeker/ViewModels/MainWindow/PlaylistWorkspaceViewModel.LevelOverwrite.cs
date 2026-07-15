using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal event EventHandler<PlaylistTableLevelOverwriteConfirmationRequestedEventArgs> PlaylistTableLevelOverwriteConfirmationRequested;

    /// <summary>
    /// Requests the shell confirmation for overwriting local chart levels from a playlist table.
    /// Recommended tables are always rejected after the shell notification, even if a consumer
    /// accidentally marks the request as confirmed.
    /// </summary>
    /// <param name="table">The playlist table whose levels would be copied.</param>
    /// <returns><see langword="true"/> only when a non-recommended table is confirmed.</returns>
    internal bool ConfirmPlaylistOverwriteLevel(BMSTable table)
    {
        if (table == null)
        {
            return false;
        }

        var request = new PlaylistTableLevelOverwriteConfirmationRequestedEventArgs(
            !string.IsNullOrWhiteSpace(table.page_url)
                && table.page_url.StartsWith("bmseeker:table.recommended"));
        EventHandler<PlaylistTableLevelOverwriteConfirmationRequestedEventArgs> handler =
            PlaylistTableLevelOverwriteConfirmationRequested
            ?? throw new InvalidOperationException("Playlist table level overwrite confirmation is not configured.");
        handler(this, request);
        return !request.IsRecommendedTable && request.Confirmed;
    }
}

internal sealed class PlaylistTableLevelOverwriteConfirmationRequestedEventArgs : EventArgs
{
    internal PlaylistTableLevelOverwriteConfirmationRequestedEventArgs(bool isRecommendedTable)
    {
        IsRecommendedTable = isRecommendedTable;
    }

    internal bool IsRecommendedTable { get; }

    internal bool Confirmed { get; set; }
}
