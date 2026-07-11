using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal void ClearDetailSourceForRegularView()
    {
        PlaylistSourceClearCommitResult commit = DetailBuildState.CommitSourceClear(DetailViewState);
        DetailBuildState.PublishSourceClear(commit);
        LogDetailWeakReferenceStatus("before_source_clear");
        detailRetentionLog("playlist_source_replace action=clear generationId="
            + commit.PreviousGenerationId
            + " sourceCount=0 disposedCount="
            + (commit.SourceRows?.Count ?? 0)
            + " playlistSourceRowCount=0 playlistViewRowCount="
            + CountDetailRows(commit.ViewRows));
    }

    private void LogDetailWeakReferenceStatus(string reason)
    {
        WeakReference<List<PlaylistDetailSourceRow>> sourceReference;
        WeakReference<System.Collections.IList> viewReference;
        long sourceGeneration;
        long viewGeneration;
        lock (DetailViewState.SyncRoot)
        {
            sourceReference = DetailViewState.Source.PreviousRowsWeakReference;
            viewReference = DetailViewState.View.PreviousRowsWeakReference;
            sourceGeneration = DetailViewState.Source.PreviousGenerationId;
            viewGeneration = DetailViewState.View.PreviousGenerationId;
        }
        List<PlaylistDetailSourceRow> sourceRows = null;
        System.Collections.IList viewRows = null;
        bool sourceAlive = sourceReference != null && sourceReference.TryGetTarget(out sourceRows);
        bool viewAlive = viewReference != null && viewReference.TryGetTarget(out viewRows);
        detailRetentionLog("playlist_weak_reference_check reason="
            + reason
            + " sourceGenerationId=" + sourceGeneration
            + " sourceAlive=" + sourceAlive
            + " playlistSourceRowCount=" + (sourceAlive ? sourceRows.Count : 0)
            + " viewGenerationId=" + viewGeneration
            + " viewAlive=" + viewAlive
            + " playlistViewRowCount=" + (viewAlive ? CountDetailRows(viewRows) : 0));
    }

    internal PlaylistDetailTerminalCommitResult ApplyDetailTerminal(
        PlaylistDetailTerminalRequest request)
    {
        if (request?.BuildRequest == null || request.ViewRows == null || request.MainRowsRequest == null)
        {
            throw new ArgumentException("A complete playlist detail terminal request is required.", nameof(request));
        }
        if (request.ReplaceSource && request.SourceRows == null)
        {
            throw new ArgumentException("Source rows are required when replacing the playlist source.", nameof(request));
        }
        RegularChartListOwner columnOwner = detailColumnOwner
            ?? throw new InvalidOperationException("Playlist detail terminal ownership is not configured.");

        var result = new PlaylistDetailTerminalCommitResult();
        MainChartListRowsTransition transition = detailMainChartList.PrepareRowsTransition(request.MainRowsRequest);
        Exception commitException = null;
        ExceptionDispatchInfo preTransferException = null;
        bool cancelTransition = false;
        lock (DetailBuildState.SyncRoot)
        {
            if (request.BuildRequest.RequestVersion != DetailBuildState.RequestVersion)
            {
                cancelTransition = true;
            }
            else
            {
                try
                {
                    DetailViewState.CommitTerminal(
                        request,
                        result,
                        transition.CommitOwnership,
                        () =>
                        {
                            result.ColumnPresentationCommit = CommitColumnPresentationWithoutNotification(
                                request.ColumnSelection.PlaylistColumnSettingsVisibility,
                                request.ColumnSelection.PlaylistSummaryColumnsSettings);
                            result.AppliedColumnMode = request.ColumnSelection.AppliedMode;
                            columnOwner.CommitExternalColumnMode(request.ColumnSelection.AppliedMode);
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

    private static int CountDetailRows(System.Collections.IEnumerable rows)
    {
        if (rows is IChartListViewMetadata metadata)
        {
            return metadata.RowCount;
        }
        int count = 0;
        if (rows != null)
        {
            foreach (object row in rows)
            {
                if (row is PlaylistDetailRow) count++;
            }
        }
        return count;
    }
}
