using System.Diagnostics;
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

    void IPlaylistDetailBuildWorkflowHost.FinalizePlaylistDetailBuild(Stopwatch viewBuildStopwatch, PlaylistDetailBuildCompletionResult completionResult)
    {
        FinalizePlaylistDetailBuild(viewBuildStopwatch, completionResult);
    }

    void IPlaylistDetailBuildWorkflowHost.LogPlaylistViewApply(string message)
    {
        LogPlaylistViewApply(message);
    }

    private void FinalizePlaylistDetailBuild(Stopwatch viewBuildStopwatch, PlaylistDetailBuildCompletionResult completionResult)
    {
        PlaylistViewApplyResult viewApplyResult = completionResult.ViewApply;
        PlaylistMainViewApplyResult mainViewApplyResult = completionResult.MainViewApply;
        FinalizeMainViewBuild(
            viewBuildStopwatch,
            completionResult.Mode,
            completionResult.RequestedMode,
            completionResult.Parameter,
            completionResult.FolderStageMs,
            viewApplyResult.KeywordStageMs,
            viewApplyResult.ModeStageMs,
            viewApplyResult.SortStageMs,
            completionResult.SortReuse,
            viewApplyResult.SortProfile,
            viewApplyResult.SourceCount,
            viewApplyResult.KeywordCount,
            viewApplyResult.ModeCount,
            viewApplyResult.ViewCount,
            mainViewApplyResult.ColumnStageMs,
            mainViewApplyResult.CallbackStageMs);
    }
}
