using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// プレイリスト aggregate の active membership と persistence ordering を所有します。
/// <para>
/// <see cref="BMSPlaylist"/> は dispatcher collection の presentation を保持しますが、
/// durable write、reload reservation、detached table の保存可否はこの owner に集約します。
/// </para>
/// </summary>
internal sealed class PlaylistAggregatePersistenceOwner
{
    private readonly PlaylistPersistenceRepository repository;

    private readonly object synchronization = new();

    private readonly HashSet<BMSTable> activeTables = [];

    private readonly Dictionary<int, BMSTable> activeTablesByPlaylistId = [];

    private readonly HashSet<BMSTable> reloadApplyReservations = [];

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BMSTable, object> reloadRetiredTables = new();

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BMSTable, object> removedTables = new();

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BMSTable, object> knownActiveTables = new();

    private IEnumerable<BMSTable> observedActiveCollection;

    private INotifyCollectionChanged observedActiveCollectionNotifications;

    private bool registrationActive;

    private bool reloadActive;

    private long activeCollectionGeneration;

    private bool hydrationPublishActive;

    internal PlaylistAggregatePersistenceOwner(PlaylistPersistenceRepository repository)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    internal long ActiveCollectionGeneration
    {
        get
        {
            lock (synchronization)
            {
                return activeCollectionGeneration;
            }
        }
    }

    internal PlaylistEntriesHydrationOwner.PlaylistHydrationTableSnapshot GetActiveCollectionSnapshot()
    {
        lock (synchronization)
        {
            return new PlaylistEntriesHydrationOwner.PlaylistHydrationTableSnapshot(
                activeCollectionGeneration,
                activeTables.ToArray());
        }
    }

    internal IDisposable TryBeginHydrationPublish(long generation)
    {
        lock (synchronization)
        {
            if (hydrationPublishActive
                || registrationActive
                || reloadActive
                || activeCollectionGeneration != generation)
            {
                return null;
            }
            hydrationPublishActive = true;
            return new HydrationPublishLease(this);
        }
    }

    internal bool TryBeginRegistration()
    {
        lock (synchronization)
        {
            if (reloadActive || registrationActive || hydrationPublishActive)
            {
                return false;
            }
            registrationActive = true;
            return true;
        }
    }

    internal void EndRegistration()
    {
        lock (synchronization)
        {
            registrationActive = false;
        }
    }

    internal bool TryBeginReload()
    {
        lock (synchronization)
        {
            if (registrationActive || reloadActive || hydrationPublishActive)
            {
                return false;
            }
            reloadActive = true;
            return true;
        }
    }

    internal void EndReload()
    {
        lock (synchronization)
        {
            reloadActive = false;
        }
    }

    internal void SetActiveCollection(IEnumerable<BMSTable> previousTables, IEnumerable<BMSTable> currentTables)
    {
        lock (synchronization)
        {
            bool sameCollection = ReferenceEquals(observedActiveCollection, currentTables);
            if (!sameCollection && observedActiveCollectionNotifications != null)
            {
                observedActiveCollectionNotifications.CollectionChanged -= OnActiveCollectionChanged;
            }

            HashSet<BMSTable> currentSet = [.. (currentTables ?? []).Where(table => table != null)];
            foreach (BMSTable table in activeTables.ToArray())
            {
                if (!currentSet.Contains(table))
                {
                    MarkRemovedUnsafe(table);
                }
            }
            foreach (BMSTable table in currentSet)
            {
                MarkActiveUnsafe(table);
            }

            if (!sameCollection)
            {
                observedActiveCollection = currentTables;
                observedActiveCollectionNotifications = currentTables as INotifyCollectionChanged;
                if (observedActiveCollectionNotifications != null)
                {
                    observedActiveCollectionNotifications.CollectionChanged += OnActiveCollectionChanged;
                }
            }
            activeCollectionGeneration++;
        }
    }

    internal void MarkActiveTables(IEnumerable<BMSTable> tables)
    {
        lock (synchronization)
        {
            foreach (BMSTable table in tables ?? [])
            {
                MarkActiveUnsafe(table);
            }
            activeCollectionGeneration++;
        }
    }

