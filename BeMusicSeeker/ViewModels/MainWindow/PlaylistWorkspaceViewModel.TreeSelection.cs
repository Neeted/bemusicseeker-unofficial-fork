using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models;
using Livet;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly DispatcherCollection<BMSTable> emptyPlaylistTreeTables;

    private DispatcherCollection<BMSTable> playlistTreeTables;

    private BMSPlaylist playlistTreeStore;

    private DispatcherCollection<BMSTable> observedPlaylistTreeTables;

    private long playlistTreeNotificationGeneration;

    private readonly object playlistTreeStoreSyncRoot = new();

    private bool isPlaylistTreeExpanded = true;

    private readonly object playlistDetailSelectionSyncRoot = new();

    private PlaylistDetailSelection playlistDetailSelection;

    private long playlistDetailSelectionRevision;

    internal event EventHandler<PlaylistTreeSelectionActivatedEventArgs> TreeSelectionActivated;

    internal event EventHandler PlaylistTablesPresentationChanged;

    internal event EventHandler PlaylistKeywordValueCandidatesChanged;

    private void DispatchPlaylistKeywordValueCandidatesChanged()
    {
        dispatchPresentation(() => PlaylistKeywordValueCandidatesChanged?.Invoke(this, EventArgs.Empty));
    }

    internal event EventHandler<PlaylistEntriesHydrationVersionChangedEventArgs> PlaylistEntriesHydrationRequested;

    internal event EventHandler<PlaylistEntriesHydrationVersionChangedEventArgs> PlaylistEntriesHydrationCompleted;

    /// <summary>
    /// プレイリストツリーが表示するテーブル source です。起動前は注入された空のコレクションを返し、起動後は <see cref="BMSPlaylist.BMSTables" /> と同じコレクション identity、順序、階層を保持します。
    /// </summary>
    public DispatcherCollection<BMSTable> PlaylistTreeTables => playlistTreeTables;

    /// <summary>
    /// Captures the playlist tree source while holding the playlist reader lock.
    /// </summary>
    internal List<BMSTable> CapturePlaylistTreeTablesSnapshot()
    {
        List<BMSTable> tableSnapshot = [];
        BMSPlaylist playlistStore = getPlaylistStore();
        bool readerLockAcquired = false;
        try
        {
            if (playlistStore != null)
            {
                playlistStore.AcquireReaderLockBMSTables();
                readerLockAcquired = true;
            }
            if (PlaylistTreeTables != null)
            {
                tableSnapshot.AddRange(PlaylistTreeTables);
            }
        }
        finally
        {
            if (playlistStore != null && readerLockAcquired)
            {
                playlistStore.FreeReaderLockBMSTables();
            }
        }
        return tableSnapshot;
    }

    internal void RefreshPlaylistTreeTables(BMSPlaylist playlistStore)
    {
        AttachPlaylistTreeStore(playlistStore);
        ApplyPlaylistTreeTablesSource();
    }

    internal void RefreshPlaylistTreePresentation()
    {
        ApplyPlaylistTreeTablesSource(raiseWhenUnchanged: true);
    }

    private void AttachPlaylistTreeStore(BMSPlaylist nextStore)
    {
        lock (playlistTreeStoreSyncRoot)
        {
            if (ReferenceEquals(playlistTreeStore, nextStore))
            {
                AttachObservedPlaylistTreeTables(nextStore?.BMSTables);
                return;
            }

            if (playlistTreeStore != null)
            {
                playlistTreeStore.PropertyChanged -= PlaylistTreeStorePropertyChanged;
                playlistTreeStore.PlaylistEntriesHydrationReceiptPublished -= PlaylistTreeStoreHydrationReceiptPublished;
            }
            if (observedPlaylistTreeTables != null)
            {
                observedPlaylistTreeTables.CollectionChanged -= PlaylistTreeTablesCollectionChanged;
            }

            playlistTreeStore = nextStore;
            Interlocked.Increment(ref playlistTreeNotificationGeneration);
            observedPlaylistTreeTables = null;
            if (playlistTreeStore != null)
            {
                playlistTreeStore.PropertyChanged += PlaylistTreeStorePropertyChanged;
                playlistTreeStore.PlaylistEntriesHydrationReceiptPublished += PlaylistTreeStoreHydrationReceiptPublished;
                AttachObservedPlaylistTreeTables(playlistTreeStore.BMSTables);
            }
        }
    }

    private void AttachObservedPlaylistTreeTables(DispatcherCollection<BMSTable> nextTables)
    {
        lock (playlistTreeStoreSyncRoot)
        {
            if (ReferenceEquals(observedPlaylistTreeTables, nextTables))
            {
                return;
            }
            if (observedPlaylistTreeTables != null)
            {
                observedPlaylistTreeTables.CollectionChanged -= PlaylistTreeTablesCollectionChanged;
            }
            observedPlaylistTreeTables = nextTables;
            Interlocked.Increment(ref playlistTreeNotificationGeneration);
            if (observedPlaylistTreeTables != null)
            {
                observedPlaylistTreeTables.CollectionChanged += PlaylistTreeTablesCollectionChanged;
            }
        }
    }

    private void ApplyPlaylistTreeTablesSource(bool raiseWhenUnchanged = false)
    {
        DispatcherCollection<BMSTable> nextTables;
        bool changed;
        lock (playlistTreeStoreSyncRoot)
        {
            nextTables = playlistTreeStore == null
                ? emptyPlaylistTreeTables
                    ?? throw new InvalidOperationException("Playlist tree source is not configured.")
                : playlistTreeStore.BMSTables;
            AttachObservedPlaylistTreeTables(nextTables);
            changed = !ReferenceEquals(playlistTreeTables, nextTables);
            if (changed)
            {
                playlistTreeTables = nextTables;
            }
        }
        if (changed || raiseWhenUnchanged)
        {
            RaisePropertyChanged(nameof(PlaylistTreeTables));
        }
    }

    public bool IsPlaylistTreeExpanded
    {
        get => isPlaylistTreeExpanded;
        set
        {
            if (isPlaylistTreeExpanded == value)
            {
                return;
            }
            isPlaylistTreeExpanded = value;
            RaisePropertyChanged(nameof(IsPlaylistTreeExpanded));
        }
    }

    internal void RequestSummarySelection()
    {
        long selectionRevision = ClearPlaylistDetailSelection();
        dispatchPresentation(
            () =>
            {
                lock (playlistDetailSelectionSyncRoot)
                {
                    if (!IsCurrentPlaylistSummarySelectionWithoutLock(selectionRevision))
                    {
                        return;
                    }
                    bool summaryModeChanged = SetPlaylistSummaryMode(enabled: true);
                    TreeSelectionActivated?.Invoke(
                        this,
                        PlaylistTreeSelectionActivatedEventArgs.Summary(
                            selectionRevision,
                            summaryModeChanged));
                    RequestPlaylistSummaryPresentationRefresh();
                }
            });
    }

    internal long ClearPlaylistDetailSelection()
    {
        lock (playlistDetailSelectionSyncRoot)
        {
            playlistDetailSelection = null;
            return ++playlistDetailSelectionRevision;
        }
    }

    internal void RequestDetailSelection(BMSTable table, PlaylistFolderNode folderNode = null)
    {
        string folderName = folderNode == null || folderNode.IsSpecial
            ? null
            : folderNode.FolderName;
        PlaylistDetailFilter filter = folderNode?.SpecialKind == PlaylistFolderNodeSpecialKind.NotOwned
            ? PlaylistDetailFilter.PlaylistNotOwnedFilterSelected
            : PlaylistDetailFilter.PlaylistFilter;
        PlaylistDetailSelection selection = new(table, folderName, filter);
        long selectionRevision;
        lock (playlistDetailSelectionSyncRoot)
        {
            playlistDetailSelection = selection;
            selectionRevision = ++playlistDetailSelectionRevision;
        }
        dispatchPresentation(
            () =>
            {
                lock (playlistDetailSelectionSyncRoot)
                {
                    if (!IsCurrentPlaylistDetailSelectionWithoutLock(selection, selectionRevision))
                    {
                        return;
                    }
                    bool summaryModeChanged = SetPlaylistSummaryMode(enabled: false);
                    TreeSelectionActivated?.Invoke(
                        this,
                        PlaylistTreeSelectionActivatedEventArgs.CreateDetail(
                            selection,
                            selectionRevision,
                            summaryModeChanged));
                }
            });
    }

    internal PlaylistDetailSelection CapturePlaylistDetailSelection()
    {
        return CapturePlaylistDetailSelection(out _);
    }

    internal PlaylistDetailSelection CapturePlaylistDetailSelection(out long selectionRevision)
    {
        lock (playlistDetailSelectionSyncRoot)
        {
            selectionRevision = playlistDetailSelectionRevision;
            return playlistDetailSelection?.Clone();
        }
    }

    internal bool IsCurrentPlaylistDetailSelection(PlaylistDetailSelection selection, long selectionRevision)
    {
        lock (playlistDetailSelectionSyncRoot)
        {
            return IsCurrentPlaylistDetailSelectionWithoutLock(selection, selectionRevision);
        }
    }

    internal bool IsCurrentPlaylistSummarySelection(long selectionRevision)
    {
        lock (playlistDetailSelectionSyncRoot)
        {
            return IsCurrentPlaylistSummarySelectionWithoutLock(selectionRevision);
        }
    }

    private bool IsCurrentPlaylistSummarySelectionWithoutLock(long selectionRevision)
    {
        return selectionRevision == playlistDetailSelectionRevision
            && playlistDetailSelection == null;
    }

    internal bool ReplaceCurrentPlaylistDetailSelectionTable(BMSTable oldTable, BMSTable newTable)
    {
        if (oldTable == null || newTable == null || ReferenceEquals(oldTable, newTable))
        {
            return false;
        }
        lock (playlistDetailSelectionSyncRoot)
        {
            if (playlistDetailSelection?.Table != oldTable)
            {
                return false;
            }
            playlistDetailSelection = playlistDetailSelection.WithTable(newTable);
            playlistDetailSelectionRevision++;
            return true;
        }
    }

    internal bool RemapCurrentPlaylistDetailFolderSelection(
        BMSTable table,
        IReadOnlyDictionary<string, string> rewrittenFolders)
    {
        if (table == null || rewrittenFolders == null || rewrittenFolders.Count == 0)
        {
            return false;
        }
        lock (playlistDetailSelectionSyncRoot)
        {
            string currentFolderName = playlistDetailSelection?.FolderName;
            if (playlistDetailSelection?.Table != table
                || currentFolderName == null
                || !rewrittenFolders.TryGetValue(currentFolderName, out string rewrittenFolder))
            {
                return false;
            }
            playlistDetailSelection = playlistDetailSelection.WithFolderName(rewrittenFolder);
            playlistDetailSelectionRevision++;
            return true;
        }
    }

    internal bool MarkCurrentPlaylistDetailEntriesChanged(BMSTable table, string reason)
    {
        lock (playlistDetailSelectionSyncRoot)
        {
            if (playlistDetailSelection?.Table != table)
            {
                return false;
            }
            IncrementDetailContentRevision(reason);
            return true;
        }
    }

    private bool IsCurrentPlaylistDetailSelectionWithoutLock(
        PlaylistDetailSelection selection,
        long selectionRevision)
    {
        return selection != null
            && selectionRevision == playlistDetailSelectionRevision
            && playlistDetailSelection != null
            && playlistDetailSelection.Matches(selection);
    }
}

