using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// プレイリスト header 読み込みと entries hydration の workflow state を所有します。
/// <para>
/// <see cref="BMSPlaylist"/> は application-facing command と observable property を公開しますが、
/// 読込みと参照公開の識別を所有し、必須変更は親操作から直接待たれます。
/// </para>
/// </summary>
internal sealed class PlaylistEntriesHydrationOwner
{
    /// <summary>受付時に既存要求版の発生元を捕捉します。</summary>
    internal Func<string, long, OperationProgressRequest> ProgressRequestFactory { get; set; }

    /// <summary>捕捉済みの読込み要求の実行境界を通知します。</summary>
    internal Action<OperationProgressRequest, bool> RequestProgressReporter { get; set; }

    private OperationProgressRequest requestedProgressRequest;

    private readonly PlaylistPersistenceRepository repository;

    private readonly Func<PlaylistHydrationTableSnapshot> tablesSnapshotProvider;

    private readonly Func<bool> isShutdownRequested;

    private readonly Action<string> logPerformance;

    private readonly SemaphoreSlim hydrationSemaphore = new(1, 1);

    private readonly object requestLock = new();

    private int running;

    private int requestedVersion;

    private int completedVersion;

    private int shutdownEpoch;

    internal sealed class PlaylistHydrationTableSnapshot
    {
        internal PlaylistHydrationTableSnapshot(long generation, IReadOnlyList<BMSTable> tables)
        {
            Generation = generation;
            Tables = tables ?? [];
        }

        internal long Generation { get; }

        internal IReadOnlyList<BMSTable> Tables { get; }
    }

    internal sealed class PlaylistHydrationResult
    {
        internal PlaylistHydrationResult(long generation)
        {
            Generation = generation;
        }

        internal long Generation { get; }
    }

    internal sealed class PlaylistHydratedTableFact
    {
        internal PlaylistHydratedTableFact(BMSTable table)
        {
            Table = table;
            if (table == null)
            {
                ReferenceSnapshot = null;
                Entries = [];
                return;
            }
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                if (!table.ArePlaylistEntriesLoaded)
                {
                    ReferenceSnapshot = null;
                    Entries = [];
                    return;
                }
                PlaylistId = table.playlist_id;
                Symbol = table.symbol;
                Name = table.name;
                EntriesRevision = table.PlaylistEntriesRevision;
                ReferenceSnapshot = new PlaylistReferenceTableSnapshot(
                    table,
                    Symbol,
                    Name,
                    (table.entries ?? []).ToArray());
            }
            Entries = ReferenceSnapshot.Entries;
        }

        internal BMSTable Table { get; }

        internal int? PlaylistId { get; }

        internal string Symbol { get; }

        internal string Name { get; }

        internal int EntriesRevision { get; }

        internal PlaylistReferenceTableSnapshot ReferenceSnapshot { get; }

