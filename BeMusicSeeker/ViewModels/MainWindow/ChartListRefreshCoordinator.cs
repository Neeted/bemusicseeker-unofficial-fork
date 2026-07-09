using System;
using System.Diagnostics;
using System.Threading;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Classifies main chart-list refresh requests before the shell performs side effects.
/// </summary>
internal static class ChartListRefreshCoordinator
{
    /// <summary>
    /// Resolves the route used by the main chart-list refresh workflow after request normalization.
    /// </summary>
    /// <param name="mode">Resolved refresh mode.</param>
    /// <param name="requestedMode">Mode originally requested by the caller.</param>
    /// <param name="currentTreeMode">Current tree selection mode.</param>
    /// <param name="hasFiles">Whether the BMS library is available.</param>
    /// <returns>Refresh route and derived flags for the shell to execute.</returns>
    internal static ChartListRefreshRoute ResolveRoute(
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        MainViewUpdateMode currentTreeMode,
        bool hasFiles)
    {
        if (!hasFiles)
        {
            return new ChartListRefreshRoute(
                ChartListRefreshRouteKind.MissingFiles,
                mode,
                requestedMode,
                currentTreeMode,
                isPlaylistTreeActive: false,
                includeBmsonRows: false);
        }

        if (IsPlayHistoryMainViewMode(mode, currentTreeMode))
        {
            return new ChartListRefreshRoute(
                ChartListRefreshRouteKind.ApplyPlayHistoryView,
                mode,
                requestedMode,
                currentTreeMode,
                isPlaylistTreeActive: false,
                includeBmsonRows: false);
        }

        bool playlistTreeActive = IsPlaylistTreeActive(mode, currentTreeMode);
        if (playlistTreeActive)
        {
            return new ChartListRefreshRoute(
                ChartListRefreshRouteKind.RegisterPlaylistSourceBuild,
                mode,
                requestedMode,
                currentTreeMode,
                isPlaylistTreeActive: true,
                includeBmsonRows: false);
        }

        return new ChartListRefreshRoute(
            ChartListRefreshRouteKind.ContinueMainLibrary,
            mode,
            requestedMode,
            currentTreeMode,
            isPlaylistTreeActive: false,
            includeBmsonRows: ShouldIncludeBmsonLibraryRows(mode, currentTreeMode));
    }

    /// <summary>
    /// Creates completion metadata and the regular chart-list build log message for the shell to publish.
    /// </summary>
    /// <param name="request">Regular chart-list build log request.</param>
    /// <param name="viewBuildStopwatch">Stopwatch used by the owning refresh workflow.</param>
    /// <param name="nextBuildRequestId">Callback that allocates the next main-view build request id.</param>
    /// <returns>Completion metadata and formatted build log message.</returns>
    internal static RegularChartListBuildCompletion CreateRegularBuildCompletion(
        RegularChartListBuildLogRequest request,
        Stopwatch viewBuildStopwatch,
        Func<long> nextBuildRequestId)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (viewBuildStopwatch == null)
        {
            throw new ArgumentNullException(nameof(viewBuildStopwatch));
        }
        if (nextBuildRequestId == null)
        {
            throw new ArgumentNullException(nameof(nextBuildRequestId));
        }

        long requestId = nextBuildRequestId();
        long endTimestamp = Stopwatch.GetTimestamp();
        int threadId = Thread.CurrentThread.ManagedThreadId;
        long elapsedMs = viewBuildStopwatch.ElapsedMilliseconds;
        string message = "main_view_build mode=" + request.Mode
            + " requestedMode=" + request.RequestedMode
            + " parameterType=" + (request.ParameterType ?? "(null)")
            + " folderMs=" + request.FolderMs
            + " keywordMs=" + request.KeywordMs
            + " modeMs=" + request.ModeMs
            + " sortMs=" + request.SortMs
            + " sortReuse=" + request.SortReuse
            + " sortProfile=" + (request.SortProfile ?? string.Empty)
            + " sortEngine=fast fastSortEnabled=" + request.FastSortEnabled
            + " isPlaylistDetailView=" + request.IsPlaylistDetailView
            + " columnMs=" + request.ColumnMs
            + " prepareSwapMs=" + request.PrepareSwapMs
            + " columnSettingMs=" + request.ColumnSettingMs
            + " setViewMs=" + request.SetViewMs
            + " columnSettingReuse=" + request.ColumnSettingReuse
            + " callbackMs=" + request.CallbackMs
            + " totalMs=" + elapsedMs
            + " folderCount=" + request.FolderCount
            + " keywordCount=" + request.KeywordCount
            + " modeCount=" + request.ModeCount
            + " viewCount=" + request.ViewCount
            + " sortColumn=" + (request.SortColumn ?? string.Empty)
            + " sortDirection=" + (request.SortDirection ?? string.Empty);

