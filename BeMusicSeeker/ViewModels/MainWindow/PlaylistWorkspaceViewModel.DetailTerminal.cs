using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal PlaylistDetailTerminalCommitResult ApplyDetailTerminal(
        PlaylistDetailTerminalRequest request,
        MainChartListViewModel mainChartList,
        PlaylistDetailBuildState buildState,
        PlaylistDetailViewState viewState,
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
        if (mainChartList == null) throw new ArgumentNullException(nameof(mainChartList));
        if (buildState == null) throw new ArgumentNullException(nameof(buildState));
        if (viewState == null) throw new ArgumentNullException(nameof(viewState));
        if (regularChartListOwner == null) throw new ArgumentNullException(nameof(regularChartListOwner));

        var result = new PlaylistDetailTerminalCommitResult();
        MainChartListRowsTransition transition = mainChartList.PrepareRowsTransition(request.MainRowsRequest);
        Exception commitException = null;
        ExceptionDispatchInfo preTransferException = null;
        bool cancelTransition = false;
        lock (buildState.SyncRoot)
        {
            if (request.BuildRequest.RequestVersion != buildState.RequestVersion)
            {
                cancelTransition = true;
            }
            else
            {
                try
                {
                    viewState.CommitTerminal(
                        request,
                        result,
                        transition.CommitOwnership,
                        () =>
                        {
                            result.ColumnPresentationCommit = CommitColumnPresentationWithoutNotification(
                                request.ColumnSelection.PlaylistColumnSettingsVisibility,
                                request.ColumnSelection.PlaylistSummaryColumnsSettings);
                            result.AppliedColumnMode = request.ColumnSelection.AppliedMode;
                            regularChartListOwner.CommitExternalColumnMode(request.ColumnSelection.AppliedMode);
                        });
                }
                catch (Exception ex)
                {
                    if (!transition.OwnershipTransferred)
                    {
                        cancelTransition = true;
                        preTransferException = ExceptionDispatchInfo.Capture(ex);
                    }
                    else
                    {
                        commitException = ex;
                    }
                }
            }
        }
        if (cancelTransition)
        {
            transition.Cancel();
            preTransferException?.Throw();
            return result;
        }

        var publishExceptions = new List<Exception>();
        if (commitException != null)
        {
            publishExceptions.Add(commitException);
        }
        TryTerminalPublish(() => result.MainRowsApply = transition.Complete(), publishExceptions);
        if (result.ColumnPresentationCommit != null)
        {
            TryTerminalPublish(() => PublishColumnPresentation(result.ColumnPresentationCommit), publishExceptions);
        }
        if (publishExceptions.Count > 0)
        {
            throw new PlaylistDetailTerminalPublishException(
                new AggregateException(publishExceptions),
                ownershipTransferred: true,
                result);
        }
        return result;
    }

    private static void TryTerminalPublish(Action action, ICollection<Exception> exceptions)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
    }
}
