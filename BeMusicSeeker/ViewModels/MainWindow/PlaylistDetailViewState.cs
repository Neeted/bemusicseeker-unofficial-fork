using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistDetailSourceSnapshotState
{
    internal List<PlaylistDetailSourceRow> Rows = [];
    internal BMSTable CurrentTable;
    internal string CurrentFolderName;
    internal PlaylistDetailFilter CurrentFilterType = PlaylistDetailFilter.PlaylistFilter;
    internal long GenerationId;
    internal PlaylistSourceIdentity? CurrentIdentity;
    internal long LastBuiltLibraryIndexVersion;
    internal long LastBuiltPlaylistRevision;
    internal int LastBuiltScoreSnapshotVersion;
    internal int LastBuiltChartInfoIndexVersion;
    internal long PlaylistContentRevision;
    internal bool IsPlaylistCellEditing;
    internal int PendingScoreSnapshotRefreshVersion;
    internal WeakReference<List<PlaylistDetailSourceRow>> PreviousRowsWeakReference;
    internal long PreviousGenerationId;
}

internal sealed class PlaylistDetailViewSnapshotState
{
    internal IList Rows = new List<object>();
    internal long GenerationId;
    internal int LastAppliedCount;
    internal PlaylistRequestIdentity? CurrentIdentity;
    internal WeakReference<IList> PreviousRowsWeakReference;
    internal long PreviousGenerationId;
}

internal sealed class PlaylistDetailViewState
{
    internal readonly object SyncRoot = new();
    internal readonly PlaylistDetailSourceSnapshotState Source = new();
    internal readonly PlaylistDetailViewSnapshotState View = new();
    internal PlaylistOpenInteractionState CurrentOpenInteraction;

    internal void CommitTerminal(
        PlaylistDetailTerminalRequest request,
        PlaylistDetailTerminalCommitResult result,
        Action commitRows,
        Action commitPresentation)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (result == null)
        {
            throw new ArgumentNullException(nameof(result));
        }
        if (commitRows == null)
        {
            throw new ArgumentNullException(nameof(commitRows));
        }
        if (commitPresentation == null)
        {
            throw new ArgumentNullException(nameof(commitPresentation));
        }

        lock (SyncRoot)
        {
            commitRows();

            if (request.ReplaceSource)
            {
                result.PreviousSourceRows = Source.Rows;
                result.PreviousSourceGenerationId = Source.GenerationId;
                if (result.PreviousSourceRows != null)
                {
                    Source.PreviousRowsWeakReference = new WeakReference<List<PlaylistDetailSourceRow>>(result.PreviousSourceRows);
                    Source.PreviousGenerationId = result.PreviousSourceGenerationId;
                }
                Source.Rows = request.SourceRows;
                Source.CurrentTable = request.CurrentTable;
                Source.CurrentFolderName = request.CurrentFolderName;
                Source.CurrentFilterType = request.CurrentFilterType;
                Source.LastBuiltLibraryIndexVersion = request.BuildRequest.Identity.LibraryIndexVersion;
                Source.LastBuiltPlaylistRevision = request.BuildRequest.Identity.PlaylistRevision;
                Source.LastBuiltScoreSnapshotVersion = request.BuildRequest.Identity.ScoreSnapshotVersion;
                Source.LastBuiltChartInfoIndexVersion = request.BuildRequest.Identity.ChartInfoIndexVersion;
                Source.CurrentIdentity = request.BuildRequest.Identity.SourceIdentity;
                Source.GenerationId++;
            }

            result.PreviousViewRows = View.Rows;
            result.PreviousViewGenerationId = View.GenerationId;
            if (result.PreviousViewRows != null)
            {
                View.PreviousRowsWeakReference = new WeakReference<IList>(result.PreviousViewRows);
                View.PreviousGenerationId = result.PreviousViewGenerationId;
            }
            View.Rows = request.ViewRows;
            View.GenerationId++;
            View.LastAppliedCount = request.ViewRows.Count;
            View.CurrentIdentity = request.BuildRequest.Identity;

            commitPresentation();
            result.SourceGenerationId = Source.GenerationId;
            result.ViewGenerationId = View.GenerationId;
            result.SourceRowsAlive = Source.Rows?.Count ?? 0;
            result.Applied = true;
        }
    }
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
    internal CancellationToken CancellationToken { get; set; }
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
    internal MainViewUpdateMode? AppliedColumnMode { get; set; }
}
