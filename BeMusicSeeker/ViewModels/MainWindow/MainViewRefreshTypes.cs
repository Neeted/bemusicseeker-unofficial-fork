namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Classifies the main-view data source that changed and may require a refresh.
/// </summary>
internal enum MainViewDataDependency
{
    Unknown,
    SourceMembership,
    IdentitySortKey,
    InstallDestination,
    ChartInfo,
    Score,
    Maintenance,
    Warning,
    ReferenceTables
}

/// <summary>
/// Describes how the main view should react to a data dependency change.
/// </summary>
internal enum MainViewRefreshAction
{
    Refresh,
    RefreshDisplay,
    SkipMainViewRefresh
}

/// <summary>
/// Captures the refresh action chosen for a main-view data change and the reason logged for diagnostics.
/// </summary>
internal readonly struct MainViewRefreshDecision
{
    internal MainViewRefreshDecision(
        MainViewRefreshAction action,
        MainViewDataDependency dependency,
        MainViewDataDependency sortDependency,
        string reason,
        string detail)
    {
        Action = action;
        Dependency = dependency;
        SortDependency = sortDependency;
        Reason = reason ?? string.Empty;
        Detail = detail ?? string.Empty;
    }

    internal MainViewRefreshAction Action { get; }

    internal MainViewDataDependency Dependency { get; }

    internal MainViewDataDependency SortDependency { get; }

    internal string Reason { get; }

    internal string Detail { get; }

    internal bool ShouldRefresh => Action == MainViewRefreshAction.Refresh;

    internal bool ShouldRefreshDisplay => Action == MainViewRefreshAction.RefreshDisplay;
}
