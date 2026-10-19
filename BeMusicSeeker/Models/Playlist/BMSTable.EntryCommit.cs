using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

public partial class BMSTable
{
    private readonly record struct FolderKey(string Name);

    private readonly Dictionary<BMSTableEntry, long> entryOrder = [];
    private readonly Dictionary<FolderKey, HashSet<BMSTableEntry>> entriesByFolder = [];
    private readonly Dictionary<(string Folder, string Key), HashSet<BMSTableEntry>> entriesByIdentity = [];
    private readonly Dictionary<FolderKey, int> activeFolderCounts = [];
    private long nextEntryOrder;
    private int entryLookupRevision;

    /// <summary>所属と解決キーだけの版です。memo・URL・level の更新で所持数を再走査しません。</summary>
    internal int EntryLookupRevision => entryLookupRevision;

    private void RebuildEntryMembership()
    {
        entryOrder.Clear();
        entriesByFolder.Clear();
        entriesByIdentity.Clear();
        activeFolderCounts.Clear();
        nextEntryOrder = 0;
        foreach (BMSTableEntry entry in _entries) { IndexEntry(entry); }
        entryLookupRevision++;
    }

    private void IndexEntry(BMSTableEntry entry)
    {
        entryOrder[entry] = nextEntryOrder++;
        if (!entriesByFolder.TryGetValue(new(entry.folder), out HashSet<BMSTableEntry> folder))
        {
            entriesByFolder.Add(new(entry.folder), folder = []);
        }
        folder.Add(entry);
        (string folder, string) identity = (entry.folder, getEntryIdentityKey(entry));
        if (!entriesByIdentity.TryGetValue(identity, out HashSet<BMSTableEntry> matches))
        {
            entriesByIdentity.Add(identity, matches = []);
        }
        matches.Add(entry);
        if (!entry.is_removed)
        {
            activeFolderCounts.TryGetValue(new(entry.folder), out int count);
            activeFolderCounts[new(entry.folder)] = count + 1;
        }
    }

    private void AppendEntry(BMSTableEntry entry)
    {
        _entries = _entries.Add(entry);
        _entriesView = _entries;
        IndexEntry(entry);
    }

    /// <summary>正本内の同じ entry 参照だけを判定します。hash・path で別項目へ読み替えません。</summary>
    internal bool ContainsEntry(BMSTableEntry entry) => entry != null && entryOrder.ContainsKey(entry);

    /// <summary>生存 entry の値と表の版を同じ reader 区間で捕捉します。</summary>
    internal PlaylistEntryReadSnapshot CaptureEntryReadSnapshot(BMSTableEntry entry)
    {
        using (ReaderWriterLock.GetReaderGuard())
        {
            return new(this, PlaylistEntriesRevision,
                ContainsEntry(entry) ? [new(entry, null, entry.CaptureValue(), null, entryOrder[entry])] : []);
        }
    }

    /// <summary>初回・明示全置換の入力として、entry の不変値と版を同時捕捉します。</summary>
    internal PlaylistEntryReadSnapshot CaptureEntriesReadSnapshot()
    {
        using (ReaderWriterLock.GetReaderGuard())
        {
            return new(this, PlaylistEntriesRevision,
                [.. _entries.Select(entry => new PlaylistEntryChange(entry, null, entry.CaptureValue(), null, entryOrder[entry]))]);
        }
    }

    /// <summary>保存候補用に header を複製します。現在の header は DB 確定まで変更しません。</summary>
    internal BMSTable CreatePersistenceHeaderSnapshot(DateTime lastUpdate)
    {
        var snapshot = (BMSTable)MemberwiseClone();
        snapshot.last_update = lastUpdate;
        return snapshot;
    }

    /// <summary>writer 内で対象値の確定、entry 版と lookup 依存の更新を一回に揃えます。</summary>
    internal PlaylistEntryCommitReceipt CommitEntryValues(IReadOnlyList<PlaylistEntryChange> changes, bool folderOrderChanged = false)
    {
        int previous = _PlaylistEntriesRevision;
        if (changes.Count == 0 && !folderOrderChanged) { return new(this, previous, previous, []); }
        foreach (PlaylistEntryChange change in changes)
        {
            if (change.Before?.Md5 != change.After?.Md5 || change.Before?.Sha256 != change.After?.Sha256
                || change.Before?.IsRemoved != change.After?.IsRemoved
                || change.BeforeOrder.HasValue != change.AfterOrder.HasValue)
            {
                entryLookupRevision++;
                break;
            }
        }
        // 通知は排他解放後の確定結果配送が担います。
        _PlaylistEntriesRevision++;
        return new(this, previous, _PlaylistEntriesRevision, [.. changes],
            FolderOrder: folderOrderChanged ? [.. _Folder_order] : default);
    }

