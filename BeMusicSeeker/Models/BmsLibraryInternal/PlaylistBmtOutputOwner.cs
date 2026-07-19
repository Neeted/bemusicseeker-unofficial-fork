using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Newtonsoft.Json.Linq;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// beatoraja の playlist BMT ファイル、manifest、config URL 同期を所有します。
/// <para>
/// 準備した immutable な table snapshot は playlist aggregate / hydration owner から取得し、
/// durable file mutation と live config apply はこの owner の gate で直列化します。
/// </para>
/// </summary>
internal sealed class PlaylistBmtOutputOwner
{
    private readonly PlaylistAggregatePersistenceOwner playlistAggregatePersistenceOwner;

    private readonly PlaylistEntriesHydrationOwner playlistEntriesHydrationOwner;

    private readonly Func<BeatorajaBmtOptionsSnapshot> optionsProvider;

    private readonly Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> songHashResolverFactory;

    private readonly Func<Func<string, string, string, Func<Task>, bool>> startupSchedulerProvider;

    private readonly Action<string> logPerformance;

    private readonly Action<Exception, string> logWarning;

    private readonly Func<bool> isShutdownRequested;

    private readonly object exportQueueLock = new();

    private readonly object fileMutationLock = new();

    private readonly HashSet<int> pendingExportPlaylistIds = [];

    private int exportQueued;

    private long fullExportGeneration;

    private long urlSyncGeneration;

    private int fullExportActiveCount;

    private int backgroundActiveCount;

    private long exportProgressOperationSeed;

    internal PlaylistBmtOutputOwner(
        PlaylistAggregatePersistenceOwner playlistAggregatePersistenceOwner,
        PlaylistEntriesHydrationOwner playlistEntriesHydrationOwner,
        Func<BeatorajaBmtOptionsSnapshot> optionsProvider,
        Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> songHashResolverFactory,
        Func<Func<string, string, string, Func<Task>, bool>> startupSchedulerProvider,
        Action<string> logPerformance,
        Action<Exception, string> logWarning,
        Func<bool> isShutdownRequested)
    {
        this.playlistAggregatePersistenceOwner = playlistAggregatePersistenceOwner ?? throw new ArgumentNullException(nameof(playlistAggregatePersistenceOwner));
        this.playlistEntriesHydrationOwner = playlistEntriesHydrationOwner ?? throw new ArgumentNullException(nameof(playlistEntriesHydrationOwner));
        this.optionsProvider = optionsProvider ?? throw new ArgumentNullException(nameof(optionsProvider));
        this.songHashResolverFactory = songHashResolverFactory;
        this.startupSchedulerProvider = startupSchedulerProvider;
        this.logPerformance = logPerformance;
        this.logWarning = logWarning;
        this.isShutdownRequested = isShutdownRequested ?? throw new ArgumentNullException(nameof(isShutdownRequested));
    }

    internal Action<PlaylistSyncProgressSnapshot> ExportProgressReporter { get; set; }

    internal bool IsShutdownRequested => isShutdownRequested();

    internal bool HasBlockingWork =>
        IsFullExportActive()
        || Volatile.Read(ref backgroundActiveCount) != 0
        || Volatile.Read(ref exportQueued) != 0;

    internal string GetShutdownBlockingWorkLogFields()
    {
        return "beatorajaBmtFullExportActive=" + FormatBool(IsFullExportActive())
            + " beatorajaBmtBackgroundActiveCount=" + Volatile.Read(ref backgroundActiveCount)
            + " beatorajaBmtExportQueued=" + Volatile.Read(ref exportQueued);
    }

    internal void RequestShutdown(string reason)
    {
        Interlocked.Increment(ref fullExportGeneration);
        Interlocked.Increment(ref urlSyncGeneration);
        Interlocked.Exchange(ref exportQueued, 0);
        lock (exportQueueLock)
        {
            pendingExportPlaylistIds.Clear();
        }
        Log("beatoraja_bmt_output shutdown requested reason=" + FormatTextForLog(reason));
    }

