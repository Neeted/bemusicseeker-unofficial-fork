using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;

namespace BeMusicSeeker.ViewModels;

internal interface IPlaylistDetailBuildWorkflowHost
{
    bool TryPatchPlaylistSourceChartInfoIndex(PlaylistBuildRequest request, CancellationToken cancellationToken, out int sourceCount, out int dependencyCount, out int patchedCount, out long elapsedMs);

    bool ApplyPlaylistViewWithoutSourceRebuild(PlaylistBuildRequest request, CancellationToken cancellationToken);

    bool RebuildPlaylistSource(PlaylistBuildRequest request, CancellationToken cancellationToken);

    PlaylistViewApplyResult ApplyPlaylistViewFromCurrentSource(MainViewUpdateMode mode);

    bool IsLatestPlaylistSourceBuildRequest(int requestVersion);

    PlaylistMainViewApplyResult ApplyPlaylistDetailViewRowsToMainView(
        PlaylistBuildRequest request,
        IList finalRows,
        int viewCount,
        MainViewUpdateMode columnSettingMode,
        Stopwatch viewBuildStopwatch);

    MainViewUpdateMode GetCurrentTreeViewFilterTypeSelected();

    MainViewUpdateMode ResolvePlaylistColumnSettingMode(MainWindowViewModel.PlaylistFilterType filterType);

    void FinalizePlaylistDetailBuild(Stopwatch viewBuildStopwatch, PlaylistDetailBuildCompletionResult completionResult);

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

    internal static void FinalizePlaylistDetailBuild(
        IPlaylistDetailBuildWorkflowHost host,
        Stopwatch viewBuildStopwatch,
        PlaylistDetailBuildCompletionResult completionResult)
    {
        if (host == null)
        {
            throw new ArgumentNullException(nameof(host));
        }

        host.FinalizePlaylistDetailBuild(viewBuildStopwatch, completionResult);
    }

    internal static void FinalizeRebuiltPlaylistDetailBuild(
        IPlaylistDetailBuildWorkflowHost host,
        Stopwatch viewBuildStopwatch,
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        object parameter,
        PlaylistRebuildExecutionResult executionResult,
        PlaylistMainViewApplyResult mainViewApplyResult)
    {
        if (executionResult == null)
        {
            throw new ArgumentNullException(nameof(executionResult));
        }

        var completionResult = new PlaylistDetailBuildCompletionResult(
            mode,
            requestedMode,
            parameter,
            executionResult.FolderStageMs,
            executionResult.ViewApply,
            mainViewApplyResult);
        FinalizePlaylistDetailBuild(host, viewBuildStopwatch, completionResult);
    }

    internal static void FinalizeViewOnlyPlaylistDetailBuild(
        IPlaylistDetailBuildWorkflowHost host,
        Stopwatch viewBuildStopwatch,
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        object parameter,
        PlaylistViewApplyResult viewApplyResult,
        PlaylistMainViewApplyResult mainViewApplyResult)
    {
        var completionResult = new PlaylistDetailBuildCompletionResult(
            mode,
            requestedMode,
            parameter,
            folderStageMs: 0L,
            viewApplyResult,
            mainViewApplyResult);
        FinalizePlaylistDetailBuild(host, viewBuildStopwatch, completionResult);
    }

    internal static bool ApplyPlaylistViewWithoutSourceRebuild(
        IPlaylistDetailBuildWorkflowHost host,
        PlaylistBuildRequest request,
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

        MainViewUpdateMode mode = request.Mode;
        MainViewUpdateMode requestedMode = request.RequestedMode;
        object parameter = request.Parameter;
        var viewBuildStopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        PlaylistViewApplyResult viewApplyResult = host.ApplyPlaylistViewFromCurrentSource(mode);
        IList finalRows = viewApplyResult.FinalRows;
        int viewCount = viewApplyResult.ViewCount;
        if (cancellationToken.IsCancellationRequested || !host.IsLatestPlaylistSourceBuildRequest(request.RequestVersion))
        {
            return true;
        }

        PlaylistMainViewApplyResult mainViewApplyResult = host.ApplyPlaylistDetailViewRowsToMainView(
            request,
            finalRows,
            viewCount,
            host.ResolvePlaylistColumnSettingMode(request.Identity.FilterType),
            viewBuildStopwatch);
        MainViewUpdateMode currentTreeViewFilterTypeSelected = host.GetCurrentTreeViewFilterTypeSelected();
        FinalizeViewOnlyPlaylistDetailBuild(host, viewBuildStopwatch, currentTreeViewFilterTypeSelected, requestedMode, parameter, viewApplyResult, mainViewApplyResult);
        return true;
    }
}
