using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal event EventHandler<PlaylistSummaryRemovalConfirmationRequestedEventArgs> PlaylistSummaryRemovalConfirmationRequested;

    /// <summary>
    /// Confirms and removes the playlist tables represented by summary rows.
    /// </summary>
    /// <param name="rows">Rows selected in the playlist summary table.</param>
    /// <returns>A task for the existing playlist removal and summary refresh operation.</returns>
    internal Task RemovePlaylistSummaryRowsAsync(IEnumerable<PlaylistSummaryRow> rows)
    {
        List<PlaylistSummaryRow> selectedRows = [.. (rows ?? []).Where(row => row != null)];
        if (selectedRows.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (!ConfirmPlaylistSummaryRemoval())
        {
            return Task.CompletedTask;
        }

        return RemoveTablesAsync(selectedRows.Select(row => row.TableRef));
    }

    private bool ConfirmPlaylistSummaryRemoval()
    {
        EventHandler<PlaylistSummaryRemovalConfirmationRequestedEventArgs> handler =
            PlaylistSummaryRemovalConfirmationRequested
            ?? throw new InvalidOperationException("Playlist summary removal confirmation is not configured.");
        var request = new PlaylistSummaryRemovalConfirmationRequestedEventArgs();
        handler(this, request);
        return request.Confirmed;
    }
}

internal sealed class PlaylistSummaryRemovalConfirmationRequestedEventArgs : EventArgs
{
    internal bool Confirmed { get; set; }
}
