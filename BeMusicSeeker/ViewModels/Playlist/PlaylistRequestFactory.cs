using System.ComponentModel;
using BeMusicSeeker.Models;

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
    /// 検索式の意味を保持したまま、要求比較に不要な前後空白を除きます。
    /// </summary>
    /// <param name="keywordFilter">検索欄から捕捉した式。</param>
    /// <returns>大小文字を保持した検索式。空白だけの入力は空文字列。</returns>
    /// <remarks>正規表現の \d と \D などを区別するため、通常語の大小文字無視は検索側に任せます。</remarks>
    internal static string NormalizeKeywordFilter(string keywordFilter)
    {
        if (string.IsNullOrWhiteSpace(keywordFilter))
        {
            return string.Empty;
        }

        return keywordFilter.Trim();
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
    /// 詳細要求の合流とsource再利用に使う、選択・条件・必要な確定位置を正規化します。
    /// </summary>
    /// <param name="table">選択した表。</param>
    /// <param name="selectionScope">元集合の選択範囲。</param>
    /// <param name="folderName">選択した実folder名。rootはnull。</param>
    /// <param name="filterType">詳細filter種別。</param>
    /// <param name="keywordFilter">検索条件。</param>
    /// <param name="modeFilter">譜面mode条件。</param>
    /// <param name="sortParameters">表示順条件。</param>
    /// <param name="libraryIndexVersion">必要なカタログ確定版。</param>
    /// <param name="playlistRevision">必要な表entry確定版。</param>
    /// <param name="scoreSnapshotVersion">要求では必要なscore確定位置、採用済み入力では元集合全体の反映済み位置。</param>
    /// <param name="chartInfoIndexVersion">必要な詳細解析版。</param>
    /// <param name="hasResolvedSelection">解決済み選択の有無。</param>
    /// <returns>正規化した詳細要求の識別。</returns>
    internal static PlaylistRequestIdentity CreateIdentity(
        BMSTable table,
        PlaylistDetailSelectionScope selectionScope,
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
            selectionScope,
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
