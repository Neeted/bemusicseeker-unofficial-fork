using System;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Builds normalized playlist request identities and source invalidation reasons outside the shell ViewModel.
/// </summary>
internal static class PlaylistRequestFactory
{
    /// <summary>
    /// Normalizes a playlist folder name for request identity comparison while preserving the null root-playlist marker.
    /// </summary>
    /// <param name="folderName">Folder name from the selected playlist tree node.</param>
    /// <returns>Trimmed folder name, or <see langword="null"/> when the request targets the playlist root.</returns>
    internal static string NormalizeFolderName(string folderName)
    {
        if (folderName == null)
        {
            return null;
        }

        return folderName.Trim();
    }

    /// <summary>
    /// Normalizes a playlist keyword filter for request identity comparison.
    /// </summary>
    /// <param name="keywordFilter">Keyword filter text from the main search box.</param>
    /// <returns>Upper-invariant trimmed keyword text, or an empty string for blank input.</returns>
    internal static string NormalizeKeywordFilter(string keywordFilter)
    {
        if (string.IsNullOrWhiteSpace(keywordFilter))
        {
            return string.Empty;
        }

        return keywordFilter.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Normalizes playlist sort column name for request identity comparison.
    /// </summary>
    /// <param name="sortParameters">Current sort parameters.</param>
    /// <returns>Sort column name, or an empty string when no explicit sort is set.</returns>
    internal static string NormalizeSortColumnName(ChartListSortParameters sortParameters)
    {
        return sortParameters?.ColumnsName ?? string.Empty;
    }

    /// <summary>
    /// Normalizes playlist sort direction for request identity comparison.
    /// </summary>
    /// <param name="sortParameters">Current sort parameters.</param>
    /// <returns>Current sort direction, or ascending when no explicit sort is set.</returns>
    internal static ListSortDirection NormalizeSortDirection(ChartListSortParameters sortParameters)
    {
        return sortParameters?.Direction ?? ListSortDirection.Ascending;
    }

    /// <summary>
    /// Creates the normalized identity used to coalesce and reuse playlist build requests.
    /// </summary>
    /// <param name="table">Playlist table selected by the current tree node.</param>
    /// <param name="folderName">Playlist folder name selected by the current tree node.</param>
    /// <param name="filterType">Playlist filter type selected by the current tree node.</param>
    /// <param name="keywordFilter">Keyword filter text.</param>
    /// <param name="modeFilter">BMS mode filter.</param>
    /// <param name="sortParameters">Current sort parameters.</param>
    /// <param name="libraryIndexVersion">Library resolve-index version used by the source build.</param>
    /// <param name="playlistRevision">Playlist revision used by the source build.</param>
    /// <param name="scoreSnapshotVersion">Score snapshot version used by the source build.</param>
    /// <param name="chartInfoIndexVersion">Chart-info index version used by the source build.</param>
    /// <param name="hasResolvedSelection">Whether the selected playlist source was resolved.</param>
    /// <returns>Normalized playlist request identity.</returns>
    internal static PlaylistRequestIdentity CreateIdentity(
        BMSTable table,
        string folderName,
        PlaylistDetailFilter filterType,
        string keywordFilter,
        ChartModeFilter modeFilter,
        ChartListSortParameters sortParameters,
        long libraryIndexVersion,
        long playlistRevision,
        int scoreSnapshotVersion,
        int chartInfoIndexVersion,
        bool hasResolvedSelection)
    {
        return new PlaylistRequestIdentity(
            table,
            NormalizeFolderName(folderName),
            filterType,
            NormalizeKeywordFilter(keywordFilter),
            modeFilter,
            NormalizeSortColumnName(sortParameters),
            NormalizeSortDirection(sortParameters),
            libraryIndexVersion,
            playlistRevision,
            scoreSnapshotVersion,
            chartInfoIndexVersion,
            hasResolvedSelection);
    }

    /// <summary>
    /// Determines why playlist source rows must be rebuilt.
    /// </summary>
    /// <param name="selectionChanged">Whether the selected table/folder/filter changed.</param>
    /// <param name="libraryIndexInvalidated">Whether the library resolve index changed.</param>
    /// <param name="playlistRevisionInvalidated">Whether the playlist source revision changed.</param>
    /// <param name="scoreSnapshotInvalidated">Whether the score snapshot version changed.</param>
    /// <param name="chartInfoIndexInvalidated">Whether the chart-info index version changed.</param>
    /// <param name="sourceMissing">Whether source rows are missing.</param>
    /// <returns>Diagnostic reason string consumed by playlist refresh logs.</returns>
    internal static string DetermineSourceInvalidationReason(
        bool selectionChanged,
        bool libraryIndexInvalidated,
        bool playlistRevisionInvalidated,
        bool scoreSnapshotInvalidated,
        bool chartInfoIndexInvalidated,
        bool sourceMissing)
    {
        if (selectionChanged)
        {
            return "selection_changed";
        }

        if (libraryIndexInvalidated)
        {
            return "library_index";
        }

        if (playlistRevisionInvalidated)
        {
            return "playlist_revision";
        }

        if (scoreSnapshotInvalidated)
        {
            return "score_snapshot_version";
        }

        if (chartInfoIndexInvalidated)
        {
            return "chart_info_index";
        }

        if (sourceMissing)
        {
            return "source_missing";
        }

        return "none";
    }
}
