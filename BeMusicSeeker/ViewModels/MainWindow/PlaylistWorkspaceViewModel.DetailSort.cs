using System;
using System.ComponentModel;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private ChartListSortParameters playlistDetailSortParameters;

    private long playlistDetailSortRevision;

    /// <summary>
    /// Raised after the playlist-detail owner accepts a different table sort.
    /// </summary>
    internal event EventHandler<MainChartListSortRequestedEventArgs> PlaylistDetailSortChanged;

    /// <summary>
    /// Gets the sort snapshot owned by the playlist-detail workflow.
    /// </summary>
    internal ChartListSortParameters PlaylistDetailSortParameters => CapturePlaylistDetailSortParameters();

    /// <summary>
    /// Captures the current playlist-detail sort without exposing mutable owner state.
    /// </summary>
    internal ChartListSortParameters CapturePlaylistDetailSortParameters()
    {
        return playlistDetailSortParameters == null
            ? null
            : new ChartListSortParameters
            {
                ColumnsName = playlistDetailSortParameters.ColumnsName,
                Direction = playlistDetailSortParameters.Direction
            };
    }

    /// <summary>
    /// Initializes the detail sort from the sort that was active before entering the detail view.
    /// The captured value is used only on the first entry; subsequent detail interactions remain
    /// owned by the playlist workspace.
    /// </summary>
    /// <param name="fallback">The sort active in the shared table before the detail view was entered.</param>
    internal void InitializePlaylistDetailSort(ChartListSortParameters fallback)
    {
        if (playlistDetailSortParameters != null || fallback == null)
        {
            return;
        }

        playlistDetailSortParameters = new ChartListSortParameters
        {
            ColumnsName = fallback.ColumnsName,
            Direction = fallback.Direction
        };
        RaisePropertyChanged(nameof(PlaylistDetailSortParameters));
    }

    /// <summary>
    /// Accepts a shared main-table sort interaction for the playlist-detail workflow.
    /// </summary>
    /// <param name="columnName">The requested detail-row sort member.</param>
    /// <param name="direction">The requested sort direction.</param>
    internal void RequestPlaylistDetailSort(string columnName, ListSortDirection direction)
    {
        string normalizedColumnName = string.IsNullOrWhiteSpace(columnName)
            ? nameof(PlaylistDetailRow.Title)
            : columnName;
        if (playlistDetailSortParameters != null
            && string.Equals(playlistDetailSortParameters.ColumnsName, normalizedColumnName, StringComparison.Ordinal)
            && playlistDetailSortParameters.Direction == direction)
        {
            return;
        }

        playlistDetailSortParameters = new ChartListSortParameters
        {
            ColumnsName = normalizedColumnName,
            Direction = direction
        };
        RaisePropertyChanged(nameof(PlaylistDetailSortParameters));
        PlaylistDetailSortChanged?.Invoke(
            this,
            new MainChartListSortRequestedEventArgs(
                normalizedColumnName,
                direction,
                MainChartListSortTarget.Regular,
                ++playlistDetailSortRevision));
    }

    /// <summary>
    /// Returns whether a detail-sort refresh request still represents the current owner state.
    /// </summary>
    internal bool IsCurrentPlaylistDetailSortRequest(MainChartListSortRequestedEventArgs request)
    {
        return request != null
            && request.Target == MainChartListSortTarget.Regular
            && request.OwnerRevision == playlistDetailSortRevision
            && playlistDetailSortParameters != null
            && string.Equals(playlistDetailSortParameters.ColumnsName, request.ColumnName, StringComparison.Ordinal)
            && playlistDetailSortParameters.Direction == request.Direction;
    }
}
