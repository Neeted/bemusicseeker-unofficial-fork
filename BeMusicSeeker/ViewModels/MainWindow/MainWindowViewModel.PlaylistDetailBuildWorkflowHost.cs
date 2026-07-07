using System.Threading;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel : IPlaylistDetailBuildWorkflowHost
{
    bool IPlaylistDetailBuildWorkflowHost.TryPatchPlaylistSourceChartInfoIndex(PlaylistBuildRequest request, CancellationToken cancellationToken, out int sourceCount, out int dependencyCount, out int patchedCount, out long elapsedMs)
    {
        return TryPatchPlaylistSourceChartInfoIndex(request, cancellationToken, out sourceCount, out dependencyCount, out patchedCount, out elapsedMs);
    }

    bool IPlaylistDetailBuildWorkflowHost.ApplyPlaylistViewWithoutSourceRebuild(PlaylistBuildRequest request, CancellationToken cancellationToken)
    {
        return ApplyPlaylistViewWithoutSourceRebuild(request, cancellationToken);
    }

    bool IPlaylistDetailBuildWorkflowHost.RebuildPlaylistSource(PlaylistBuildRequest request, CancellationToken cancellationToken)
    {
        return RebuildPlaylistSource(request, cancellationToken);
    }

    void IPlaylistDetailBuildWorkflowHost.LogPlaylistViewApply(string message)
    {
        LogPlaylistViewApply(message);
    }
}