    internal void ReplaceActive(BMSTable oldTable, BMSTable newTable)
    {
        lock (synchronization)
        {
            RemoveActiveUnsafe(oldTable);
            MarkActiveUnsafe(newTable);
            activeCollectionGeneration++;
        }
    }

    internal bool IsActive(BMSTable table)
    {
        if (table == null)
        {
            return false;
        }
        lock (synchronization)
        {
            return activeTables.Contains(table);
        }
    }

    internal BMSTable ResolveOwningTableForEntry(BMSTableEntry entry)
    {
        bool parentRetiredOrRemoved = false;
        if (entry?.parent != null)
        {
            lock (synchronization)
            {
                parentRetiredOrRemoved = IsParentUnavailableUnsafe(entry.parent);
                if (!parentRetiredOrRemoved && activeTables.Contains(entry.parent))
                {
                    return entry.parent;
                }
            }
        }
        if (entry?.playlist_id == null)
        {
            return parentRetiredOrRemoved ? null : entry?.parent;
        }
        lock (synchronization)
        {
            if (activeTablesByPlaylistId.TryGetValue(entry.playlist_id.Value, out BMSTable activeTable))
            {
                return activeTable;
            }
            if (entry.parent != null && !parentRetiredOrRemoved && !IsParentUnavailableUnsafe(entry.parent))
            {
                return entry.parent;
            }
        }
        return null;
    }

