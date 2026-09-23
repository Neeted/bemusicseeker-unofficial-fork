using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Provides the playlist/library owner boundary used by the lamp viewer window.
/// </summary>
internal sealed class PlaylistLampViewerOpenContext
{
    /// <summary>Creates an immutable open context for one active playlist table.</summary>
    /// <param name="playlistId">Stable playlist identity.</param>
    /// <param name="playlistName">Current playlist display name.</param>
    /// <param name="table">The active table resolved from the playlist owner.</param>
    /// <param name="playlist">The playlist owner.</param>
    /// <param name="library">The chart library owner.</param>
    internal PlaylistLampViewerOpenContext(
        string playlistId,
        string playlistName,
        BMSTable table,
        BMSPlaylist playlist,
        BMSLibrary library)
    {
        PlaylistId = playlistId ?? string.Empty;
        PlaylistName = playlistName ?? string.Empty;
        Table = table ?? throw new ArgumentNullException(nameof(table));
        Playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
        Library = library ?? throw new ArgumentNullException(nameof(library));
    }

    /// <summary>Stable playlist identity used by the lamp source.</summary>
    internal string PlaylistId { get; }

    /// <summary>Current playlist display name.</summary>
    internal string PlaylistName { get; }

    /// <summary>Active table resolved from the current playlist store.</summary>
    internal BMSTable Table { get; }

    /// <summary>Playlist owner used by the lamp data source.</summary>
    internal BMSPlaylist Playlist { get; }

    /// <summary>Library owner used by the lamp data source.</summary>
    internal BMSLibrary Library { get; }
}

