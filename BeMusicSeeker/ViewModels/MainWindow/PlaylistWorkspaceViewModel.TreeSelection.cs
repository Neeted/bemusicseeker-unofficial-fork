using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using Livet;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly DispatcherCollection<BMSTable> emptyPlaylistTreeTables;

    private DispatcherCollection<BMSTable> playlistTreeTables;

    private bool isPlaylistTreeExpanded = true;

    private readonly object playlistDetailSelectionSyncRoot = new();

    private PlaylistDetailSelection playlistDetailSelection;

    private long playlistDetailSelectionRevision;

    internal event EventHandler<PlaylistTreeSelectionRequestedEventArgs> TreeSelectionRequested;

    /// <summary>
    /// プレイリストツリーが表示するテーブル source です。起動前は注入された空のコレクションを返し、起動後は <see cref="BMSPlaylist.BMSTables" /> と同じコレクション identity、順序、階層を保持します。
    /// </summary>
    public DispatcherCollection<BMSTable> PlaylistTreeTables => playlistTreeTables
        ?? throw new InvalidOperationException("Playlist tree source is not configured.");

    internal void RefreshPlaylistTreeTables(BMSPlaylist playlistStore)
    {
        DispatcherCollection<BMSTable> nextTables = playlistStore == null
            ? emptyPlaylistTreeTables
                ?? throw new InvalidOperationException("Playlist tree source is not configured.")
            : playlistStore.BMSTables;
        if (ReferenceEquals(playlistTreeTables, nextTables))
        {
            return;
        }
        playlistTreeTables = nextTables;
        RaisePropertyChanged(nameof(PlaylistTreeTables));
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
        TreeSelectionRequested?.Invoke(this, PlaylistTreeSelectionRequestedEventArgs.Summary(selectionRevision));
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
        TreeSelectionRequested?.Invoke(
            this,
            PlaylistTreeSelectionRequestedEventArgs.CreateDetail(selection, selectionRevision));
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

    internal bool TryExecuteCurrentPlaylistSummarySelection(long selectionRevision, Action apply)
    {
        if (apply == null)
        {
            throw new ArgumentNullException(nameof(apply));
        }
        lock (playlistDetailSelectionSyncRoot)
        {
            if (!IsCurrentPlaylistSummarySelectionWithoutLock(selectionRevision))
            {
                return false;
            }
            apply();
            return true;
        }
    }

    internal bool TryExecuteCurrentPlaylistDetailSelection(
        PlaylistDetailSelection selection,
        long selectionRevision,
        Action apply)
    {
        if (apply == null)
        {
            throw new ArgumentNullException(nameof(apply));
        }
        lock (playlistDetailSelectionSyncRoot)
        {
            if (!IsCurrentPlaylistDetailSelectionWithoutLock(selection, selectionRevision))
            {
                return false;
            }
            apply();
            return true;
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

internal sealed class PlaylistTreeSelectionRequestedEventArgs : EventArgs
{
    private PlaylistTreeSelectionRequestedEventArgs(
        bool isSummary,
        PlaylistDetailSelection detail,
        long selectionRevision)
    {
        IsSummary = isSummary;
        Detail = detail;
        SelectionRevision = selectionRevision;
    }

    internal bool IsSummary { get; }

    internal PlaylistDetailSelection Detail { get; }

    internal long SelectionRevision { get; }

    internal static PlaylistTreeSelectionRequestedEventArgs Summary(long selectionRevision) => new(true, null, selectionRevision);

    internal static PlaylistTreeSelectionRequestedEventArgs CreateDetail(
        PlaylistDetailSelection detail,
        long selectionRevision)
    {
        return new PlaylistTreeSelectionRequestedEventArgs(
            isSummary: false,
            detail ?? throw new ArgumentNullException(nameof(detail)),
            selectionRevision);
    }
}