    /// <summary>writer 内で同じ entry の旧新値と明示順序を捕捉します。</summary>
    internal PlaylistEntryChange CaptureEntryChange(BMSTableEntry entry, PlaylistEntryValue before)
        => new(entry, before, entry.CaptureValue(), entryOrder[entry], entryOrder[entry]);

    /// <summary>保存候補を同じ参照へ適用し、hash に依存する所属索引も同じ writer 内で揃えます。</summary>
    internal void ApplyEntryCandidate(BMSTableEntry entry, BMSTableEntry candidate)
    {
        long order = entryOrder[entry];
        RemoveIndexedEntry(entry);
        entry.ApplyCommittedEdit(candidate);
        entry.folder = candidate.folder;
        IndexEntry(entry);
        entryOrder[entry] = order;
    }

    /// <summary>所属列を構造共有し、変更する行だけを保存候補として保全した局所変更です。</summary>
    internal sealed record EntryMutation
    {
        internal BMSTable Table { get; init; }
        internal int BaseRevision { get; init; }
        internal ImmutableList<BMSTableEntry> Entries { get; init; }
        internal ImmutableArray<PlaylistEntryChange> Changes { get; init; }
        internal IReadOnlyList<(BMSTableEntry Entry, BMSTableEntry Candidate)> Additions { get; init; }
        internal DateTime LastUpdate { get; init; }
        internal BMSTable Header { get; init; }
        internal ImmutableArray<string> FolderOrder { get; init; }
        internal bool HasChanges => !Changes.IsEmpty || !FolderOrder.IsDefault;
    }

