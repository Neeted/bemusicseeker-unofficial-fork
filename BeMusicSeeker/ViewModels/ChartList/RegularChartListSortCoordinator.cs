using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Carries the selected rows and updated folder sort snapshot after the regular sort stage.
/// </summary>
internal readonly struct RegularChartListSortResult
{
    private RegularChartListSortResult(
        IList rowsView,
        bool sortReuse,
        string sortProfile,
        List<LibraryChartRow> folderSortSourceSnapshot,
        List<LibraryChartRow> folderSortResultSnapshot,
        string folderSortColumnName,
        ListSortDirection? folderSortDirection,
        LibraryChartSortMetrics sortMetrics = default)
    {
        RowsView = rowsView;
        SortReuse = sortReuse;
        SortProfile = sortProfile ?? string.Empty;
        FolderSortSourceSnapshot = folderSortSourceSnapshot;
        FolderSortResultSnapshot = folderSortResultSnapshot;
        FolderSortColumnName = folderSortColumnName;
        FolderSortDirection = folderSortDirection;
        SortMetrics = sortMetrics;
    }

    internal IList RowsView { get; }

    internal bool SortReuse { get; }

    internal string SortProfile { get; }

    internal List<LibraryChartRow> FolderSortSourceSnapshot { get; }

    internal List<LibraryChartRow> FolderSortResultSnapshot { get; }

    internal string FolderSortColumnName { get; }

    internal ListSortDirection? FolderSortDirection { get; }

    internal LibraryChartSortMetrics SortMetrics { get; }

    internal static RegularChartListSortResult Bypass(
        IList rowsView,
        List<LibraryChartRow> folderSortSourceSnapshot,
        List<LibraryChartRow> folderSortResultSnapshot,
        string folderSortColumnName,
        ListSortDirection? folderSortDirection)
    {
        return new RegularChartListSortResult(rowsView, sortReuse: false, "bypass", folderSortSourceSnapshot, folderSortResultSnapshot, folderSortColumnName, folderSortDirection);
    }

    internal static RegularChartListSortResult ReuseFolderSnapshot(
        IList rowsView,
        List<LibraryChartRow> folderSortSourceSnapshot,
        List<LibraryChartRow> folderSortResultSnapshot,
        string folderSortColumnName,
        ListSortDirection? folderSortDirection)
    {
        return new RegularChartListSortResult(rowsView, sortReuse: true, "reuse", folderSortSourceSnapshot, folderSortResultSnapshot, folderSortColumnName, folderSortDirection);
    }

    internal static RegularChartListSortResult ReuseSortCache(
        IList rowsView,
        List<LibraryChartRow> folderSortSourceSnapshot,
        List<LibraryChartRow> folderSortResultSnapshot,
        string folderSortColumnName,
        ListSortDirection? folderSortDirection,
        LibraryChartSortMetrics sortMetrics = default)
    {
        return new RegularChartListSortResult(rowsView, sortReuse: true, "reuse", folderSortSourceSnapshot, folderSortResultSnapshot, folderSortColumnName, folderSortDirection, sortMetrics);
    }

    internal static RegularChartListSortResult Sorted(
        IList rowsView,
        string sortProfile,
        List<LibraryChartRow> folderSortSourceSnapshot,
        List<LibraryChartRow> folderSortResultSnapshot,
        string folderSortColumnName,
        ListSortDirection? folderSortDirection,
        LibraryChartSortMetrics sortMetrics = default)
    {
        return new RegularChartListSortResult(rowsView, sortReuse: false, sortProfile, folderSortSourceSnapshot, folderSortResultSnapshot, folderSortColumnName, folderSortDirection, sortMetrics);
    }
}
