using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// プレイリスト header 読み込みと entries hydration の workflow state を所有します。
/// <para>
/// <see cref="BMSPlaylist"/> は application-facing command と observable property を公開しますが、
/// hydration の coalescing、version、failure、shutdown、completion ordering はこの owner に集約します。
/// </para>
/// </summary>
internal sealed class PlaylistEntriesHydrationOwner
{
    private readonly PlaylistPersistenceRepository repository;

    private readonly Func<PlaylistHydrationTableSnapshot> tablesSnapshotProvider;

    private readonly Func<long, IDisposable> hydrationPublishLeaseProvider;

    private readonly Func<bool> isShutdownRequested;

    private readonly Func<Func<string, string, string, Func<Task>, bool>> startupSchedulerProvider;

    private readonly Action<string> logPerformance;

    private readonly Action<Exception, string> logCompletionFailure;

    private readonly SemaphoreSlim hydrationSemaphore = new(1, 1);

    private readonly object requestLock = new();

    private PlaylistHydrationContinuationIntent pendingContinuation;

    private bool pendingRequest;

    private int queued;

    private bool workStarted;

    private int running;

    private int requestedVersion;

    private int completedVersion;

    private int failedThroughVersion;

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

    internal sealed class PlaylistHydrationContinuationIntent
    {
        internal PlaylistHydrationContinuationIntent(
            bool runExternalSyncAfterHydration,
            bool queueBeatorajaBmtExportAfterHydration,
            bool runCustomFolderOutputRepairAfterHydration,
            bool verifyRootOutputDirectoryRows)
        {
            RunExternalSyncAfterHydration = runExternalSyncAfterHydration;
            QueueBeatorajaBmtExportAfterHydration = queueBeatorajaBmtExportAfterHydration;
            RunCustomFolderOutputRepairAfterHydration = runCustomFolderOutputRepairAfterHydration;
            VerifyRootOutputDirectoryRows = verifyRootOutputDirectoryRows;
        }

        internal bool RunExternalSyncAfterHydration { get; }

        internal bool QueueBeatorajaBmtExportAfterHydration { get; }

        internal bool RunCustomFolderOutputRepairAfterHydration { get; }

        internal bool VerifyRootOutputDirectoryRows { get; }

        internal PlaylistHydrationContinuationIntent WithoutExternalSync()
        {
            return new PlaylistHydrationContinuationIntent(
                runExternalSyncAfterHydration: false,
                QueueBeatorajaBmtExportAfterHydration,
                RunCustomFolderOutputRepairAfterHydration,
                VerifyRootOutputDirectoryRows);
        }

        internal static PlaylistHydrationContinuationIntent Merge(
            PlaylistHydrationContinuationIntent current,
            PlaylistHydrationContinuationIntent next)
        {
            if (current == null)
            {
                return next;
            }
            if (next == null)
            {
                return current;
            }
            return new PlaylistHydrationContinuationIntent(
                current.RunExternalSyncAfterHydration || next.RunExternalSyncAfterHydration,
                current.QueueBeatorajaBmtExportAfterHydration || next.QueueBeatorajaBmtExportAfterHydration,
                current.RunCustomFolderOutputRepairAfterHydration || next.RunCustomFolderOutputRepairAfterHydration,
                current.VerifyRootOutputDirectoryRows || next.VerifyRootOutputDirectoryRows);
        }
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
        internal PlaylistEntriesHydrationReceipt(
            long generation,
            int shutdownEpoch,
            int requestVersion,
            string reason,
            IReadOnlyList<PlaylistHydratedTableFact> tables,
            PlaylistHydrationContinuationIntent continuation)
        {
            Generation = generation;
            ShutdownEpoch = shutdownEpoch;
            RequestVersion = requestVersion;
            Reason = reason ?? string.Empty;
            Tables = Array.AsReadOnly([.. (tables ?? []).Where(table => table != null)]);
            Continuation = continuation;
        }

        internal long Generation { get; }

        internal int ShutdownEpoch { get; }

        internal int RequestVersion { get; }

        internal string Reason { get; }

