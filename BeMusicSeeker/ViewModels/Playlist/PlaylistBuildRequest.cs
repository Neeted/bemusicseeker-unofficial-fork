namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Represents one playlist detail build request processed by the worker.
/// </summary>
internal sealed class PlaylistBuildRequest
{
    internal int RequestVersion;

    internal long MainViewBuildRequestId;

    internal MainViewUpdateMode Mode;

    internal MainViewUpdateMode RequestedMode;

    internal ChartListFilterSnapshot Filters;

    internal ChartListSortParameters SortParameters;

    internal MainViewUpdateMode CurrentTreeMode;

    internal PlaylistOpenReadinessSnapshot OpenReadiness;

    internal PlaylistRequestIdentity Identity;

    internal bool UseCoalescingWindow;

    internal int LastBuiltScoreSnapshotVersion;

    internal string SourceInvalidationReason;
}
