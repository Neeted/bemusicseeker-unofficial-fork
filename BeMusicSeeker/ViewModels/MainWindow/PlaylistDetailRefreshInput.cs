using System.ComponentModel;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Immutable shell snapshot for one playlist-detail refresh request.
/// </summary>
internal sealed class PlaylistDetailRefreshInput
{
    internal PlaylistDetailRefreshInput(
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        BMSTable table,
        string folderName,
        PlaylistDetailFilter filterType,
        bool hasResolvedSelection,
        string keywordFilter,
        ChartModeFilter modeFilter,
        string sortColumnName,
        ListSortDirection sortDirection,
        MainViewUpdateMode currentTreeMode,
        bool useCoalescingWindow,
        PlaylistOpenReadinessSnapshot openReadiness)
    {
        Mode = mode;
        RequestedMode = requestedMode;
        Table = table;
        FolderName = folderName;
        FilterType = filterType;
        HasResolvedSelection = hasResolvedSelection;
        KeywordFilter = keywordFilter ?? string.Empty;
        ModeFilter = modeFilter;
        SortColumnName = sortColumnName ?? string.Empty;
        SortDirection = sortDirection;
        CurrentTreeMode = currentTreeMode;
        UseCoalescingWindow = useCoalescingWindow;
        OpenReadiness = openReadiness;
    }

    internal MainViewUpdateMode Mode { get; }

    internal MainViewUpdateMode RequestedMode { get; }

    internal BMSTable Table { get; }

    internal string FolderName { get; }

    internal PlaylistDetailFilter FilterType { get; }

    internal bool HasResolvedSelection { get; }

    internal string KeywordFilter { get; }

    internal ChartModeFilter ModeFilter { get; }

    internal string SortColumnName { get; }

    internal ListSortDirection SortDirection { get; }

    internal MainViewUpdateMode CurrentTreeMode { get; }

    internal bool UseCoalescingWindow { get; }

    internal PlaylistOpenReadinessSnapshot OpenReadiness { get; }
}
