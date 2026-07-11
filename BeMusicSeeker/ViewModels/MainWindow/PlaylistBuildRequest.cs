namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Represents one playlist detail build request processed by the worker.
/// </summary>
internal sealed class PlaylistBuildRequest
{
    internal int RequestVersion;

    internal MainViewUpdateMode Mode;

    internal MainViewUpdateMode RequestedMode;

    internal object Parameter;

    internal ChartListFilterSnapshot Filters;

    internal PlaylistRequestIdentity Identity;

    internal bool UseCoalescingWindow;

    internal int LastBuiltScoreSnapshotVersion;

    internal string SourceInvalidationReason;
}