/// <summary>
/// Carries one atomic folder or overall/category navigation request from a lamp viewer
/// to the playlist workspace.
/// </summary>
internal sealed class PlaylistLampNavigationRequestedEventArgs : EventArgs
{
    /// <summary>Creates a navigation request notification.</summary>
    /// <param name="request">The source segment intent, including its navigation scope.</param>
    /// <param name="selection">The resolved playlist/folder selection.</param>
    /// <param name="filterSnapshot">The replacement chart filter.</param>
    /// <param name="revision">Monotonic workspace request revision.</param>
    internal PlaylistLampNavigationRequestedEventArgs(
        PlaylistLampViewerNavigationRequest request,
        PlaylistDetailSelection selection,
        ChartListFilterSnapshot filterSnapshot,
        long revision,
        long filterRevision)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Selection = selection ?? throw new ArgumentNullException(nameof(selection));
        FilterSnapshot = filterSnapshot ?? throw new ArgumentNullException(nameof(filterSnapshot));
        Revision = revision;
        FilterRevision = filterRevision;
    }

    /// <summary>Original typed lamp segment intent and navigation scope.</summary>
    internal PlaylistLampViewerNavigationRequest Request { get; }

    /// <summary>Resolved playlist detail selection.</summary>
    internal PlaylistDetailSelection Selection { get; }

    /// <summary>Replacement filter snapshot with the previous keyword replaced.</summary>
    internal ChartListFilterSnapshot FilterSnapshot { get; }

    /// <summary>Workspace navigation revision.</summary>
    internal long Revision { get; }

    /// <summary>Detail-filter revision paired with this navigation intent.</summary>
    internal long FilterRevision { get; }
}

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>
    /// Raised after a lamp segment has been resolved into one folder-and-filter intent.
    /// </summary>
    internal event EventHandler<PlaylistLampNavigationRequestedEventArgs> PlaylistLampNavigationRequested;

    /// <summary>
    /// Resolves a context-menu table against the current playlist store and returns the
    /// immutable dependencies required for a fresh lamp viewer instance.
    /// </summary>
    /// <param name="table">The table captured by the context menu.</param>
    /// <returns>An open context, or <see langword="null"/> when the table is stale or incomplete.</returns>
    internal PlaylistLampViewerOpenContext CapturePlaylistLampViewerOpenContext(BMSTable table)
    {
        BMSTable activeTable = ResolveActivePlaylistTable(table, table?.name);
        return CreatePlaylistLampViewerOpenContext(activeTable);
    }

    /// <summary>
    /// Resolves a summary row's table against the current playlist store for a fresh lamp viewer.
    /// </summary>
    /// <param name="row">The single summary row captured by the context menu.</param>
    /// <returns>An open context, or <see langword="null"/> when the row is stale.</returns>
    internal PlaylistLampViewerOpenContext CapturePlaylistLampViewerOpenContext(PlaylistSummaryRow row)
    {
        BMSTable activeTable = ResolveActivePlaylistSummaryTable(row);
        return CreatePlaylistLampViewerOpenContext(activeTable);
    }

    /// <summary>
    /// Converts a folder-scoped core segment into one atomic playlist selection and filter publication.
    /// Stale/deleted tables and unknown normal folders are intentionally ignored.
    /// </summary>
    /// <param name="request">The typed segment intent produced by the viewer.</param>
    /// <returns><see langword="true"/> when a request was accepted for presentation.</returns>
    internal bool TryRequestPlaylistLampNavigation(PlaylistLampSegmentInvocationRequest request)
    {
        return request != null
            && TryRequestPlaylistLampNavigation(PlaylistLampViewerNavigationRequest.ForFolder(request));
    }

    /// <summary>
    /// Converts a typed viewer category into one atomic playlist selection and filter
    /// publication. Overall requests select the active table root; folder requests retain
    /// the existing normal-folder guard.
    /// </summary>
    /// <param name="request">The scoped viewer category intent.</param>
    /// <returns><see langword="true"/> when a request was accepted for presentation.</returns>
    internal bool TryRequestPlaylistLampNavigation(PlaylistLampViewerNavigationRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.PlaylistId))
        {
            return false;
        }

        BMSTable activeTable = FindActivePlaylistTableById(request.PlaylistId);
        if (activeTable == null
            || (request.Scope == PlaylistLampViewerNavigationScope.Folder
                && !ContainsNormalFolder(activeTable, request.FolderName)))
        {
            return false;
        }

        string keywordFilter = PlaylistLampNavigationQuery.Create(request);
        if (string.IsNullOrWhiteSpace(keywordFilter))
        {
            return false;
        }

        ChartListFilterSnapshot currentFilter = CapturePlaylistDetailFilterSnapshot();
        var nextFilter = new ChartListFilterSnapshot(keywordFilter, currentFilter.ModeFilter);
        var selection = new PlaylistDetailSelection(
            activeTable,
            request.Scope == PlaylistLampViewerNavigationScope.Overall
                ? PlaylistDetailSelectionScope.OverallNormalFolders
                : PlaylistDetailSelectionScope.Folder,
            request.Scope == PlaylistLampViewerNavigationScope.Overall ? null : request.FolderName,
            PlaylistDetailFilter.PlaylistFilter);
        long revision;
        lock (playlistDetailSelectionSyncRoot)
        {
            playlistDetailSelection = selection;
            revision = ++playlistDetailSelectionRevision;
        }
        playlistDetailFilterSnapshot = nextFilter;
        long filterRevision = ++playlistDetailFilterRevision;
        RequestPlaylistSummaryMode(enabled: false);
        RaisePropertyChanged(nameof(PlaylistDetailFilterSnapshot));

        // Both the selection and its replacement filter are captured above before the
        // dispatcher callback is queued. Subscribers therefore observe one complete intent,
        // never a transient folder with the previous keyword.
        dispatchPresentation(
            () =>
            {
                if (!IsCurrentPlaylistLampNavigation(revision, filterRevision, selection, nextFilter))
                {
                    return;
                }
                PlaylistLampNavigationRequested?.Invoke(
                    this,
                    new PlaylistLampNavigationRequestedEventArgs(
                        request,
                        selection.Clone(),
                        new ChartListFilterSnapshot(nextFilter.KeywordFilter, nextFilter.ModeFilter),
                        revision,
                        filterRevision));
            });
        return true;
    }

    /// <summary>Returns whether the specified lamp navigation callback still owns the workspace state.</summary>
    /// <param name="selectionRevision">Selection revision captured by the request.</param>
    /// <param name="filterRevision">Filter revision captured by the request.</param>
    /// <param name="selection">Selection captured by the request.</param>
    /// <param name="filter">Filter captured by the request.</param>
    /// <returns><see langword="true"/> when both owner snapshots are current.</returns>
    internal bool IsCurrentPlaylistLampNavigation(
        long selectionRevision,
        long filterRevision,
        PlaylistDetailSelection selection,
        ChartListFilterSnapshot filter)
    {
        bool selectionCurrent;
        lock (playlistDetailSelectionSyncRoot)
        {
            selectionCurrent = selectionRevision == playlistDetailSelectionRevision
                && playlistDetailSelection?.Matches(selection) == true;
        }
        return selectionCurrent
            && filterRevision == playlistDetailFilterRevision
            && playlistDetailFilterSnapshot != null
            && filter != null
            && string.Equals(playlistDetailFilterSnapshot.KeywordFilter, filter.KeywordFilter, StringComparison.Ordinal)
            && playlistDetailFilterSnapshot.ModeFilter == filter.ModeFilter;
    }

    private PlaylistLampViewerOpenContext CreatePlaylistLampViewerOpenContext(BMSTable activeTable)
    {
        if (activeTable?.playlist_id is not int playlistId)
        {
            return null;
        }
        BMSPlaylist playlist = getPlaylistStore();
        BMSLibrary library = getPlaylistLibrary();
        if (playlist == null || library == null || !playlist.ContainsBMSTable(activeTable))
        {
            return null;
        }
        return new PlaylistLampViewerOpenContext(
            playlistId.ToString(CultureInfo.InvariantCulture),
            activeTable.name,
            activeTable,
            playlist,
            library);
    }

    private BMSTable FindActivePlaylistTableById(string playlistId)
    {
        if (!int.TryParse(playlistId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericId))
        {
            return null;
        }
        BMSPlaylist playlist = getPlaylistStore();
        if (playlist == null)
        {
            return null;
        }
        playlist.AcquireReaderLockBMSTables();
        try
        {
            return (playlist.BMSTables ?? [])
                .FirstOrDefault(table => table?.playlist_id == numericId);
        }
        finally
        {
            playlist.FreeReaderLockBMSTables();
        }
    }

    private static bool ContainsNormalFolder(BMSTable table, string folderName)
    {
        return table != null
            && !string.Equals(folderName, "[NO SONG]", StringComparison.Ordinal)
            && (table.folder_list ?? []).Any(folder => string.Equals(folder, folderName, StringComparison.Ordinal));
    }
}

