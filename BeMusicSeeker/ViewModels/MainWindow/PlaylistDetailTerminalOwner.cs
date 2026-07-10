using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistDetailTerminalOwner
{
    private readonly PlaylistDetailBuildState buildState;
    private readonly PlaylistDetailViewState viewState;
    private readonly MainChartListViewModel mainChartList;
    private readonly PlaylistWorkspaceViewModel playlistWorkspace;
    private readonly Action<MainViewUpdateMode?> commitExternalColumnMode;

    internal PlaylistDetailTerminalOwner(
        PlaylistDetailBuildState buildState,
        PlaylistDetailViewState viewState,
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        Action<MainViewUpdateMode?> commitExternalColumnMode)
    {
        this.buildState = buildState ?? throw new ArgumentNullException(nameof(buildState));
        this.viewState = viewState ?? throw new ArgumentNullException(nameof(viewState));
        this.mainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        this.playlistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        this.commitExternalColumnMode = commitExternalColumnMode ?? throw new ArgumentNullException(nameof(commitExternalColumnMode));
    }

    internal PlaylistDetailTerminalCommitResult TryApply(PlaylistDetailTerminalRequest request)
    {
        if (request?.BuildRequest == null || request.ViewRows == null || request.MainRowsRequest == null)
        {
            throw new ArgumentException("A complete playlist detail terminal request is required.", nameof(request));
        }
        if (request.ReplaceSource && request.SourceRows == null)
        {
            throw new ArgumentException("Source rows are required when replacing the playlist source.", nameof(request));
        }

        var result = new PlaylistDetailTerminalCommitResult();
        try
        {
            MainChartListCoordinatedRowsApplyResult coordinated = mainChartList.ApplyCoordinatedRows(
                request.MainRowsRequest,
                commitRows =>
                {
                    lock (buildState.SyncRoot)
                    {
                        if (request.BuildRequest.RequestVersion != buildState.RequestVersion)
                        {
                            return false;
                        }
                        viewState.CommitTerminal(
                            request,
                            result,
                            commitRows,
                            () =>
                            {
                                result.ColumnPresentationCommit = playlistWorkspace.CommitColumnPresentationWithoutNotification(
                                    request.ColumnSelection.PlaylistColumnSettingsVisibility,
                                    request.ColumnSelection.PlaylistSummaryColumnsSettings);
                                commitExternalColumnMode(request.ColumnSelection.AppliedMode);
                            });
                        return true;
                    }
                },
                () =>
                {
                    if (result.ColumnPresentationCommit != null)
                    {
                        playlistWorkspace.PublishColumnPresentation(result.ColumnPresentationCommit);
                    }
                });
            result.MainRowsApply = coordinated.RowsApply;
            return result;
        }
        catch (MainChartListCoordinatedPublishException ex)
        {
            throw new PlaylistDetailTerminalPublishException(ex, ownershipTransferred: true);
        }
    }

}

internal sealed class PlaylistSourceClearCommitResult
{
    internal PlaylistSourceClearCommitResult(
        List<PlaylistDetailSourceRow> sourceRows,
        IList viewRows,
        long previousGenerationId,
        CancellationTokenSource buildCancellation)
    {
        SourceRows = sourceRows;
        ViewRows = viewRows;
        PreviousGenerationId = previousGenerationId;
        BuildCancellation = buildCancellation;
    }

    internal List<PlaylistDetailSourceRow> SourceRows { get; }
    internal IList ViewRows { get; }
    internal long PreviousGenerationId { get; }
    internal CancellationTokenSource BuildCancellation { get; }
}

internal sealed class PlaylistDetailTerminalRequest
{
    internal PlaylistBuildRequest BuildRequest { get; set; }
    internal bool ReplaceSource { get; set; }
    internal List<PlaylistDetailSourceRow> SourceRows { get; set; }
    internal BMSTable CurrentTable { get; set; }
    internal string CurrentFolderName { get; set; }
    internal PlaylistDetailFilter CurrentFilterType { get; set; }
    internal IList ViewRows { get; set; }
    internal MainChartListColumnSelection ColumnSelection { get; set; }
    internal MainChartListRowsApplyRequest MainRowsRequest { get; set; }
}

internal sealed class PlaylistDetailTerminalCommitResult
{
    internal bool Applied { get; set; }
    internal List<PlaylistDetailSourceRow> PreviousSourceRows { get; set; }
    internal long PreviousSourceGenerationId { get; set; }
    internal long SourceGenerationId { get; set; }
    internal IList PreviousViewRows { get; set; }
    internal long PreviousViewGenerationId { get; set; }
    internal long ViewGenerationId { get; set; }
    internal int SourceRowsAlive { get; set; }
    internal MainChartListRowsApplyResult MainRowsApply { get; set; }
    internal PlaylistColumnPresentationCommit ColumnPresentationCommit { get; set; }
}
