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
            FolderOrder: folderOrderChanged ? (_Folder_order ?? []).ToImmutableArray() : default);
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
        internal PlaylistHeaderEdit HeaderEdit { get; init; }
        internal ImmutableArray<string> FolderOrder { get; init; }
        internal bool HasChanges => !Changes.IsEmpty || !FolderOrder.IsDefault || HeaderEdit != null && HeaderEdit.Fields != PlaylistHeaderFields.None;
    }

    /// <summary>headerとprefixの候補を現在値から作ります。prefixは同時変換し、所属参照・順序・更新日時を保持します。</summary>
    internal EntryMutation PrepareHeaderMutation(PlaylistHeaderEdit edit, IPlaylistEntryMutationWorkObserver observer = null)
    {
        BMSTable header = CreatePersistenceHeaderSnapshot(last_update);
        header.ApplyHeaderFields(edit);
        IReadOnlyDictionary<string, string> map = header.CreateValidatedCompatibleFolderPrefixRewriteMap(compat_prefix, header.compat_prefix);
        List<(BMSTableEntry Entry, BMSTableEntry Candidate)> additions = [];
        List<PlaylistEntryChange> changes = [];
        foreach ((string folder, string rewritten) in map)
        {
            if (!entriesByFolder.TryGetValue(new(folder), out HashSet<BMSTableEntry> members)) { continue; }
            foreach (BMSTableEntry entry in members.OrderBy(entry => entryOrder[entry]))
            {
                observer?.OnEntryReferenceRead();
                BMSTableEntry candidate = entry.CreatePlaylistReloadSnapshot();
                observer?.OnCandidateCaptured();
                candidate.parent = this;
                candidate.folder = rewritten;
                additions.Add((entry, candidate));
                changes.Add(new(entry, entry.CaptureValue(), candidate.CaptureValue(), entryOrder[entry], entryOrder[entry]));
            }
        }
        if (map.Count > 0)
        {
            header._Folder_order = (header._Folder_order ?? []).Select(folder =>
                map.TryGetValue(folder ?? string.Empty, out string rewritten) ? rewritten : folder).Distinct(StringComparer.Ordinal).ToImmutableArray();
        }
        return new EntryMutation
        {
            Table = this,
            BaseRevision = PlaylistEntriesRevision,
            Entries = _entries,
            Changes = [.. changes],
            Additions = additions,
            LastUpdate = last_update,
            Header = header,
            HeaderEdit = edit,
            FolderOrder = (_Folder_order ?? []).SequenceEqual(header._Folder_order ?? [], StringComparer.Ordinal) ? default : (header._Folder_order ?? []).ToImmutableArray()
        };
    }

    /// <summary>編集対象のheader属性だけを無通知で設定します。候補準備とDB確定後の同writer内で使用します。</summary>
    /// <param name="edit">同じwriter内の現行値から準備した編集内容。</param>
    /// <param name="applyFolderOrder">候補準備では順序を所有します。確定時はfalseとし、完成済み候補の不変順序を共有します。</param>
    internal void ApplyHeaderFields(PlaylistHeaderEdit edit, bool applyFolderOrder = true)
    {
        bool Has(PlaylistHeaderFields field) => (edit.Fields & field) != 0;
        ApplyCommittedPlaylistNames(Has(PlaylistHeaderFields.Name) ? edit.Name : name,
            Has(PlaylistHeaderFields.Symbol) ? edit.Symbol : symbol);
        if (applyFolderOrder && Has(PlaylistHeaderFields.FolderOrder)) { _Folder_order = edit.FolderOrder; }
        if (Has(PlaylistHeaderFields.FolderSortKey)) { folder_sort_key = edit.FolderSortKey; }
        if (Has(PlaylistHeaderFields.FolderSortAscending)) { folder_sort_ascending = edit.FolderSortAscending; }
        if (Has(PlaylistHeaderFields.IgnoreFolderOutput)) { ignore_folder_output = edit.IgnoreFolderOutput; }
        if (Has(PlaylistHeaderFields.EntryType)) { entry_type = edit.EntryType; }
        if (Has(PlaylistHeaderFields.PageUrl)) { Page_url = edit.PageUrl; }
        if (Has(PlaylistHeaderFields.HeaderUrl)) { Header_url = edit.HeaderUrl; }
        if (Has(PlaylistHeaderFields.DataUrl)) { Data_url = edit.DataUrl; }
        if (Has(PlaylistHeaderFields.IsExternalSync)) { if (edit.IsExternalSync) { EnableExternalSync(); } else { DisableExternalSync(); } }
        if (Has(PlaylistHeaderFields.OutputDirectory)) { Output_dir = edit.OutputDirectory; }
        else if (Has(PlaylistHeaderFields.Name)) { Output_dir = output_dir; }
        if (Has(PlaylistHeaderFields.CustomFolderOutputBaseName)) { custom_folder_output_base_name = edit.CustomFolderOutputBaseName; }
        if (Has(PlaylistHeaderFields.IsRootFolder)) { is_root_folder = edit.IsRootFolder; }
        if (Has(PlaylistHeaderFields.CompatPrefix)) { compat_prefix = edit.CompatPrefix; }
    }

    /// <summary>DB確定後のheader通知をguard解放後に試みます。失敗しても後続通知を試み、確定値は戻しません。</summary>
    internal void PublishHeaderCommitProperties(PlaylistHeaderEdit edit, PlaylistEntryCommitReceipt receipt)
    {
        List<Exception> failures = [];
        void Publish(string property) { try { RaisePropertyChanged(property); } catch (Exception failure) { failures.Add(failure); } }
        if ((edit.Fields & PlaylistHeaderFields.Name) != 0) { Publish(nameof(name)); }
        if ((edit.Fields & PlaylistHeaderFields.Symbol) != 0) { Publish(nameof(symbol)); }
        if (receipt.Revision != receipt.BaseRevision) { Publish(nameof(PlaylistEntriesRevision)); }
        if (!receipt.FolderOrder.IsDefault || receipt.Changes.Any(change => change.Before?.Folder != change.After?.Folder))
        { Publish(nameof(Folder_order)); Publish(nameof(folder_list)); Publish(nameof(FolderNodes)); }
        if (failures.Count == 1) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures.Count > 1) { throw new AggregateException(failures); }
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

    /// <summary>削除とフォルダ別追加の候補を writer 内で準備します。明示追加は同じ所属キーの削除履歴を今回入力へ置換し、生存重複の既存値を優先します。空フォルダ保持の対象には空白名も含み、live 値・所属・日時・版は変更しません。</summary>
    /// <param name="preserveEmptyFoldersAfterRemoval">削除・移動元の非空フォルダを空になっても保持するか。改名・統合では元フォルダを残さないためfalseとします。</param>
    /// <param name="replaceRemovedDestinations">明示追加ではtrue。フォルダ改名・統合だけfalseとし、既存の順序による衝突規則を維持します。</param>
    internal EntryMutation PrepareEntryMutation(IReadOnlyCollection<BMSTableEntry> removals,
        IReadOnlyList<(BMSTableEntry Entry, string Folder)> additions,
        IPlaylistEntryMutationWorkObserver observer = null, bool preserveEmptyFoldersAfterRemoval = true,
        bool replaceRemovedDestinations = true)
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
            if (replaceRemovedDestinations && entriesByIdentity.TryGetValue(identity, out HashSet<BMSTableEntry> histories))
            {
                // SQLの正確削除には入力候補でなく、履歴自身の保存列を渡します。
                foreach (BMSTableEntry history in histories.Where(item => item.is_removed)) { Remove(history, preserveEmptyFolder: false); }
                if (movesExisting && histories.Any(item => !item.is_removed && !removed.Contains(item)))
                {
                    Remove(entry, preserveEmptyFolder: false);
                    return;
                }
            }
            if (!replaceRemovedDestinations && movesExisting && entriesByIdentity.TryGetValue(identity, out HashSet<BMSTableEntry> destination))
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
            if (replaceRemovedDestinations) { candidate.is_removed = false; }
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
            if (!string.IsNullOrEmpty(folder) && Count(folder) == 0) { Add(BMSTableEntry.CreateDummyBMSTableEntry(), folder); }
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

    /// <summary>対象フォルダの候補と衝突する移動先候補だけで名称変更・空文字所属への統合を準備し、空白名を含む非空名への改名は元の明示順序を保持します。</summary>
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
        EntryMutation mutation = PrepareEntryMutation(removals, additions, observer,
            preserveEmptyFoldersAfterRemoval: false, replaceRemovedDestinations: false);
        List<string> order = [.. _Folder_order ?? []];
        if (string.IsNullOrEmpty(after))
        {
            bool hasRootEntries = members.Any(entry => entry.md5 != BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER)
                || entriesByFolder.Any(pair => string.IsNullOrEmpty(pair.Key.Name)
                    && pair.Value.Count > (entriesByIdentity.TryGetValue((pair.Key.Name, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER),
                        out HashSet<BMSTableEntry> dummies) ? dummies.Count : 0));
            if (hasRootEntries && !order.Contains(string.Empty)) { order.Insert(0, string.Empty); }
            order = [.. order.Where(name => name != before)];
        }
        else { order = [.. order.Select(name => name == before ? after : name).Distinct()]; }
        if (order.SequenceEqual(_Folder_order ?? [])) { return mutation; }
        DateTime lastUpdate = mutation.Changes.IsEmpty ? GetNextLastUpdate(last_update) : mutation.LastUpdate;
        BMSTable header = CreatePersistenceHeaderSnapshot(lastUpdate);
        var frozenOrder = order.ToImmutableArray();
        header._Folder_order = frozenOrder;
        return mutation with { Header = header, LastUpdate = lastUpdate, FolderOrder = frozenOrder };
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
            entry.is_removed = candidate.is_removed;
            entry.ApplyCommittedEdit(candidate);
            IndexEntry(entry);
        }
        _entries = mutation.Entries;
        _entriesView = _entries;
        if (mutation.HeaderEdit != null) { ApplyHeaderFields(mutation.HeaderEdit, applyFolderOrder: false); }
        if (!playlist_id.HasValue) { playlist_id = mutation.Header.playlist_id; bmt_sort = mutation.Header.bmt_sort; }
        last_update = mutation.LastUpdate;
        if (!mutation.FolderOrder.IsDefault) { _Folder_order = mutation.Header._Folder_order; }
        if (!mutation.FolderOrder.IsDefault || mutation.Changes.Any(change =>
            change.BeforeOrder.HasValue != change.AfterOrder.HasValue
            || change.Before?.Folder != change.After?.Folder
            || change.Before?.IsRemoved != change.After?.IsRemoved))
        {
            RebuildFolderState(publishNotifications: false);
        }
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