/// <summary>Serializes typed lamp categories to the existing chart search language.</summary>
internal static class PlaylistLampNavigationQuery
{
    /// <summary>Creates one exact category query for a folder-scoped core segment.</summary>
    /// <param name="request">The typed segment intent.</param>
    /// <returns>A chart-search expression, or an empty string for an invalid request.</returns>
    internal static string Create(PlaylistLampSegmentInvocationRequest request)
    {
        return request == null
            ? string.Empty
            : Create(request.Kind, request.ClearCategory, request.RankCategory);
    }

    /// <summary>Creates one exact category query for a scoped viewer segment.</summary>
    /// <param name="request">The scoped viewer segment intent.</param>
    /// <returns>A chart-search expression, or an empty string for an invalid request.</returns>
    internal static string Create(PlaylistLampViewerNavigationRequest request)
    {
        return request == null
            ? string.Empty
            : Create(request.Kind, request.ClearCategory, request.RankCategory);
    }

    private static string Create(
        PlaylistLampSegmentKind kind,
        PlaylistLampClearCategory? clearCategory,
        PlaylistLampRankCategory? rankCategory)
    {
        if (kind == PlaylistLampSegmentKind.Clear && clearCategory is { } clear)
        {
            return clear switch
            {
                PlaylistLampClearCategory.MAX => "clear:max",
                PlaylistLampClearCategory.PERFECT => "clear:pf",
                PlaylistLampClearCategory.FC => "clear:fc",
                PlaylistLampClearCategory.EXHARD => "clear:exh",
                PlaylistLampClearCategory.HARD => "clear:hc",
                PlaylistLampClearCategory.NORMAL => "clear:nc",
                PlaylistLampClearCategory.EASY => "clear:ec",
                PlaylistLampClearCategory.ASSIST => "clear:ae|lae",
                PlaylistLampClearCategory.FAILED => "clear:f",
                PlaylistLampClearCategory.NP => "clear:np|nosong",
                _ => string.Empty
            };
        }
        if (kind == PlaylistLampSegmentKind.Rank && rankCategory is { } rank)
        {
            return rank switch
            {
                PlaylistLampRankCategory.AAA => "rank:aaa|max",
                PlaylistLampRankCategory.AA => "rank:aa",
                PlaylistLampRankCategory.A => "rank:a",
                PlaylistLampRankCategory.B => "rank:b",
                PlaylistLampRankCategory.C => "rank:c",
                PlaylistLampRankCategory.D => "rank:d",
                PlaylistLampRankCategory.E => "rank:e",
                PlaylistLampRankCategory.F => "rank:f -clear:np|nosong",
                PlaylistLampRankCategory.NP => "clear:np|nosong",
                _ => string.Empty
            };
        }
        return string.Empty;
    }
}
