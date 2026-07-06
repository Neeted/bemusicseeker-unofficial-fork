using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Coordinates the sort/cache stage for materialized regular chart-list rows.
/// </summary>
internal static class RegularChartListSortCoordinator
{
    /// <summary>
    /// Applies sort, folder sort reuse, and normal-library sort cache selection.
    /// </summary>
    /// <param name="request">Regular chart-list refresh request.</param>
    /// <param name="stage">Materialized regular chart-list stage state.</param>
    /// <param name="context">Sort context and root-owned cache callbacks.</param>
    /// <returns>Sort result and updated folder sort snapshot state.</returns>
    internal static RegularChartListSortResult ApplySort(
        RegularChartListRefreshRequest request,
        RegularChartListStageState stage,
        RegularChartListSortContext context)
    {
        if (stage == null)
        {
            throw new ArgumentNullException(nameof(stage));
        }
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        List<LibraryChartRow> modeRows = stage.ModeRows as List<LibraryChartRow> ?? [.. stage.ModeRows];
        if (request.Mode > MainViewUpdateMode.SortUpdated)
        {
            return RegularChartListSortResult.Bypass(
                new List<LibraryChartRow>(modeRows),
                context.FolderSortSourceSnapshot,
                context.FolderSortResultSnapshot,
                context.FolderSortColumnName,
                context.FolderSortDirection);
        }

        string columnName = request.SortColumnName;
        ListSortDirection direction = request.SortDirection;
        bool isTreeSelectionRequest = request.RequestedMode != MainViewUpdateMode.TreeViewFilterNotChanged
            && request.RequestedMode < MainViewUpdateMode.KeywordFilterUpdated;
        bool isFolderMode = request.Mode == MainViewUpdateMode.FolderFilterSelected;
        bool isFullNormalLibraryResult = context.CurrentTreeMode == MainViewUpdateMode.FolderFilterSelected
            && !context.HasVirtualNormalLibraryTreeFilter
            && string.IsNullOrWhiteSpace(request.KeywordFilter)
            && request.ModeFilter == MainWindowViewModel.ModeFilterType.All
            && !context.IsPlaylistDetailView
            && modeRows.Count == stage.FolderCount
            && modeRows.Count == stage.KeywordCount
            && modeRows.Count == stage.ModeCount;

        if (isFolderMode
            && isTreeSelectionRequest
            && context.FolderSortSourceSnapshot != null
            && context.FolderSortResultSnapshot != null
            && string.Equals(context.FolderSortColumnName, columnName, StringComparison.Ordinal)
            && context.FolderSortDirection == direction
            && IsSameReferenceSequence(modeRows, context.FolderSortSourceSnapshot))
        {
            return RegularChartListSortResult.ReuseFolderSnapshot(
                context.FolderSortResultSnapshot,
                modeRows,
                context.FolderSortResultSnapshot,
                columnName,
                direction);
        }

        if (context.TryGetNormalLibrarySortCache != null
            && context.TryGetNormalLibrarySortCache(isFullNormalLibraryResult, columnName, direction, modeRows.Count, out List<LibraryChartRow> cachedRows, out NormalLibrarySortCacheKey sortCacheKey))
        {
            if (context.CreateSortCacheMetrics == null)
            {
                throw new InvalidOperationException("Sort cache metrics callback is required when normal-library sort cache is enabled.");
            }
            var sortCacheStopwatch = Stopwatch.StartNew();
            IList rowsView = cachedRows;
            sortCacheStopwatch.Stop();
            context.LogSortDetail?.Invoke(context.CreateSortCacheMetrics(sortCacheKey, sortCacheStopwatch.ElapsedMilliseconds, true));
            return RegularChartListSortResult.ReuseSortCache(
                rowsView,
                isFolderMode ? modeRows : context.FolderSortSourceSnapshot,
                context.FolderSortResultSnapshot,
                isFolderMode ? columnName : context.FolderSortColumnName,
                isFolderMode ? direction : context.FolderSortDirection);
        }

        List<LibraryChartRow> sortedRows = LibraryChartRowSortEngine.SortForMainView(
            modeRows,
            request.SortParameters,
            context.IsPlaylistDetailView,
            useLegacySortForDataGrid: false,
            out string sortProfile,
            out LibraryChartSortMetrics sortMetrics);
        IList nextRowsView = sortedRows;

        if (isFullNormalLibraryResult
            && context.TryNormalizeSortCacheColumn != null
            && context.TryNormalizeSortCacheColumn(columnName, out string normalizedCacheColumnName)
            && context.CreateSortCacheKey != null
            && context.StoreSortCache != null)
        {
            NormalLibrarySortCacheKey newCacheKey = context.CreateSortCacheKey(normalizedCacheColumnName, direction, sortedRows.Count);
            context.StoreSortCache(newCacheKey, sortedRows);
            sortMetrics = new LibraryChartSortMetrics(
                sortMetrics.RowCount,
                sortMetrics.ColumnName,
                sortMetrics.Direction,
                sortMetrics.PropertyTypeName,
                sortMetrics.SortProfile,
                sortMetrics.StringSortKind,
                sortMetrics.SortMs,
                sortReuse: false,
                sortCacheKey: normalizedCacheColumnName,
                sortCacheGeneration: context.GetSortCacheGenerationForLog?.Invoke(newCacheKey) ?? 0L,
                sortCacheHit: false);
        }
        context.LogSortDetail?.Invoke(sortMetrics);

        return RegularChartListSortResult.Sorted(
            nextRowsView,
            sortProfile,
            isFolderMode ? modeRows : context.FolderSortSourceSnapshot,
            isFolderMode ? sortedRows : context.FolderSortResultSnapshot,
            isFolderMode ? columnName : context.FolderSortColumnName,
            isFolderMode ? direction : context.FolderSortDirection);
    }