        internal IReadOnlyList<PlaylistHydratedTableFact> Tables { get; }

        internal PlaylistHydrationContinuationIntent Continuation { get; }
    }

    internal sealed class PlaylistEntriesHydrationReceiptEventArgs : EventArgs
    {
        internal PlaylistEntriesHydrationReceiptEventArgs(PlaylistEntriesHydrationReceipt receipt)
        {
            Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        }

        internal PlaylistEntriesHydrationReceipt Receipt { get; }

        internal Exception CompositionFailure { get; set; }

        internal PlaylistHydrationContinuationIntent RetryContinuation { get; set; }

        internal bool RetryRequested { get; set; }
    }

    internal PlaylistEntriesHydrationOwner(
        PlaylistPersistenceRepository repository,
        Func<PlaylistHydrationTableSnapshot> tablesSnapshotProvider,
        Func<long, IDisposable> hydrationPublishLeaseProvider,
        Func<bool> isShutdownRequested,
        Func<Func<string, string, string, Func<Task>, bool>> startupSchedulerProvider,
        Action<string> logPerformance,
        Action<Exception, string> logCompletionFailure)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.tablesSnapshotProvider = tablesSnapshotProvider ?? throw new ArgumentNullException(nameof(tablesSnapshotProvider));
        this.hydrationPublishLeaseProvider = hydrationPublishLeaseProvider ?? throw new ArgumentNullException(nameof(hydrationPublishLeaseProvider));
        this.isShutdownRequested = isShutdownRequested ?? throw new ArgumentNullException(nameof(isShutdownRequested));
        this.startupSchedulerProvider = startupSchedulerProvider ?? throw new ArgumentNullException(nameof(startupSchedulerProvider));
        this.logPerformance = logPerformance ?? throw new ArgumentNullException(nameof(logPerformance));
        this.logCompletionFailure = logCompletionFailure ?? throw new ArgumentNullException(nameof(logCompletionFailure));
    }

    internal event Action<bool> RunningChanged;

    internal event Action<int> HydrationRequested;

    internal event Action<int> HydrationCompleted;

    internal event EventHandler<PlaylistEntriesHydrationReceiptEventArgs> HydrationReceiptPublished;

    internal bool PlaylistEntriesHydrationRunning => Volatile.Read(ref running) != 0;

    internal int PlaylistEntriesHydrationRequestedVersion => Volatile.Read(ref requestedVersion);

    internal int PlaylistEntriesHydrationCompletedVersion => Volatile.Read(ref completedVersion);

    internal bool HasBlockingWork => PlaylistEntriesHydrationRunning || Volatile.Read(ref queued) != 0;

    internal List<BMSTable> LoadPlaylistHeaders(out long loadTablesMs)
    {
        var stopwatch = Stopwatch.StartNew();
        List<BMSTable> tables = repository.LoadPlaylistHeaders();
        stopwatch.Stop();
        loadTablesMs = stopwatch.ElapsedMilliseconds;
        return tables;
    }

    internal void ClearPendingForShutdown(string reason)
    {
        int requestedVersionAtShutdown;
        lock (requestLock)
        {
            shutdownEpoch++;
            requestedVersionAtShutdown = requestedVersion;
            pendingRequest = false;
            pendingContinuation = null;
            if (!workStarted)
            {
                queued = 0;
            }
        }
        SetCompletedVersion(requestedVersionAtShutdown);
        logPerformance("playlist_entries_hydration pending_cleared reason=" + (reason ?? string.Empty));
    }

    internal void CompleteQueueForShutdown(string reason, string shutdownReason)
    {
        int requestedVersionAtCompletion;
        lock (requestLock)
        {
            requestedVersionAtCompletion = requestedVersion;
            pendingRequest = false;
            pendingContinuation = null;
            queued = 0;
            workStarted = false;
        }
        SetCompletedVersion(requestedVersionAtCompletion);
        logPerformance("playlist_entries_hydration skipped reason=" + (shutdownReason ?? "shutdown_requested")
            + " requestReason=" + (reason ?? string.Empty));
    }

    internal void QueueDeferredPlaylistEntriesHydration(
        string reason,
        PlaylistHydrationContinuationIntent continuation = null)
    {
        if (isShutdownRequested())
        {
            logPerformance("playlist_entries_hydration skipped reason=shutdown_requested requestReason=" + (reason ?? string.Empty));
            return;
        }

        string requestReason = reason ?? string.Empty;
        int version;
        bool shouldSchedule;
        lock (requestLock)
        {
            if (isShutdownRequested())
            {
                logPerformance("playlist_entries_hydration skipped reason=shutdown_requested requestReason=" + requestReason);
                return;
            }
            version = Interlocked.Increment(ref requestedVersion);
            pendingRequest = true;
            pendingContinuation = PlaylistHydrationContinuationIntent.Merge(pendingContinuation, continuation);
            shouldSchedule = queued == 0;
            queued = 1;
        }
        HydrationRequested?.Invoke(version);
        logPerformance("playlist_entries_hydration queue reason=" + requestReason
            + " version=" + version
            + " runExternalSyncAfterHydration="
            + (continuation?.RunExternalSyncAfterHydration == true).ToString().ToLowerInvariant()
            + " queueBeatorajaBmtExportAfterHydration="
            + (continuation?.QueueBeatorajaBmtExportAfterHydration == true).ToString().ToLowerInvariant());

        if (!shouldSchedule)
        {
            logPerformance("playlist_entries_hydration coalesced reason=" + requestReason + " version=" + version);
            return;
        }

        void ScheduleWork(Func<Task> work, string scheduledRequestReason)
        {
            Func<string, string, string, Func<Task>, bool> startupScheduler = startupSchedulerProvider();
            if (startupScheduler != null)
            {
                if (startupScheduler("playlist_entries_hydration", scheduledRequestReason, null, work))
                {
                    return;
                }
                CompleteQueueForShutdown(scheduledRequestReason, "startup_scheduler_rejected");
                return;
            }
            if (isShutdownRequested())
            {
                CompleteQueueForShutdown(scheduledRequestReason, "shutdown_requested");
                return;
            }
            Task.Run(work).ObserveFault("QueueDeferredPlaylistEntriesHydration");
        }

        async Task Work()
        {
            bool failed = false;
            PlaylistHydrationContinuationIntent failedContinuation = null;
            bool failedRetryRequested = false;
            int startedRequestVersion;
            int workShutdownEpoch;
            lock (requestLock)
            {
                workShutdownEpoch = shutdownEpoch;
                startedRequestVersion = requestedVersion;
                workStarted = true;
            }
            try
            {
                if (IsShutdownOrEpochChanged(workShutdownEpoch))
                {
                    SetCompletedVersion(PlaylistEntriesHydrationRequestedVersion);
                    logPerformance("playlist_entries_hydration skipped reason=shutdown_requested requestReason=" + requestReason);
                    return;
                }

                while (true)
                {
                    PlaylistHydrationResult hydrationResult = null;
                    IDisposable hydrationPublishLease = null;
                    while (hydrationPublishLease == null)
                    {
                        hydrationResult = await EnsureAllPlaylistEntriesLoadedAsync(requestReason, publishCompletedVersion: false).ConfigureAwait(false);
                        if (IsShutdownOrEpochChanged(workShutdownEpoch))
                        {
                            SetCompletedVersion(PlaylistEntriesHydrationRequestedVersion);
                            logPerformance("playlist_entries_hydration post_load_skipped reason=shutdown_requested requestReason=" + requestReason);
                            return;
                        }
                        if (!IsCurrentGeneration(hydrationResult.Generation))
                        {
                            continue;
                        }
                        hydrationPublishLease = hydrationPublishLeaseProvider(hydrationResult.Generation);
                        if (hydrationPublishLease == null)
                        {
                            await Task.Yield();
                        }
                    }

                    try
                    {
                        PlaylistHydrationContinuationIntent mergedContinuation;
                        int batchVersion;
                        bool shutdownAfterDrain;
                        lock (requestLock)
                        {
                            shutdownAfterDrain = isShutdownRequested() || shutdownEpoch != workShutdownEpoch;
                            batchVersion = PlaylistEntriesHydrationRequestedVersion;
                            if (shutdownAfterDrain)
                            {
                                pendingRequest = false;
                                pendingContinuation = null;
                                mergedContinuation = null;
                            }
                            else
                            {
                                mergedContinuation = pendingContinuation;
                                pendingRequest = false;
                                pendingContinuation = null;
                            }
                        }
                        if (shutdownAfterDrain)
                        {
                            SetCompletedVersion(batchVersion);
                            return;
                        }

                        if (!IsCurrentGeneration(hydrationResult.Generation))
                        {
                            lock (requestLock)
                            {
                                pendingRequest = true;
                                pendingContinuation = PlaylistHydrationContinuationIntent.Merge(
                                    pendingContinuation,
                                    mergedContinuation);
                            }
                            continue;
                        }
                        if (IsShutdownOrEpochChanged(workShutdownEpoch))
                        {
                            SetCompletedVersion(batchVersion);
                            return;
                        }

                        PlaylistEntriesHydrationReceipt receipt = CreateHydrationReceipt(
                            hydrationResult.Generation,
                            workShutdownEpoch,
                            batchVersion,
                            requestReason,
                            mergedContinuation);
                        if (receipt == null)
                        {
                            lock (requestLock)
                            {
                                pendingRequest = true;
                                pendingContinuation = PlaylistHydrationContinuationIntent.Merge(
                                    pendingContinuation,
                                    mergedContinuation);
                            }
                            continue;
                        }
                        hydrationPublishLease.Dispose();
                        hydrationPublishLease = null;
                        if (IsShutdownOrEpochChanged(workShutdownEpoch))
                        {
                            SetCompletedVersion(batchVersion);
                            return;
                        }
                        failedContinuation = mergedContinuation;
                        try
                        {
                            PublishHydrationReceipt(
                                receipt,
                                out failedContinuation,
                                out failedRetryRequested);
                        }
                        catch (Exception) when (failedRetryRequested)
                        {
                            if (IsShutdownOrEpochChanged(workShutdownEpoch))
                            {
                                SetCompletedVersion(PlaylistEntriesHydrationRequestedVersion);
                                return;
                            }
                            lock (requestLock)
                            {
                                pendingRequest = true;
                                pendingContinuation = PlaylistHydrationContinuationIntent.Merge(
                                    pendingContinuation,
                                    failedContinuation);
                            }
                            failedContinuation = null;
                            failedRetryRequested = false;
                            logPerformance("playlist_entries_hydration retry reason=" + requestReason);
                            continue;
                        }
                        if (IsShutdownOrEpochChanged(workShutdownEpoch))
                        {
                            SetCompletedVersion(batchVersion);
                            return;
                        }
                        SetCompletedVersion(batchVersion);
                        return;
                    }
                    finally
                    {
                        hydrationPublishLease?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                failed = true;
                lock (requestLock)
                {
                    bool independentRequest = pendingRequest
                        && requestedVersion > startedRequestVersion;
                    if (failedRetryRequested)
                    {
                        pendingContinuation = PlaylistHydrationContinuationIntent.Merge(
                            pendingContinuation,
                            failedContinuation);
                        pendingRequest = true;
                    }
                    else if (!independentRequest)
                    {
                        pendingRequest = false;
                        pendingContinuation = null;
                    }
                }
                logCompletionFailure(ex, requestReason);
                throw;
            }
            finally
            {
                bool hasPendingRequest;
                lock (requestLock)
                {
                    if (failed)
                    {
                        failedThroughVersion = Math.Max(failedThroughVersion, requestedVersion);
                        hasPendingRequest = !isShutdownRequested()
                            && shutdownEpoch == workShutdownEpoch
                            && pendingRequest;
                        queued = hasPendingRequest ? 1 : 0;
                        workStarted = false;
                    }
                    else
                    {
                        hasPendingRequest = !isShutdownRequested()
                            && shutdownEpoch == workShutdownEpoch
                            && (pendingRequest || pendingContinuation != null);
                        queued = hasPendingRequest ? 1 : 0;
                        workStarted = false;
                    }
                }
                if (hasPendingRequest)
                {
                    logPerformance("playlist_entries_hydration reschedule reason=" + requestReason);
                    ScheduleWork(Work, requestReason);
                }
            }
        }

        ScheduleWork(Work, requestReason);
    }

    internal async Task<PlaylistHydrationResult> EnsureAllPlaylistEntriesLoadedAsync(string reason, bool publishCompletedVersion = true)
    {
        string requestReason = reason ?? string.Empty;
        if (isShutdownRequested())
        {
            SetCompletedVersion(PlaylistEntriesHydrationRequestedVersion);
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
                    if (publishCompletedVersion)
                    {
                        PublishDirectCompletionVersionIfNoPostLoadWork();
                    }
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
                if (publishCompletedVersion)
                {
                    PublishDirectCompletionVersionIfNoPostLoadWork();
                }
                return new PlaylistHydrationResult(currentSnapshot.Generation);
            }
        }
        catch (Exception ex)
        {
            List<BMSTable> tablesToMarkFailed = GetTablesSnapshot().Tables
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

    private void SetCompletedVersion(int value)
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
                HydrationCompleted?.Invoke(value);
                return;
            }
        }
    }

    private PlaylistEntriesHydrationReceipt CreateHydrationReceipt(
        long generation,
        int shutdownEpoch,
        int requestVersion,
        string reason,
        PlaylistHydrationContinuationIntent continuation)
    {
        return TryCreateStableReceipt(
            generation,
            shutdownEpoch,
            requestVersion,
            reason,
            continuation);
    }

    internal PlaylistEntriesHydrationReceipt CreateReceiptForCurrentTables(
        PlaylistEntriesHydrationReceipt source,
        PlaylistHydrationContinuationIntent continuation)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }
        return TryCreateStableReceipt(
                requiredGeneration: null,
                requiredShutdownEpoch: source.ShutdownEpoch,
                requestVersion: source.RequestVersion,
                reason: source.Reason,
                continuation: continuation)
            ?? throw new InvalidOperationException("Playlist hydration receipt snapshot changed while composing the consumer receipt.");
    }

    private PlaylistEntriesHydrationReceipt TryCreateStableReceipt(
        long? requiredGeneration,
        int? requiredShutdownEpoch,
        int requestVersion,
        string reason,
        PlaylistHydrationContinuationIntent continuation)
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
                continuation);
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

    private void PublishHydrationReceipt(
        PlaylistEntriesHydrationReceipt receipt,
        out PlaylistHydrationContinuationIntent retryContinuation,
        out bool retryRequested)
    {
        retryContinuation = receipt?.Continuation;
        retryRequested = false;
        if (receipt == null)
        {
            return;
        }
        PlaylistEntriesHydrationReceiptEventArgs eventArgs = new(receipt);
        try
        {
            HydrationReceiptPublished?.Invoke(this, eventArgs);
        }
        catch (Exception ex)
        {
            retryContinuation = eventArgs.RetryContinuation ?? retryContinuation;
            retryRequested = eventArgs.RetryRequested;
            logCompletionFailure(ex, receipt.Reason);
            throw;
        }
        if (eventArgs.CompositionFailure != null)
        {
            retryContinuation = eventArgs.RetryContinuation ?? retryContinuation;
            retryRequested = eventArgs.RetryRequested;
            logCompletionFailure(eventArgs.CompositionFailure, receipt.Reason);
            throw new InvalidOperationException(
                "Playlist hydration consumer composition failed.",
                eventArgs.CompositionFailure);
        }
    }

    private void PublishDirectCompletionVersionIfNoPostLoadWork()
    {
        int completionVersion;
        lock (requestLock)
        {
            if (workStarted
                || pendingContinuation != null)
            {
                return;
            }
            completionVersion = requestedVersion;
            if (completionVersion <= failedThroughVersion)
            {
                return;
            }
        }
        SetCompletedVersion(completionVersion);
    }

}
