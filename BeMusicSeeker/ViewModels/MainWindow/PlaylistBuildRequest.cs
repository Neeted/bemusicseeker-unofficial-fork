namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Represents one playlist detail build request processed by the worker.
/// </summary>
internal sealed class PlaylistBuildRequest
{
    internal int RequestVersion;

    internal MainWindowViewModel.viewUpdateMode Mode;

    internal MainWindowViewModel.viewUpdateMode RequestedMode;

    internal object Parameter;

    internal MainWindowViewModel.PlaylistRequestIdentity Identity;

    internal bool UseCoalescingWindow;

    internal int LastBuiltScoreSnapshotVersion;

    internal string SourceInvalidationReason;
}
