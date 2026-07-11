using System;

namespace BeMusicSeeker.ViewModels;

internal static class PlaylistDetailTerminalCoordinator
{
    internal static PlaylistDetailTerminalCommitResult Apply(
        PlaylistDetailTerminalRequest request,
        MainChartListViewModel mainChartList,
        PlaylistDetailBuildState buildState,
        PlaylistDetailViewState viewState,
        PlaylistWorkspaceViewModel playlistWorkspace,
        RegularChartListOwner regularChartListOwner)
    {
        if (request?.BuildRequest == null || request.ViewRows == null || request.MainRowsRequest == null)
        {
            throw new ArgumentException("A complete playlist detail terminal request is required.", nameof(request));
        }
        if (request.ReplaceSource && request.SourceRows == null)
        {
            throw new ArgumentException("Source rows are required when replacing the playlist source.", nameof(request));
        }
        if (mainChartList == null)
        {
            throw new ArgumentNullException(nameof(mainChartList));
        }
        if (buildState == null)
        {
            throw new ArgumentNullException(nameof(buildState));
        }
        if (viewState == null)
        {
            throw new ArgumentNullException(nameof(viewState));
        }
        if (playlistWorkspace == null)
        {
            throw new ArgumentNullException(nameof(playlistWorkspace));
        }
        if (regularChartListOwner == null)
        {
            throw new ArgumentNullException(nameof(regularChartListOwner));
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
                                result.AppliedColumnMode = request.ColumnSelection.AppliedMode;
                                regularChartListOwner.CommitExternalColumnMode(request.ColumnSelection.AppliedMode);
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
            throw new PlaylistDetailTerminalPublishException(ex, ownershipTransferred: true, result);
        }
    }
}
