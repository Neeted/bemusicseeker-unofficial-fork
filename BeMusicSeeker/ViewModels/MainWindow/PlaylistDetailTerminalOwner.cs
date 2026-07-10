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

        MainChartListPreparedRowsApply prepared = mainChartList.PrepareRowsApply(request.MainRowsRequest);
        MainChartListRowsCommit mainRowsCommit = null;
        var result = new PlaylistDetailTerminalCommitResult();
        bool stale = false;
        Exception commitException = null;
        try
        {
            lock (buildState.SyncRoot)
            {
                if (request.BuildRequest.RequestVersion != buildState.RequestVersion)
                {
                    stale = true;
                }
                else
                {
                    lock (viewState.SyncRoot)
                    {
                        mainRowsCommit = mainChartList.CommitPreparedRowsWithoutDisposal(prepared);

                        if (request.ReplaceSource)
                        {
                            result.PreviousSourceRows = viewState.Source.Rows;
                            result.PreviousSourceGenerationId = viewState.Source.GenerationId;
                            if (result.PreviousSourceRows != null)
                            {
                                viewState.Source.PreviousRowsWeakReference = new WeakReference<List<PlaylistDetailSourceRow>>(result.PreviousSourceRows);
                                viewState.Source.PreviousGenerationId = result.PreviousSourceGenerationId;
                            }
                            viewState.Source.Rows = request.SourceRows;
                            viewState.Source.CurrentTable = request.CurrentTable;
                            viewState.Source.CurrentFolderName = request.CurrentFolderName;
                            viewState.Source.CurrentFilterType = request.CurrentFilterType;
                            viewState.Source.LastBuiltLibraryIndexVersion = request.BuildRequest.Identity.LibraryIndexVersion;
                            viewState.Source.LastBuiltPlaylistRevision = request.BuildRequest.Identity.PlaylistRevision;
                            viewState.Source.LastBuiltScoreSnapshotVersion = request.BuildRequest.Identity.ScoreSnapshotVersion;
                            viewState.Source.LastBuiltChartInfoIndexVersion = request.BuildRequest.Identity.ChartInfoIndexVersion;
                            viewState.Source.CurrentIdentity = request.BuildRequest.Identity.SourceIdentity;
                            viewState.Source.GenerationId++;
                        }

                        result.PreviousViewRows = viewState.View.Rows;
                        result.PreviousViewGenerationId = viewState.View.GenerationId;
                        if (result.PreviousViewRows != null)
                        {
                            viewState.View.PreviousRowsWeakReference = new WeakReference<IList>(result.PreviousViewRows);
                            viewState.View.PreviousGenerationId = result.PreviousViewGenerationId;
                        }
                        viewState.View.Rows = request.ViewRows;
                        viewState.View.GenerationId++;
                        viewState.View.LastAppliedCount = request.ViewRows.Count;
                        viewState.View.CurrentIdentity = request.BuildRequest.Identity;

                        result.ColumnPresentationCommit = playlistWorkspace.CommitColumnPresentationWithoutNotification(
                            request.ColumnSelection.PlaylistColumnSettingsVisibility,
                            request.ColumnSelection.PlaylistSummaryColumnsSettings);
                        result.SourceGenerationId = viewState.Source.GenerationId;
                        result.ViewGenerationId = viewState.View.GenerationId;
                        result.SourceRowsAlive = viewState.Source.Rows?.Count ?? 0;
                        result.Applied = true;
                        commitExternalColumnMode(request.ColumnSelection.AppliedMode);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (mainRowsCommit == null)
            {
                mainChartList.CancelPreparedRowsApply(prepared);
                throw;
            }
            commitException = ex;
        }

        if (stale)
        {
            mainChartList.CancelPreparedRowsApply(prepared);
            return result;
        }

        List<Exception> terminalExceptions = [];
        if (commitException != null)
        {
            terminalExceptions.Add(commitException);
        }
        TryTerminalAction(() => mainChartList.DisposeCommittedRows(mainRowsCommit), terminalExceptions);
        TryTerminalAction(() => result.MainRowsApply = mainChartList.PublishRowsCommit(mainRowsCommit), terminalExceptions);
        if (result.ColumnPresentationCommit != null)
        {
            TryTerminalAction(() => playlistWorkspace.PublishColumnPresentation(result.ColumnPresentationCommit), terminalExceptions);
        }
        if (terminalExceptions.Count > 0)
        {
            throw new PlaylistDetailTerminalPublishException(
                new AggregateException(terminalExceptions),
                ownershipTransferred: true);
        }
        return result;
    }

    internal PlaylistSourceClearCommitResult CommitSourceClearWithoutCallbacks()
    {
        List<PlaylistDetailSourceRow> sourceRows;
        IList viewRows;
        long previousGenerationId;
        CancellationTokenSource buildCancellation;
        lock (buildState.SyncRoot)
        {
            buildState.RequestVersion++;
            buildState.PendingRequest = null;
            buildCancellation = buildState.CurrentBuildCancellation;
            lock (viewState.SyncRoot)
            {
                sourceRows = viewState.Source.Rows;
                previousGenerationId = viewState.Source.GenerationId;
                viewRows = viewState.View.Rows;
                long previousViewGenerationId = viewState.View.GenerationId;
                if (sourceRows != null)
                {
                    viewState.Source.PreviousRowsWeakReference = new WeakReference<List<PlaylistDetailSourceRow>>(sourceRows);
                    viewState.Source.PreviousGenerationId = previousGenerationId;
                }
                if (viewRows != null)
                {
                    viewState.View.PreviousRowsWeakReference = new WeakReference<IList>(viewRows);
                    viewState.View.PreviousGenerationId = previousViewGenerationId;
                }
                viewState.Source.Rows = [];
                viewState.View.Rows = new List<object>();
                viewState.Source.CurrentTable = null;
                viewState.Source.CurrentFolderName = null;
                viewState.Source.CurrentFilterType = MainWindowViewModel.PlaylistFilterType.PlaylistFilter;
                viewState.View.CurrentIdentity = null;
                viewState.Source.CurrentIdentity = null;
                viewState.CurrentOpenInteraction = null;
                viewState.Source.LastBuiltLibraryIndexVersion = 0L;
                viewState.Source.LastBuiltPlaylistRevision = 0L;
                viewState.Source.LastBuiltScoreSnapshotVersion = 0;
                viewState.Source.LastBuiltChartInfoIndexVersion = 0;
                viewState.Source.GenerationId = 0L;
                viewState.View.GenerationId = 0L;
                viewState.View.LastAppliedCount = 0;
                viewState.Source.IsPlaylistCellEditing = false;
                viewState.Source.PendingScoreSnapshotRefreshVersion = 0;
            }
            buildState.CurrentBuildRequest = null;
        }
        return new PlaylistSourceClearCommitResult(sourceRows, viewRows, previousGenerationId, buildCancellation);
    }

    internal void PublishSourceClear(PlaylistSourceClearCommitResult commit)
    {
        if (commit == null)
        {
            throw new ArgumentNullException(nameof(commit));
        }
        try
        {
            commit.BuildCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void TryTerminalAction(Action action, ICollection<Exception> exceptions)
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
    internal MainWindowViewModel.PlaylistFilterType CurrentFilterType { get; set; }
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
