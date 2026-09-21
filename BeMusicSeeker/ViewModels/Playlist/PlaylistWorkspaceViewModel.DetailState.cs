using System;
using System.Collections;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal long DetailSourceGenerationId
    {
        get
        {
            lock (DetailViewState.SyncRoot)
            {
                return DetailViewState.Source.GenerationId;
            }
        }
    }

    internal long DetailViewGenerationId
    {
        get
        {
            lock (DetailViewState.SyncRoot)
            {
                return DetailViewState.View.GenerationId;
            }
        }
    }

    internal PlaylistPreviousDetailRowsSnapshot CapturePreviousDetailRowsSnapshot()
    {
        WeakReference<List<PlaylistDetailSourceRow>> sourceReference;
        WeakReference<IList> viewReference;
        lock (DetailViewState.SyncRoot)
        {
            sourceReference = DetailViewState.Source.PreviousRowsWeakReference;
            viewReference = DetailViewState.View.PreviousRowsWeakReference;
        }
        List<PlaylistDetailSourceRow> sourceRows = null;
        IList viewRows = null;
        bool sourceAlive = sourceReference != null
            && sourceReference.TryGetTarget(out sourceRows);
        bool viewAlive = viewReference != null
            && viewReference.TryGetTarget(out viewRows);
        return new PlaylistPreviousDetailRowsSnapshot(
            sourceAlive,
            sourceAlive ? sourceRows.Count : 0,
            viewAlive,
            viewAlive ? CountDetailRows(viewRows) : 0);
    }

    internal event EventHandler<PlaylistDetailScoreSnapshotRefreshRequestedEventArgs> PlaylistDetailScoreSnapshotRefreshRequested;

    internal void RequestPlaylistDetailScoreSnapshotRefresh(int scoreSnapshotVersion)
    {
        if (!IsPlaylistDetailViewActive || IsPlaylistSummaryMode)
        {
            return;
        }

        int lastBuiltVersion;
        bool deferredByEdit;
        lock (DetailViewState.SyncRoot)
        {
            lastBuiltVersion = DetailViewState.Source.LastBuiltScoreSnapshotVersion;
            if (scoreSnapshotVersion <= lastBuiltVersion)
            {
                return;
            }
            if (DetailViewState.Source.IsPlaylistCellEditing)
            {
                DetailViewState.Source.PendingScoreSnapshotRefreshVersion = Math.Max(
                    DetailViewState.Source.PendingScoreSnapshotRefreshVersion,
                    scoreSnapshotVersion);
                deferredByEdit = true;
            }
            else
            {
                deferredByEdit = false;
            }
        }

        PublishPlaylistDetailScoreSnapshotRefreshRequested(
            scoreSnapshotVersion,
            lastBuiltVersion,
            deferredByEdit,
            refreshRequired: !deferredByEdit);
    }

    private void PublishPlaylistDetailScoreSnapshotRefreshRequested(
        int scoreSnapshotVersion,
        int lastBuiltVersion,
        bool deferredByEdit,
        bool refreshRequired)
    {
        PlaylistDetailScoreSnapshotRefreshRequested?.Invoke(
            this,
            new PlaylistDetailScoreSnapshotRefreshRequestedEventArgs(
                scoreSnapshotVersion,
                lastBuiltVersion,
                deferredByEdit,
                refreshRequired));
    }
}

internal readonly struct PlaylistPreviousDetailRowsSnapshot
{
    internal PlaylistPreviousDetailRowsSnapshot(
        bool sourceAlive,
        int sourceRowCount,
        bool viewAlive,
        int viewRowCount)
    {
        SourceAlive = sourceAlive;
        SourceRowCount = sourceRowCount;
        ViewAlive = viewAlive;
        ViewRowCount = viewRowCount;
    }

    internal bool SourceAlive { get; }

    internal int SourceRowCount { get; }

    internal bool ViewAlive { get; }

    internal int ViewRowCount { get; }
}

internal sealed class PlaylistDetailScoreSnapshotRefreshRequestedEventArgs : EventArgs
{
    internal PlaylistDetailScoreSnapshotRefreshRequestedEventArgs(
        int scoreSnapshotVersion,
        int lastBuiltVersion,
        bool deferredByEdit,
        bool refreshRequired)
    {
        ScoreSnapshotVersion = scoreSnapshotVersion;
        LastBuiltVersion = lastBuiltVersion;
        DeferredByEdit = deferredByEdit;
        RefreshRequired = refreshRequired;
    }

    internal int ScoreSnapshotVersion { get; }

    internal int LastBuiltVersion { get; }

    internal bool DeferredByEdit { get; }

    internal bool RefreshRequired { get; }
}
