using System;
using System.Collections;
using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistDetailSourceSnapshotState
{
    internal List<PlaylistDetailSourceRow> Rows = [];
    internal BMSTable CurrentTable;
    internal string CurrentFolderName;
    internal MainWindowViewModel.PlaylistFilterType CurrentFilterType = MainWindowViewModel.PlaylistFilterType.PlaylistFilter;
    internal long GenerationId;
    internal MainWindowViewModel.PlaylistSourceIdentity? CurrentIdentity;
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
    internal MainWindowViewModel.PlaylistRequestIdentity? CurrentIdentity;
    internal WeakReference<IList> PreviousRowsWeakReference;
    internal long PreviousGenerationId;
}

internal sealed class PlaylistDetailViewState
{
    internal readonly object SyncRoot = new();
    internal readonly PlaylistDetailSourceSnapshotState Source = new();
    internal readonly PlaylistDetailViewSnapshotState View = new();
    internal PlaylistOpenInteractionState CurrentOpenInteraction;
}