    private static bool IsSameReferenceSequence<T>(IReadOnlyList<T> left, IReadOnlyList<T> right) where T : class
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left == null || right == null || left.Count != right.Count)
        {
            return false;
        }
        for (int i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i], right[i]))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// Root-owned callbacks and state needed by the regular chart-list sort stage.
/// </summary>
internal sealed class RegularChartListSortContext
{
    internal MainViewUpdateMode CurrentTreeMode { get; set; }

    internal bool HasVirtualNormalLibraryTreeFilter { get; set; }

    internal bool IsPlaylistDetailView { get; set; }

    internal List<LibraryChartRow> FolderSortSourceSnapshot { get; set; }

    internal List<LibraryChartRow> FolderSortResultSnapshot { get; set; }

    internal string FolderSortColumnName { get; set; }

    internal ListSortDirection? FolderSortDirection { get; set; }

    internal TryGetNormalLibrarySortCacheCallback TryGetNormalLibrarySortCache { get; set; }

    internal TryNormalizeSortCacheColumnCallback TryNormalizeSortCacheColumn { get; set; }

    internal CreateSortCacheKeyCallback CreateSortCacheKey { get; set; }

    internal Action<NormalLibrarySortCacheKey, List<LibraryChartRow>> StoreSortCache { get; set; }

    internal Func<NormalLibrarySortCacheKey, long, bool, LibraryChartSortMetrics> CreateSortCacheMetrics { get; set; }

    internal Func<NormalLibrarySortCacheKey, long> GetSortCacheGenerationForLog { get; set; }

    internal Action<LibraryChartSortMetrics> LogSortDetail { get; set; }
}

internal delegate bool TryGetNormalLibrarySortCacheCallback(
    bool isEligible,
    string columnName,
    ListSortDirection direction,
    int rowCount,
    out List<LibraryChartRow> rows,
    out NormalLibrarySortCacheKey cacheKey);

internal delegate bool TryNormalizeSortCacheColumnCallback(string columnName, out string normalizedColumnName);

internal delegate NormalLibrarySortCacheKey CreateSortCacheKeyCallback(string normalizedColumnName, ListSortDirection direction, int rowCount);

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
        ListSortDirection? folderSortDirection)
    {
        RowsView = rowsView;
        SortReuse = sortReuse;
        SortProfile = sortProfile ?? string.Empty;
        FolderSortSourceSnapshot = folderSortSourceSnapshot;
        FolderSortResultSnapshot = folderSortResultSnapshot;
        FolderSortColumnName = folderSortColumnName;
        FolderSortDirection = folderSortDirection;
    }

    internal IList RowsView { get; }

    internal bool SortReuse { get; }

    internal string SortProfile { get; }

    internal List<LibraryChartRow> FolderSortSourceSnapshot { get; }

    internal List<LibraryChartRow> FolderSortResultSnapshot { get; }

    internal string FolderSortColumnName { get; }

    internal ListSortDirection? FolderSortDirection { get; }

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
        ListSortDirection? folderSortDirection)
    {
        return new RegularChartListSortResult(rowsView, sortReuse: true, "reuse", folderSortSourceSnapshot, folderSortResultSnapshot, folderSortColumnName, folderSortDirection);
    }

    internal static RegularChartListSortResult Sorted(
        IList rowsView,
        string sortProfile,
        List<LibraryChartRow> folderSortSourceSnapshot,
        List<LibraryChartRow> folderSortResultSnapshot,
        string folderSortColumnName,
        ListSortDirection? folderSortDirection)
    {
        return new RegularChartListSortResult(rowsView, sortReuse: false, sortProfile, folderSortSourceSnapshot, folderSortResultSnapshot, folderSortColumnName, folderSortDirection);
    }
}
