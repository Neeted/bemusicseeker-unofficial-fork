using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal interface IPlaylistDetailBuildWorkflowHost
{
    bool TryPatchPlaylistSourceChartInfoIndex(PlaylistBuildRequest request, CancellationToken cancellationToken, out int sourceCount, out int dependencyCount, out int patchedCount, out long elapsedMs);

    bool ApplyPlaylistViewWithoutSourceRebuild(PlaylistBuildRequest request, CancellationToken cancellationToken);

    bool RebuildPlaylistSource(PlaylistBuildRequest request, CancellationToken cancellationToken);

    void WaitPlaylistDetailBuildGate(CancellationToken cancellationToken);

    void ReleasePlaylistDetailBuildGate();

    bool TryResolvePlaylistSelection(
        MainViewUpdateMode mode,
        object parameter,
        out BMSTable bmsTable,
        out string folderName,
        out MainWindowViewModel.PlaylistFilterType filterType);

    void LogPlaylistSourceBuild(string message);

    PlaylistSourceBuildStageResult BuildPlaylistSourceForRequest(
        PlaylistBuildRequest request,
        BMSTable bmsTable,
        string folderName,
        bool onlyNotOwned,
        Stopwatch viewBuildStopwatch,
        CancellationToken cancellationToken,
        ref string cancellationStage);

    PlaylistViewApplyResult ApplyPlaylistViewFromRebuiltSource(
        MainViewUpdateMode mode,
        List<PlaylistDetailSourceRow> sourceRows,
        int sourceCount,
        ref IList finalRows);

    PlaylistViewApplyResult ApplyPlaylistViewFromCurrentSource(MainViewUpdateMode mode);

    bool IsLatestPlaylistSourceBuildRequest(int requestVersion);

    PlaylistDetailTerminalApplyResult TryCommitPlaylistDetailTerminal(
        PlaylistBuildRequest request,
        bool replaceSource,
        List<PlaylistDetailSourceRow> sourceRows,
        BMSTable currentTable,
        string currentFolderName,
        MainWindowViewModel.PlaylistFilterType currentFilterType,
        IList finalRows,
        int viewCount,
        MainViewUpdateMode columnSettingMode,
        Stopwatch viewBuildStopwatch);

    MainViewUpdateMode GetCurrentTreeViewFilterTypeSelected();

    MainViewUpdateMode ResolvePlaylistColumnSettingMode(MainWindowViewModel.PlaylistFilterType filterType);

    void DisposePlaylistViewRows(IEnumerable viewRows);

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
            host.DisposePlaylistViewRows(finalRows);
            return true;
        }

        PlaylistDetailTerminalApplyResult terminalApplyResult;
        try
        {
            terminalApplyResult = host.TryCommitPlaylistDetailTerminal(
                request,
                replaceSource: false,
                sourceRows: null,
                currentTable: null,
                currentFolderName: null,
                request.Identity.FilterType,
                finalRows,
                viewCount,
                host.ResolvePlaylistColumnSettingMode(request.Identity.FilterType),
                viewBuildStopwatch);
        }
        catch (PlaylistDetailTerminalPublishException)
        {
            throw;
        }
        catch
        {
            host.DisposePlaylistViewRows(finalRows);
            throw;
        }
        if (!terminalApplyResult.Applied)
        {
            host.DisposePlaylistViewRows(finalRows);
            return true;
        }
        MainViewUpdateMode currentTreeViewFilterTypeSelected = host.GetCurrentTreeViewFilterTypeSelected();
        FinalizeViewOnlyPlaylistDetailBuild(host, viewBuildStopwatch, currentTreeViewFilterTypeSelected, requestedMode, parameter, viewApplyResult, terminalApplyResult.MainViewApply);
        return true;
    }

    internal static bool RebuildPlaylistSource(
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

        int requestVersion = request.RequestVersion;
        MainViewUpdateMode mode = request.Mode;
        MainViewUpdateMode requestedMode = request.RequestedMode;
        object parameter = request.Parameter;
        var viewBuildStopwatch = Stopwatch.StartNew();
        int sourceCount = 0;
        List<PlaylistDetailSourceRow> sourceRows = null;
        IList finalRows = null;
        bool gateEntered = false;
        string cancellationStage = "before_start";
        try
        {
            host.WaitPlaylistDetailBuildGate(cancellationToken);
            gateEntered = true;
            if (!host.IsLatestPlaylistSourceBuildRequest(requestVersion))
            {
                host.LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=stale_before_start mode=" + mode + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "stale_before_start"));
                return true;
            }
            if (!host.TryResolvePlaylistSelection(mode, parameter, out BMSTable bmsTable, out string folderName, out MainWindowViewModel.PlaylistFilterType filterType))
            {
                return false;
            }
            bool onlyNotOwned = filterType == MainWindowViewModel.PlaylistFilterType.PlaylistNotOwnedFilterSelected;
            host.LogPlaylistSourceBuild("started version=" + requestVersion + " mode=" + mode + " parameterType=" + (parameter?.GetType().Name ?? "(null)") + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown"));
            PlaylistSourceBuildStageResult sourceBuildStageResult = host.BuildPlaylistSourceForRequest(request, bmsTable, folderName, onlyNotOwned, viewBuildStopwatch, cancellationToken, ref cancellationStage);
            sourceRows = sourceBuildStageResult.SourceBuild.SourceRows;
            sourceCount = sourceBuildStageResult.SourceBuild.SourceCount;
            if (cancellationToken.IsCancellationRequested || !host.IsLatestPlaylistSourceBuildRequest(requestVersion))
            {
                host.LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=after_build mode=" + mode + " sourceCount=" + sourceCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown"));
                return true;
            }
            cancellationStage = "view_apply";
            PlaylistViewApplyResult viewApplyResult = host.ApplyPlaylistViewFromRebuiltSource(mode, sourceRows, sourceCount, ref finalRows);
            var executionResult = new PlaylistRebuildExecutionResult(sourceBuildStageResult, viewApplyResult);
            int viewCount = executionResult.ViewCount;
            if (cancellationToken.IsCancellationRequested || !host.IsLatestPlaylistSourceBuildRequest(requestVersion))
            {
                host.LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=after_apply mode=" + mode + " sourceCount=" + sourceCount + " viewCount=" + viewCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown"));
                return true;
            }
            cancellationStage = "ui_apply";
            PlaylistDetailTerminalApplyResult terminalApplyResult;
            try
            {
                terminalApplyResult = host.TryCommitPlaylistDetailTerminal(
                    request,
                    replaceSource: true,
                    sourceRows,
                    bmsTable,
                    folderName,
                    filterType,
                    finalRows,
                    viewCount,
                    host.ResolvePlaylistColumnSettingMode(filterType),
                    viewBuildStopwatch);
            }
            catch (PlaylistDetailTerminalPublishException)
            {
                sourceRows = null;
                finalRows = null;
                throw;
            }
            if (!terminalApplyResult.Applied)
            {
                host.LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=stale_terminal_commit mode=" + mode + " sourceCount=" + sourceCount + " viewCount=" + viewCount);
                return true;
            }
            sourceRows = null;
            finalRows = null;
            int disposedSourceRowsCount = terminalApplyResult.PreviousSourceCount;
            FinalizeRebuiltPlaylistDetailBuild(host, viewBuildStopwatch, mode, requestedMode, parameter, executionResult, terminalApplyResult.MainViewApply);
            host.LogPlaylistSourceBuild("completed version=" + requestVersion + " mode=" + mode + " sourceCount=" + sourceCount + " viewCount=" + viewCount + " disposedSourceRows=" + disposedSourceRowsCount + " scoreTargets=" + executionResult.ScoreUpdateTargetCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown") + " libraryIndexMs=" + executionResult.LibraryIndexMs + " libraryIndexAccess=" + executionResult.LibraryIndexAccess + " libraryIndexBuildMs=" + executionResult.LibraryIndexBuildMs + " entryResolveMs=" + executionResult.EntryResolveMs + " scoreProbeMs=" + executionResult.ScoreProbeMs + " scoreProbeMatchedScoreCount=" + executionResult.ScoreProbeMetrics.MatchedScoreCount + " sourceMaterializeMs=" + executionResult.SourceMaterializeMs + " viewMaterializeMs=" + executionResult.ViewMaterializeMs + " totalMs=" + viewBuildStopwatch.ElapsedMilliseconds);
            return true;
        }
        catch (OperationCanceledException)
        {
            host.LogPlaylistSourceBuild("cancelled version=" + requestVersion + " stage=" + cancellationStage + " mode=" + mode + " sourceCount=" + sourceCount + " scoreSnapshotVersion=" + request.Identity.ScoreSnapshotVersion + " lastBuiltScoreSnapshotVersion=" + request.LastBuiltScoreSnapshotVersion + " sourceInvalidatedReason=" + (request.SourceInvalidationReason ?? "unknown"));
            return true;
        }
        finally
        {
            try
            {
                if (sourceRows != null)
                {
                    host.LogPlaylistSourceBuild("discarded version=" + requestVersion + " mode=" + mode + " discardedRows=" + sourceCount);
                }
                if (finalRows != null)
                {
                    host.DisposePlaylistViewRows(finalRows);
                }
            }
            finally
            {
                if (gateEntered)
                {
                    host.ReleasePlaylistDetailBuildGate();
                }
            }
        }
    }
}
