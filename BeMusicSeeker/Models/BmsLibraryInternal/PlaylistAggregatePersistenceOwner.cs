using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Livet;

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
    private const int PlaylistDiffSampleLogCount = 3;

    internal sealed class ComparablePlaylistEntryRow
    {
        public string Md5 { get; set; }

        public string Sha256 { get; set; }

        public string Level { get; set; }

        public string Title { get; set; }

        public string Artist { get; set; }

        public string Lr2BmsId { get; set; }

        public string Url { get; set; }

        public string UrlDiff { get; set; }

        public string NameDiff { get; set; }

        public string Comment { get; set; }

        public string Fingerprint { get; set; }
    }

    internal sealed class PlaylistContentDiffResult
    {
        public bool HasChanges { get; set; }

        public int PersistedOnlyCount { get; set; }

        public int ReloadedOnlyCount { get; set; }

        public IReadOnlyList<string> PersistedOnlySamples { get; set; }

        public IReadOnlyList<string> ReloadedOnlySamples { get; set; }
    }

    private sealed class PlaylistHashChangeResult
    {
        public bool HeaderKnownChanged { get; set; }

        public bool HeaderHashInitialized { get; set; }

        public bool HeaderHashMigrated { get; set; }

        public bool DataKnownChanged { get; set; }

        public bool DataHashInitialized { get; set; }
    }

    internal sealed class PlaylistReloadPersistenceDecision
    {
        public bool EntryFingerprintChanged { get; internal set; }

        public bool HeaderKnownChanged { get; internal set; }

        public bool HeaderHashInitialized { get; internal set; }

        public bool HeaderHashMigrated { get; internal set; }

        public bool DataKnownChanged { get; internal set; }

        public bool DataHashInitialized { get; internal set; }

        public bool UpdatesLastUpdate => HeaderKnownChanged || DataKnownChanged;

        public bool NeedsHeaderPersistence => HeaderKnownChanged || HeaderHashInitialized || HeaderHashMigrated || DataKnownChanged || DataHashInitialized;

        public bool NeedsEntryPersistence => EntryFingerprintChanged || DataKnownChanged || DataHashInitialized;

        public bool NeedsStatePersistence => NeedsHeaderPersistence || NeedsEntryPersistence;

        public bool NeedsBmtExport => NeedsStatePersistence;
    }

    [Serializable]
    internal sealed class PlaylistReloadApplyException : InvalidOperationException
    {
        internal PlaylistReloadApplyException()
            : base("Playlist reload result could not be applied to the active table.")
        {
        }

        internal PlaylistReloadApplyException(string message)
            : base(message)
        {
        }

        internal PlaylistReloadApplyException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        private PlaylistReloadApplyException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
        }
    }

    private readonly PlaylistPersistenceRepository repository;

    private readonly ReaderWriterLockSlimWrapper activeCollectionLock;

    private readonly object synchronization = new();

    private readonly HashSet<BMSTable> activeTables = [];

    private readonly List<BMSTable> activeTableOrder = [];

    private readonly Dictionary<int, BMSTable> activeTablesByPlaylistId = [];

    private readonly HashSet<BMSTable> reloadApplyReservations = [];

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BMSTable, object> reloadRetiredTables = new();

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BMSTable, object> removedTables = new();

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BMSTable, object> knownActiveTables = new();

    private IEnumerable<BMSTable> observedActiveCollection;

    private DispatcherCollection<BMSTable> activeTableCollection;

    private PlaylistEntriesHydrationOwner entriesHydrationOwner;

    private INotifyCollectionChanged observedActiveCollectionNotifications;

    private bool registrationActive;

    private bool reloadActive;

    private long activeCollectionGeneration;

    private bool hydrationPublishActive;

    internal PlaylistAggregatePersistenceOwner(
        PlaylistPersistenceRepository repository,
        ReaderWriterLockSlimWrapper activeCollectionLock)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.activeCollectionLock = activeCollectionLock ?? throw new ArgumentNullException(nameof(activeCollectionLock));
    }

    internal void AttachEntriesHydrationOwner(PlaylistEntriesHydrationOwner owner)
    {
        if (owner == null)
        {
            throw new ArgumentNullException(nameof(owner));
        }
        lock (synchronization)
        {
            if (entriesHydrationOwner != null)
            {
                throw new InvalidOperationException("Playlist entries hydration owner is already attached.");
            }
            entriesHydrationOwner = owner;
        }
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
                activeTableOrder.ToArray());
        }
    }

    internal IDisposable TryBeginHydrationPublish(long generation)
    {
        IDisposable collectionPublicationGuard = activeCollectionLock.GetWriterGuard();
        lock (synchronization)
        {
            if (hydrationPublishActive
                || registrationActive
                || reloadActive
                || reloadApplyReservations.Count > 0
                || activeCollectionGeneration != generation)
            {
                collectionPublicationGuard.Dispose();
                return null;
            }
            hydrationPublishActive = true;
            return new HydrationPublishLease(this, collectionPublicationGuard);
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

            SynchronizeActiveCollectionUnsafe(currentTables);

            if (!sameCollection)
            {
                observedActiveCollection = currentTables;
                activeTableCollection = currentTables as DispatcherCollection<BMSTable>;
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

    internal void CommitTablesWithEntries(
        IEnumerable<BMSTable> tables,
        Action<int, int, string> progressCallback = null,
        bool allowReloadReservation = false,
        bool requireCurrentTarget = true,
        string hydrationReason = "PlaylistPersistence")
    {
        ExecuteTablesWithEntriesCommit(
            tables,
            progressCallback,
            allowReloadReservation,
            requireCurrentTarget,
            hydrationReason,
            ensureEntriesLoaded: true);
    }

    internal void ReplaceTablesWithEntries(
        IEnumerable<BMSTable> tables,
        Action<int, int, string> progressCallback = null,
        bool allowReloadReservation = false,
        bool requireCurrentTarget = true)
    {
        ExecuteTablesWithEntriesCommit(
            tables,
            progressCallback,
            allowReloadReservation,
            requireCurrentTarget,
            hydrationReason: null,
            ensureEntriesLoaded: false);
    }

    private void ExecuteTablesWithEntriesCommit(
        IEnumerable<BMSTable> tables,
        Action<int, int, string> progressCallback,
        bool allowReloadReservation,
        bool requireCurrentTarget,
        string hydrationReason,
        bool ensureEntriesLoaded)
    {
        List<BMSTable> tableList = [.. (tables ?? [])
            .Where(table => table != null)
            .Distinct()
            .OrderBy(table => table.playlist_id ?? int.MaxValue)
            .ThenBy(table => table.name ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode)];
        if (tableList.Count == 0)
        {
            return;
        }

        IDisposable collectionReadGuard = null;
        bool hasCollectionReadLock = activeCollectionLock.IsReadLockHeld;
        try
        {
            if (requireCurrentTarget && !hasCollectionReadLock)
            {
                collectionReadGuard = activeCollectionLock.GetReaderGuard();
                hasCollectionReadLock = true;
            }

            if (ensureEntriesLoaded)
            {
                if (entriesHydrationOwner == null)
                {
                    throw new InvalidOperationException("Playlist entries hydration owner is not attached.");
                }
                foreach (BMSTable table in tableList)
                {
                    entriesHydrationOwner.EnsurePlaylistEntriesLoaded(
                        table,
                        hydrationReason ?? "PlaylistPersistence");
                }
            }

            List<IDisposable> writerGuards = [];
            try
            {
                foreach (BMSTable table in tableList)
                {
                    writerGuards.Add(table.ReaderWriterLock.GetWriterGuard());
                }

                lock (synchronization)
                {
                    EnsureWriteAllowedUnsafe(tableList, allowReloadReservation, requireCurrentTarget);
                    PersistTablesWithEntriesUnsafe(tableList, progressCallback);
                }
            }
            finally
            {
                for (int index = writerGuards.Count - 1; index >= 0; index--)
                {
                    writerGuards[index]?.Dispose();
                }
            }
        }
        finally
        {
            collectionReadGuard?.Dispose();
        }
    }

    private void PersistTablesWithEntriesUnsafe(
        IReadOnlyList<BMSTable> tableList,
        Action<int, int, string> progressCallback)
    {
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

    private bool TryPersistReloadedTable(
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

    internal IReadOnlyList<BMSTableEntry> LoadPersistedActivePlaylistEntries(int? playlistId)
    {
        return repository.LoadPersistedPlaylistEntries(playlistId, activeOnly: true);
    }

    internal bool TryApplyReloadedTable(
        BMSTable oldTable,
        BMSTable newTable,
        PlaylistReloadPersistenceDecision persistenceDecision,
        int sourceEntriesRevision,
        DateTime sourceLastUpdate,
        string sourceStateFingerprint,
        bool requireCurrentTargetForApply)
    {
        if (!TryReserveReload(
            oldTable,
            requireCurrentTargetForApply,
            out bool targetWasActiveAtReservation,
            out bool alreadyReserved))
        {
            if (alreadyReserved)
            {
                throw new PlaylistReloadApplyException(
                    "Another playlist reload is already applying for this target.");
            }
            return false;
        }

        try
        {
            using (oldTable.ReaderWriterLock.GetReaderGuard())
            {
                if (requireCurrentTargetForApply
                    && (!targetWasActiveAtReservation
                        || IsRetiredOrRemoved(oldTable)))
                {
                    return false;
                }
                bool persisted = TryPersistReloadedTable(
                    oldTable,
                    newTable,
                    sourceEntriesRevision,
                    sourceLastUpdate,
                    sourceStateFingerprint,
                    persistenceDecision?.NeedsEntryPersistence == true,
                    persistenceDecision?.NeedsHeaderPersistence == true,
                    out bool staleSnapshot);
                if (staleSnapshot)
                {
                    throw new PlaylistReloadApplyException(
                        "Playlist reload result was based on a stale local playlist snapshot.");
                }
                if (!persisted)
                {
                    return false;
                }
            }

            if (persistenceDecision?.NeedsStatePersistence == true)
            {
                bool replacementCompleted;
                bool replacementApplied;
                try
                {
                    bool replacementBlocked = requireCurrentTargetForApply
                        && IsRetiredOrRemoved(oldTable);
                    if (replacementBlocked)
                    {
                        replacementCompleted = true;
                        replacementApplied = false;
                    }
                    else
                    {
                        // Retire the old object before crossing the dispatcher boundary.
                        // Entry commits then resolve to the replacement (or fail closed if
                        // the replacement is removed) during the small UI-queue window.
                        MarkReloadRetired(oldTable);
                        replacementApplied = ReplaceActiveTableInCollection(oldTable, newTable, out replacementCompleted);
                    }
                }
                catch (Exception ex)
                {
                    if (IsActive(oldTable))
                    {
                        RemoveReloadRetired(oldTable);
                    }
                    ReconcileUnappliedReload(oldTable, newTable);
                    throw new PlaylistReloadApplyException(
                        "Playlist table replacement failed.",
                        ex);
                }
                if (!replacementCompleted || !replacementApplied)
                {
                    if (IsActive(oldTable))
                    {
                        RemoveReloadRetired(oldTable);
                    }
                    ReconcileUnappliedReload(oldTable, newTable);
                    if (requireCurrentTargetForApply)
                    {
                        throw new PlaylistReloadApplyException();
                    }
                    return false;
                }
            }
            else if (requireCurrentTargetForApply && !IsActive(oldTable))
            {
                ReconcileUnappliedReload(oldTable, newTable);
                return false;
            }
            return true;
        }
        finally
        {
            ReleaseReload(oldTable);
        }
    }

    private void ReconcileUnappliedReload(BMSTable oldTable, BMSTable newTable)
    {
        BMSTable tableToCommit = null;
        bool deleteNewTable = false;
        using (activeCollectionLock.GetReaderGuard())
        {
            DispatcherCollection<BMSTable> tables = activeTableCollection;
            bool oldTableIsActive = tables?.Contains(oldTable) == true;
            BMSTable replacementTable = !oldTableIsActive && newTable?.playlist_id.HasValue == true
                ? tables?.FirstOrDefault(table => table?.playlist_id == newTable.playlist_id.Value)
                : null;
            if (oldTableIsActive)
            {
                tableToCommit = oldTable;
            }
            else
            {
                tableToCommit = replacementTable;
                deleteNewTable = tableToCommit == null && newTable?.playlist_id.HasValue == true;
            }
            if (tableToCommit != null)
            {
                entriesHydrationOwner.EnsurePlaylistEntriesLoaded(tableToCommit, "PlaylistReloadReconciliation");
                ReplaceTablesWithEntries(
                    [tableToCommit],
                    allowReloadReservation: true,
                    requireCurrentTarget: true);
            }
        }
        if (tableToCommit == null && deleteNewTable)
        {
            using (activeCollectionLock.GetReaderGuard())
            {
                DispatcherCollection<BMSTable> tables = activeTableCollection;
                bool replacementIsActive = newTable?.playlist_id.HasValue == true
                    && tables?.Any(table => table?.playlist_id == newTable.playlist_id.Value) == true;
                if (!replacementIsActive)
                {
                    DeleteUnpublishedTables([newTable]);
                }
            }
        }
    }

    private bool ReplaceActiveTableInCollection(BMSTable oldTable, BMSTable newTable, out bool replacementCompleted)
    {
        replacementCompleted = false;
        DispatcherCollection<BMSTable> tables = activeTableCollection;
        if (tables == null)
        {
            return false;
        }

        bool replaced = false;
        object replacementGate = new();
        int replacementAllowed = 1;
        void ReplaceCore()
        {
            int index = tables.IndexOf(oldTable);
            if (index >= 0)
            {
                try
                {
                    tables[index] = newTable;
                    replaced = true;
                }
                finally
                {
                    if (index < tables.Count && ReferenceEquals(tables[index], newTable))
                    {
                        ReplaceActive(oldTable, newTable);
                    }
                }
            }
        }

        Dispatcher dispatcher = GetActiveTableDispatcher(tables);
        if (dispatcher == null && Application.Current == null && tables.Dispatcher == null)
        {
            tables.Dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher = tables.Dispatcher;
        }
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            try
            {
                lock (replacementGate)
                {
                    if (replacementAllowed != 0)
                    {
                        using (activeCollectionLock.GetWriterGuard())
                        {
                            ReplaceCore();
                        }
                    }
                }
                replacementCompleted = true;
                return replaced;
            }
            catch (Exception ex)
            {
                replacementCompleted = true;
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "playlist_table_replace_direct_failed");
                return false;
            }
        }

        System.Windows.Threading.DispatcherOperation operation;
        try
        {
            operation = dispatcher.BeginInvoke((Action)delegate
            {
                lock (replacementGate)
                {
                    if (replacementAllowed != 0)
                    {
                        using (activeCollectionLock.GetWriterGuard())
                        {
                            ReplaceCore();
                        }
                    }
                }
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
        {
            replacementCompleted = true;
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "playlist_table_replace_dispatch_enqueue_failed");
            return false;
        }
        if (Application.Current == null)
        {
            lock (replacementGate)
            {
                replacementAllowed = 0;
                try
                {
                    operation.Abort();
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
                {
                }
            }
            replacementCompleted = true;
            return replaced;
        }
        try
        {
            operation.Wait(TimeSpan.FromSeconds(5));
            if (operation.Status == DispatcherOperationStatus.Completed)
            {
                replacementCompleted = true;
                return replaced;
            }
            lock (replacementGate)
            {
                replacementAllowed = 0;
                try
                {
                    operation.Abort();
                }
                catch (InvalidOperationException)
                {
                }
            }
            if (!replaced)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn("playlist_table_replace_dispatch_wait_incomplete status=" + operation.Status);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ThreadInterruptedException)
        {
            lock (replacementGate)
            {
                replacementAllowed = 0;
                try
                {
                    operation.Abort();
                }
                catch (InvalidOperationException)
                {
                }
            }
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "playlist_table_replace_dispatch_wait_failed");
        }
        replacementCompleted = true;
        return replaced;
    }

    private static Dispatcher GetActiveTableDispatcher(DispatcherCollection<BMSTable> tables)
    {
        if (tables == null)
        {
            return null;
        }
        if (Application.Current == null)
        {
            Dispatcher currentDispatcher = Dispatcher.CurrentDispatcher;
            if (tables.Dispatcher == null || !tables.Dispatcher.CheckAccess())
            {
                tables.Dispatcher = currentDispatcher;
            }
            return currentDispatcher;
        }
        return tables.Dispatcher ?? DispatcherHelper.UIDispatcher ?? Application.Current?.Dispatcher;
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

    private bool TryReserveReload(BMSTable oldTable, bool requireCurrentTarget, out bool targetWasActiveAtReservation, out bool alreadyReserved)
    {
        while (true)
        {
            lock (synchronization)
            {
                if (hydrationPublishActive)
                {
                    Monitor.Wait(synchronization, 50);
                    continue;
                }
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
    }

    private bool IsRetiredOrRemoved(BMSTable table)
    {
        lock (synchronization)
        {
            return IsRetiredOrRemovedUnsafe(table);
        }
    }

    private void MarkReloadRetired(BMSTable table)
    {
        lock (synchronization)
        {
            reloadRetiredTables.GetValue(table, _ => new object());
        }
    }

    private void RemoveReloadRetired(BMSTable table)
    {
        lock (synchronization)
        {
            reloadRetiredTables.Remove(table);
        }
    }

    private void ReleaseReload(BMSTable table)
    {
        lock (synchronization)
        {
            reloadApplyReservations.Remove(table);
        }
    }

    private void EnsureWriteAllowedUnsafe(IEnumerable<BMSTable> tables, bool allowReloadReservation, bool requireCurrentTarget)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null)];
        if (hydrationPublishActive)
        {
            throw new InvalidOperationException("Playlist hydration receipt publication is in progress.");
        }
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
        if (!activeTableOrder.Contains(table))
        {
            activeTableOrder.Add(table);
        }
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
        activeTableOrder.Remove(table);
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

    private void EndHydrationPublish(IDisposable collectionPublicationGuard)
    {
        lock (synchronization)
        {
            hydrationPublishActive = false;
            Monitor.PulseAll(synchronization);
        }
        collectionPublicationGuard?.Dispose();
    }

    private sealed class HydrationPublishLease : IDisposable
    {
        private readonly PlaylistAggregatePersistenceOwner owner;

        private readonly IDisposable collectionPublicationGuard;

        private int disposed;

        internal HydrationPublishLease(
            PlaylistAggregatePersistenceOwner owner,
            IDisposable collectionPublicationGuard)
        {
            this.owner = owner;
            this.collectionPublicationGuard = collectionPublicationGuard;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.EndHydrationPublish(collectionPublicationGuard);
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
            SynchronizeActiveCollectionUnsafe(observedActiveCollection);
        }
    }

    private void SynchronizeActiveCollectionUnsafe(IEnumerable<BMSTable> currentTables)
    {
        List<BMSTable> orderedTables = [.. (currentTables ?? [])
            .Where(table => table != null)
            .Distinct()];
        HashSet<BMSTable> currentSet = [.. orderedTables];
        foreach (BMSTable table in activeTables.ToArray())
        {
            if (!currentSet.Contains(table))
            {
                MarkRemovedUnsafe(table);
            }
        }
        foreach (BMSTable table in orderedTables)
        {
            MarkActiveUnsafe(table);
        }
        activeTableOrder.Clear();
        activeTableOrder.AddRange(orderedTables);
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
    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, bool logLastUpdateDecision = false)
    {
        return MergeReloadedBMSTableState(oldTable, reloadedTable, BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)), out bool _, logLastUpdateDecision);
    }

    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, IReadOnlyCollection<ComparablePlaylistEntryRow> persistedActiveRows, out bool hasContentChanges, bool logLastUpdateDecision = false)
    {
        return MergeReloadedBMSTableState(oldTable, reloadedTable, persistedActiveRows, out hasContentChanges, out _, logLastUpdateDecision);
    }

    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, IReadOnlyCollection<ComparablePlaylistEntryRow> persistedActiveRows, out bool hasContentChanges, out bool hasStateToPersist, bool logLastUpdateDecision = false)
    {
        BMSTable mergedTable = MergeReloadedBMSTableState(oldTable, reloadedTable, persistedActiveRows, out PlaylistReloadPersistenceDecision persistenceDecision, logLastUpdateDecision);
        hasContentChanges = persistenceDecision.UpdatesLastUpdate;
        hasStateToPersist = persistenceDecision.NeedsStatePersistence;
        return mergedTable;
    }

    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, IReadOnlyCollection<ComparablePlaylistEntryRow> persistedActiveRows, out PlaylistReloadPersistenceDecision persistenceDecision, bool logLastUpdateDecision = false)
    {
        return MergeReloadedBMSTableState(oldTable, reloadedTable, persistedActiveRows, out _, out _, out persistenceDecision, logLastUpdateDecision);
    }

    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, IReadOnlyCollection<ComparablePlaylistEntryRow> persistedActiveRows, out bool hasContentChanges, out bool hasStateToPersist, out PlaylistReloadPersistenceDecision persistenceDecision, bool logLastUpdateDecision = false)
    {
        if (oldTable == null)
        {
            throw new ArgumentNullException("oldTable");
        }
        if (reloadedTable == null)
        {
            throw new ArgumentNullException("reloadedTable");
        }

        BMSTable newTable = reloadedTable;
        var list = persistedActiveRows?.Where(row => row != null).ToList();
        IReadOnlyList<ComparablePlaylistEntryRow> normalizedPersistedRows = list ?? (IReadOnlyList<ComparablePlaylistEntryRow>)[];
        IReadOnlyList<ComparablePlaylistEntryRow> normalizedReloadedRows = BuildComparablePlaylistEntryRows(newTable.entries);
        PlaylistContentDiffResult playlistContentDiffResult = AnalyzePlaylistContentDiff(normalizedPersistedRows, normalizedReloadedRows);
        PlaylistHashChangeResult hashChangeResult = AnalyzePlaylistHashChanges(oldTable, newTable);
        persistenceDecision = new PlaylistReloadPersistenceDecision
        {
            EntryFingerprintChanged = playlistContentDiffResult.HasChanges,
            HeaderKnownChanged = hashChangeResult.HeaderKnownChanged,
            HeaderHashInitialized = hashChangeResult.HeaderHashInitialized,
            HeaderHashMigrated = hashChangeResult.HeaderHashMigrated,
            DataKnownChanged = hashChangeResult.DataKnownChanged,
            DataHashInitialized = hashChangeResult.DataHashInitialized
        };
        hasContentChanges = persistenceDecision.UpdatesLastUpdate;
        hasStateToPersist = persistenceDecision.NeedsStatePersistence;

        Dictionary<string, List<BMSTableEntry>> newEntriesByMd5 = BuildEntryLookup(newTable.entries, entry => entry.md5, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<BMSTableEntry>> newEntriesBySha256 = BuildEntryLookup(newTable.entries, entry => entry.sha256, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<BMSTableEntry>> newEntriesByComparableRow = BuildComparableEntryLookup(newTable.entries);

        List<BMSTableEntry> matchedOldEntries = [.. oldTable.entries.Where(delegate (BMSTableEntry oe)
        {
            BMSTableEntry matchedNew = ResolveReloadedEntryMatch(oe, newEntriesByMd5, newEntriesBySha256, newEntriesByComparableRow);
            if (matchedNew == null)
            {
                return false;
            }
            matchedNew.memo = oe.memo;
            matchedNew.adddate = oe.adddate;
            matchedNew.is_removed = false;
            return true;
        })];

        DateTime oldLastUpdate = oldTable.last_update;
        DateTime reloadedLastUpdate = newTable.last_update;
        if (oldTable.playlist_id.HasValue)
        {
            newTable.last_update = persistenceDecision.UpdatesLastUpdate ? DateTime.Now : oldTable.last_update;
        }
        else if (persistenceDecision.UpdatesLastUpdate)
        {
            newTable.last_update = ((reloadedLastUpdate != default) ? reloadedLastUpdate : DateTime.Now);
        }
        else
        {
            newTable.last_update = ((reloadedLastUpdate != default) ? reloadedLastUpdate : oldTable.last_update);
        }

        List<BMSTableEntry> removedEntries = [.. oldTable.entries
            .Except(matchedOldEntries)
            .Select(entry => entry.CreatePlaylistReloadSnapshot())];
        foreach (BMSTableEntry item in removedEntries)
        {
            item.is_removed = true;
        }
        newTable.entries = [.. newTable.entries, .. removedEntries];
        bool lastUpdateChanged = newTable.last_update != oldLastUpdate;
        if (logLastUpdateDecision)
        {
            if (playlistContentDiffResult.HasChanges)
            {
                LogPlaylistContentDiff(newTable.name, playlistContentDiffResult);
            }
            Ribbit.Logging.NLogWrapper.FileLogger?.Info("playlist_resync last_update_decision table=" + (newTable.name ?? string.Empty) + " changed=" + persistenceDecision.UpdatesLastUpdate.ToString().ToLowerInvariant() + " entryFingerprintChanged=" + playlistContentDiffResult.HasChanges.ToString().ToLowerInvariant() + " headerChanged=" + hashChangeResult.HeaderKnownChanged.ToString().ToLowerInvariant() + " dataChanged=" + hashChangeResult.DataKnownChanged.ToString().ToLowerInvariant() + " headerInitialized=" + hashChangeResult.HeaderHashInitialized.ToString().ToLowerInvariant() + " headerMigrated=" + hashChangeResult.HeaderHashMigrated.ToString().ToLowerInvariant() + " dataInitialized=" + hashChangeResult.DataHashInitialized.ToString().ToLowerInvariant() + " persistHeader=" + persistenceDecision.NeedsHeaderPersistence.ToString().ToLowerInvariant() + " persistEntry=" + persistenceDecision.NeedsEntryPersistence.ToString().ToLowerInvariant() + " old=" + oldLastUpdate.ToString("O") + " reloaded=" + reloadedLastUpdate.ToString("O") + " final=" + newTable.last_update.ToString("O"));
        }
        return newTable;
    }

    private static PlaylistHashChangeResult AnalyzePlaylistHashChanges(BMSTable oldTable, BMSTable newTable)
    {
        var result = new PlaylistHashChangeResult();
        if (oldTable == null || newTable == null)
        {
            return result;
        }
        ApplyHeaderHashChange(oldTable, newTable, result);
        ApplyHashChange(
            oldTable.data_sha256,
            newTable.data_sha256,
            () => result.DataHashInitialized = true,
            () => result.DataKnownChanged = true);
        return result;
    }

    private static void ApplyHeaderHashChange(BMSTable oldTable, BMSTable newTable, PlaylistHashChangeResult result)
    {
        string normalizedOldHash = NormalizeHash(oldTable.header_sha256);
        string normalizedNewHash = NormalizeHash(newTable.header_sha256);
        if (string.Equals(normalizedOldHash, normalizedNewHash, StringComparison.Ordinal))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(normalizedOldHash) && !string.IsNullOrWhiteSpace(normalizedNewHash))
        {
            result.HeaderHashInitialized = true;
            return;
        }
        string loadedRawHeaderHash = NormalizeHash(newTable.LoadedRawHeaderSha256);
        if (!string.IsNullOrWhiteSpace(loadedRawHeaderHash)
            && string.Equals(normalizedOldHash, loadedRawHeaderHash, StringComparison.Ordinal))
        {
            result.HeaderHashMigrated = true;
            return;
        }
        result.HeaderKnownChanged = true;
    }

    private static void ApplyHashChange(string oldHash, string newHash, Action markInitialized, Action markKnownChanged)
    {
        string normalizedOldHash = NormalizeHash(oldHash);
        string normalizedNewHash = NormalizeHash(newHash);
        if (string.Equals(normalizedOldHash, normalizedNewHash, StringComparison.Ordinal))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(normalizedOldHash) && !string.IsNullOrWhiteSpace(normalizedNewHash))
        {
            markInitialized?.Invoke();
            return;
        }
        markKnownChanged?.Invoke();
    }

    private static Dictionary<string, List<BMSTableEntry>> BuildEntryLookup(IEnumerable<BMSTableEntry> entries, Func<BMSTableEntry, string> keySelector, IEqualityComparer<string> comparer)
    {
        return entries.Where(delegate (BMSTableEntry entry)
        {
            string text = keySelector(entry);
            return !string.IsNullOrWhiteSpace(text);
        }).GroupBy(entry => keySelector(entry), comparer).ToDictionary(group => group.Key, group => group.ToList(), comparer);
    }

    private static BMSTableEntry ResolveReloadedEntryMatch(BMSTableEntry oldEntry, Dictionary<string, List<BMSTableEntry>> entriesByMd5, Dictionary<string, List<BMSTableEntry>> entriesBySha256, Dictionary<string, List<BMSTableEntry>> entriesByComparableRow)
    {
        List<BMSTableEntry> list = GetEntryMatchCandidates(oldEntry.md5, entriesByMd5) ?? GetEntryMatchCandidates(oldEntry.sha256, entriesBySha256);
        if (list == null)
        {
            ComparablePlaylistEntryRow comparableRow = CreateComparablePlaylistEntryRow(oldEntry);
            if (comparableRow != null)
            {
                list = GetEntryMatchCandidates(comparableRow.Fingerprint, entriesByComparableRow);
            }
        }
        return SelectBestMatchedEntry(oldEntry, list);
    }

    private static Dictionary<string, List<BMSTableEntry>> BuildComparableEntryLookup(IEnumerable<BMSTableEntry> entries)
    {
        var dictionary = new Dictionary<string, List<BMSTableEntry>>(StringComparer.Ordinal);
        foreach (BMSTableEntry entry in entries ?? [])
        {
            ComparablePlaylistEntryRow comparableRow = CreateComparablePlaylistEntryRow(entry);
            if (comparableRow == null)
            {
                continue;
            }
            if (!dictionary.TryGetValue(comparableRow.Fingerprint, out List<BMSTableEntry> value))
            {
                value = [];
                dictionary[comparableRow.Fingerprint] = value;
            }
            value.Add(entry);
        }
        return dictionary;
    }

    internal static IReadOnlyList<ComparablePlaylistEntryRow> BuildComparablePlaylistEntryRows(IEnumerable<BMSTableEntry> entries)
    {
        if (entries == null)
        {
            return [];
        }
        return [.. entries.Select(CreateComparablePlaylistEntryRow).Where(row => row != null)];
    }

    internal static ComparablePlaylistEntryRow CreateComparablePlaylistEntryRow(BMSTableEntry entry)
    {
        if (entry == null)
        {
            return null;
        }
        var comparableRow = new ComparablePlaylistEntryRow
        {
            Md5 = NormalizeHash(entry.md5),
            Sha256 = NormalizeHash(entry.sha256),
            Level = NormalizeLevel(entry.level),
            Title = NormalizeText(entry.title),
            Artist = NormalizeText(entry.artist),
            Lr2BmsId = NormalizeText(entry.lr2_bmsid),
            Url = NormalizeText(entry.url),
            UrlDiff = NormalizeText(entry.url_diff),
            NameDiff = NormalizeText(entry.name_diff),
            Comment = NormalizeText(entry.comment)
        };
        if (!HasMeaningfulComparableContent(comparableRow))
        {
            return null;
        }
        return new ComparablePlaylistEntryRow
        {
            Md5 = comparableRow.Md5,
            Sha256 = comparableRow.Sha256,
            Level = comparableRow.Level,
            Title = comparableRow.Title,
            Artist = comparableRow.Artist,
            Lr2BmsId = comparableRow.Lr2BmsId,
            Url = comparableRow.Url,
            UrlDiff = comparableRow.UrlDiff,
            NameDiff = comparableRow.NameDiff,
            Comment = comparableRow.Comment,
            Fingerprint = BuildComparablePlaylistEntryFingerprint(comparableRow)
        };
    }

    internal static PlaylistContentDiffResult AnalyzePlaylistContentDiff(IEnumerable<ComparablePlaylistEntryRow> persistedRows, IEnumerable<ComparablePlaylistEntryRow> reloadedRows)
    {
        Dictionary<string, int> dictionary = BuildComparableRowFingerprintCounts(persistedRows);
        Dictionary<string, int> dictionary2 = BuildComparableRowFingerprintCounts(reloadedRows);
        var playlistContentDiffResult = new PlaylistContentDiffResult
        {
            PersistedOnlySamples = [],
            ReloadedOnlySamples = []
        };
        List<string> list = null;
        List<string> list2 = null;
        foreach (string item in dictionary.Keys.Union(dictionary2.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
        {
            dictionary.TryGetValue(item, out int value);
            dictionary2.TryGetValue(item, out int value2);
            if (value == value2)
            {
                continue;
            }
            if (value > value2)
            {
                int num = value - value2;
                playlistContentDiffResult.PersistedOnlyCount += num;
                list ??= new List<string>(PlaylistDiffSampleLogCount);
                AppendDiffSamples(list, item, num);
            }
            else
            {
                int num2 = value2 - value;
                playlistContentDiffResult.ReloadedOnlyCount += num2;
                list2 ??= new List<string>(PlaylistDiffSampleLogCount);
                AppendDiffSamples(list2, item, num2);
            }
        }
        playlistContentDiffResult.HasChanges = playlistContentDiffResult.PersistedOnlyCount > 0 || playlistContentDiffResult.ReloadedOnlyCount > 0;
        playlistContentDiffResult.PersistedOnlySamples = (IReadOnlyList<string>)(list ?? (IReadOnlyList<string>)[]);
        playlistContentDiffResult.ReloadedOnlySamples = (IReadOnlyList<string>)(list2 ?? (IReadOnlyList<string>)[]);
        return playlistContentDiffResult;
    }

    internal static bool HasPlaylistContentChanges(IEnumerable<ComparablePlaylistEntryRow> persistedRows, IEnumerable<ComparablePlaylistEntryRow> reloadedRows)
    {
        return AnalyzePlaylistContentDiff(persistedRows, reloadedRows).HasChanges;
    }

    private static Dictionary<string, int> BuildComparableRowFingerprintCounts(IEnumerable<ComparablePlaylistEntryRow> rows)
    {
        var dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
        if (rows == null)
        {
            return dictionary;
        }
        foreach (ComparablePlaylistEntryRow row in rows.Where(row => row != null))
        {
            if (dictionary.TryGetValue(row.Fingerprint, out int value))
            {
                dictionary[row.Fingerprint] = value + 1;
            }
            else
            {
                dictionary[row.Fingerprint] = 1;
            }
        }
        return dictionary;
    }

    private static void AppendDiffSamples(List<string> samples, string fingerprint, int count)
    {
        if (samples == null || count <= 0)
        {
            return;
        }
        int num = PlaylistDiffSampleLogCount - samples.Count;
        if (num <= 0)
        {
            return;
        }
        for (int i = 0; i < count && i < num; i++)
        {
            samples.Add(fingerprint);
        }
    }

    private static void LogPlaylistContentDiff(string tableName, PlaylistContentDiffResult diffResult)
    {
        if (diffResult == null || !diffResult.HasChanges)
        {
            return;
        }
        Ribbit.Logging.NLogWrapper.FileLogger?.Info("playlist_resync diff_summary table=" + (tableName ?? string.Empty) + " persistedOnlyCount=" + diffResult.PersistedOnlyCount + " reloadedOnlyCount=" + diffResult.ReloadedOnlyCount + " sampleCount=" + PlaylistDiffSampleLogCount);
        Ribbit.Logging.NLogWrapper.FileLogger?.Info("playlist_resync diff_samples table=" + (tableName ?? string.Empty) + " persistedOnly=[" + JoinDiffSamplesForLog(diffResult.PersistedOnlySamples) + "] reloadedOnly=[" + JoinDiffSamplesForLog(diffResult.ReloadedOnlySamples) + "]");
    }

    private static string JoinDiffSamplesForLog(IEnumerable<string> samples)
    {
        IEnumerable<string> enumerable = samples ?? (IEnumerable<string>)[];
        return string.Join(", ", enumerable.Select(EscapeFingerprintForLog));
    }

    private static string EscapeFingerprintForLog(string fingerprint)
    {
        if (fingerprint == null)
        {
            return string.Empty;
        }
        return fingerprint.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static bool HasMeaningfulComparableContent(ComparablePlaylistEntryRow row)
    {
        return row != null && (!string.IsNullOrWhiteSpace(row.Md5) || !string.IsNullOrWhiteSpace(row.Sha256) || !string.IsNullOrWhiteSpace(row.Level) || !string.IsNullOrWhiteSpace(row.Title) || !string.IsNullOrWhiteSpace(row.Artist) || !string.IsNullOrWhiteSpace(row.Lr2BmsId) || !string.IsNullOrWhiteSpace(row.Url) || !string.IsNullOrWhiteSpace(row.UrlDiff) || !string.IsNullOrWhiteSpace(row.NameDiff) || !string.IsNullOrWhiteSpace(row.Comment));
    }

    private static string NormalizeHash(string value)
    {
        string text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return text.ToLowerInvariant();
    }

    private static string NormalizeText(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }
        int num = value.IndexOf('\0');
        if (num >= 0)
        {
            value = value.Substring(0, num);
        }
        var stringBuilder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
            {
                continue;
            }
            stringBuilder.Append(c);
        }
        return stringBuilder.ToString().Trim();
    }

    private static string NormalizeLevel(double? value)
    {
        if (!value.HasValue)
        {
            return string.Empty;
        }
        return value.Value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string BuildComparablePlaylistEntryFingerprint(ComparablePlaylistEntryRow row)
    {
        var stringBuilder = new StringBuilder();
        AppendComparableFingerprintPart(stringBuilder, row.Md5);
        AppendComparableFingerprintPart(stringBuilder, row.Sha256);
        AppendComparableFingerprintPart(stringBuilder, row.Level);
        AppendComparableFingerprintPart(stringBuilder, row.Title);
        AppendComparableFingerprintPart(stringBuilder, row.Artist);
        AppendComparableFingerprintPart(stringBuilder, row.Lr2BmsId);
        AppendComparableFingerprintPart(stringBuilder, row.Url);
        AppendComparableFingerprintPart(stringBuilder, row.UrlDiff);
        AppendComparableFingerprintPart(stringBuilder, row.NameDiff);
        AppendComparableFingerprintPart(stringBuilder, row.Comment);
        return stringBuilder.ToString();
    }

    private static void AppendComparableFingerprintPart(StringBuilder builder, string value)
    {
        string text = value ?? string.Empty;
        builder.Append(text.Length).Append(':').Append(text).Append('|');
    }

    private static List<BMSTableEntry> GetEntryMatchCandidates(string key, Dictionary<string, List<BMSTableEntry>> lookup)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        lookup.TryGetValue(key, out List<BMSTableEntry> value);
        if (value == null || value.Count == 0)
        {
            return null;
        }
        return value;
    }

    private static BMSTableEntry SelectBestMatchedEntry(BMSTableEntry oldEntry, List<BMSTableEntry> candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return null;
        }
        if (candidates.Count == 1)
        {
            return candidates[0];
        }
        return candidates.OrderBy(ne => Math.Abs((ne.level ?? 0.0) - (oldEntry.level ?? 0.0))).First();
    }

    /// <summary>
    /// プレイリストの構成差分有無を判定します。
    /// </summary>
    /// <param name="oldTable">既存プレイリスト。</param>
    /// <param name="newTable">再取得プレイリスト。</param>
    /// <param name="matchedOldEntryCount">新旧で対応付けられた既存エントリ数。</param>
    /// <returns>構成差分があれば <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="oldTable"/> または <paramref name="newTable"/> が <see langword="null"/> の場合。</exception>
    internal static bool HasPlaylistStructuralChanges(BMSTable oldTable, BMSTable newTable, int matchedOldEntryCount)
    {
        if (oldTable == null)
        {
            throw new ArgumentNullException("oldTable");
        }
        if (newTable == null)
        {
            throw new ArgumentNullException("newTable");
        }
        List<string> oldFolderList = oldTable.folder_list;
        List<string> newFolderList = newTable.folder_list;
        return newTable.entries.Count != matchedOldEntryCount || matchedOldEntryCount != oldTable.entries.Where(entry => !entry.is_removed).Count() || oldFolderList.Except(newFolderList).Any() || newFolderList.Except(oldFolderList).Any();
    }

}