internal sealed class PlaylistDetailSelection
{
    internal PlaylistDetailSelection(
        BMSTable table,
        string folderName,
        PlaylistDetailFilter filter)
    {
        Table = table;
        FolderName = folderName;
        Filter = filter;
    }

    internal BMSTable Table { get; }

    internal string FolderName { get; }

    internal PlaylistDetailFilter Filter { get; }

    internal PlaylistDetailSelection Clone()
    {
        return new PlaylistDetailSelection(Table, FolderName, Filter);
    }

    internal PlaylistDetailSelection WithTable(BMSTable table)
    {
        return new PlaylistDetailSelection(table, FolderName, Filter);
    }

    internal PlaylistDetailSelection WithFolderName(string folderName)
    {
        return new PlaylistDetailSelection(Table, folderName, Filter);
    }

    internal bool Matches(PlaylistDetailSelection other)
    {
        return other != null
            && ReferenceEquals(Table, other.Table)
            && string.Equals(FolderName, other.FolderName, StringComparison.Ordinal)
            && Filter == other.Filter;
    }
}

internal sealed class PlaylistTreeSelectionActivatedEventArgs : EventArgs
{
    private PlaylistTreeSelectionActivatedEventArgs(
        bool isSummary,
        PlaylistDetailSelection detail,
        long selectionRevision,
        bool summaryModeChanged)
    {
        IsSummary = isSummary;
        Detail = detail;
        SelectionRevision = selectionRevision;
        SummaryModeChanged = summaryModeChanged;
    }

    internal bool IsSummary { get; }

    internal PlaylistDetailSelection Detail { get; }

    internal long SelectionRevision { get; }

    internal bool SummaryModeChanged { get; }

    internal static PlaylistTreeSelectionActivatedEventArgs Summary(
        long selectionRevision,
        bool summaryModeChanged)
        => new(true, null, selectionRevision, summaryModeChanged);

    internal static PlaylistTreeSelectionActivatedEventArgs CreateDetail(
        PlaylistDetailSelection detail,
        long selectionRevision,
        bool summaryModeChanged)
    {
        return new PlaylistTreeSelectionActivatedEventArgs(
            isSummary: false,
            detail ?? throw new ArgumentNullException(nameof(detail)),
            selectionRevision,
            summaryModeChanged);
    }
}