        internal IReadOnlyList<PlaylistReferenceEntrySnapshot> Entries { get; }
    }

    internal sealed class PlaylistEntriesHydrationReceipt
    {
        /// <summary>確定した読込み内容と同じ要求の表示発生元を、後続処理へ変更不能な受領として渡します。</summary>
        internal PlaylistEntriesHydrationReceipt(
            long generation,
            int shutdownEpoch,
            int requestVersion,
            string reason,
            IReadOnlyList<PlaylistHydratedTableFact> tables,
            OperationProgressRequest progressRequest = null)
        {
            ProgressRequest = progressRequest;
            Generation = generation;
            ShutdownEpoch = shutdownEpoch;
            RequestVersion = requestVersion;
            Reason = reason ?? string.Empty;
            Tables = Array.AsReadOnly([.. (tables ?? []).Where(table => table != null)]);
        }

        /// <summary>この読込み受領を生産した要求の表示識別です。派生受領にも保持します。</summary>
        internal OperationProgressRequest ProgressRequest { get; }

        internal long Generation { get; }

        internal int ShutdownEpoch { get; }

        internal int RequestVersion { get; }

        internal string Reason { get; }

        internal IReadOnlyList<PlaylistHydratedTableFact> Tables { get; }

    }

    internal sealed class PlaylistEntriesHydrationReceiptEventArgs : EventArgs
    {
        internal PlaylistEntriesHydrationReceiptEventArgs(PlaylistEntriesHydrationReceipt receipt)
        {
            Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        }

        internal PlaylistEntriesHydrationReceipt Receipt { get; }


    }

    internal PlaylistEntriesHydrationOwner(
        PlaylistPersistenceRepository repository,
        Func<PlaylistHydrationTableSnapshot> tablesSnapshotProvider,
        Func<bool> isShutdownRequested,
        Action<string> logPerformance)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.tablesSnapshotProvider = tablesSnapshotProvider ?? throw new ArgumentNullException(nameof(tablesSnapshotProvider));
        this.isShutdownRequested = isShutdownRequested ?? throw new ArgumentNullException(nameof(isShutdownRequested));
        this.logPerformance = logPerformance ?? throw new ArgumentNullException(nameof(logPerformance));
    }

    internal event Action<bool> RunningChanged;

    /// <summary>受付版と、その受付時に捕捉した表示識別を通知します。</summary>
    internal event Action<int, OperationProgressRequest> HydrationRequested;

    /// <summary>確定した完了版と、同じ読込み実行の表示識別を通知します。</summary>
    internal event Action<int, OperationProgressRequest> HydrationCompleted;

    internal bool PlaylistEntriesHydrationRunning => Volatile.Read(ref running) != 0;

    internal int PlaylistEntriesHydrationRequestedVersion => Volatile.Read(ref requestedVersion);

    internal int PlaylistEntriesHydrationCompletedVersion => Volatile.Read(ref completedVersion);

    internal bool HasBlockingWork => PlaylistEntriesHydrationRunning;

    internal List<BMSTable> LoadPlaylistHeaders(out long loadTablesMs)
    {
        var stopwatch = Stopwatch.StartNew();
        List<BMSTable> tables = repository.LoadPlaylistHeaders();
        stopwatch.Stop();
        loadTablesMs = stopwatch.ElapsedMilliseconds;
        return tables;
    }

    /// <summary>終了後の受付識別を更新します。親P操作が開始済みTaskを実終端まで待ちます。</summary>
    internal void ClearPendingForShutdown(string reason)
    {
        Interlocked.Increment(ref shutdownEpoch);
    }

    /// <summary>親操作内でエントリ読込みを直接待ち、確定した参照公開用の受領を返します。予約と自動再試行を行いません。</summary>
    internal async Task<PlaylistEntriesHydrationReceipt> HydrateAsync(string reason)
    {
        int version = Interlocked.Increment(ref requestedVersion);
        OperationProgressRequest request = ProgressRequestFactory?.Invoke("playlist_entries_hydration", version);
        requestedProgressRequest = request;
        Action<OperationProgressRequest, bool> reporter = RequestProgressReporter;
        HydrationRequested?.Invoke(version, request);
        reporter?.Invoke(request, true);
        try
        {
            PlaylistHydrationResult result = await EnsureAllPlaylistEntriesLoadedAsync(reason).ConfigureAwait(false);
            return CreateHydrationReceipt(result.Generation, Volatile.Read(ref shutdownEpoch), version, reason, request)
                ?? throw new InvalidOperationException("Playlist hydration snapshot could not be published.");
        }
        finally
        {
            reporter?.Invoke(request, false);
        }
    }

    /// <summary>同じP内の必要出力と参照公開の成功後だけ完了版を通知します。</summary>
    internal void CompleteHydration(PlaylistEntriesHydrationReceipt receipt)
    {
        SetCompletedVersion(receipt.RequestVersion, receipt.ProgressRequest);
    }

    /// <summary>項目の読取り準備を待ちます。必要出力・参照公開を含む親操作の完了版は更新しません。</summary>
    internal async Task<PlaylistHydrationResult> EnsureAllPlaylistEntriesLoadedAsync(string reason)
    {
        string requestReason = reason ?? string.Empty;
        if (isShutdownRequested())
        {
            logPerformance("playlist_entries_hydration ensure_all_skipped reason=shutdown_requested requestReason=" + requestReason);
            return new PlaylistHydrationResult(GetTablesSnapshot().Generation);
        }

        await hydrationSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            while (true)
            {
                PlaylistHydrationTableSnapshot tablesSnapshot = GetTablesSnapshot();
                if (tablesSnapshot.Tables.Count > 0
                    && tablesSnapshot.Tables.All(table => table.ArePlaylistEntriesLoaded))
                {
                    return new PlaylistHydrationResult(tablesSnapshot.Generation);
                }

                SetRunning(true);
                var stopwatchTotal = Stopwatch.StartNew();
                PlaylistEntriesHydrationLoadResult loadResult = repository.LoadStartupPlaylistEntries();
                List<BMSTableEntry> source = loadResult.Entries;
                var stopwatchGroup = Stopwatch.StartNew();
                Dictionary<int, List<BMSTableEntry>> entriesByPlaylistId = [];
                foreach (BMSTableEntry entryItem in source)
                {
                    if (!entryItem.playlist_id.HasValue)
                    {
                        continue;
                    }
                    int key = entryItem.playlist_id.Value;
                    if (!entriesByPlaylistId.TryGetValue(key, out List<BMSTableEntry> value))
                    {
                        value = [];
                        entriesByPlaylistId[key] = value;
                    }
                    value.Add(entryItem);
                }
                stopwatchGroup.Stop();
                int removedEntryCount = source.Count(entry => entry != null && entry.is_removed);
                int activeEntryCount = source.Count - removedEntryCount;
                var stopwatchAssign = Stopwatch.StartNew();
                int assignedTableCount = 0;
                foreach (BMSTable table in tablesSnapshot.Tables)
                {
                    if (table == null || table.ArePlaylistEntriesLoaded)
                    {
                        continue;
                    }
                    using (table.ReaderWriterLock.GetWriterGuard())
                    {
                        if (table.ArePlaylistEntriesLoaded)
                        {
                            continue;
                        }
                        if (table.playlist_id.HasValue
                            && entriesByPlaylistId.TryGetValue(table.playlist_id.Value, out List<BMSTableEntry> value))
                        {
                            table.entries = value;
                        }
                        else
                        {
                            table.entries = [];
                        }
                    }
                    assignedTableCount++;
                }
                stopwatchAssign.Stop();
                stopwatchTotal.Stop();
                long entryLoadRowsPerMs = loadResult.DbReadMs <= 0
                    ? source.Count
                    : source.Count / Math.Max(1L, loadResult.DbReadMs);
                logPerformance("playlist_entries_hydration done reason=" + requestReason
                    + " projection=" + loadResult.Projection
                    + " tableCount=" + tablesSnapshot.Tables.Count
                    + " assignedTableCount=" + assignedTableCount
                    + " rows=" + source.Count
                    + " entryCount=" + source.Count
                    + " activeEntryCount=" + activeEntryCount
                    + " removedEntryCount=" + removedEntryCount
                    + " readOnly=" + loadResult.ReadOnly.ToString().ToLowerInvariant()
                    + " dbLockWaitMs=" + loadResult.DbLockWaitMs
                    + " dbReadMs=" + loadResult.DbReadMs
                    + " materializeMs=" + loadResult.MaterializeMs
                    + " groupMs=" + stopwatchGroup.ElapsedMilliseconds
                    + " assignMs=" + stopwatchAssign.ElapsedMilliseconds
                    + " totalMs=" + stopwatchTotal.ElapsedMilliseconds
                    + " entryLoadRowsPerMs=" + entryLoadRowsPerMs);

                PlaylistHydrationTableSnapshot currentSnapshot = GetTablesSnapshot();
                if (currentSnapshot.Generation != tablesSnapshot.Generation)
                {
                    continue;
                }
                if (!currentSnapshot.Tables.Where(table => table != null).All(table => table.ArePlaylistEntriesLoaded))
                {
                    continue;
                }
                return new PlaylistHydrationResult(currentSnapshot.Generation);
            }
        }
        catch (Exception ex)
        {
            var tablesToMarkFailed = GetTablesSnapshot().Tables
                .Where(table => table != null && !table.ArePlaylistEntriesLoaded)
                .ToList();
            foreach (BMSTable table in tablesToMarkFailed)
            {
                using (table.ReaderWriterLock.GetWriterGuard())
                {
                    if (!table.ArePlaylistEntriesLoaded)
                    {
                        table.MarkEntriesLoadFailed(ex.Message);
                    }
                }
            }
            logPerformance("playlist_entries_hydration failed reason=" + requestReason + " message=" + ex.Message);
            throw;
        }
        finally
        {
            SetRunning(false);
            hydrationSemaphore.Release();
        }
    }

    internal void EnsurePlaylistEntriesLoaded(BMSTable table, string reason)
    {
        if (table == null || table.ArePlaylistEntriesLoaded)
        {
            return;
        }
        hydrationSemaphore.Wait();
        try
        {
            if (table.ArePlaylistEntriesLoaded)
            {
                return;
            }
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                if (table.ArePlaylistEntriesLoaded)
                {
                    return;
                }
                table.MarkEntriesLoading();
            }
            string requestReason = reason ?? string.Empty;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                List<BMSTableEntry> entries = [.. repository.LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: false)];
                using (table.ReaderWriterLock.GetWriterGuard())
                {
                    table.entries = entries;
                }
                stopwatch.Stop();
                int removedEntryCount = entries.Count(entry => entry != null && entry.is_removed);
                logPerformance("playlist_entries_load_table done reason=" + requestReason
                    + " playlistId=" + (table.playlist_id.HasValue ? table.playlist_id.Value.ToString(CultureInfo.InvariantCulture) : "(null)")
                    + " name=\"" + (table.name ?? string.Empty).Replace("\"", "\"\"") + "\""
                    + " entryCount=" + entries.Count
                    + " activeEntryCount=" + (entries.Count - removedEntryCount)
                    + " removedEntryCount=" + removedEntryCount
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                using (table.ReaderWriterLock.GetWriterGuard())
                {
                    table.MarkEntriesLoadFailed(ex.Message);
                }
                logPerformance("playlist_entries_load_table failed reason=" + requestReason + " message=" + ex.Message);
                throw;
            }
        }
        finally
        {
            hydrationSemaphore.Release();
        }
    }

    private PlaylistHydrationTableSnapshot GetTablesSnapshot()
    {
        return tablesSnapshotProvider() ?? new PlaylistHydrationTableSnapshot(0, []);
    }

    private bool IsCurrentGeneration(long generation)
    {
        return GetTablesSnapshot().Generation == generation;
    }

    internal bool IsReceiptCurrent(PlaylistEntriesHydrationReceipt receipt)
    {
        if (receipt == null
            || isShutdownRequested()
            || Volatile.Read(ref shutdownEpoch) != receipt.ShutdownEpoch
            || !IsCurrentGeneration(receipt.Generation))
        {
            return false;
        }
        foreach (PlaylistHydratedTableFact fact in receipt.Tables)
        {
            BMSTable table = fact?.Table;
            if (table == null)
            {
                continue;
            }
            using (table.ReaderWriterLock.GetReaderGuard())
            {
                if (!table.ArePlaylistEntriesLoaded
                    || table.playlist_id != fact.PlaylistId
                    || table.PlaylistEntriesRevision != fact.EntriesRevision
                    || !string.Equals(table.symbol, fact.Symbol, StringComparison.Ordinal)
                    || !string.Equals(table.name, fact.Name, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private bool IsShutdownOrEpochChanged(int workShutdownEpoch)
    {
        return isShutdownRequested() || Volatile.Read(ref shutdownEpoch) != workShutdownEpoch;
    }

    private void SetRunning(bool value)
    {
        if (Interlocked.Exchange(ref running, value ? 1 : 0) != (value ? 1 : 0))
        {
            RunningChanged?.Invoke(value);
        }
    }

    private void SetCompletedVersion(int value, OperationProgressRequest progressRequest = null)
    {
        while (true)
        {
            int current = Volatile.Read(ref completedVersion);
            if (value <= current)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref completedVersion, value, current) == current)
            {
                OperationProgressRequest request;
                lock (requestLock)
                {
                    request = progressRequest ?? (requestedProgressRequest?.Version == value ? requestedProgressRequest : null);
                }
                HydrationCompleted?.Invoke(value, request);
                return;
            }
        }
    }

    private PlaylistEntriesHydrationReceipt CreateHydrationReceipt(
        long generation,
        int shutdownEpoch,
        int requestVersion,
        string reason,
        OperationProgressRequest progressRequest = null)
    {
        return TryCreateStableReceipt(
            generation,
            shutdownEpoch,
            requestVersion,
            reason,
            progressRequest);
    }

    private PlaylistEntriesHydrationReceipt TryCreateStableReceipt(
        long? requiredGeneration,
        int? requiredShutdownEpoch,
        int requestVersion,
        string reason,
        OperationProgressRequest progressRequest = null)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            PlaylistHydrationTableSnapshot snapshot = GetTablesSnapshot();
            if (requiredGeneration.HasValue && snapshot.Generation != requiredGeneration.Value)
            {
                continue;
            }
            if (requiredShutdownEpoch.HasValue
                && Volatile.Read(ref shutdownEpoch) != requiredShutdownEpoch.Value)
            {
                continue;
            }
            PlaylistHydratedTableFact[] facts = snapshot.Tables
                .Where(table => table != null)
                .Select(table => new PlaylistHydratedTableFact(table))
                .ToArray();
            if (facts.Any(fact => fact.ReferenceSnapshot == null))
            {
                continue;
            }
            PlaylistHydrationTableSnapshot currentSnapshot = GetTablesSnapshot();
            if (!AreSameTableSnapshot(snapshot, currentSnapshot)
                || (requiredShutdownEpoch.HasValue
                    && Volatile.Read(ref shutdownEpoch) != requiredShutdownEpoch.Value))
            {
                continue;
            }
            return new PlaylistEntriesHydrationReceipt(
                snapshot.Generation,
                Volatile.Read(ref shutdownEpoch),
                requestVersion,
                reason,
                facts,
                progressRequest);
        }
        return null;
    }

    private static bool AreSameTableSnapshot(
        PlaylistHydrationTableSnapshot first,
        PlaylistHydrationTableSnapshot second)
    {
        if (first == null
            || second == null
            || first.Generation != second.Generation
            || first.Tables.Count != second.Tables.Count)
        {
            return false;
        }
        for (int index = 0; index < first.Tables.Count; index++)
        {
            if (!ReferenceEquals(first.Tables[index], second.Tables[index]))
            {
                return false;
            }
        }
        return true;
    }



}