        return new RegularChartListBuildCompletion(
            requestId,
            endTimestamp,
            threadId,
            elapsedMs,
            request.IsPlaylistDetailView,
            message);
    }

    /// <summary>
    /// Creates virtual chart-list sort metrics shared by normal-library and subset views.
    /// </summary>
    /// <param name="order">Order applied to the virtual view.</param>
    /// <param name="sortStageMs">Elapsed milliseconds spent in the sort stage.</param>
    /// <param name="sortCacheHit">Whether the order came from cache.</param>
    /// <param name="sortCacheGeneration">Sort-cache generation for diagnostics.</param>
    /// <param name="orderCacheLookupMs">Elapsed milliseconds spent looking up the order cache.</param>
    /// <param name="orderBuildMs">Elapsed milliseconds spent building the order.</param>
    /// <returns>Sort metrics for main-view logging.</returns>
    internal static LibraryChartSortMetrics CreateVirtualSortMetrics(
        ChartListOrder order,
        long sortStageMs,
        bool sortCacheHit,
        long sortCacheGeneration,
        long orderCacheLookupMs,
        long orderBuildMs)
    {
        if (order == null)
        {
            throw new ArgumentNullException(nameof(order));
        }

        return new LibraryChartSortMetrics(
            order.Count,
            order.ColumnName,
            order.Direction,
            order.PropertyTypeName,
            order.SortProfile,
            order.StringSortKind,
            sortStageMs,
            sortReuse: sortCacheHit,
            sortCacheKey: order.ColumnName,
            sortCacheGeneration: sortCacheGeneration,
            sortCacheHit: sortCacheHit,
            orderCacheLookupMs: orderCacheLookupMs,
            orderBuildMs: orderBuildMs);
    }

    /// <summary>
    /// Resolves the main chart-table column setting mode for a refresh request.
    /// </summary>
    /// <param name="mode">Current refresh mode.</param>
    /// <param name="currentTreeMode">Current tree selection mode.</param>
    /// <returns>Column setting mode that should be applied.</returns>
    internal static MainViewUpdateMode ResolveMainColumnSettingMode(
        MainViewUpdateMode mode,
        MainViewUpdateMode currentTreeMode)
    {
        return mode switch
        {
            MainViewUpdateMode.TreeViewFilterNotChanged
                or MainViewUpdateMode.KeywordFilterUpdated
                or MainViewUpdateMode.ModeFilterUpdated
                or MainViewUpdateMode.SortUpdated => currentTreeMode,
            _ => mode,
        };
    }

    /// <summary>
    /// Determines whether the refresh should use the play-history main view.
    /// </summary>
    /// <param name="mode">Current refresh mode.</param>
    /// <param name="currentTreeMode">Current tree selection mode.</param>
    /// <returns>true when the play-history view should handle the request.</returns>
    internal static bool IsPlayHistoryMainViewMode(
        MainViewUpdateMode mode,
        MainViewUpdateMode currentTreeMode)
    {
        return ResolveMainColumnSettingMode(mode, currentTreeMode) == MainViewUpdateMode.PlayHistorySelected;
    }

    /// <summary>
    /// Determines whether the refresh should use the playlist detail pipeline.
    /// </summary>
    /// <param name="mode">Current refresh mode.</param>
    /// <param name="currentTreeMode">Current tree selection mode.</param>
    /// <returns>true when the playlist detail pipeline should handle the request.</returns>
    internal static bool IsPlaylistTreeActive(
        MainViewUpdateMode mode,
        MainViewUpdateMode currentTreeMode)
    {
        if (IsPlaylistViewMode(mode))
        {
            return true;
        }
        if (!IsPlaylistViewMode(currentTreeMode))
        {
            return false;
        }
        return mode == MainViewUpdateMode.TreeViewFilterNotChanged
            || mode == MainViewUpdateMode.KeywordFilterUpdated
            || mode == MainViewUpdateMode.ModeFilterUpdated
            || mode == MainViewUpdateMode.SortUpdated;
    }

    /// <summary>
    /// Determines whether normal library rows should include bmson-backed charts for the request.
    /// </summary>
    /// <param name="mode">Current refresh mode.</param>
    /// <param name="currentTreeMode">Current tree selection mode.</param>
    /// <returns>true when bmson-backed normal library rows should be included.</returns>
    internal static bool ShouldIncludeBmsonLibraryRows(
        MainViewUpdateMode mode,
        MainViewUpdateMode currentTreeMode)
    {
        if (mode == MainViewUpdateMode.FullScanAllChartsFilterSelected
            || currentTreeMode == MainViewUpdateMode.FullScanAllChartsFilterSelected)
        {
            return true;
        }
        if (IsPlaylistTreeActive(mode, currentTreeMode))
        {
            return false;
        }
        if (Enum.IsDefined(typeof(MainWindowViewModel.MaintenanceFilterType), (int)mode))
        {
            return false;
        }
        if (Enum.IsDefined(typeof(MainWindowViewModel.InstallFilterType), (int)mode))
        {
            return false;
        }
        return true;
    }

    private static bool IsPlaylistViewMode(MainViewUpdateMode mode)
    {
        return mode == MainViewUpdateMode.PlaylistFilterSelected
            || mode == MainViewUpdateMode.PlaylistNotOwnedFilterSelected;
    }
}

