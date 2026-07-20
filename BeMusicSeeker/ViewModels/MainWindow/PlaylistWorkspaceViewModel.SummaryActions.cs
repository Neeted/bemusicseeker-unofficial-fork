using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal event EventHandler<PlaylistSummaryExternalSyncConfirmationRequestedEventArgs> PlaylistSummaryExternalSyncConfirmationRequested;

    internal async Task HandlePlaylistSummaryCellActionAsync(
        IEnumerable<PlaylistSummaryRow> selectedRows,
        PlaylistSummaryRow row,
        string columnId)
    {
        if (row == null || string.IsNullOrWhiteSpace(columnId))
        {
            return;
        }

        switch (columnId)
        {
            case "Link":
                await OpenPlaylistSummaryUriAsync(row.LinkUri);
                return;
            case "Header":
                await OpenPlaylistSummaryUriAsync(row.HeaderUri);
                return;
            case "Data":
                await OpenPlaylistSummaryUriAsync(row.DataUri);
                return;
            case "IsExternalSync":
                await ApplyPlaylistSummaryCellActionAsync(
                    selectedRows,
                    columnId,
                    !row.IsExternalSync);
                return;
            case "IsRootFolder":
                await ApplyPlaylistSummaryCellActionAsync(
                    selectedRows,
                    columnId,
                    !row.IsRootFolder);
                return;
            case "IsBmtOutput":
                await ApplyPlaylistSummaryCellActionAsync(
                    selectedRows,
                    columnId,
                    !row.IsBmtOutput);
                return;
        }
    }

    internal bool CanDropSummaryRowsInBmtOrder(IEnumerable<PlaylistSummaryRow> rows)
    {
        return IsPlaylistSummarySortedByBmtSortAscending
            && CapturePlaylistSummaryActionRows(rows).Count > 0;
    }

    /// <summary>
    /// Applies a playlist-summary flag-cell action through the workspace owner.
    /// </summary>
    /// <param name="rows">Rows selected in the summary table.</param>
    /// <param name="columnId">The summary column whose flag was activated.</param>
    /// <param name="value">The requested flag value.</param>
    /// <returns>A task for the owned persistence and refresh operation.</returns>
    internal async Task ApplyPlaylistSummaryCellActionAsync(
        IEnumerable<PlaylistSummaryRow> rows,
        string columnId,
        bool value)
    {
        List<PlaylistSummaryRow> actionRows = CapturePlaylistSummaryActionRows(rows);
        if (actionRows.Count == 0)
        {
            return;
        }

        switch (columnId)
        {
            case "IsExternalSync":
                if (!ConfirmPlaylistSummaryExternalSyncChange(value))
                {
                    return;
                }
                await Task.Run(() => ApplyPlaylistSummaryFlags(actionRows, value, null));
                return;

            case "IsRootFolder":
                await Task.Run(() => ApplyPlaylistSummaryFlags(actionRows, null, value));
                return;

            case "IsBmtOutput":
                await Task.Run(() => ApplyPlaylistSummaryBmtOutput(actionRows, value));
                return;

            default:
                return;
        }
    }

    private static List<PlaylistSummaryRow> CapturePlaylistSummaryActionRows(
        IEnumerable<PlaylistSummaryRow> rows)
    {
        var actionRows = new List<PlaylistSummaryRow>();
        var seenTables = new HashSet<BMSTable>();
        foreach (PlaylistSummaryRow row in rows ?? [])
        {
            if (row?.TableRef != null && seenTables.Add(row.TableRef))
            {
                actionRows.Add(row);
            }
        }
        return actionRows;
    }

    private bool ConfirmPlaylistSummaryExternalSyncChange(bool enable)
    {
        EventHandler<PlaylistSummaryExternalSyncConfirmationRequestedEventArgs> handler =
            PlaylistSummaryExternalSyncConfirmationRequested
            ?? throw new InvalidOperationException("Playlist summary external-sync confirmation is not configured.");
        var request = new PlaylistSummaryExternalSyncConfirmationRequestedEventArgs(enable);
        handler(this, request);
        return request.Confirmed;
    }
}

internal sealed class PlaylistSummaryExternalSyncConfirmationRequestedEventArgs : EventArgs
{
    internal PlaylistSummaryExternalSyncConfirmationRequestedEventArgs(bool enable)
    {
        Enable = enable;
    }

    internal bool Enable { get; }

    internal bool Confirmed { get; set; }
}
