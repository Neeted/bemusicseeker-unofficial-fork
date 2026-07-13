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

    internal PlaylistScoreRefreshDecision EvaluateScoreSnapshotRefresh(int scoreSnapshotVersion)
    {
        lock (DetailViewState.SyncRoot)
        {
            int lastBuiltVersion = DetailViewState.Source.LastBuiltScoreSnapshotVersion;
            if (scoreSnapshotVersion <= lastBuiltVersion)
            {
                return PlaylistScoreRefreshDecision.NotRequired(lastBuiltVersion);
            }
            if (DetailViewState.Source.IsPlaylistCellEditing)
            {
                DetailViewState.Source.PendingScoreSnapshotRefreshVersion = Math.Max(
                    DetailViewState.Source.PendingScoreSnapshotRefreshVersion,
                    scoreSnapshotVersion);
                return PlaylistScoreRefreshDecision.CreateDeferred(lastBuiltVersion);
            }
            return PlaylistScoreRefreshDecision.Required(lastBuiltVersion);
        }
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

internal readonly struct PlaylistScoreRefreshDecision
{
    private PlaylistScoreRefreshDecision(bool refreshRequired, bool deferred, int lastBuiltVersion)
    {
        RefreshRequired = refreshRequired;
        Deferred = deferred;
        LastBuiltVersion = lastBuiltVersion;
    }

    internal bool RefreshRequired { get; }

    internal bool Deferred { get; }

    internal int LastBuiltVersion { get; }

    internal static PlaylistScoreRefreshDecision NotRequired(int lastBuiltVersion) => new(false, false, lastBuiltVersion);

    internal static PlaylistScoreRefreshDecision CreateDeferred(int lastBuiltVersion) => new(false, true, lastBuiltVersion);

    internal static PlaylistScoreRefreshDecision Required(int lastBuiltVersion) => new(true, false, lastBuiltVersion);
}
