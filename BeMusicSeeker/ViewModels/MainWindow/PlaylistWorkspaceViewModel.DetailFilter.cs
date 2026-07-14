using System;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private ChartListFilterSnapshot playlistDetailFilterSnapshot;

    private long playlistDetailFilterRevision;

    /// <summary>
    /// Raised after the playlist-detail owner accepts a different table filter.
    /// </summary>
    internal event EventHandler<PlaylistDetailFilterChangedEventArgs> PlaylistDetailFilterChanged;

    /// <summary>
    /// Gets the filter snapshot owned by the playlist-detail workflow.
    /// </summary>
    internal ChartListFilterSnapshot PlaylistDetailFilterSnapshot => CapturePlaylistDetailFilterSnapshot();

    /// <summary>
    /// Captures the current playlist-detail filter without exposing mutable owner state.
    /// </summary>
    internal ChartListFilterSnapshot CapturePlaylistDetailFilterSnapshot()
    {
        return ClonePlaylistDetailFilterSnapshot(playlistDetailFilterSnapshot ?? ChartListFilterSnapshot.Default);
    }

    /// <summary>
    /// Initializes the detail filter from the shared filter control before entering the detail view.
    /// </summary>
    /// <param name="fallback">The filter values visible before the detail view was entered.</param>
    internal void InitializePlaylistDetailFilter(ChartListFilterSnapshot fallback)
    {
        ChartListFilterSnapshot nextSnapshot = ClonePlaylistDetailFilterSnapshot(fallback ?? ChartListFilterSnapshot.Default);
        if (playlistDetailFilterSnapshot != null
            && string.Equals(playlistDetailFilterSnapshot.KeywordFilter, nextSnapshot.KeywordFilter, StringComparison.Ordinal)
            && playlistDetailFilterSnapshot.ModeFilter == nextSnapshot.ModeFilter)
        {
            return;
        }

        playlistDetailFilterSnapshot = nextSnapshot;
        RaisePropertyChanged(nameof(PlaylistDetailFilterSnapshot));
    }

    /// <summary>
    /// Accepts a shared filter interaction for the playlist-detail workflow.
    /// </summary>
    /// <param name="updateMode">The filter dimension that changed.</param>
    /// <param name="filterSnapshot">The raw keyword and mode values from the shared control.</param>
    internal void RequestPlaylistDetailFilter(MainViewUpdateMode updateMode, ChartListFilterSnapshot filterSnapshot)
    {
        if (updateMode != MainViewUpdateMode.KeywordFilterUpdated
            && updateMode != MainViewUpdateMode.ModeFilterUpdated)
        {
            throw new ArgumentOutOfRangeException(nameof(updateMode), updateMode, "A playlist-detail filter update is required.");
        }
        if (filterSnapshot == null)
        {
            throw new ArgumentNullException(nameof(filterSnapshot));
        }
        if (filterSnapshot.ModeFilter == ChartModeFilter.None)
        {
            return;
        }

        if (playlistDetailFilterSnapshot != null
            && string.Equals(playlistDetailFilterSnapshot.KeywordFilter, filterSnapshot.KeywordFilter, StringComparison.Ordinal)
            && playlistDetailFilterSnapshot.ModeFilter == filterSnapshot.ModeFilter)
        {
            return;
        }

        playlistDetailFilterSnapshot = ClonePlaylistDetailFilterSnapshot(filterSnapshot);
        RaisePropertyChanged(nameof(PlaylistDetailFilterSnapshot));
        PlaylistDetailFilterChanged?.Invoke(
            this,
            new PlaylistDetailFilterChangedEventArgs(
                updateMode,
                ClonePlaylistDetailFilterSnapshot(playlistDetailFilterSnapshot),
                ++playlistDetailFilterRevision));
    }

    /// <summary>
    /// Returns whether a detail-filter refresh request still represents the current owner state.
    /// </summary>
    internal bool IsCurrentPlaylistDetailFilterRequest(PlaylistDetailFilterChangedEventArgs request)
    {
        return request != null
            && request.OwnerRevision == playlistDetailFilterRevision
            && playlistDetailFilterSnapshot != null
            && request.FilterSnapshot != null
            && string.Equals(request.FilterSnapshot.KeywordFilter, playlistDetailFilterSnapshot.KeywordFilter, StringComparison.Ordinal)
            && request.FilterSnapshot.ModeFilter == playlistDetailFilterSnapshot.ModeFilter;
    }

    private static ChartListFilterSnapshot ClonePlaylistDetailFilterSnapshot(ChartListFilterSnapshot value)
    {
        return value == null
            ? new ChartListFilterSnapshot(null, ChartModeFilter.All)
            : new ChartListFilterSnapshot(value.KeywordFilter, value.ModeFilter);
    }
}

internal sealed class PlaylistDetailFilterChangedEventArgs : EventArgs
{
    internal PlaylistDetailFilterChangedEventArgs(
        MainViewUpdateMode updateMode,
        ChartListFilterSnapshot filterSnapshot,
        long ownerRevision)
    {
        UpdateMode = updateMode;
        FilterSnapshot = filterSnapshot ?? throw new ArgumentNullException(nameof(filterSnapshot));
        OwnerRevision = ownerRevision;
    }

    internal MainViewUpdateMode UpdateMode { get; }

    internal ChartListFilterSnapshot FilterSnapshot { get; }

    internal long OwnerRevision { get; }
}