/// <summary>
/// Describes the shell action selected for a normalized main chart-list refresh request.
/// </summary>
internal enum ChartListRefreshRouteKind
{
    /// <summary>
    /// The library is unavailable, so the refresh should stop.
    /// </summary>
    MissingFiles,

    /// <summary>
    /// The request should be handled by the play-history main view.
    /// </summary>
    ApplyPlayHistoryView,

    /// <summary>
    /// The request should be registered with the playlist detail build pipeline.
    /// </summary>
    RegisterPlaylistSourceBuild,

    /// <summary>
    /// The request should continue through the normal library chart-list pipeline.
    /// </summary>
    ContinueMainLibrary
}

/// <summary>
/// Carries the route and derived flags for a normalized main chart-list refresh request.
/// </summary>
internal readonly struct ChartListRefreshRoute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChartListRefreshRoute"/> struct.
    /// </summary>
    /// <param name="kind">Route kind selected for the request.</param>
    /// <param name="mode">Resolved refresh mode.</param>
    /// <param name="requestedMode">Original caller-requested mode.</param>
    /// <param name="currentTreeMode">Current tree selection mode.</param>
    /// <param name="isPlaylistTreeActive">Whether the playlist detail tree is active for the request.</param>
    /// <param name="includeBmsonRows">Whether normal library rows should include bmson-backed charts.</param>
    internal ChartListRefreshRoute(
        ChartListRefreshRouteKind kind,
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        MainViewUpdateMode currentTreeMode,
        bool isPlaylistTreeActive,
        bool includeBmsonRows)
    {
        Kind = kind;
        Mode = mode;
        RequestedMode = requestedMode;
        CurrentTreeMode = currentTreeMode;
        IsPlaylistTreeActive = isPlaylistTreeActive;
        IncludeBmsonRows = includeBmsonRows;
    }

    /// <summary>
    /// Gets the selected refresh route.
    /// </summary>
    internal ChartListRefreshRouteKind Kind { get; }

    /// <summary>
    /// Gets the resolved refresh mode.
    /// </summary>
    internal MainViewUpdateMode Mode { get; }

    /// <summary>
    /// Gets the original caller-requested mode.
    /// </summary>
    internal MainViewUpdateMode RequestedMode { get; }

    /// <summary>
    /// Gets the current tree selection mode.
    /// </summary>
    internal MainViewUpdateMode CurrentTreeMode { get; }

    /// <summary>
    /// Gets a value indicating whether the playlist detail tree is active for the request.
    /// </summary>
    internal bool IsPlaylistTreeActive { get; }

    /// <summary>
    /// Gets a value indicating whether normal library rows should include bmson-backed charts.
    /// </summary>
    internal bool IncludeBmsonRows { get; }
}

/// <summary>
/// Carries regular chart-list build fields used to format the terminal log message.
/// </summary>
internal sealed class RegularChartListBuildLogRequest
{
    internal MainViewUpdateMode Mode { get; set; }

    internal MainViewUpdateMode RequestedMode { get; set; }

    internal string ParameterType { get; set; }

    internal long FolderMs { get; set; }

    internal long KeywordMs { get; set; }

    internal long ModeMs { get; set; }

    internal long SortMs { get; set; }

    internal bool SortReuse { get; set; }

    internal string SortProfile { get; set; }

    internal bool FastSortEnabled { get; set; }

    internal bool IsPlaylistDetailView { get; set; }

    internal long ColumnMs { get; set; }

    internal long PrepareSwapMs { get; set; }

    internal long ColumnSettingMs { get; set; }

    internal long SetViewMs { get; set; }

    internal bool ColumnSettingReuse { get; set; }

    internal long CallbackMs { get; set; }

    internal int FolderCount { get; set; }

    internal int KeywordCount { get; set; }

    internal int ModeCount { get; set; }

    internal int ViewCount { get; set; }

    internal string SortColumn { get; set; }

    internal string SortDirection { get; set; }
}

/// <summary>
/// Carries regular chart-list build completion metadata for root-owned publication.
/// </summary>
internal readonly struct RegularChartListBuildCompletion
{
    internal RegularChartListBuildCompletion(
        long requestId,
        long endTimestamp,
        int threadId,
        long elapsedMs,
        bool isPlaylistDetailView,
        string logMessage)
    {
        RequestId = requestId;
        EndTimestamp = endTimestamp;
        ThreadId = threadId;
        ElapsedMs = elapsedMs;
        IsPlaylistDetailView = isPlaylistDetailView;
        LogMessage = logMessage ?? string.Empty;
    }

    internal long RequestId { get; }

    internal long EndTimestamp { get; }

    internal int ThreadId { get; }

    internal long ElapsedMs { get; }

    internal bool IsPlaylistDetailView { get; }

    internal string LogMessage { get; }
}