    internal static int NormalizePersistedBeatorajaBmtPlaylistSettings(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null)];
        return NormalizeBeatorajaBmtSortOrder(tableList) + NormalizeBeatorajaBmtOutputTargets(tableList);
    }

    internal static int NormalizeBeatorajaBmtSortOrder(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> orderedTables = [.. (tables ?? [])
            .Where(table => table != null)
            .OrderBy(table => IsValidBeatorajaBmtSort(table.bmt_sort) ? 0 : 1)
            .ThenBy(table => GetBeatorajaBmtSortOrTail(table.bmt_sort))
            .ThenBy(table => table.name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(table => table.playlist_id ?? int.MaxValue)];
        int changedCount = 0;
        for (int index = 0; index < orderedTables.Count; index++)
        {
            int normalizedSort = index + 1;
            if (orderedTables[index].bmt_sort != normalizedSort)
            {
                orderedTables[index].bmt_sort = normalizedSort;
                changedCount++;
            }
        }
        return changedCount;
    }

    internal static int ResolveNextBeatorajaBmtSort(IEnumerable<BMSTable> tables)
    {
        return ((tables ?? [])
            .Where(table => table != null && IsValidBeatorajaBmtSort(table.bmt_sort))
            .Select(table => table.bmt_sort.Value)
            .DefaultIfEmpty(0)
            .Max()) + 1;
    }

    internal void QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath = null)
    {
        if (TrySkipForShutdown("beatoraja_bmt_export_all", reason))
        {
            return;
        }
        BeatorajaBmtOptionsSnapshot options = GetOptions();
        string outputPath = GetTablePath(options);
        bool enabled = IsOutputEnabled(options);
        bool keepFilesWhenDisabled = options.KeepBeatorajaBmtFilesWhenOutputDisabled;
        long generation = Interlocked.Increment(ref fullExportGeneration);
        Interlocked.Increment(ref urlSyncGeneration);
        async Task Work()
        {
            Interlocked.Increment(ref fullExportActiveCount);
            try
            {
                await Task.Yield();
                if (IsShutdownRequested)
                {
                    Log("beatoraja_bmt_export_all skipped reason=shutdown_requested requestReason=" + FormatTextForLog(reason));
                    return;
                }
                if (!IsCurrentFullExportGeneration(generation))
                {
                    return;
                }
                var totalStopwatch = Stopwatch.StartNew();
                long progressOperationId = Interlocked.Increment(ref exportProgressOperationSeed);
                if (!string.IsNullOrWhiteSpace(cleanupTablePath)
                    && (!enabled || !IsSameTablePath(cleanupTablePath, outputPath))
                    && (enabled || !keepFilesWhenDisabled))
                {
                    lock (fileMutationLock)
                    {
                        if (!IsCurrentFullExportGeneration(generation))
                        {
                            return;
                        }
                        SyncManagedTableUrls(cleanupTablePath, BmtTableExportService.ReadManagedTableUrls(cleanupTablePath), []);
                        BmtTableExportService.CleanupManagedFiles(cleanupTablePath);
                    }
                }
                if (!enabled)
                {
                    if (!keepFilesWhenDisabled)
                    {
                        lock (fileMutationLock)
                        {
                            if (!IsCurrentFullExportGeneration(generation))
                            {
                                return;
                            }
                            SyncManagedTableUrls(outputPath, BmtTableExportService.ReadManagedTableUrls(outputPath), []);
                        }
                    }
                    totalStopwatch.Stop();
                    Log("beatoraja_bmt_export_all skipped reason=" + FormatTextForLog(reason)
                        + " enabled=false keepFiles=" + keepFilesWhenDisabled
                        + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds);
                    return;
                }

                var snapshotStopwatch = Stopwatch.StartNew();
                List<BMSTable> tablesSnapshot = GetTablesSnapshot();
                snapshotStopwatch.Stop();
                bool progressStarted = false;
                try
                {
                    List<BMSTable> outputTablesSnapshot = [.. tablesSnapshot
                        .Where(IsBeatorajaBmtOutputTarget)
                        .OrderBy(table => GetBeatorajaBmtSortOrTail(table.bmt_sort))
                        .ThenBy(table => table.name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(table => table.playlist_id ?? int.MaxValue)];
                    List<Tuple<BMSTable, BmtTableExportService.PlaylistExportMetadata>> outputTargets = [.. outputTablesSnapshot
                        .Select(table => Tuple.Create(table, BmtTableExportService.CreatePlaylistExportMetadata(table)))];
                    BmtTableExportService.ExportPlan exportPlan = BmtTableExportService.CreateExportPlan(
                        outputPath,
                        outputTargets.Select(target => target.Item2),
                        cleanupStaleManagedFiles: true);
                    List<BMSTable> projectionTablesSnapshot = [.. outputTargets
                        .Where(target => exportPlan.RequiresProjection(target.Item2))
                        .Select(target => target.Item1)];
                    bool shouldReportProgress = projectionTablesSnapshot.Count > 0;
                    if (shouldReportProgress)
                    {
                        ReportProgress(progressOperationId, true, projectionTablesSnapshot.Count, 0, string.Empty);
                        progressStarted = true;
                    }
                    var resolverStopwatch = Stopwatch.StartNew();
                    BeatorajaBmtHashOutputMode hashOutputMode = GetHashOutputMode();
                    Func<BmtSongHashResolveRequest, Tuple<string, string>> hashResolverFunc = projectionTablesSnapshot.Count == 0 || hashOutputMode == BeatorajaBmtHashOutputMode.Original
                        ? null
                        : songHashResolverFactory?.Invoke();
                    resolverStopwatch.Stop();
                    var projectionStopwatch = Stopwatch.StartNew();
                    List<Tuple<string, JObject>> tableDataSet = BuildTableDataSetSnapshot(
                        projectionTablesSnapshot,
                        reason,
                        hashOutputMode,
                        hashResolverFunc,
                        shouldReportProgress
                            ? (completed, total, tableName) => ReportProgress(progressOperationId, true, Math.Max(total, 1), completed, tableName)
                            : null);
                    projectionStopwatch.Stop();
                    var exportStopwatch = Stopwatch.StartNew();
                    int exportProgressTotal = projectionTablesSnapshot.Count + tableDataSet.Count;
                    BmtTableExportService.ExportResult exportResult;
                    lock (fileMutationLock)
                    {
                        if (!IsCurrentFullExportGeneration(generation))
                        {
                            return;
                        }
                        exportResult = BmtTableExportService.ExportTableDataSet(
                            outputPath,
                            tableDataSet,
                            exportPlan,
                            shouldReportProgress
                                ? (completed, total, tableName) => ReportProgress(progressOperationId, true, Math.Max(exportProgressTotal, 1), projectionTablesSnapshot.Count + completed, tableName)
                                : null);
                        var urlSyncStopwatch = Stopwatch.StartNew();
                        SyncManagedTableUrls(outputPath, exportResult.PreviousManagedTables, exportResult.CurrentManagedTables);
                        urlSyncStopwatch.Stop();
                        exportStopwatch.Stop();
                        totalStopwatch.Stop();
                        Log("beatoraja_bmt_export_all completed reason=" + FormatTextForLog(reason)
                            + " tableCount=" + tablesSnapshot.Count
                            + " enabledOutputCount=" + outputTablesSnapshot.Count
                            + " outputCount=" + tableDataSet.Count
                            + " written=" + exportResult.WrittenCount
                            + " skipped=" + exportResult.SkippedWriteCount
                            + " removed=" + exportResult.RemovedCount
                            + " snapshotMs=" + snapshotStopwatch.ElapsedMilliseconds
                            + " resolverMs=" + resolverStopwatch.ElapsedMilliseconds
                            + " projectionMs=" + projectionStopwatch.ElapsedMilliseconds
                            + " exportMs=" + exportStopwatch.ElapsedMilliseconds
                            + " urlSyncMs=" + urlSyncStopwatch.ElapsedMilliseconds
                            + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds);
                    }
                }
                finally
                {
                    if (progressStarted)
                    {
                        ReportProgress(progressOperationId, false, 0, 0, string.Empty);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref fullExportActiveCount);
            }
        }
        if (TrySchedule("beatoraja_bmt_export_all", reason, null, Work, out bool schedulerAvailable))
        {
            return;
        }
        if (schedulerAvailable)
        {
            return;
        }
        if (IsShutdownRequested)
        {
            Log("beatoraja_bmt_export_all skipped reason=shutdown_requested requestReason=" + FormatTextForLog(reason));
            return;
        }
        Task.Run(Work).Logging("QueueBeatorajaBmtExportAll");
    }

    internal void QueueBeatorajaBmtExportForTable(BMSTable table, string reason)
    {
        QueueBeatorajaBmtExport(table, reason);
    }

    internal void QueueBeatorajaBmtExportForTables(IEnumerable<BMSTable> tables, string reason)
    {
        if (TrySkipForShutdown("beatoraja_bmt_export_tables", reason))
        {
            return;
        }
        if (!IsOutputEnabled(GetOptions()))
        {
            return;
        }
        List<int> playlistIds = [.. (tables ?? [])
            .Where(table => table?.playlist_id.HasValue == true)
            .Select(table => table.playlist_id.Value)
            .Distinct()];
        if (playlistIds.Count == 0)
        {
            return;
        }
        Interlocked.Increment(ref urlSyncGeneration);
        lock (exportQueueLock)
        {
            foreach (int playlistId in playlistIds)
            {
                pendingExportPlaylistIds.Add(playlistId);
            }
        }
        ScheduleBeatorajaBmtExportQueue(reason);
    }

    internal void QueueBeatorajaBmtRemoveForTable(BMSTable table, string reason)
    {
        if (TrySkipForShutdown("beatoraja_bmt_remove", reason))
        {
            return;
        }
        if (!IsOutputEnabled(GetOptions()) || table?.playlist_id.HasValue != true)
        {
            return;
        }
        string tablePath = GetTablePath();
        int playlistId = table.playlist_id.Value;
        Interlocked.Increment(ref urlSyncGeneration);
        string playlistIdentity = playlistId.ToString(CultureInfo.InvariantCulture);
        async Task Work()
        {
            Interlocked.Increment(ref backgroundActiveCount);
            try
            {
                await Task.Yield();
                while (!IsShutdownRequested && IsFullExportActive())
                {
                    await Task.Delay(250);
                }
                lock (fileMutationLock)
                {
                    if (IsShutdownRequested
                        || !IsOutputEnabled(GetOptions())
                        || !IsSameTablePath(tablePath, GetTablePath())
                        || FindTableByPlaylistId(playlistId) != null)
                    {
                        return;
                    }
                    BmtTableExportService.ExportResult exportResult = BmtTableExportService.RemoveManagedPlaylist(tablePath, playlistIdentity);
                    SyncManagedTableUrls(tablePath, exportResult.PreviousManagedTables, exportResult.CurrentManagedTables);
                    Log("beatoraja_bmt_remove completed reason=" + FormatTextForLog(reason)
                        + " playlistId=" + playlistIdentity
                        + " removed=" + exportResult.RemovedCount);
                }
            }
            finally
            {
                Interlocked.Decrement(ref backgroundActiveCount);
            }
        }
        Task.Run(Work).Logging("QueueBeatorajaBmtRemove");
    }

    internal void QueueBeatorajaBmtUrlSync(string reason)
    {
        if (TrySkipForShutdown("beatoraja_bmt_url_sync", reason) || !IsOutputEnabled(GetOptions()))
        {
            return;
        }
        long generation = Interlocked.Increment(ref urlSyncGeneration);
        async Task Work()
        {
            Interlocked.Increment(ref backgroundActiveCount);
            try
            {
                await Task.Yield();
                lock (fileMutationLock)
                {
                    if (IsShutdownRequested || generation != Interlocked.Read(ref urlSyncGeneration))
                    {
                        return;
                    }
                    string tablePath = GetTablePath();
                    List<BmtTableExportService.ManagedTableUrlEntry> managedTables = BmtTableExportService.ReadManagedTableUrls(tablePath);
                    SyncManagedTableUrls(tablePath, managedTables, managedTables, generation);
                    Log("beatoraja_bmt_url_sync completed reason=" + FormatTextForLog(reason)
                        + " managedCount=" + managedTables.Count);
                }
            }
            finally
            {
                Interlocked.Decrement(ref backgroundActiveCount);
            }
        }
        Task.Run(Work).Logging("QueueBeatorajaBmtUrlSync");
    }

    private void QueueBeatorajaBmtExport(BMSTable table, string reason)
    {
        if (TrySkipForShutdown("beatoraja_bmt_export", reason)
            || !IsOutputEnabled(GetOptions())
            || table == null
            || !table.playlist_id.HasValue)
        {
            return;
        }
        Interlocked.Increment(ref urlSyncGeneration);
        lock (exportQueueLock)
        {
            pendingExportPlaylistIds.Add(table.playlist_id.Value);
        }
        ScheduleBeatorajaBmtExportQueue(reason);
    }

    private void ScheduleBeatorajaBmtExportQueue(string reason)
    {
        if (TrySkipForShutdown("beatoraja_bmt_export_schedule", reason)
            || Interlocked.Exchange(ref exportQueued, 1) != 0)
        {
            return;
        }
        async Task Work()
        {
            Interlocked.Increment(ref backgroundActiveCount);
            try
            {
                await Task.Yield();
                if (IsShutdownRequested)
                {
                    return;
                }
                if (IsFullExportActive())
                {
                    await Task.Delay(250);
                    return;
                }
                ProcessBeatorajaBmtExportQueue(reason);
            }
            finally
            {
                Interlocked.Decrement(ref backgroundActiveCount);
                Interlocked.Exchange(ref exportQueued, 0);
                bool hasPending;
                lock (exportQueueLock)
                {
                    hasPending = pendingExportPlaylistIds.Count > 0;
                }
                if (hasPending && !IsShutdownRequested)
                {
                    ScheduleBeatorajaBmtExportQueue(reason ?? "reschedule");
                }
            }
        }
        if (TrySchedule("beatoraja_bmt_export", reason, null, Work, out bool schedulerAvailable))
        {
            return;
        }
        if (schedulerAvailable)
        {
            CompleteExportQueueForShutdown(reason, "startup_scheduler_rejected");
            return;
        }
        if (IsShutdownRequested)
        {
            CompleteExportQueueForShutdown(reason, "shutdown_requested");
            return;
        }
        Task.Run(Work).Logging("QueueBeatorajaBmtExport");
    }

    private void CompleteExportQueueForShutdown(string reason, string shutdownReason)
    {
        Interlocked.Exchange(ref exportQueued, 0);
        lock (exportQueueLock)
        {
            pendingExportPlaylistIds.Clear();
        }
        Log("beatoraja_bmt_export skipped reason=" + (shutdownReason ?? "shutdown_requested") + " requestReason=" + FormatTextForLog(reason));
    }

    private void ClearPendingExportQueue()
    {
        lock (exportQueueLock)
        {
            pendingExportPlaylistIds.Clear();
        }
    }

    private void ProcessBeatorajaBmtExportQueue(string reason)
    {
        try
        {
            if (!IsOutputEnabled(GetOptions()))
            {
                ClearPendingExportQueue();
                return;
            }
        }
        catch
        {
            ClearPendingExportQueue();
            throw;
        }
        while (!IsShutdownRequested)
        {
            bool outputEnabled;
            try
            {
                outputEnabled = IsOutputEnabled(GetOptions());
            }
            catch
            {
                ClearPendingExportQueue();
                throw;
            }
            if (!outputEnabled)
            {
                ClearPendingExportQueue();
                return;
            }
            List<int> playlistIds;
            lock (exportQueueLock)
            {
                if (pendingExportPlaylistIds.Count == 0)
                {
                    return;
                }
                playlistIds = [.. pendingExportPlaylistIds];
                pendingExportPlaylistIds.Clear();
            }
            foreach (int playlistId in playlistIds)
            {
                BMSTable table = FindTableByPlaylistId(playlistId);
                string tablePath = GetTablePath();
                BmtTableExportService.PlaylistExportMetadata exportMetadata = BmtTableExportService.CreatePlaylistExportMetadata(table);
                JObject tableData = BuildTableDataSnapshot(table, reason);
                lock (fileMutationLock)
                {
                    bool currentOutputEnabled;
                    string currentTablePath;
                    try
                    {
                        currentOutputEnabled = IsOutputEnabled(GetOptions());
                        currentTablePath = GetTablePath();
                    }
                    catch
                    {
                        ClearPendingExportQueue();
                        throw;
                    }
                    if (!currentOutputEnabled || !IsSameTablePath(tablePath, currentTablePath))
                    {
                        continue;
                    }
                    BMSTable currentTable = FindTableByPlaylistId(playlistId);
                    BmtTableExportService.PlaylistExportMetadata currentMetadata = BmtTableExportService.CreatePlaylistExportMetadata(currentTable);
                    bool preparedSnapshotIsCurrent = AreSameExportMetadata(exportMetadata, currentMetadata);
                    List<BmtTableExportService.ManagedTableUrlEntry> previousManagedTables = BmtTableExportService.ReadManagedTableUrls(tablePath);
                    if (tableData != null && IsBeatorajaBmtOutputTarget(currentTable) && preparedSnapshotIsCurrent)
                    {
                        BmtTableExportService.ExportTableData(tablePath, tableData, currentMetadata);
                        SyncManagedTableUrls(tablePath, previousManagedTables, BmtTableExportService.ReadManagedTableUrls(tablePath));
                    }
                    else if (currentTable == null || !IsBeatorajaBmtOutputTarget(currentTable))
                    {
                        BmtTableExportService.ExportResult exportResult = BmtTableExportService.RemoveManagedPlaylist(tablePath, playlistId.ToString(CultureInfo.InvariantCulture));
                        SyncManagedTableUrls(tablePath, exportResult.PreviousManagedTables, exportResult.CurrentManagedTables);
                    }
                    else if (tableData == null && preparedSnapshotIsCurrent)
                    {
                        BmtTableExportService.ExportResult exportResult = BmtTableExportService.UpdateManagedPlaylistUrlOwnership(tablePath, currentMetadata);
                        if (exportResult.Changed)
                        {
                            SyncManagedTableUrls(tablePath, exportResult.PreviousManagedTables, exportResult.CurrentManagedTables);
                        }
                    }
                    else
                    {
                        QueueBeatorajaBmtExport(currentTable, reason ?? "stale_snapshot_retry");
                    }
                }
            }
        }
    }

    private BMSTable FindTableByPlaylistId(int playlistId)
    {
        return GetTablesSnapshot().FirstOrDefault(candidate => candidate?.playlist_id == playlistId);
    }

    private static bool AreSameExportMetadata(BmtTableExportService.PlaylistExportMetadata left, BmtTableExportService.PlaylistExportMetadata right)
    {
        return left != null
            && right != null
            && string.Equals(left.PlaylistIdentity, right.PlaylistIdentity, StringComparison.Ordinal)
            && string.Equals(left.Url, right.Url, StringComparison.Ordinal)
            && string.Equals(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.HeaderSha256, right.HeaderSha256, StringComparison.Ordinal)
            && string.Equals(left.DataSha256, right.DataSha256, StringComparison.Ordinal)
            && left.LastUpdateTicks == right.LastUpdateTicks
            && string.Equals(left.ProjectionInputSha256, right.ProjectionInputSha256, StringComparison.Ordinal);
    }

    private BeatorajaBmtOptionsSnapshot GetOptions()
    {
        return optionsProvider() ?? throw new InvalidOperationException("beatoraja BMT options provider returned null.");
    }

    private static bool IsOutputEnabled(BeatorajaBmtOptionsSnapshot options)
    {
        return options.EnableBeatorajaBmtOutput && !string.IsNullOrWhiteSpace(GetTablePath(options));
    }

    private string GetTablePath()
    {
        return GetTablePath(GetOptions());
    }

    private static string GetTablePath(BeatorajaBmtOptionsSnapshot options)
    {
        if (!string.IsNullOrWhiteSpace(options.BeatorajaRootPath) && BeatorajaConfigService.IsBeatorajaRootPathValid(options.BeatorajaRootPath))
        {
            return BeatorajaConfigService.GetTablePath(options.BeatorajaRootPath);
        }
        return options.BeatorajaBmtTablePath;
    }

    private void SyncManagedTableUrls(
        string tablePath,
        IEnumerable<BmtTableExportService.ManagedTableUrlEntry> previousManagedTables,
        IEnumerable<BmtTableExportService.ManagedTableUrlEntry> currentManagedTables,
        long? generation = null)
    {
        BeatorajaBmtOptionsSnapshot options = GetOptions();
        if (string.IsNullOrWhiteSpace(options.BeatorajaRootPath) || !BeatorajaConfigService.IsBeatorajaRootPathValid(options.BeatorajaRootPath))
        {
            return;
        }
        string configuredTablePath = BeatorajaConfigService.GetTablePath(options.BeatorajaRootPath);
        if (!string.IsNullOrWhiteSpace(tablePath) && !IsSameTablePath(tablePath, configuredTablePath))
        {
            return;
        }
        List<string> previousUrls = [.. (previousManagedTables ?? [])
            .Select(entry => entry?.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))];
        IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> sortKeys = CreateTableUrlSortKeysSnapshot();
        List<string> currentUrls = options.RegisterBeatorajaBmtUrls
            ? BuildBeatorajaManagedTableUrlsForConfigSync(currentManagedTables, sortKeys)
            : [];
        try
        {
            BeatorajaConfigService.SyncTableUrls(
                options.BeatorajaRootPath,
                currentUrls,
                previousUrls,
                generation.HasValue
                    ? () => generation.Value == Interlocked.Read(ref urlSyncGeneration)
                    : null);
        }
        catch (Exception ex)
        {
            logWarning?.Invoke(ex, "beatoraja_table_url_sync_failed tablePath=" + FormatTextForLog(tablePath));
        }
    }

    private static bool IsSameTablePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        return string.Equals(LongPathFileSystem.NormalizePathForStorage(left), LongPathFileSystem.NormalizePathForStorage(right), StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> CreateTableUrlSortKeysSnapshot()
    {
        return GetTablesSnapshot()
            .Where(table => table?.playlist_id.HasValue == true)
            .GroupBy(GetPlaylistIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    BMSTable table = group.First();
                    return new BeatorajaBmtTableUrlSortKey
                    {
                        Sort = GetBeatorajaBmtSortOrTail(table.bmt_sort),
                        Name = table.name ?? string.Empty,
                        PlaylistId = table.playlist_id ?? int.MaxValue
                    };
                },
                StringComparer.Ordinal);
    }

    internal static List<string> BuildBeatorajaManagedTableUrlsForConfigSync(
        IEnumerable<BmtTableExportService.ManagedTableUrlEntry> currentManagedTables,
        IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> sortKeys)
    {
        return [.. (currentManagedTables ?? [])
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Url))
            .OrderBy(entry => ResolveTableUrlSortKey(entry, sortKeys).IsKnown ? 0 : 1)
            .ThenBy(entry => ResolveTableUrlSortKey(entry, sortKeys).Sort)
            .ThenBy(entry => ResolveTableUrlSortKey(entry, sortKeys).Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => ResolveTableUrlSortKey(entry, sortKeys).PlaylistId)
            .ThenBy(entry => entry.PlaylistIdentity ?? string.Empty, StringComparer.Ordinal)
            .Select(entry => entry.Url)];
    }

    private static BeatorajaBmtTableUrlSortKey ResolveTableUrlSortKey(
        BmtTableExportService.ManagedTableUrlEntry entry,
        IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> sortKeys)
    {
        if (entry != null
            && !string.IsNullOrEmpty(entry.PlaylistIdentity)
            && sortKeys != null
            && sortKeys.TryGetValue(entry.PlaylistIdentity, out BeatorajaBmtTableUrlSortKey key))
        {
            return key;
        }
        return new BeatorajaBmtTableUrlSortKey
        {
            IsKnown = false,
            Sort = int.MaxValue,
            Name = entry?.Name ?? string.Empty,
            PlaylistId = int.MaxValue
        };
    }

    internal sealed class BeatorajaBmtTableUrlSortKey
    {
        public bool IsKnown { get; set; } = true;

        public int Sort { get; set; }

        public string Name { get; set; }

        public int PlaylistId { get; set; }
    }

    private List<BMSTable> GetTablesSnapshot()
    {
        return [.. (playlistAggregatePersistenceOwner.GetActiveCollectionSnapshot()?.Tables ?? [])
            .Where(table => table != null)];
    }

    private static string GetPlaylistIdentity(BMSTable table)
    {
        return table?.playlist_id?.ToString(CultureInfo.InvariantCulture);
    }

    private List<Tuple<string, JObject>> BuildTableDataSetSnapshot(
        List<BMSTable> tablesSnapshot,
        string reason,
        BeatorajaBmtHashOutputMode hashOutputMode,
        Func<BmtSongHashResolveRequest, Tuple<string, string>> hashResolverFunc,
        Action<int, int, string> progressReporter)
    {
        if (tablesSnapshot == null || tablesSnapshot.Count == 0)
        {
            return [];
        }
        var projectionInputs = new BeatorajaBmtTableProjectionInput[tablesSnapshot.Count];
        for (int index = 0; index < tablesSnapshot.Count; index++)
        {
            projectionInputs[index] = CreateProjectionInput(tablesSnapshot[index], reason, index);
        }
        var projectionResults = new Tuple<string, JObject>[projectionInputs.Length];
        int projectedCount = 0;
        object progressLock = new();
        Parallel.ForEach(
            projectionInputs,
            new ParallelOptions { MaxDegreeOfParallelism = ResolveProjectionDegree(projectionInputs.Length) },
            input =>
            {
                if (input?.Snapshot != null)
                {
                    BmtTableExportService.ISongHashResolver hashResolver = hashResolverFunc == null ? null : new BeatorajaBmtSongHashResolver(hashResolverFunc);
                    JObject tableData = BmtTableExportService.BuildTableData(input.Snapshot, hashResolver, hashOutputMode);
                    if (tableData != null)
                    {
                        projectionResults[input.Index] = Tuple.Create(input.PlaylistIdentity, tableData);
                    }
                }
                lock (progressLock)
                {
                    projectedCount++;
                    progressReporter?.Invoke(projectedCount, projectionInputs.Length, input?.TableName);
                }
            });
        return [.. projectionResults.Where(result => result != null)];
    }

    private BeatorajaBmtTableProjectionInput CreateProjectionInput(BMSTable table, string reason, int index)
    {
        if (table == null)
        {
            return new BeatorajaBmtTableProjectionInput { Index = index };
        }
        playlistEntriesHydrationOwner.EnsurePlaylistEntriesLoaded(table, reason ?? "BeatorajaBmtExport");
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return new BeatorajaBmtTableProjectionInput
            {
                Index = index,
                PlaylistIdentity = GetPlaylistIdentity(table),
                TableName = table.name,
                Snapshot = BmtTableExportService.CreateProjectionSnapshot(table)
            };
        }
    }

    private JObject BuildTableDataSnapshot(BMSTable table, string reason)
    {
        BeatorajaBmtHashOutputMode hashOutputMode = GetHashOutputMode();
        Func<BmtSongHashResolveRequest, Tuple<string, string>> hashResolverFunc = hashOutputMode == BeatorajaBmtHashOutputMode.Original ? null : songHashResolverFactory?.Invoke();
        return BuildTableDataSnapshot(table, reason, hashOutputMode, hashResolverFunc);
    }

    private JObject BuildTableDataSnapshot(BMSTable table, string reason, BeatorajaBmtHashOutputMode hashOutputMode, Func<BmtSongHashResolveRequest, Tuple<string, string>> hashResolverFunc)
    {
        if (!IsBeatorajaBmtOutputTarget(table))
        {
            return null;
        }
        playlistEntriesHydrationOwner.EnsurePlaylistEntriesLoaded(table, reason ?? "BeatorajaBmtExport");
        BmtTableExportService.ISongHashResolver hashResolver = hashResolverFunc == null ? null : new BeatorajaBmtSongHashResolver(hashResolverFunc);
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return BmtTableExportService.BuildTableData(table, hashResolver, hashOutputMode);
        }
    }

    private BeatorajaBmtHashOutputMode GetHashOutputMode()
    {
        return BmtTableExportService.NormalizeHashOutputMode(GetOptions().BeatorajaBmtHashOutputMode);
    }

    private static int ResolveProjectionDegree(int count)
    {
        return Math.Max(1, Math.Min(Math.Min(Environment.ProcessorCount, 4), Math.Max(count, 1)));
    }

    private bool TrySkipForShutdown(string operation, string reason)
    {
        if (!IsShutdownRequested)
        {
            return false;
        }
        Log((operation ?? "background_work") + " skipped reason=shutdown_requested requestReason=" + FormatTextForLog(reason));
        return true;
    }

    private bool TrySchedule(string operation, string reason, string dependency, Func<Task> work, out bool schedulerAvailable)
    {
        Func<string, string, string, Func<Task>, bool> scheduler = startupSchedulerProvider?.Invoke();
        schedulerAvailable = scheduler != null;
        if (scheduler == null)
        {
            return false;
        }
        if (scheduler(operation, reason ?? "queue", dependency, work))
        {
            return true;
        }
        Log(operation + " skipped reason=startup_scheduler_rejected requestReason=" + FormatTextForLog(reason));
        return false;
    }

    private void ReportProgress(long operationId, bool isActive, int totalCount, int completedCount, string currentTableName)
    {
        ExportProgressReporter?.Invoke(new PlaylistSyncProgressSnapshot
        {
            IsActive = isActive,
            OperationId = operationId,
            TotalTableCount = totalCount,
            CompletedTableCount = completedCount,
            CurrentTableName = currentTableName ?? string.Empty,
            LabelFormat = Resources.Beatoraja_bmt_export_progress_label_format,
            SingleLabel = Resources.Beatoraja_bmt_export_progress_single_label
        });
    }

    private bool IsCurrentFullExportGeneration(long generation)
    {
        return generation == Interlocked.Read(ref fullExportGeneration);
    }

    private bool IsFullExportActive()
    {
        return Volatile.Read(ref fullExportActiveCount) > 0;
    }

    private static bool IsBeatorajaBmtOutputTarget(BMSTable table)
    {
        return table?.is_bmt_output != false;
    }

    private static bool IsValidBeatorajaBmtSort(int? sort)
    {
        return sort.HasValue && sort.Value > 0;
    }

    private static int GetBeatorajaBmtSortOrTail(int? sort)
    {
        return IsValidBeatorajaBmtSort(sort) ? sort.Value : int.MaxValue;
    }

    private static int NormalizeBeatorajaBmtOutputTargets(IEnumerable<BMSTable> tables)
    {
        int changedCount = 0;
        foreach (BMSTable table in (tables ?? []).Where(table => table != null && !table.is_bmt_output.HasValue))
        {
            table.is_bmt_output = true;
            changedCount++;
        }
        return changedCount;
    }

    private static string FormatTextForLog(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }

    private static string FormatBool(bool value)
    {
        return value.ToString().ToLowerInvariant();
    }

    private void Log(string message)
    {
        logPerformance?.Invoke(message);
    }

    private sealed class BeatorajaBmtSongHashResolver(Func<BmtSongHashResolveRequest, Tuple<string, string>> resolve)
        : BmtTableExportService.ISongHashResolver
    {
        public BmtTableExportService.SongHashResolution Resolve(BmtSongHashResolveRequest request)
        {
            Tuple<string, string> resolved = resolve?.Invoke(request);
            return resolved == null ? null : new BmtTableExportService.SongHashResolution(resolved.Item1, resolved.Item2);
        }
    }

    private sealed class BeatorajaBmtTableProjectionInput
    {
        public int Index { get; set; }

        public string PlaylistIdentity { get; set; }

        public string TableName { get; set; }

        public BmtTableExportService.TableDataProjectionSnapshot Snapshot { get; set; }
    }
}