    private int FindEntryPosition(BMSTableEntry entry, IPlaylistEntryMutationWorkObserver observer)
    {
        long order = entryOrder[entry];
        int low = 0, high = _entries.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            observer?.OnEntryReferenceRead();
            long candidateOrder = entryOrder[_entries[middle]];
            if (candidateOrder == order) { return middle; }
            if (candidateOrder < order) { low = middle + 1; } else { high = middle - 1; }
        }
        throw new InvalidOperationException("Playlist entry membership and order differ.");
    }

    /// <summary>削除とフォルダ別追加の候補を writer 内で準備します。live 値・所属・日時・版は変更しません。</summary>
    internal EntryMutation PrepareEntryMutation(IReadOnlyCollection<BMSTableEntry> removals,
        IReadOnlyList<(BMSTableEntry Entry, string Folder)> additions,
        IPlaylistEntryMutationWorkObserver observer = null, bool preserveEmptyFoldersAfterRemoval = true)
    {
        HashSet<BMSTableEntry> removed = [];
        HashSet<string> preserveEmptyFolders = new(StringComparer.Ordinal);
        Dictionary<FolderKey, int> folderCounts = [];
        Dictionary<(string Folder, string Key), BMSTableEntry> addedIdentities = [];
        HashSet<BMSTableEntry> addedReferences = [];
        List<(BMSTableEntry Entry, BMSTableEntry Candidate)> added = [];
        int Count(string folder) => folderCounts.TryGetValue(new(folder), out int count) ? count
            : entriesByFolder.TryGetValue(new(folder), out HashSet<BMSTableEntry> members) ? members.Count : 0;
        void Remove(BMSTableEntry entry, bool preserveEmptyFolder = true)
        {
            if (removed.Add(entry)) { folderCounts[new(entry.folder)] = Count(entry.folder) - 1; }
            if (preserveEmptyFolder) { preserveEmptyFolders.Add(entry.folder); }
        }
        foreach (BMSTableEntry entry in removals)
        {
            observer?.OnEntryReferenceRead();
            if (ContainsEntry(entry)) { Remove(entry, preserveEmptyFoldersAfterRemoval); }
        }
        void Add(BMSTableEntry entry, string folder)
        {
            observer?.OnEntryReferenceRead();
            folder ??= string.Empty;
            (string folder, string) identity = (folder, getEntryIdentityKey(entry));
            bool movesExisting = ContainsEntry(entry) && !removed.Contains(entry) && entry.folder != folder;
            if (movesExisting && entriesByIdentity.TryGetValue(identity, out HashSet<BMSTableEntry> destination))
            {
                foreach (BMSTableEntry existing in destination.Where(item => !removed.Contains(item)))
                {
                    if (entryOrder[existing] < entryOrder[entry]) { Remove(entry, preserveEmptyFolder: false); return; }
                    Remove(existing, preserveEmptyFolder: false);
                }
            }
            if (addedReferences.Contains(entry)) { return; }
            if (addedIdentities.ContainsKey(identity)
                || (entriesByIdentity.TryGetValue(identity, out HashSet<BMSTableEntry> matches)
                    && matches.Any(match => !removed.Contains(match)))) { return; }
            if (entry.md5 == BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER && (folder.Length == 0 || Count(folder) > 0)) { return; }
            if (entry.md5 != BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER
                && entriesByIdentity.TryGetValue((folder, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER), out HashSet<BMSTableEntry> dummies))
            {
                foreach (BMSTableEntry dummy in dummies) { Remove(dummy); }
            }
            if (entry.md5 != BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER
                && addedIdentities.Remove((folder, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER), out BMSTableEntry stagedDummy))
            {
                added.RemoveAll(item => ReferenceEquals(item.Entry, stagedDummy));
                addedReferences.Remove(stagedDummy);
                folderCounts[new(folder)] = Count(folder) - 1;
            }
            BMSTableEntry candidate = entry.CreatePlaylistReloadSnapshot();
            observer?.OnCandidateCaptured();
            candidate.parent = this;
            candidate.folder = folder;
            candidate.NormalizeForPlaylistPersistence();
            if (movesExisting) { folderCounts[new(entry.folder)] = Count(entry.folder) - 1; }
            added.Add((entry, candidate));
            addedReferences.Add(entry);
            addedIdentities.Add(identity, entry);
            folderCounts[new(folder)] = Count(folder) + 1;
        }
        foreach ((BMSTableEntry entry, string folder) in additions) { Add(entry, folder); }
        foreach (string folder in preserveEmptyFolders)
        {
            if (!string.IsNullOrWhiteSpace(folder) && Count(folder) == 0) { Add(BMSTableEntry.CreateDummyBMSTableEntry(), folder); }
        }
        ImmutableList<BMSTableEntry> candidateEntries = _entries;
        List<PlaylistEntryChange> changes = [];
        foreach (BMSTableEntry entry in removed.OrderByDescending(entry => entryOrder[entry]))
        {
            candidateEntries = candidateEntries.RemoveAt(FindEntryPosition(entry, observer));
            observer?.OnMembershipChanged();
            changes.Add(new(entry, entry.CaptureValue(), null, entryOrder[entry], null));
        }
        long order = nextEntryOrder;
        foreach ((BMSTableEntry entry, BMSTableEntry candidate) in added)
        {
            if (ContainsEntry(entry) && !removed.Contains(entry))
            {
                changes.Add(new(entry, entry.CaptureValue(), candidate.CaptureValue(), entryOrder[entry], entryOrder[entry]));
            }
            else
            {
                candidateEntries = candidateEntries.Add(entry);
                observer?.OnMembershipChanged();
                changes.Add(new(entry, null, candidate.CaptureValue(), null, order++));
            }
        }
        DateTime lastUpdate = changes.Count == 0 ? last_update : GetNextLastUpdate(last_update);
        return new EntryMutation
        {
            Table = this,
            BaseRevision = PlaylistEntriesRevision,
            Entries = candidateEntries,
            Changes = [.. changes],
            Additions = added,
            LastUpdate = lastUpdate,
            Header = CreatePersistenceHeaderSnapshot(lastUpdate)
        };
    }

    /// <summary>対象フォルダの候補と衝突する移動先候補だけで、名称変更・ルートへの統合を準備します。</summary>
    internal EntryMutation PrepareFolderRename(string before, string after, IPlaylistEntryMutationWorkObserver observer = null)
    {
        string destination = after ?? string.Empty;
        List<BMSTableEntry> members = entriesByFolder.TryGetValue(new(before), out HashSet<BMSTableEntry> folder)
            ? [.. folder.OrderBy(entry => entryOrder[entry])] : [];
        List<BMSTableEntry> removals = [];
        List<(BMSTableEntry Entry, string Folder)> additions = [];
        int destinationCount = before == destination ? 0 : entriesByFolder.TryGetValue(new(destination), out HashSet<BMSTableEntry> destinationMembers)
            ? destinationMembers.Count : 0;
        bool removesDummies = destination.Length == 0 || members.Count + destinationCount > 1;
        if (removesDummies && before != destination
            && entriesByIdentity.TryGetValue((destination, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER), out HashSet<BMSTableEntry> destinationDummies))
        {
            removals.AddRange(destinationDummies);
        }
        foreach (BMSTableEntry entry in members)
        {
            observer?.OnEntryReferenceRead();
            if (removesDummies && entry.md5 == BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER) { removals.Add(entry); }
            else { additions.Add((entry, destination)); }
        }
        EntryMutation mutation = PrepareEntryMutation(removals, additions, observer, preserveEmptyFoldersAfterRemoval: false);
        List<string> order = [.. _Folder_order ?? []];
        if (string.IsNullOrWhiteSpace(after))
        {
            bool hasRootEntries = members.Any(entry => entry.md5 != BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER)
                || entriesByFolder.Any(pair => string.IsNullOrWhiteSpace(pair.Key.Name)
                    && pair.Value.Count > (entriesByIdentity.TryGetValue((pair.Key.Name, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER),
                        out HashSet<BMSTableEntry> dummies) ? dummies.Count : 0));
            if (hasRootEntries && !order.Contains(string.Empty)) { order.Insert(0, string.Empty); }
            order = [.. order.Where(name => name != before)];
        }
        else { order = [.. order.Select(name => name == before ? after : name).Distinct()]; }
        if (order.SequenceEqual(_Folder_order ?? [])) { return mutation; }
        DateTime lastUpdate = mutation.Changes.IsEmpty ? GetNextLastUpdate(last_update) : mutation.LastUpdate;
        BMSTable header = CreatePersistenceHeaderSnapshot(lastUpdate);
        header._Folder_order = order;
        return mutation with { Header = header, LastUpdate = lastUpdate, FolderOrder = [.. order] };
    }

    private void RemoveIndexedEntry(BMSTableEntry entry)
    {
        entryOrder.Remove(entry);
        HashSet<BMSTableEntry> folder = entriesByFolder[new(entry.folder)];
        folder.Remove(entry);
        if (folder.Count == 0) { entriesByFolder.Remove(new(entry.folder)); }
        (string folder, string) identity = (entry.folder, getEntryIdentityKey(entry));
        HashSet<BMSTableEntry> matches = entriesByIdentity[identity];
        matches.Remove(entry);
        if (matches.Count == 0) { entriesByIdentity.Remove(identity); }
        if (!entry.is_removed)
        {
            int count = activeFolderCounts[new(entry.folder)] - 1;
            if (count == 0) { activeFolderCounts.Remove(new(entry.folder)); }
            else { activeFolderCounts[new(entry.folder)] = count; }
        }
    }

    /// <summary>同じ writer 内で保存済み局所候補を採用します。DB 前失敗には live rollback 自体が不要です。</summary>
    internal PlaylistEntryCommitReceipt ApplyEntryMutation(EntryMutation mutation)
    {
        if (mutation.BaseRevision != PlaylistEntriesRevision) { throw new InvalidOperationException("Playlist entry mutation is stale."); }
        foreach (PlaylistEntryChange change in mutation.Changes)
        {
            if (change.After == null) { RemoveIndexedEntry(change.Entry); }
        }
        foreach ((BMSTableEntry entry, BMSTableEntry candidate) in mutation.Additions)
        {
            if (ContainsEntry(entry)) { ApplyEntryCandidate(entry, candidate); continue; }
            entry.parent = this;
            entry.folder = candidate.folder;
            entry.ApplyCommittedEdit(candidate);
            IndexEntry(entry);
        }
        _entries = mutation.Entries;
        _entriesView = _entries;
        last_update = mutation.LastUpdate;
        if (!mutation.FolderOrder.IsDefault) { _Folder_order = [.. mutation.FolderOrder]; }
        InvalidateFolderStateCache();
        return CommitEntryValues(mutation.Changes, folderOrderChanged: !mutation.FolderOrder.IsDefault);
    }

    /// <summary>確定済み差分の排他解放後に、版と必要なフォルダ表示変更だけを通知します。</summary>
    internal void PublishEntryCommitProperties(bool membershipChanged)
    {
        RaisePropertyChanged(nameof(PlaylistEntriesRevision));
        if (membershipChanged)
        {
            RaisePropertyChanged(nameof(Folder_order));
            RaisePropertyChanged(nameof(folder_list));
            RaisePropertyChanged(nameof(FolderNodes));
        }
    }
}
