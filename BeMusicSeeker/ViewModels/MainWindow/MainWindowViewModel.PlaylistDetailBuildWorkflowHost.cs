using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public partial class MainWindowViewModel : IPlaylistDetailBuildWorkflowHost
{
    bool IPlaylistDetailBuildWorkflowHost.TryPatchPlaylistSourceChartInfoIndex(PlaylistBuildRequest request, CancellationToken cancellationToken, out int sourceCount, out int dependencyCount, out int patchedCount, out long elapsedMs)
    {
        return TryPatchPlaylistSourceChartInfoIndex(request, cancellationToken, out sourceCount, out dependencyCount, out patchedCount, out elapsedMs);
    }

    bool IPlaylistDetailBuildWorkflowHost.ApplyPlaylistViewWithoutSourceRebuild(PlaylistBuildRequest request, CancellationToken cancellationToken)
    {
        return PlaylistDetailBuildWorkflowCoordinator.ApplyPlaylistViewWithoutSourceRebuild(this, request, cancellationToken);
    }

    bool IPlaylistDetailBuildWorkflowHost.RebuildPlaylistSource(PlaylistBuildRequest request, CancellationToken cancellationToken)
    {
        return PlaylistDetailBuildWorkflowCoordinator.RebuildPlaylistSource(this, request, cancellationToken);
    }

    void IPlaylistDetailBuildWorkflowHost.WaitPlaylistDetailBuildGate(CancellationToken cancellationToken)
    {
        playlistDetailBuildState.BuildGate.Wait(cancellationToken);
    }

    void IPlaylistDetailBuildWorkflowHost.ReleasePlaylistDetailBuildGate()
    {
        playlistDetailBuildState.BuildGate.Release();
    }

    bool IPlaylistDetailBuildWorkflowHost.TryResolvePlaylistSelection(
        MainViewUpdateMode mode,
        object parameter,
        out BMSTable bmsTable,
        out string folderName,
        out PlaylistFilterType filterType)
    {
        return TryResolvePlaylistSelection(mode, parameter, out bmsTable, out folderName, out filterType);
    }

    void IPlaylistDetailBuildWorkflowHost.LogPlaylistSourceBuild(string message)
    {
        LogPlaylistSourceBuild(message);
    }

    PlaylistSourceBuildStageResult IPlaylistDetailBuildWorkflowHost.BuildPlaylistSourceForRequest(
        PlaylistBuildRequest request,
        BMSTable bmsTable,
        string folderName,
        bool onlyNotOwned,
        Stopwatch viewBuildStopwatch,
        CancellationToken cancellationToken,
        ref string cancellationStage)
    {
        return BuildPlaylistSourceForRequest(request, bmsTable, folderName, onlyNotOwned, viewBuildStopwatch, cancellationToken, ref cancellationStage);
    }

    PlaylistViewApplyResult IPlaylistDetailBuildWorkflowHost.ApplyPlaylistViewFromRebuiltSource(
        MainViewUpdateMode mode,
        List<PlaylistDetailSourceRow> sourceRows,
        int sourceCount,
        ref IList finalRows)
    {
        return ApplyPlaylistViewFromRebuiltSource(mode, sourceRows, sourceCount, ref finalRows);
    }

    PlaylistViewApplyResult IPlaylistDetailBuildWorkflowHost.ApplyPlaylistViewFromCurrentSource(MainViewUpdateMode mode)
    {
        return ApplyPlaylistViewFromCurrentSource(mode);
    }

    bool IPlaylistDetailBuildWorkflowHost.IsLatestPlaylistSourceBuildRequest(int requestVersion)
    {
        return IsLatestPlaylistSourceBuildRequest(requestVersion);
    }

    PlaylistMainViewApplyResult IPlaylistDetailBuildWorkflowHost.ApplyPlaylistDetailViewRowsToMainView(
        PlaylistBuildRequest request,
        IList finalRows,
        int viewCount,
        MainViewUpdateMode columnSettingMode,
        Stopwatch viewBuildStopwatch)
    {
        return ApplyPlaylistDetailViewRowsToMainView(request, finalRows, viewCount, columnSettingMode, viewBuildStopwatch);
    }

    MainViewUpdateMode IPlaylistDetailBuildWorkflowHost.GetCurrentTreeViewFilterTypeSelected()
    {
        return treeViewFilterTypeSelected;
    }

    MainViewUpdateMode IPlaylistDetailBuildWorkflowHost.ResolvePlaylistColumnSettingMode(PlaylistFilterType filterType)
    {
        return ResolvePlaylistColumnSettingMode(filterType);
    }

    List<PlaylistDetailSourceRow> IPlaylistDetailBuildWorkflowHost.ReplacePlaylistSourceRows(
        List<PlaylistDetailSourceRow> sourceRows,
        BMSTable currentTable,
        string currentFolderName,
        PlaylistFilterType currentFilterType,
        PlaylistRequestIdentity requestIdentity)
    {
        return ReplacePlaylistSourceRows(sourceRows, currentTable, currentFolderName, currentFilterType, requestIdentity);
    }

    int IPlaylistDetailBuildWorkflowHost.CountPlaylistSourceRows(IEnumerable<PlaylistDetailSourceRow> rows)
    {
        return CountPlaylistSourceRows(rows);
    }

    void IPlaylistDetailBuildWorkflowHost.DisposePlaylistViewRows(IEnumerable viewRows)
    {
        DisposePlaylistViewRows(viewRows);
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
