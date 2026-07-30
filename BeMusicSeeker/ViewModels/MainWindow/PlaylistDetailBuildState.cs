using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Holds playlist detail build worker queue and cancellation state.
/// </summary>
internal sealed class PlaylistDetailBuildState
{
    /// <summary>
    /// Serializes request queue and build cancellation updates.
    /// </summary>
    internal readonly object SyncRoot = new();

    /// <summary>
    /// Limits playlist source build execution to one worker iteration.
    /// </summary>
    internal readonly SemaphoreSlim BuildGate = new(1, 1);

    /// <summary>
    /// Latest playlist build request version.
    /// </summary>
    internal int RequestVersion;

    /// <summary>
    /// Token source for the active or most recent playlist build lifecycle.
    /// </summary>
    internal CancellationTokenSource Cancellation = new();

    /// <summary>
    /// Token source for the request currently being built.
    /// </summary>
    internal CancellationTokenSource CurrentBuildCancellation;

    /// <summary>
    /// Request currently held by the worker, including quiet-window candidates.
    /// </summary>
    internal PlaylistBuildRequest CurrentBuildRequest;

    /// <summary>
    /// Latest pending playlist build request.
    /// </summary>
    internal PlaylistBuildRequest PendingRequest;

    /// <summary>
    /// Whether the playlist build worker is currently running.
    /// </summary>
    internal bool WorkerRunning;

    /// <summary>
    /// Whether shutdown cancellation has been requested for the current worker lifecycle.
    /// </summary>
    internal bool ShutdownCancellationRequested;

    internal PlaylistSourceRetirementRequest PrepareSourceRetirement()
    {
        lock (SyncRoot)
        {
            RequestVersion++;
            PendingRequest = null;
            CurrentBuildRequest = null;
            return new PlaylistSourceRetirementRequest(
                RequestVersion,
                CurrentBuildCancellation);
        }
    }

    internal PlaylistSourceClearCommitResult CommitPreparedSourceRetirement(
        PlaylistDetailViewState viewState,
        PlaylistSourceRetirementRequest request)
    {
        if (viewState == null)
        {
            throw new ArgumentNullException(nameof(viewState));
        }
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        lock (SyncRoot)
        {
            if (request.RequestVersion != RequestVersion)
            {
                return null;
            }
            return CommitViewStateClearUnsafe(viewState, buildCancellation: null);
        }
    }

    internal PlaylistSourceClearCommitResult CommitSourceClear(PlaylistDetailViewState viewState)
    {
        if (viewState == null)
        {
            throw new ArgumentNullException(nameof(viewState));
        }

        lock (SyncRoot)
        {
            RequestVersion++;
            PendingRequest = null;
            CurrentBuildRequest = null;
            return CommitViewStateClearUnsafe(viewState, CurrentBuildCancellation);
        }
    }

    private static PlaylistSourceClearCommitResult CommitViewStateClearUnsafe(
        PlaylistDetailViewState viewState,
        CancellationTokenSource buildCancellation)
    {
        List<PlaylistDetailSourceRow> sourceRows;
        IList viewRows;
        long previousGenerationId;
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
            viewState.Source.CurrentFilterType = PlaylistDetailFilter.PlaylistFilter;
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
        return new PlaylistSourceClearCommitResult(sourceRows, viewRows, previousGenerationId, buildCancellation);
    }

    internal void PublishSourceRetirement(PlaylistSourceRetirementRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        try
        {
            request.BuildCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
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
}

internal sealed class PlaylistSourceRetirementRequest
{
    internal PlaylistSourceRetirementRequest(
        int requestVersion,
        CancellationTokenSource buildCancellation)
    {
        RequestVersion = requestVersion;
        BuildCancellation = buildCancellation;
    }

    internal int RequestVersion { get; }

    internal CancellationTokenSource BuildCancellation { get; }
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
