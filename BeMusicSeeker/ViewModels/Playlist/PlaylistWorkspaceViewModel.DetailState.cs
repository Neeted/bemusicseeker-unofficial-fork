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