    internal void ReplaceTablesWithEntries(
        IEnumerable<BMSTable> tables,
        Action<int, int, string> progressCallback = null,
        bool allowReloadReservation = false,
        bool requireCurrentTarget = true)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null).Distinct()];
        if (tableList.Count == 0)
        {
            return;
        }
        lock (synchronization)
        {
            EnsureWriteAllowedUnsafe(tableList, allowReloadReservation, requireCurrentTarget);
            Dictionary<BMSTable, int?> previousPlaylistIds = CapturePlaylistIds(tableList);
            try
            {
                repository.ReplaceTablesWithEntries(tableList, progressCallback);
                foreach (BMSTable table in tableList)
                {
                    RefreshActiveIdentityUnsafe(table);
                }
            }
            catch
            {
                RestorePlaylistIdsUnsafe(previousPlaylistIds);
                throw;
            }
        }
    }

    internal void ReplaceHeaders(
        IEnumerable<BMSTable> tables,
        bool allowReloadReservation = false,
        bool requireCurrentTarget = true)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null).Distinct()];
        if (tableList.Count == 0)
        {
            return;
        }
        lock (synchronization)
        {
            EnsureWriteAllowedUnsafe(tableList, allowReloadReservation, requireCurrentTarget);
            Dictionary<BMSTable, int?> previousPlaylistIds = CapturePlaylistIds(tableList);
            try
            {
                repository.ReplaceHeaders(tableList);
                foreach (BMSTable table in tableList)
                {
                    RefreshActiveIdentityUnsafe(table);
                }
            }
            catch
            {
                RestorePlaylistIdsUnsafe(previousPlaylistIds);
                throw;
            }
        }
    }

    internal void ReplaceHeader(
        BMSTable table,
        bool allowReloadReservation = false,
        bool requireCurrentTarget = true)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }
        ReplaceHeaders([table], allowReloadReservation, requireCurrentTarget);
    }

    internal bool TryPersistReloadedTable(
        BMSTable oldTable,
        BMSTable newTable,
        int sourceEntriesRevision,
        DateTime sourceLastUpdate,
        string sourceStateFingerprint,
        bool needsEntryPersistence,
        bool needsHeaderPersistence,
        out bool staleSnapshot)
    {
        lock (synchronization)
        {
            staleSnapshot = false;
            if (!reloadApplyReservations.Contains(oldTable))
            {
                return false;
            }
            if (oldTable.PlaylistEntriesRevision != sourceEntriesRevision
                || oldTable.last_update != sourceLastUpdate
                || !string.Equals(
                    CreateReloadSourceFingerprint(oldTable),
                    sourceStateFingerprint,
                    StringComparison.Ordinal))
            {
                staleSnapshot = true;
                return false;
            }
            if (IsRetiredOrRemovedUnsafe(oldTable))
            {
                return false;
            }
            Dictionary<BMSTable, int?> previousPlaylistIds = CapturePlaylistIds([newTable]);
            try
            {
                if (needsEntryPersistence)
                {
                    repository.ReplaceTablesWithEntries([newTable]);
                }
                else if (needsHeaderPersistence)
                {
                    repository.ReplaceHeader(newTable);
                }
            }
            catch
            {
                RestorePlaylistIdsUnsafe(previousPlaylistIds);
                throw;
            }
            RefreshActiveIdentityUnsafe(newTable);
            return true;
        }
    }

    internal static string CreateReloadSourceFingerprint(BMSTable table)
    {
        var builder = new StringBuilder();
        void Append(object value)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append('|');
        }

        Append(table?.playlist_id);
        Append(table?.name);
        Append(table?.symbol);
        Append(table?.folder_sort_key);
        Append(table?.folder_sort_ascending);
        Append(table?.entry_type);
        Append(table?.Page_url?.OriginalString);
        Append(table?.Header_url?.OriginalString);
        Append(table?.Data_url?.OriginalString);
        Append(table?.tag);
        Append(table?.header_sha256);
        Append(table?.data_sha256);
        Append(table?.compat_prefix);
        Append(table?.last_update.Ticks);
        Append(table?.org_name);
        Append(table?.org_symbol);
        Append(table?.ignore_folder_output);
        Append(table?.is_external_sync);
        Append(table?.Output_dir);
        Append(table?.custom_folder_output_base_name);
        Append(table?.is_root_folder);
        Append(table?.bmt_sort);
        Append(table?.is_bmt_output);
        foreach (string folder in table?.Folder_order ?? [])
        {
            Append(folder);
        }
        foreach (LR2SongDBExtended.playlist_course course in table?.Courses ?? [])
        {
            Append(course?.course_order);
            Append(course?.course_json);
        }
        foreach (BMSTableEntry entry in table?.entries ?? [])
        {
            Append(entry?.playlist_id);
            Append(entry?.md5);
            Append(entry?.sha256);
            Append(entry?.level);
            Append(entry?.title);
            Append(entry?.artist);
            Append(entry?.folder);
            Append(entry?.lr2_bmsid);
            Append(entry?.url);
            Append(entry?.url_diff);
            Append(entry?.name_diff);
            Append(entry?.comment);
            Append(entry?.memo);
            Append(entry?.is_removed);
            Append(entry?.adddate.Ticks);
            Append(string.Join("\u001e", entry?.Org_md5 ?? []));
        }
        return builder.ToString();
    }

    internal void DeleteUnpublishedTables(
        IEnumerable<BMSTable> tables,
        IReadOnlyDictionary<BMSTable, int?> originalPlaylistIds = null)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null).Distinct()];
        if (tableList.Count == 0)
        {
            return;
        }
        lock (synchronization)
        {
            foreach (BMSTable table in tableList)
            {
                if (activeTables.Contains(table)
                    || reloadApplyReservations.Contains(table)
                    || reloadRetiredTables.TryGetValue(table, out _)
                    || (table.playlist_id.HasValue
                        && activeTablesByPlaylistId.TryGetValue(table.playlist_id.Value, out BMSTable activeTable)
                        && !ReferenceEquals(activeTable, table)))
                {
                    throw new InvalidOperationException("Published playlist persistence cannot be rolled back as an unpublished registration.");
                }
            }
            repository.DeleteTables(tableList);
            foreach (BMSTable table in tableList)
            {
                table.playlist_id = originalPlaylistIds != null
                    && originalPlaylistIds.TryGetValue(table, out int? originalPlaylistId)
                    ? originalPlaylistId
                    : null;
                RefreshActiveIdentityUnsafe(table);
            }
        }
    }

    internal void LoadPlaylistDump(string sql)
    {
        lock (synchronization)
        {
            repository.LoadPlaylistDump(sql);
        }
    }

    internal void DeleteActiveTables(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null).Distinct()];
        if (tableList.Count == 0)
        {
            return;
        }
        lock (synchronization)
        {
            EnsureRemovalAllowedUnsafe(tableList);
            repository.DeleteTables(tableList);
            foreach (BMSTable table in tableList)
            {
                MarkRemovedUnsafe(table);
            }
            activeCollectionGeneration++;
        }
    }

    internal BMSTable CommitEntry(
        BMSTableEntry entry,
        string editedPropertyName,
        Func<IDisposable> acquireCollectionReadGuard = null)
    {
        if (!entry.playlist_id.HasValue)
        {
            return null;
        }
        bool hasParentReference = entry.parent != null;
        BMSTableEntry persistedEntry = entry;
        bool persisted = false;
        BMSTable owningTable;
        using (acquireCollectionReadGuard?.Invoke())
        {
            owningTable = ResolveOwningTableForEntry(entry);
        }
        bool hadOwningTable = owningTable != null;
        for (int attempt = 0; attempt < 600 && !persisted; attempt++)
        {
            bool retryAfterReload = false;
            using (acquireCollectionReadGuard?.Invoke())
            {
                if (attempt > 0 && hadOwningTable)
                {
                    owningTable = ResolveOwningTableForEntry(entry);
                }
                if (owningTable != null)
                {
                    using (owningTable.ReaderWriterLock.GetWriterGuard())
                    {
                        lock (synchronization)
                        {
                            if (reloadApplyReservations.Contains(owningTable)
                                || IsRetiredOrRemovedUnsafe(owningTable))
                            {
                                retryAfterReload = true;
                            }
                            if (!retryAfterReload)
                            {
                                EnsureWriteAllowedUnsafe([owningTable], allowReloadReservation: false, requireCurrentTarget: false);
                                if (owningTable.entries.Contains(entry))
                                {
                                    if (entry.is_removed)
                                    {
                                        throw new InvalidOperationException("Playlist entry is no longer active in the playlist.");
                                    }
                                    persistedEntry = entry;
                                }
                                else if (activeTables.Contains(owningTable))
                                {
                                    persistedEntry = FindCurrentPlaylistEntryForCommit(owningTable, entry)
                                        ?? throw new InvalidOperationException("Playlist entry is no longer present in the active playlist.");
                                    persistedEntry.ApplyPlaylistEditableStateFrom(entry, editedPropertyName);
                                }
                                else
                                {
                                    persistedEntry = entry;
                                }
                                if (!owningTable.is_external_sync)
                                {
                                    persistedEntry.MaterializeEffectiveUrlsIntoPersistedValues();
                                }
                                persistedEntry.NormalizeForPlaylistPersistence();
                                owningTable.last_update = BMSTable.GetNextLastUpdate(owningTable.last_update);
                                repository.ReplaceEntry(persistedEntry, owningTable);
                                persisted = true;
                            }
                        }
                    }
                }
                else if (hadOwningTable)
                {
                    retryAfterReload = true;
                }
                else
                {
                    lock (synchronization)
                    {
                        if (hasParentReference)
                        {
                            throw new InvalidOperationException("Playlist entry owner is no longer active in the playlist collection.");
                        }
                        if (!repository.HasPlaylistHeader(entry.playlist_id.Value))
                        {
                            throw new InvalidOperationException("Playlist persistence target is no longer present in the database.");
                        }
                        entry.MaterializeEffectiveUrlsIntoPersistedValues();
                        entry.NormalizeForPlaylistPersistence();
                        repository.ReplaceEntry(entry, null);
                        persisted = true;
                    }
                }
            }
            if (retryAfterReload)
            {
                Thread.Sleep(10);
                continue;
            }
        }
        if (!persisted)
        {
            throw new InvalidOperationException("Playlist persistence target changed while the entry was being saved.");
        }
        return owningTable;
    }

    internal bool TryReserveReload(BMSTable oldTable, bool requireCurrentTarget, out bool targetWasActiveAtReservation, out bool alreadyReserved)
    {
        lock (synchronization)
        {
            targetWasActiveAtReservation = !requireCurrentTarget || activeTables.Contains(oldTable);
            alreadyReserved = false;
            if (requireCurrentTarget
                && (!targetWasActiveAtReservation || IsRetiredOrRemovedUnsafe(oldTable)))
            {
                return false;
            }
            if (!reloadApplyReservations.Add(oldTable))
            {
                alreadyReserved = true;
                return false;
            }
            return true;
        }
    }

    internal bool IsReloadReservationActive(BMSTable table)
    {
        lock (synchronization)
        {
            return reloadApplyReservations.Contains(table);
        }
    }

    internal bool IsRetiredOrRemoved(BMSTable table)
    {
        lock (synchronization)
        {
            return IsRetiredOrRemovedUnsafe(table);
        }
    }

    internal void MarkReloadRetired(BMSTable table)
    {
        lock (synchronization)
        {
            reloadRetiredTables.GetValue(table, _ => new object());
        }
    }

    internal void RemoveReloadRetired(BMSTable table)
    {
        lock (synchronization)
        {
            reloadRetiredTables.Remove(table);
        }
    }

    internal void ReleaseReload(BMSTable table)
    {
        lock (synchronization)
        {
            reloadApplyReservations.Remove(table);
        }
    }

    private void EnsureWriteAllowedUnsafe(IEnumerable<BMSTable> tables, bool allowReloadReservation, bool requireCurrentTarget)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null)];
        if (!allowReloadReservation
            && tableList.Any(table => reloadApplyReservations.Contains(table) || IsRetiredOrRemovedUnsafe(table)))
        {
            throw new InvalidOperationException("Playlist reload is applying a newer playlist snapshot.");
        }
        if (requireCurrentTarget && tableList.Any(table => !activeTables.Contains(table)))
        {
            throw new InvalidOperationException("Playlist persistence target is no longer active.");
        }
    }

    private void EnsureRemovalAllowedUnsafe(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null)];
        if (hydrationPublishActive
            || tableList.Any(table => reloadApplyReservations.Contains(table) || reloadRetiredTables.TryGetValue(table, out _)))
        {
            throw new InvalidOperationException("Playlist reload is applying a newer playlist snapshot.");
        }
        foreach (BMSTable table in tableList)
        {
            if (!activeTables.Contains(table))
            {
                throw new InvalidOperationException("Playlist persistence target is no longer active.");
            }
            if (table.playlist_id.HasValue
                && activeTablesByPlaylistId.TryGetValue(table.playlist_id.Value, out BMSTable activeTable)
                && !ReferenceEquals(activeTable, table))
            {
                throw new InvalidOperationException("Playlist persistence target was replaced while it was being removed.");
            }
        }
    }

    private bool IsParentUnavailableUnsafe(BMSTable table)
    {
        return reloadApplyReservations.Contains(table)
            || reloadRetiredTables.TryGetValue(table, out _)
            || removedTables.TryGetValue(table, out _)
            || knownActiveTables.TryGetValue(table, out _);
    }

    private bool IsRetiredOrRemovedUnsafe(BMSTable table)
    {
        return reloadRetiredTables.TryGetValue(table, out _)
            || removedTables.TryGetValue(table, out _);
    }

    private void MarkActiveUnsafe(BMSTable table)
    {
        if (table == null)
        {
            return;
        }
        activeTables.Add(table);
        removedTables.Remove(table);
        reloadRetiredTables.Remove(table);
        if (table.playlist_id.HasValue)
        {
            activeTablesByPlaylistId[table.playlist_id.Value] = table;
        }
        knownActiveTables.GetValue(table, _ => new object());
    }

    private void RefreshActiveIdentityUnsafe(BMSTable table)
    {
        if (table == null || !activeTables.Contains(table))
        {
            return;
        }
        foreach (KeyValuePair<int, BMSTable> pair in activeTablesByPlaylistId.ToArray())
        {
            if (ReferenceEquals(pair.Value, table))
            {
                activeTablesByPlaylistId.Remove(pair.Key);
            }
        }
        if (table.playlist_id.HasValue)
        {
            activeTablesByPlaylistId[table.playlist_id.Value] = table;
        }
    }

    private static Dictionary<BMSTable, int?> CapturePlaylistIds(IEnumerable<BMSTable> tables)
    {
        return (tables ?? [])
            .Where(table => table != null)
            .Distinct()
            .ToDictionary(table => table, table => table.playlist_id);
    }

    private void RestorePlaylistIdsUnsafe(Dictionary<BMSTable, int?> previousPlaylistIds)
    {
        foreach (KeyValuePair<BMSTable, int?> pair in previousPlaylistIds ?? [])
        {
            pair.Key.playlist_id = pair.Value;
            RefreshActiveIdentityUnsafe(pair.Key);
        }
    }

    private void RemoveActiveUnsafe(BMSTable table)
    {
        if (table == null)
        {
            return;
        }
        activeTables.Remove(table);
        if (table.playlist_id.HasValue
            && activeTablesByPlaylistId.TryGetValue(table.playlist_id.Value, out BMSTable activeTable)
            && ReferenceEquals(activeTable, table))
        {
            activeTablesByPlaylistId.Remove(table.playlist_id.Value);
        }
    }

    private void MarkRemovedUnsafe(BMSTable table)
    {
        if (table == null)
        {
            return;
        }
        RemoveActiveUnsafe(table);
        removedTables.GetValue(table, _ => new object());
    }

    private void EndHydrationPublish()
    {
        lock (synchronization)
        {
            hydrationPublishActive = false;
        }
    }

    private sealed class HydrationPublishLease : IDisposable
    {
        private readonly PlaylistAggregatePersistenceOwner owner;

        private int disposed;

        internal HydrationPublishLease(PlaylistAggregatePersistenceOwner owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.EndHydrationPublish();
            }
        }
    }

    private void OnActiveCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        lock (synchronization)
        {
            if (!ReferenceEquals(sender, observedActiveCollectionNotifications))
            {
                return;
            }
            activeCollectionGeneration++;
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                HashSet<BMSTable> currentSet = [.. (observedActiveCollection ?? []).Where(table => table != null)];
                foreach (BMSTable table in activeTables.ToArray())
                {
                    if (!currentSet.Contains(table))
                    {
                        MarkRemovedUnsafe(table);
                    }
                }
                foreach (BMSTable table in currentSet)
                {
                    MarkActiveUnsafe(table);
                }
                return;
            }

            foreach (BMSTable table in e.OldItems?.OfType<BMSTable>() ?? [])
            {
                if (!(observedActiveCollection ?? []).Contains(table))
                {
                    MarkRemovedUnsafe(table);
                }
            }
            foreach (BMSTable table in e.NewItems?.OfType<BMSTable>() ?? [])
            {
                MarkActiveUnsafe(table);
            }
        }
    }

    private static BMSTableEntry FindCurrentPlaylistEntryForCommit(BMSTable table, BMSTableEntry source)
    {
        if (table?.entries == null || source == null)
        {
            return null;
        }
        if (table.entries.Any(candidate => ReferenceEquals(candidate, source) && !candidate.is_removed))
        {
            return source;
        }
        List<BMSTableEntry> matches = [.. table.entries.Where(candidate =>
            candidate != null
            && !candidate.is_removed
            && PlaylistEntryIdentityMatches(candidate, source))];
        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool PlaylistEntryIdentityMatches(BMSTableEntry candidate, BMSTableEntry source)
    {
        bool sameHash = !string.IsNullOrWhiteSpace(source.md5)
            ? string.Equals(candidate.md5, source.md5, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(source.sha256)
                    && string.Equals(candidate.sha256, source.sha256, StringComparison.OrdinalIgnoreCase))
            : !string.IsNullOrWhiteSpace(source.sha256)
                && string.Equals(candidate.sha256, source.sha256, StringComparison.OrdinalIgnoreCase);
        bool sameFolder = string.Equals(candidate.folder, source.folder, StringComparison.Ordinal);
        bool sameLr2BmsId = string.Equals(candidate.lr2_bmsid, source.lr2_bmsid, StringComparison.Ordinal);
        bool sameTitle = string.Equals(candidate.title, source.title, StringComparison.Ordinal);
        return sameHash
            && (sameFolder || sameLr2BmsId || sameTitle);
    }
}
