using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
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
                if (!await ConfirmPlaylistSummaryExternalSyncChangeAsync(value))
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

    private Task<bool> ConfirmPlaylistSummaryExternalSyncChangeAsync(bool enable)
    {
        return ConfirmPlaylistWorkspaceDialogAsync(
            new UiConfirmationRequest(
                enable
                    ? BeMusicSeeker.Properties.Resources.Confirm_EnablePlaylistSyncModeLoseLocalChanges
                    : BeMusicSeeker.Properties.Resources.Confirm_DisablePlaylistSyncModeRemoteChangesNotApplied,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Exclamation,
                MessageBoxResult.Cancel),
            enable
                ? "Playlist summary sync enable confirmation"
                : "Playlist summary sync disable confirmation");
    }
}
