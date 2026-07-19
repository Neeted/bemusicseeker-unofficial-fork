using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Ribbit.Util.Extensions;
using PlaylistTableUpdateContext = BeMusicSeeker.Models.BMSPlaylist.PlaylistTableUpdateContext;

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

    private readonly Action<bool, IReadOnlyList<Action<PlaylistTableUpdateContext>>> updateTables;

    private readonly Action<string> logPerformance;

    private readonly Action<Exception, string> logCompletionFailure;

    private readonly SemaphoreSlim hydrationSemaphore = new(1, 1);

    private readonly object requestLock = new();

    private readonly List<Action<PlaylistTableUpdateContext>> pendingUpdateCallbacks = [];

    private readonly List<Action> pendingCompletionActions = [];

    private bool pendingRunExternalSync;

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

    internal PlaylistEntriesHydrationOwner(
        PlaylistPersistenceRepository repository,
        Func<PlaylistHydrationTableSnapshot> tablesSnapshotProvider,
        Func<long, IDisposable> hydrationPublishLeaseProvider,
        Func<bool> isShutdownRequested,
        Func<Func<string, string, string, Func<Task>, bool>> startupSchedulerProvider,
        Action<bool, IReadOnlyList<Action<PlaylistTableUpdateContext>>> updateTables,
        Action<string> logPerformance,
        Action<Exception, string> logCompletionFailure)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.tablesSnapshotProvider = tablesSnapshotProvider ?? throw new ArgumentNullException(nameof(tablesSnapshotProvider));
        this.hydrationPublishLeaseProvider = hydrationPublishLeaseProvider ?? throw new ArgumentNullException(nameof(hydrationPublishLeaseProvider));
        this.isShutdownRequested = isShutdownRequested ?? throw new ArgumentNullException(nameof(isShutdownRequested));
        this.startupSchedulerProvider = startupSchedulerProvider ?? throw new ArgumentNullException(nameof(startupSchedulerProvider));
        this.updateTables = updateTables ?? throw new ArgumentNullException(nameof(updateTables));
        this.logPerformance = logPerformance ?? throw new ArgumentNullException(nameof(logPerformance));
        this.logCompletionFailure = logCompletionFailure ?? throw new ArgumentNullException(nameof(logCompletionFailure));
    }

    internal event PropertyChangedEventHandler PropertyChanged;

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
            pendingRunExternalSync = false;
            pendingUpdateCallbacks.Clear();
            pendingCompletionActions.Clear();
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
            pendingRunExternalSync = false;
            pendingUpdateCallbacks.Clear();
            pendingCompletionActions.Clear();
            queued = 0;
            workStarted = false;
        }
        SetCompletedVersion(requestedVersionAtCompletion);
        logPerformance("playlist_entries_hydration skipped reason=" + (shutdownReason ?? "shutdown_requested")
            + " requestReason=" + (reason ?? string.Empty));
    }

    internal void QueueDeferredPlaylistEntriesHydration(
        string reason,
        bool runExternalSyncAfterHydration = false,
        IReadOnlyList<Action<PlaylistTableUpdateContext>> updateCallbackActions = null,
        IReadOnlyList<Action> completionActions = null)
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
            pendingRunExternalSync |= runExternalSyncAfterHydration;
            if (updateCallbackActions != null)
            {
                pendingUpdateCallbacks.AddRange(updateCallbackActions.Where(action => action != null));
            }
            if (completionActions != null)
            {
                pendingCompletionActions.AddRange(completionActions.Where(action => action != null));
            }
            shouldSchedule = queued == 0;
            queued = 1;
        }
        RaisePropertyChanged(nameof(PlaylistEntriesHydrationRequestedVersion));
        logPerformance("playlist_entries_hydration queue reason=" + requestReason
            + " version=" + version
            + " runExternalSyncAfterHydration=" + runExternalSyncAfterHydration.ToString().ToLowerInvariant());

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
            Task.Run(work).Logging("QueueDeferredPlaylistEntriesHydration");
        }

        async Task Work()
        {
            bool failed = false;
            int workShutdownEpoch;
            lock (requestLock)
            {
                workShutdownEpoch = shutdownEpoch;
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
                        bool mergedRunExternalSync;
                        List<Action<PlaylistTableUpdateContext>> mergedUpdateCallbacks;
                        List<Action> mergedCompletionActions;
                        int batchVersion;
                        bool shutdownAfterDrain;
                        lock (requestLock)
                        {
                            shutdownAfterDrain = isShutdownRequested() || shutdownEpoch != workShutdownEpoch;
                            batchVersion = PlaylistEntriesHydrationRequestedVersion;
                            if (shutdownAfterDrain)
                            {
                                pendingRequest = false;
                                pendingRunExternalSync = false;
                                pendingUpdateCallbacks.Clear();
                                pendingCompletionActions.Clear();
                                mergedRunExternalSync = false;
                                mergedUpdateCallbacks = [];
                                mergedCompletionActions = [];
                            }
                            else
                            {
                                mergedRunExternalSync = pendingRunExternalSync;
                                mergedUpdateCallbacks = [.. pendingUpdateCallbacks];
                                mergedCompletionActions = [.. pendingCompletionActions];
                                pendingRequest = false;
                                pendingRunExternalSync = false;
                                pendingUpdateCallbacks.Clear();
                                pendingCompletionActions.Clear();
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
                                pendingRunExternalSync |= mergedRunExternalSync;
                                pendingUpdateCallbacks.InsertRange(0, mergedUpdateCallbacks);
                                pendingCompletionActions.InsertRange(0, mergedCompletionActions);
                            }
                            continue;
                        }
                        if (IsShutdownOrEpochChanged(workShutdownEpoch))
                        {
                            SetCompletedVersion(batchVersion);
                            return;
                        }

                        if (mergedRunExternalSync || mergedUpdateCallbacks.Count > 0)
                        {
                            var stopwatchUpdateTables = Stopwatch.StartNew();
                            updateTables(mergedRunExternalSync, mergedUpdateCallbacks);
                            stopwatchUpdateTables.Stop();
                            logPerformance("playlist_entries_hydration post_update_tables reason=" + requestReason
                                + " reloadExtPlaylist=" + mergedRunExternalSync.ToString().ToLowerInvariant()
                                + " callbackCount=" + mergedUpdateCallbacks.Count
                                + " elapsedMs=" + stopwatchUpdateTables.ElapsedMilliseconds);
                        }
                        if (IsShutdownOrEpochChanged(workShutdownEpoch))
                        {
                            SetCompletedVersion(batchVersion);
                            return;
                        }

                        foreach (Action completionAction in mergedCompletionActions)
                        {
                            if (IsShutdownOrEpochChanged(workShutdownEpoch))
                            {
                                SetCompletedVersion(batchVersion);
                                return;
                            }
                            try
                            {
                                completionAction();
                            }
                            catch (Exception ex)
                            {
                                logCompletionFailure(ex, requestReason);
                            }
                        }
                        SetCompletedVersion(batchVersion);
                        return;
                    }
                    finally
                    {
                        hydrationPublishLease.Dispose();
                    }
                }
            }
            catch
            {
                failed = true;
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
                        pendingRequest = false;
                        pendingRunExternalSync = false;
                        pendingUpdateCallbacks.Clear();
                        pendingCompletionActions.Clear();
                        queued = 0;
                        workStarted = false;
                        hasPendingRequest = false;
                    }
                    else
                    {
                        hasPendingRequest = !isShutdownRequested()
                            && shutdownEpoch == workShutdownEpoch
                            && (pendingRequest
                                || pendingRunExternalSync
                                || pendingUpdateCallbacks.Count > 0
                                || pendingCompletionActions.Count > 0);
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

    private bool IsShutdownOrEpochChanged(int workShutdownEpoch)
    {
        return isShutdownRequested() || Volatile.Read(ref shutdownEpoch) != workShutdownEpoch;
    }

    private void SetRunning(bool value)
    {
        if (Interlocked.Exchange(ref running, value ? 1 : 0) != (value ? 1 : 0))
        {
            RaisePropertyChanged(nameof(PlaylistEntriesHydrationRunning));
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
                RaisePropertyChanged(nameof(PlaylistEntriesHydrationCompletedVersion));
                return;
            }
        }
    }

    private void PublishDirectCompletionVersionIfNoPostLoadWork()
    {
        int completionVersion;
        lock (requestLock)
        {
            if (workStarted
                || pendingRunExternalSync
                || pendingUpdateCallbacks.Count > 0
                || pendingCompletionActions.Count > 0)
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

    private void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
