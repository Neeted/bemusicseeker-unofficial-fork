using System;
using System.Threading;

namespace BeMusicSeeker.ViewModels;

internal interface IPlaylistDetailBuildWorkflowHost
{
    bool TryPatchPlaylistSourceChartInfoIndex(PlaylistBuildRequest request, CancellationToken cancellationToken, out int sourceCount, out int dependencyCount, out int patchedCount, out long elapsedMs);

    bool ApplyPlaylistViewWithoutSourceRebuild(PlaylistBuildRequest request, CancellationToken cancellationToken);

    bool RebuildPlaylistSource(PlaylistBuildRequest request, CancellationToken cancellationToken);

    void LogPlaylistViewApply(string message);
}

internal static class PlaylistDetailBuildWorkflowCoordinator
{
    internal static bool TryBuildPlaylistViewAndApply(
        IPlaylistDetailBuildWorkflowHost host,
        PlaylistBuildRequest request,
        PlaylistDetailBuildStateSnapshot stateSnapshot,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return false;
        }
        if (host == null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        PlaylistDetailBuildDecision decision = PlaylistDetailBuildDecisionService.Decide(request, stateSnapshot);
        if (decision.Action == PlaylistDetailBuildAction.PatchChartInfoThenApplyView
            && host.TryPatchPlaylistSourceChartInfoIndex(request, cancellationToken, out int patchedSourceCount, out int chartInfoDependencyCount, out int chartInfoPatchedCount, out long chartInfoPatchMs))
        {
            host.LogPlaylistViewApply("chart_info_patch requestVersion=" + request.RequestVersion + " sourceCount=" + patchedSourceCount + " dependencyCount=" + chartInfoDependencyCount + " patchedCount=" + chartInfoPatchedCount + " chartInfoIndexVersion=" + request.Identity.ChartInfoIndexVersion + " elapsedMs=" + chartInfoPatchMs);
            return host.ApplyPlaylistViewWithoutSourceRebuild(request, cancellationToken);
        }

        if (decision.Action == PlaylistDetailBuildAction.RebuildSource)
        {
            request.LastBuiltScoreSnapshotVersion = decision.LastBuiltScoreSnapshotVersion;
            request.SourceInvalidationReason = decision.SourceInvalidationReason;
            return host.RebuildPlaylistSource(request, cancellationToken);
        }

        host.LogPlaylistViewApply("source_reuse requestVersion=" + request.RequestVersion + " presentationChanged=" + decision.PresentationIdentityChanged + " sourceIdentityChanged=false keyword=\"" + (request.Identity.KeywordFilter ?? string.Empty).Replace("\"", "\"\"") + "\" modeFilter=" + request.Identity.ModeFilter + " sortColumn=" + (request.Identity.SortColumnName ?? string.Empty) + " sortDirection=" + request.Identity.SortDirection);
        return host.ApplyPlaylistViewWithoutSourceRebuild(request, cancellationToken);
    }
}
