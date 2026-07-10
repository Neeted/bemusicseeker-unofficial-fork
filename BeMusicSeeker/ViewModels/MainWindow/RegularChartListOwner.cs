using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns request lifetime, derived rows, sort reuse, warmup lifetime, and terminal publication for the regular chart list.
/// </summary>
internal sealed class RegularChartListOwner : IDisposable
{
    private const int StartupVirtualOrderPrewarmMaxPriority = 3;
    private readonly object syncRoot = new();
    private readonly MainChartListViewModel mainChartList;
    private readonly PlaylistWorkspaceViewModel playlistWorkspace;
    private readonly Action<string> log;
    private readonly Action<Action> dispatchToUi;
    private readonly Dictionary<NormalLibrarySortCacheKey, List<LibraryChartRow>> sortCache = [];
    private readonly Dictionary<NormalLibrarySortCacheKey, ChartListOrder> virtualOrderCache = [];
    private readonly Dictionary<VirtualChartSubsetSortCacheKey, ChartListOrder> virtualSubsetOrderCache = [];
    private readonly Dictionary<MainViewSummaryCacheKey, int> virtualSummaryCache = [];
    private readonly Dictionary<MainViewSummaryCacheKey, VirtualSummaryWork> virtualSummaryRunning = [];
    private CancellationTokenSource currentCancellation;
    private long currentRequestId;
    private bool regularRequestActive;
    private IReadOnlyList<LibraryChartRow> folderRows;
    private IReadOnlyList<LibraryChartRow> keywordRows;
    private IReadOnlyList<LibraryChartRow> modeRows;
    private List<LibraryChartRow> folderSortSourceSnapshot;
    private List<LibraryChartRow> folderSortResultSnapshot;
    private string folderSortColumnName;
    private ListSortDirection? folderSortDirection;
    private MainViewUpdateMode? lastAppliedColumnMode;
    private RegularChartListCompletion lastCompletion;
    private List<ChartListSourceRow> virtualSourceRows;
    private bool virtualSourceRowsAvailable;
    private long virtualSourceRowsGeneration;
    private bool virtualSourceRowsIncludeBmson;
    private long sourceGeneration;
    private long sortKeyGeneration;
    private long warningGeneration;
    private long installDestinationGeneration;
    private long maintenanceGeneration;
    private long referenceTablesGeneration;
    private int handledOwnedCollectionVersion;
    private int virtualSummaryCacheVersion;
    private int virtualSummaryRunId;
    private CancellationTokenSource virtualOrderPrewarmCancellation;
    private Task virtualOrderPrewarmCompletion = Task.CompletedTask;
    private int virtualOrderPrewarmRunId;
    private bool disposed;

    internal RegularChartListOwner(
        MainChartListViewModel mainChartList,
        PlaylistWorkspaceViewModel playlistWorkspace,
        Action<string> log,
        Action<Action> dispatchToUi)
    {
        this.mainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        this.playlistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
    }

    internal RegularChartListCompletion LastCompletion
    {
        get
        {
            lock (syncRoot)
            {
                return lastCompletion;
            }
        }
    }

    internal void CommitExternalColumnMode(MainViewUpdateMode? mode)
    {
        if (!mode.HasValue)
        {
            return;
        }
        lock (syncRoot)
        {
            lastAppliedColumnMode = mode.Value;
        }
    }

    internal MainViewUpdateMode? LastAppliedColumnMode
    {
        get
        {
            lock (syncRoot)
            {
                return lastAppliedColumnMode;
            }
        }
    }

    internal RegularChartListRequestLease BeginRequest()
    {
        if (!TryBeginRequest(out RegularChartListRequestLease lease))
        {
            throw new ObjectDisposedException(nameof(RegularChartListOwner));
        }
        return lease;
    }

    internal bool TryBeginRequest(out RegularChartListRequestLease lease)
    {
        CancellationTokenSource previous;
        lock (syncRoot)
        {
            if (disposed)
            {
                lease = null;
                return false;
            }
            previous = currentCancellation;
            currentCancellation = new CancellationTokenSource();
            currentRequestId = MainViewBuildRequestSequence.Next();
            regularRequestActive = true;
            lease = new RegularChartListRequestLease(currentRequestId, currentCancellation.Token);
        }
        CancelAndDispose(previous);
        return true;
    }

    internal void InvalidatePendingRequest()
    {
        CancellationTokenSource previous;
        lock (syncRoot)
        {
            previous = currentCancellation;
            currentCancellation = null;
            currentRequestId = 0L;
            regularRequestActive = false;
        }
        CancelAndDispose(previous);
    }

    internal bool IsCurrentRegularRows(IList expectedRows)
    {
        lock (syncRoot)
        {
            return regularRequestActive && ReferenceEquals(mainChartList.Rows, expectedRows);
        }
    }

    internal bool HasFolderRows
    {
        get
        {
            lock (syncRoot)
            {
                return folderRows != null;
            }
        }
    }

    internal bool HasKeywordRows
    {
        get
        {
            lock (syncRoot)
            {
                return keywordRows != null;
            }
        }
    }

    internal bool HasModeRows
    {
        get
        {
            lock (syncRoot)
            {
                return modeRows != null;
            }
        }
    }

    internal void ResetDerivedCaches()
    {
        InvalidatePendingRequest();
        lock (syncRoot)
        {
            folderRows = null;
            keywordRows = null;
            modeRows = null;
            folderSortSourceSnapshot = null;
            folderSortResultSnapshot = null;
            folderSortColumnName = null;
            folderSortDirection = null;
        }
    }

    internal void ClearSortCache()
    {
        CancellationTokenSource previous;
        CancellationTokenSource prewarmCancellation;
        lock (syncRoot)
        {
            previous = InvalidateCurrentRequestUnsafe();
            sortKeyGeneration++;
            ClearAllSortCachesUnsafe(clearSourceRows: true);
            prewarmCancellation = GetActivePrewarmCancellationUnsafe();
        }
        CancelAndDispose(previous);
        Cancel(prewarmCancellation);
    }

    internal bool IsVirtualOrderPrewarmRunning
    {
        get
        {
            lock (syncRoot)
            {
                return !virtualOrderPrewarmCompletion.IsCompleted;
            }
        }
    }

    internal bool TryBeginVirtualOrderPrewarm(out RegularChartListPrewarmLease lease)
    {
        CancellationTokenSource previous;
        lock (syncRoot)
        {
            if (disposed || !virtualOrderPrewarmCompletion.IsCompleted)
            {
                lease = null;
                return false;
            }
            previous = virtualOrderPrewarmCancellation;
            virtualOrderPrewarmCancellation = new CancellationTokenSource();
            lease = new RegularChartListPrewarmLease(
                ++virtualOrderPrewarmRunId,
                virtualOrderPrewarmCancellation.Token);
            virtualOrderPrewarmCompletion = lease.Completion;
        }
        CancelAndDispose(previous);
        return true;
    }

    internal static IReadOnlyList<VirtualNormalLibrarySortDescriptor> CreateDefaultVirtualOrderPrewarmDescriptors()
    {
        return CreateVirtualOrderPrewarmDescriptors(StartupVirtualOrderPrewarmMaxPriority);
    }

    internal static int ResolveVirtualOrderPrewarmDegree(int descriptorCount)
    {
        if (descriptorCount <= 1)
        {
            return 1;
        }
        int processorDegree = Math.Max(1, Environment.ProcessorCount - 1);
        return Math.Max(1, Math.Min(Math.Min(processorDegree, 4), descriptorCount));
    }

    private static IReadOnlyList<VirtualNormalLibrarySortDescriptor> CreateVirtualOrderPrewarmDescriptors(int maxPrewarmPriority)
    {
        return [.. ChartListOrder.GetVirtualSortColumnMetadata()
            .Select((column, index) => new { Column = column, Index = index })
            .Where(item => item.Column.PrewarmPriority > 0)
            .Where(item => item.Column.PrewarmPriority <= maxPrewarmPriority)
            .OrderBy(item => item.Column.PrewarmPriority)
            .ThenBy(item => GetVirtualOrderPrewarmOrder(item.Column.NormalizedColumnName))
            .ThenBy(item => item.Index)
            .SelectMany(item => new[]
            {
                new VirtualNormalLibrarySortDescriptor(item.Column.NormalizedColumnName, ListSortDirection.Ascending, item.Column.PrewarmPriority),
                new VirtualNormalLibrarySortDescriptor(item.Column.NormalizedColumnName, ListSortDirection.Descending, item.Column.PrewarmPriority)
            })];
    }

    private static int GetVirtualOrderPrewarmOrder(string columnName)
    {
        return columnName switch
        {
            nameof(LibraryChartRow.Title) => 0,
            nameof(LibraryChartRow.Folder) => 1,
            nameof(LibraryChartRow.path) => 2,
            nameof(LibraryChartRow.Artist) => 3,
            nameof(LibraryChartRow.clear) => 0,
            nameof(LibraryChartRow.rateDouble) => 1,
            nameof(LibraryChartRow.minbp) => 2,
            nameof(LibraryChartRow.ChartJudgeSortKey) => 3,
            nameof(LibraryChartRow.ChartNotes) => 4,
            nameof(LibraryChartRow.ChartLongNotes) => 5,
            nameof(LibraryChartRow.ChartScratchNotes) => 6,
            nameof(LibraryChartRow.ChartMainBpmSortKey) => 7,
            nameof(LibraryChartRow.ChartMinBpmSortKey) => 8,
            nameof(LibraryChartRow.ChartMaxBpmSortKey) => 9,
            nameof(LibraryChartRow.ChartSoflanCount) => 10,
            nameof(LibraryChartRow.ChartTotalSortKey) => 11,
            nameof(LibraryChartRow.ChartTotalPerNoteSortKey) => 12,
            nameof(LibraryChartRow.ChartDurationSortKey) => 13,
            nameof(LibraryChartRow.ChartDensitySortKey) => 14,
            nameof(LibraryChartRow.ChartPeakDensitySortKey) => 15,
            nameof(LibraryChartRow.ChartEndDensitySortKey) => 16,
            _ => int.MaxValue,
        };
    }

    internal int CacheCount
    {
        get
        {
            lock (syncRoot)
            {
                return GetCacheCountUnsafe();
            }
        }
    }

    internal long SourceGeneration
    {
        get
        {
            lock (syncRoot)
            {
                return sourceGeneration;
            }
        }
    }

    internal long SortKeyGeneration
    {
        get
        {
            lock (syncRoot)
            {
                return sortKeyGeneration;
            }
        }
    }

    internal long WarningGeneration => ReadGeneration(() => warningGeneration);

    internal long InstallDestinationGeneration => ReadGeneration(() => installDestinationGeneration);

    internal long MaintenanceGeneration => ReadGeneration(() => maintenanceGeneration);

    internal long ReferenceTablesGeneration => ReadGeneration(() => referenceTablesGeneration);

    internal int InvalidateSource()
    {
        CancellationTokenSource previous;
        CancellationTokenSource prewarmCancellation;
        int cacheCount;
        lock (syncRoot)
        {
            cacheCount = GetCacheCountUnsafe();
            sourceGeneration++;
            previous = InvalidateCurrentRequestUnsafe();
            ClearAllSortCachesUnsafe(clearSourceRows: true);
            prewarmCancellation = GetActivePrewarmCancellationUnsafe();
        }
        CancelAndDispose(previous);
        Cancel(prewarmCancellation);
        return cacheCount;
    }

    internal bool TryInvalidateSourceForOwnedCollectionVersion(int currentVersion, out int cacheCount)
    {
        CancellationTokenSource previous = null;
        CancellationTokenSource prewarmCancellation;
        lock (syncRoot)
        {
            if (currentVersion > 0 && currentVersion == handledOwnedCollectionVersion)
            {
                cacheCount = 0;
                return false;
            }
            handledOwnedCollectionVersion = currentVersion;
            cacheCount = GetCacheCountUnsafe();
            sourceGeneration++;
            previous = InvalidateCurrentRequestUnsafe();
            ClearAllSortCachesUnsafe(clearSourceRows: true);
            prewarmCancellation = GetActivePrewarmCancellationUnsafe();
        }
        CancelAndDispose(previous);
        Cancel(prewarmCancellation);
        return true;
    }

    internal void InvalidateVirtualSourceRows()
    {
        _ = InvalidateSource();
    }

    internal int InvalidateIdentitySortKeys(bool clearSourceRows)
    {
        CancellationTokenSource previous;
        CancellationTokenSource prewarmCancellation;
        int cacheCount;
        lock (syncRoot)
        {
            cacheCount = GetCacheCountUnsafe();
            sortKeyGeneration++;
            previous = InvalidateCurrentRequestUnsafe();
            sortCache.Clear();
            virtualOrderCache.Clear();
            virtualSubsetOrderCache.Clear();
            virtualSummaryCache.Clear();
            virtualSummaryCacheVersion++;
            if (clearSourceRows)
            {
                ClearVirtualSourceRowsUnsafe();
            }
            prewarmCancellation = GetActivePrewarmCancellationUnsafe();
        }
        CancelAndDispose(previous);
        Cancel(prewarmCancellation);
        return cacheCount;
    }

    internal static bool IsSortCacheCandidate(string columnName)
    {
        return ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out _);
    }

    internal int InvalidateSortCacheByDependency(MainViewDataDependency dependency, out int cacheCount)
    {
        CancellationTokenSource previous;
        CancellationTokenSource prewarmCancellation;
        int removed;
        lock (syncRoot)
        {
            IncrementDependencyGenerationUnsafe(dependency);
            cacheCount = GetCacheCountUnsafe();
            previous = InvalidateCurrentRequestUnsafe();
            removed = PruneCacheUnsafe(sortCache, dependency)
                + PruneCacheUnsafe(virtualOrderCache, dependency)
                + PruneCacheUnsafe(virtualSubsetOrderCache, dependency);
            prewarmCancellation = GetActivePrewarmCancellationUnsafe();
        }
        CancelAndDispose(previous);
        Cancel(prewarmCancellation);
        return removed;
    }

    internal RegularVirtualSourceRowsLookup LookupVirtualSourceRows(bool includeBmsonRows)
    {
        lock (syncRoot)
        {
            bool cacheHit = virtualSourceRowsAvailable
                && virtualSourceRows != null
                && virtualSourceRowsGeneration == sourceGeneration
                && virtualSourceRowsIncludeBmson == includeBmsonRows;
            return new RegularVirtualSourceRowsLookup(
                includeBmsonRows,
                sourceGeneration,
                sortKeyGeneration,
                cacheHit ? virtualSourceRows : null,
                cacheHit);
        }
    }

    internal void TryPublishVirtualSourceRows(
        RegularVirtualSourceRowsLookup lookup,
        List<ChartListSourceRow> rows)
    {
        lock (syncRoot)
        {
            if (disposed
                || sourceGeneration != lookup.SourceGeneration
                || sortKeyGeneration != lookup.SortKeyGeneration)
            {
                return;
            }
            virtualSourceRows = rows;
            virtualSourceRowsAvailable = true;
            virtualSourceRowsGeneration = lookup.SourceGeneration;
            virtualSourceRowsIncludeBmson = lookup.IncludeBmsonRows;
        }
    }

    internal NormalLibrarySortCacheGenerationSnapshot CaptureSortGeneration(
        string columnName,
        RegularChartListExternalVersions externalVersions)
    {
        lock (syncRoot)
        {
            ResolveDependencyGenerationsUnsafe(
                columnName,
                externalVersions,
                out long score,
                out long chartInfo,
                out long maintenance,
                out long warning,
                out long installDestination,
                out long referenceTables);
            return new NormalLibrarySortCacheGenerationSnapshot(
                sourceGeneration,
                sortKeyGeneration,
                score,
                chartInfo,
                maintenance,
                warning,
                installDestination,
                referenceTables);
        }
    }

    internal NormalLibrarySortCacheKey CreateVirtualOrderKey(
        long expectedSourceGeneration,
        long expectedSortKeyGeneration,
        string columnName,
        ListSortDirection direction,
        int rowCount,
        RegularChartListExternalVersions externalVersions)
    {
        lock (syncRoot)
        {
            ResolveDependencyGenerationsUnsafe(
                columnName,
                externalVersions,
                out long score,
                out long chartInfo,
                out long maintenance,
                out long warning,
                out long installDestination,
                out long referenceTables);
            return new NormalLibrarySortCacheKey(
                expectedSourceGeneration,
                expectedSortKeyGeneration,
                score,
                chartInfo,
                maintenance,
                warning,
                installDestination,
                referenceTables,
                columnName,
                direction,
                rowCount);
        }
    }

    internal VirtualChartSubsetSortCacheKey CreateVirtualSubsetOrderKey(
        long expectedSourceGeneration,
        long expectedSortKeyGeneration,
        int treeMode,
        string subsetName,
        long sourceRowsSignature,
        string columnName,
        ListSortDirection direction,
        int rowCount,
        RegularChartListExternalVersions externalVersions)
    {
        lock (syncRoot)
        {
            ResolveDependencyGenerationsUnsafe(
                columnName,
                externalVersions,
                out long score,
                out long chartInfo,
                out long maintenance,
                out long warning,
                out long installDestination,
                out long referenceTables);
            return new VirtualChartSubsetSortCacheKey(
                expectedSourceGeneration,
                expectedSortKeyGeneration,
                score,
                chartInfo,
                maintenance,
                warning,
                installDestination,
                referenceTables,
                treeMode,
                subsetName,
                sourceRowsSignature,
                columnName,
                direction,
                rowCount);
        }
    }

    internal bool TryGetVirtualOrder(NormalLibrarySortCacheKey key, out ChartListOrder order)
    {
        lock (syncRoot)
        {
            return virtualOrderCache.TryGetValue(key, out order) && order != null;
        }
    }

    internal bool TryGetVirtualSubsetOrder(VirtualChartSubsetSortCacheKey key, out ChartListOrder order)
    {
        lock (syncRoot)
        {
            return virtualSubsetOrderCache.TryGetValue(key, out order) && order != null;
        }
    }

    internal bool TryPublishVirtualOrder(
        NormalLibrarySortCacheKey key,
        ChartListOrder order,
        RegularChartListExternalVersions currentExternalVersions)
    {
        lock (syncRoot)
        {
            if (disposed || !IsCurrentUnsafe(key, currentExternalVersions))
            {
                return false;
            }
            virtualOrderCache[key] = order;
            return true;
        }
    }

    internal bool TryPublishVirtualSubsetOrder(
        VirtualChartSubsetSortCacheKey key,
        ChartListOrder order,
        RegularChartListExternalVersions currentExternalVersions)
    {
        lock (syncRoot)
        {
            if (disposed || !IsCurrentUnsafe(key, currentExternalVersions))
            {
                return false;
            }
            virtualSubsetOrderCache[key] = order;
            return true;
        }
    }

    internal bool IsCurrentVirtualGeneration(long expectedSourceGeneration, long expectedSortKeyGeneration)
    {
        lock (syncRoot)
        {
            return !disposed
                && sourceGeneration == expectedSourceGeneration
                && sortKeyGeneration == expectedSortKeyGeneration;
        }
    }

    internal RegularChartListBuildResult Build(
        RegularChartListRequestLease lease,
        RegularChartListRefreshRequest request,
        RegularChartListBuildInput input)
    {
        if (lease == null)
        {
            throw new ArgumentNullException(nameof(lease));
        }
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }
        lease.Token.ThrowIfCancellationRequested();
        Stopwatch stopwatch = input.Stopwatch ?? Stopwatch.StartNew();
        long stageStartMs = stopwatch.ElapsedMilliseconds;

        IReadOnlyList<LibraryChartRow> existingFolderRows;
        List<LibraryChartRow> existingFolderSortSource;
        List<LibraryChartRow> existingFolderSortResult;
        string existingFolderSortColumn;
        ListSortDirection? existingFolderSortDirection;
        lock (syncRoot)
        {
            existingFolderRows = folderRows;
            existingFolderSortSource = folderSortSourceSnapshot;
            existingFolderSortResult = folderSortResultSnapshot;
            existingFolderSortColumn = folderSortColumnName;
            existingFolderSortDirection = folderSortDirection;
        }

        IReadOnlyList<LibraryChartRow> nextFolderRows = input.HasFolderRowsOverride
            ? RegularChartListStageState.Materialize(input.FolderRowsOverride)
            : RegularChartListStageState.Materialize(existingFolderRows);
        long folderMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        lease.Token.ThrowIfCancellationRequested();
        stageStartMs = stopwatch.ElapsedMilliseconds;
        IReadOnlyList<LibraryChartRow> nextKeywordRows = !string.IsNullOrWhiteSpace(request.KeywordFilter)
            ? RegularChartListStageState.Materialize(RegularChartListFilterService.ApplyKeywordFilter(nextFolderRows, request.KeywordFilter))
            : nextFolderRows;
        long keywordMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        lease.Token.ThrowIfCancellationRequested();
        stageStartMs = stopwatch.ElapsedMilliseconds;
        IReadOnlyList<LibraryChartRow> nextModeRows = request.ModeFilter != MainWindowViewModel.ModeFilterType.All
            ? RegularChartListStageState.Materialize(RegularChartListFilterService.ApplyModeFilter(nextKeywordRows, request.ModeFilter))
            : nextKeywordRows;
        long modeMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        lease.Token.ThrowIfCancellationRequested();

        var stage = new RegularChartListStageState
        {
            FolderRows = nextFolderRows,
            KeywordRows = nextKeywordRows,
            ModeRows = nextModeRows
        };
        stageStartMs = stopwatch.ElapsedMilliseconds;
        RegularChartListSortResult sort = ApplySort(
            lease,
            request,
            stage,
            input,
            existingFolderSortSource,
            existingFolderSortResult,
            existingFolderSortColumn,
            existingFolderSortDirection,
            out NormalLibrarySortCacheKey? pendingCacheKey,
            out List<LibraryChartRow> pendingCacheRows);
        long sortMs = stopwatch.ElapsedMilliseconds - stageStartMs;

        return new RegularChartListBuildResult(stage, sort, pendingCacheKey, pendingCacheRows, folderMs, keywordMs, modeMs, sortMs);
    }

    internal RegularChartListTerminalResult TryCommit(
        RegularChartListRequestLease lease,
        RegularChartListBuildResult build,
        RegularChartListTerminalInput input)
    {
        if (lease == null || build == null || input == null)
        {
            throw new ArgumentException("A complete regular chart-list terminal request is required.");
        }

        return TryCommitCore(lease, build, input);
    }

    internal RegularChartListTerminalResult TryCommitVirtual(
        RegularChartListRequestLease lease,
        RegularChartListTerminalInput input)
    {
        if (lease == null || input == null)
        {
            throw new ArgumentException("A complete virtual regular chart-list terminal request is required.");
        }

        return TryCommitCore(lease, null, input);
    }

    internal bool TryGetVirtualSummary(MainViewSummaryCacheKey key, out int distinctFolderCount)
    {
        lock (syncRoot)
        {
            return virtualSummaryCache.TryGetValue(key, out distinctFolderCount);
        }
    }

    internal void ScheduleVirtualSummary(
        RegularChartListRequestLease lease,
        MainViewSummaryCacheKey key,
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IList expectedRows,
        string reason)
    {
        if (lease == null || sourceRows == null || expectedRows == null)
        {
            return;
        }

        int runId;
        VirtualSummaryWork work;
        bool joinedRunningWork = false;
        lock (syncRoot)
        {
            if (!IsCurrentUnsafe(lease) || virtualSummaryCache.ContainsKey(key))
            {
                return;
            }
            if (virtualSummaryRunning.TryGetValue(key, out VirtualSummaryWork runningWork))
            {
                runningWork.Lease = lease;
                runningWork.ExpectedRows = expectedRows;
                joinedRunningWork = true;
                runId = 0;
                work = null!;
            }
            else
            {
                runId = ++virtualSummaryRunId;
                work = new VirtualSummaryWork(lease, expectedRows, virtualSummaryCacheVersion);
                virtualSummaryRunning[key] = work;
            }
        }
        if (joinedRunningWork)
        {
            log("main_summary_folder_count queued reason=" + (reason ?? string.Empty)
                + " rowCount=" + key.RowCount
                + " skipped=already_running");
            return;
        }

        log("main_summary_folder_count queued reason=" + (reason ?? string.Empty)
            + " runId=" + runId
            + " rowCount=" + key.RowCount
            + " cacheHit=False");
        Task.Run(() => RunVirtualSummary(runId, key, sourceRows, reason, work));
    }

    internal static IReadOnlyList<ChartListSourceRow> SelectSourceRowsByOrder(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IReadOnlyList<int> orderedIndexes)
    {
        if (sourceRows == null || orderedIndexes == null)
        {
            return [];
        }
        return [.. orderedIndexes
            .Where(index => index >= 0 && index < sourceRows.Count)
            .Select(index => sourceRows[index])
            .Where(row => row != null)];
    }

    internal static int CountDistinctFolders(IEnumerable<ChartListSourceRow> rows)
    {
        if (rows == null)
        {
            return -1;
        }
        return rows.Select(row => row?.Folder)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private RegularChartListTerminalResult TryCommitCore(
        RegularChartListRequestLease lease,
        RegularChartListBuildResult build,
        RegularChartListTerminalInput input)
    {
        lock (syncRoot)
        {
            if (!IsCurrentUnsafe(lease))
            {
                return RegularChartListTerminalResult.Stale();
            }
        }

        MainChartListPreparedRowsApply prepared = mainChartList.PrepareRowsApply(input.RowsRequest);
        MainChartListRowsCommit rowsCommit = null;
        PlaylistColumnPresentationCommit columnCommit = null;
        bool committed = false;
        try
        {
            lock (syncRoot)
            {
                if (!IsCurrentUnsafe(lease))
                {
                    return RegularChartListTerminalResult.Stale();
                }

                rowsCommit = mainChartList.CommitPreparedRowsWithoutDisposal(prepared);
                columnCommit = playlistWorkspace.CommitColumnPresentationWithoutNotification(
                    input.ColumnSelection.PlaylistColumnSettingsVisibility,
                    input.ColumnSelection.PlaylistSummaryColumnsSettings);
                if (build != null)
                {
                    folderRows = build.Stage.FolderRows;
                    keywordRows = build.Stage.KeywordRows;
                    modeRows = build.Stage.ModeRows;
                    folderSortSourceSnapshot = build.Sort.FolderSortSourceSnapshot;
                    folderSortResultSnapshot = build.Sort.FolderSortResultSnapshot;
                    folderSortColumnName = build.Sort.FolderSortColumnName;
                    folderSortDirection = build.Sort.FolderSortDirection;
                    if (build.PendingCacheKey.HasValue && build.PendingCacheRows != null)
                    {
                        sortCache[build.PendingCacheKey.Value] = build.PendingCacheRows;
                    }
                }
                if (input.ColumnSelection.AppliedMode.HasValue)
                {
                    lastAppliedColumnMode = input.ColumnSelection.AppliedMode.Value;
                }
                lastCompletion = new RegularChartListCompletion(
                    lease.RequestId,
                    Stopwatch.GetTimestamp(),
                    Thread.CurrentThread.ManagedThreadId,
                    input.Mode,
                    input.Stopwatch.ElapsedMilliseconds);
                committed = true;
            }
        }
        finally
        {
            if (!committed)
            {
                mainChartList.CancelPreparedRowsApply(prepared);
            }
        }

        List<Exception> publishExceptions = [];
        MainChartListRowsApplyResult rowsApply = default;
        TryPublish(() => mainChartList.DisposeCommittedRows(rowsCommit), publishExceptions);
        TryPublish(() => rowsApply = mainChartList.PublishRowsCommit(rowsCommit), publishExceptions);
        TryPublish(() => playlistWorkspace.PublishColumnPresentation(columnCommit), publishExceptions);
        if (publishExceptions.Count > 0)
        {
            throw new RegularChartListTerminalPublishException(new AggregateException(publishExceptions));
        }
        return RegularChartListTerminalResult.Committed(rowsApply);
    }

    private void RunVirtualSummary(
        int runId,
        MainViewSummaryCacheKey key,
        IReadOnlyList<ChartListSourceRow> sourceRows,
        string reason,
        VirtualSummaryWork work)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            log("main_summary_folder_count start reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " rowCount=" + key.RowCount);
            int distinctFolderCount = CountDistinctFolders(sourceRows);
            stopwatch.Stop();
            bool isCurrent;
            IList expectedRows;
            lock (syncRoot)
            {
                if (virtualSummaryRunning.TryGetValue(key, out VirtualSummaryWork currentWork)
                    && ReferenceEquals(currentWork, work))
                {
                    virtualSummaryRunning.Remove(key);
                }
                isCurrent = work.CacheVersion == virtualSummaryCacheVersion
                    && IsCurrentUnsafe(work.Lease);
                expectedRows = work.ExpectedRows;
                if (isCurrent)
                {
                    virtualSummaryCache[key] = distinctFolderCount;
                }
            }
            if (!isCurrent)
            {
                log("main_summary_folder_count stale_skipped reason=" + (reason ?? string.Empty)
                    + " runId=" + runId
                    + " rowCount=" + key.RowCount
                    + " distinctFolderCount=" + distinctFolderCount
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                return;
            }

            log("main_summary_folder_count done reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " rowCount=" + key.RowCount
                + " distinctFolderCount=" + distinctFolderCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " cacheHit=False");
            dispatchToUi(() =>
            {
                if (IsCurrentRegularRows(expectedRows))
                {
                    mainChartList.TryUpdateNormalSummary(expectedRows, key.RowCount, distinctFolderCount);
                }
            });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            lock (syncRoot)
            {
                if (virtualSummaryRunning.TryGetValue(key, out VirtualSummaryWork currentWork)
                    && ReferenceEquals(currentWork, work))
                {
                    virtualSummaryRunning.Remove(key);
                }
            }
            log("main_summary_folder_count failed reason=" + (reason ?? string.Empty)
                + " runId=" + runId
                + " rowCount=" + key.RowCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name);
        }
    }

    private sealed class VirtualSummaryWork
    {
        internal VirtualSummaryWork(
            RegularChartListRequestLease lease,
            IList expectedRows,
            int cacheVersion)
        {
            Lease = lease;
            ExpectedRows = expectedRows;
            CacheVersion = cacheVersion;
        }

        internal RegularChartListRequestLease Lease { get; set; }

        internal IList ExpectedRows { get; set; }

        internal int CacheVersion { get; }
    }

    internal Task StopAsync()
    {
        CancellationTokenSource requestCancellation;
        CancellationTokenSource prewarmCancellation;
        Task prewarmCompletion;
        lock (syncRoot)
        {
            if (disposed)
            {
                return virtualOrderPrewarmCompletion;
            }
            disposed = true;
            requestCancellation = currentCancellation;
            currentCancellation = null;
            currentRequestId = 0L;
            regularRequestActive = false;
            prewarmCancellation = virtualOrderPrewarmCancellation;
            prewarmCompletion = virtualOrderPrewarmCompletion;
        }
        CancelAndDispose(requestCancellation);
        Cancel(prewarmCancellation);
        return DrainPrewarmAsync(prewarmCompletion, prewarmCancellation);
    }

    public void Dispose()
    {
        _ = StopAsync();
    }

    private RegularChartListSortResult ApplySort(
        RegularChartListRequestLease lease,
        RegularChartListRefreshRequest request,
        RegularChartListStageState stage,
        RegularChartListBuildInput input,
        List<LibraryChartRow> existingFolderSortSource,
        List<LibraryChartRow> existingFolderSortResult,
        string existingFolderSortColumn,
        ListSortDirection? existingFolderSortDirection,
        out NormalLibrarySortCacheKey? pendingCacheKey,
        out List<LibraryChartRow> pendingCacheRows)
    {
        pendingCacheKey = null;
        pendingCacheRows = null;
        List<LibraryChartRow> rows = stage.ModeRows as List<LibraryChartRow> ?? [.. stage.ModeRows];
        if (request.Mode > MainViewUpdateMode.SortUpdated)
        {
            return RegularChartListSortResult.Bypass(new List<LibraryChartRow>(rows), existingFolderSortSource, existingFolderSortResult, existingFolderSortColumn, existingFolderSortDirection);
        }

        string columnName = request.SortColumnName;
        ListSortDirection direction = request.SortDirection;
        bool isTreeSelectionRequest = request.RequestedMode != MainViewUpdateMode.TreeViewFilterNotChanged
            && request.RequestedMode < MainViewUpdateMode.KeywordFilterUpdated;
        bool isFolderMode = request.Mode == MainViewUpdateMode.FolderFilterSelected;
        bool fullNormalResult = input.CurrentTreeMode == MainViewUpdateMode.FolderFilterSelected
            && !input.HasVirtualNormalLibraryTreeFilter
            && string.IsNullOrWhiteSpace(request.KeywordFilter)
            && request.ModeFilter == MainWindowViewModel.ModeFilterType.All
            && !input.IsPlaylistDetailView
            && rows.Count == stage.FolderCount && rows.Count == stage.KeywordCount && rows.Count == stage.ModeCount;

        if (isFolderMode && isTreeSelectionRequest
            && existingFolderSortSource != null && existingFolderSortResult != null
            && string.Equals(existingFolderSortColumn, columnName, StringComparison.Ordinal)
            && existingFolderSortDirection == direction
            && IsSameReferenceSequence(rows, existingFolderSortSource))
        {
            return RegularChartListSortResult.ReuseFolderSnapshot(existingFolderSortResult, rows, existingFolderSortResult, columnName, direction);
        }

        NormalLibrarySortCacheKey cacheKey = default;
        if (fullNormalResult
            && ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out string normalizedColumn))
        {
            cacheKey = input.SortCacheGeneration.Create(normalizedColumn, direction, rows.Count);
            lock (syncRoot)
            {
                if (IsCurrentUnsafe(lease) && sortCache.TryGetValue(cacheKey, out List<LibraryChartRow> cachedRows))
                {
                    return RegularChartListSortResult.ReuseSortCache(cachedRows, isFolderMode ? rows : existingFolderSortSource, existingFolderSortResult, isFolderMode ? columnName : existingFolderSortColumn, isFolderMode ? direction : existingFolderSortDirection, CreateSortMetrics(cacheKey, 0L, cacheHit: true));
                }
            }
        }

        lease.Token.ThrowIfCancellationRequested();
        List<LibraryChartRow> sortedRows = LibraryChartRowSortEngine.SortForMainView(
            rows,
            request.SortParameters,
            input.IsPlaylistDetailView,
            useLegacySortForDataGrid: false,
            out string sortProfile,
            out LibraryChartSortMetrics metrics);
        lease.Token.ThrowIfCancellationRequested();
        if (fullNormalResult && !string.IsNullOrWhiteSpace(cacheKey.ColumnName))
        {
            pendingCacheKey = cacheKey;
            pendingCacheRows = sortedRows;
            metrics = CreateSortMetrics(cacheKey, metrics.SortMs, cacheHit: false);
        }
        return RegularChartListSortResult.Sorted(sortedRows, sortProfile, isFolderMode ? rows : existingFolderSortSource, isFolderMode ? sortedRows : existingFolderSortResult, isFolderMode ? columnName : existingFolderSortColumn, isFolderMode ? direction : existingFolderSortDirection, metrics);
    }

    private bool IsCurrentUnsafe(RegularChartListRequestLease lease)
    {
        return regularRequestActive
            && currentRequestId == lease.RequestId
            && !lease.Token.IsCancellationRequested;
    }

    private static LibraryChartSortMetrics CreateSortMetrics(NormalLibrarySortCacheKey key, long sortMs, bool cacheHit)
    {
        string profile = "library_chart_string_fast_ordinal_ignore_case";
        string stringKind = "ordinal_ignore_case";
        string propertyType = nameof(String);
        if (ChartListOrder.TryGetVirtualSortColumnMetadata(key.ColumnName, out ChartListOrderColumnMetadata metadata))
        {
            propertyType = metadata.PropertyTypeName;
            stringKind = metadata.StringSortKind;
            if (metadata.KeyKind == ChartListOrderKeyKind.Comparable)
            {
                profile = "library_chart_comparable_fast";
            }
        }
        return new LibraryChartSortMetrics(key.RowCount, key.ColumnName, key.Direction, propertyType, profile, stringKind, sortMs, cacheHit, key.ColumnName, key.SortKeyGeneration, cacheHit);
    }

    private static bool IsSameReferenceSequence<T>(IReadOnlyList<T> left, IReadOnlyList<T> right) where T : class
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i], right[i])) return false;
        }
        return true;
    }

    private static bool DoesSortColumnDependOn(string columnName, MainViewDataDependency dependency)
    {
        if (!ChartListOrder.TryGetVirtualSortColumnMetadata(columnName, out ChartListOrderColumnMetadata metadata))
        {
            return false;
        }
        return metadata.Dependency == dependency
            || (metadata.Dependency == MainViewDataDependency.Warning
                && dependency is MainViewDataDependency.InstallDestination or MainViewDataDependency.Maintenance);
    }

    private CancellationTokenSource InvalidateCurrentRequestUnsafe()
    {
        CancellationTokenSource previous = currentCancellation;
        currentCancellation = null;
        currentRequestId = 0L;
        regularRequestActive = false;
        return previous;
    }

    private CancellationTokenSource GetActivePrewarmCancellationUnsafe()
    {
        return virtualOrderPrewarmCompletion.IsCompleted
            ? null
            : virtualOrderPrewarmCancellation;
    }

    private long ReadGeneration(Func<long> accessor)
    {
        lock (syncRoot)
        {
            return accessor();
        }
    }

    private int GetCacheCountUnsafe()
    {
        return sortCache.Count
            + virtualOrderCache.Count
            + virtualSubsetOrderCache.Count
            + (virtualSourceRowsAvailable ? 1 : 0);
    }

    private void ClearAllSortCachesUnsafe(bool clearSourceRows)
    {
        sortCache.Clear();
        virtualOrderCache.Clear();
        virtualSubsetOrderCache.Clear();
        virtualSummaryCache.Clear();
        virtualSummaryCacheVersion++;
        if (clearSourceRows)
        {
            ClearVirtualSourceRowsUnsafe();
        }
    }

    private void ClearVirtualSourceRowsUnsafe()
    {
        virtualSourceRows = null;
        virtualSourceRowsAvailable = false;
    }

    private void IncrementDependencyGenerationUnsafe(MainViewDataDependency dependency)
    {
        switch (dependency)
        {
            case MainViewDataDependency.Warning:
                warningGeneration++;
                break;
            case MainViewDataDependency.InstallDestination:
                installDestinationGeneration++;
                break;
            case MainViewDataDependency.Maintenance:
                maintenanceGeneration++;
                break;
            case MainViewDataDependency.ReferenceTables:
                referenceTablesGeneration++;
                break;
        }
    }

    private static int PruneCacheUnsafe<TValue>(
        Dictionary<NormalLibrarySortCacheKey, TValue> cache,
        MainViewDataDependency dependency)
    {
        NormalLibrarySortCacheKey[] keys = [.. cache.Keys];
        int removed = 0;
        foreach (NormalLibrarySortCacheKey key in keys)
        {
            if (DoesSortColumnDependOn(key.ColumnName, dependency) && cache.Remove(key))
            {
                removed++;
            }
        }
        return removed;
    }

    private static int PruneCacheUnsafe(
        Dictionary<VirtualChartSubsetSortCacheKey, ChartListOrder> cache,
        MainViewDataDependency dependency)
    {
        VirtualChartSubsetSortCacheKey[] keys = [.. cache.Keys];
        int removed = 0;
        foreach (VirtualChartSubsetSortCacheKey key in keys)
        {
            if (DoesSortColumnDependOn(key.ColumnName, dependency) && cache.Remove(key))
            {
                removed++;
            }
        }
        return removed;
    }

    private void ResolveDependencyGenerationsUnsafe(
        string columnName,
        RegularChartListExternalVersions externalVersions,
        out long score,
        out long chartInfo,
        out long maintenance,
        out long warning,
        out long installDestination,
        out long referenceTables)
    {
        score = 0L;
        chartInfo = 0L;
        maintenance = 0L;
        warning = 0L;
        installDestination = 0L;
        referenceTables = 0L;
        if (!ChartListOrder.TryGetVirtualSortColumnMetadata(columnName, out ChartListOrderColumnMetadata metadata))
        {
            return;
        }

        switch (metadata.Dependency)
        {
            case MainViewDataDependency.Score:
                score = externalVersions.Score;
                break;
            case MainViewDataDependency.ChartInfo:
                chartInfo = externalVersions.ChartInfo;
                break;
            case MainViewDataDependency.Maintenance:
                maintenance = GetMaintenanceGenerationUnsafe(externalVersions);
                break;
            case MainViewDataDependency.Warning:
                warning = warningGeneration;
                installDestination = installDestinationGeneration;
                maintenance = GetMaintenanceGenerationUnsafe(externalVersions);
                break;
            case MainViewDataDependency.InstallDestination:
                installDestination = installDestinationGeneration;
                break;
            case MainViewDataDependency.ReferenceTables:
                referenceTables = referenceTablesGeneration;
                break;
        }
    }

    private long GetMaintenanceGenerationUnsafe(RegularChartListExternalVersions externalVersions)
    {
        return (externalVersions.MaintenanceHydration << 32)
            ^ (maintenanceGeneration & 0xffffffffL);
    }

    private bool IsCurrentUnsafe(
        NormalLibrarySortCacheKey key,
        RegularChartListExternalVersions externalVersions)
    {
        NormalLibrarySortCacheGenerationSnapshot current = CaptureSortGenerationUnsafe(key.ColumnName, externalVersions);
        return sourceGeneration == key.SourceGeneration
            && sortKeyGeneration == key.SortKeyGeneration
            && current.Score == key.ScoreGeneration
            && current.ChartInfo == key.ChartInfoGeneration
            && current.Maintenance == key.MaintenanceGeneration
            && current.Warning == key.WarningGeneration
            && current.InstallDestination == key.InstallDestinationGeneration
            && current.ReferenceTables == key.ReferenceTablesGeneration;
    }

    private bool IsCurrentUnsafe(
        VirtualChartSubsetSortCacheKey key,
        RegularChartListExternalVersions externalVersions)
    {
        NormalLibrarySortCacheGenerationSnapshot current = CaptureSortGenerationUnsafe(key.ColumnName, externalVersions);
        return sourceGeneration == key.SourceGeneration
            && sortKeyGeneration == key.SortKeyGeneration
            && current.Score == key.ScoreGeneration
            && current.ChartInfo == key.ChartInfoGeneration
            && current.Maintenance == key.MaintenanceGeneration
            && current.Warning == key.WarningGeneration
            && current.InstallDestination == key.InstallDestinationGeneration
            && current.ReferenceTables == key.ReferenceTablesGeneration;
    }

    private NormalLibrarySortCacheGenerationSnapshot CaptureSortGenerationUnsafe(
        string columnName,
        RegularChartListExternalVersions externalVersions)
    {
        ResolveDependencyGenerationsUnsafe(
            columnName,
            externalVersions,
            out long score,
            out long chartInfo,
            out long maintenance,
            out long warning,
            out long installDestination,
            out long referenceTables);
        return new NormalLibrarySortCacheGenerationSnapshot(
            sourceGeneration,
            sortKeyGeneration,
            score,
            chartInfo,
            maintenance,
            warning,
            installDestination,
            referenceTables);
    }

    private static void CancelAndDispose(CancellationTokenSource cancellation)
    {
        if (cancellation == null) return;
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }

    private static void Cancel(CancellationTokenSource cancellation)
    {
        if (cancellation == null) return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task DrainPrewarmAsync(Task completion, CancellationTokenSource cancellation)
    {
        try
        {
            await (completion ?? Task.CompletedTask).ConfigureAwait(false);
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private static void TryPublish(Action action, ICollection<Exception> exceptions)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            exceptions.Add(ex);
        }
    }
}

internal sealed class RegularChartListRequestLease
{
    internal RegularChartListRequestLease(long requestId, CancellationToken token)
    {
        RequestId = requestId;
        Token = token;
    }
    internal long RequestId { get; }
    internal CancellationToken Token { get; }
}

internal sealed class RegularChartListPrewarmLease : IDisposable
{
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int completed;

    internal RegularChartListPrewarmLease(int runId, CancellationToken token)
    {
        RunId = runId;
        Token = token;
    }

    internal int RunId { get; }

    internal CancellationToken Token { get; }

    internal Task Completion => completion.Task;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
        {
            completion.TrySetResult(true);
        }
    }
}

internal sealed class RegularChartListBuildInput
{
    internal MainViewUpdateMode CurrentTreeMode { get; set; }
    internal bool HasVirtualNormalLibraryTreeFilter { get; set; }
    internal bool IsPlaylistDetailView { get; set; }
    internal bool HasFolderRowsOverride { get; set; }
    internal IEnumerable<LibraryChartRow> FolderRowsOverride { get; set; }
    internal NormalLibrarySortCacheGenerationSnapshot SortCacheGeneration { get; set; }
    internal Stopwatch Stopwatch { get; set; }
}

internal sealed class RegularChartListBuildResult
{
    internal RegularChartListBuildResult(RegularChartListStageState stage, RegularChartListSortResult sort, NormalLibrarySortCacheKey? pendingCacheKey, List<LibraryChartRow> pendingCacheRows, long folderMs, long keywordMs, long modeMs, long sortMs)
    {
        Stage = stage;
        Sort = sort;
        PendingCacheKey = pendingCacheKey;
        PendingCacheRows = pendingCacheRows;
        FolderMs = folderMs;
        KeywordMs = keywordMs;
        ModeMs = modeMs;
        SortMs = sortMs;
    }
    internal RegularChartListStageState Stage { get; }
    internal RegularChartListSortResult Sort { get; }
    internal NormalLibrarySortCacheKey? PendingCacheKey { get; }
    internal List<LibraryChartRow> PendingCacheRows { get; }
    internal long FolderMs { get; }
    internal long KeywordMs { get; }
    internal long ModeMs { get; }
    internal long SortMs { get; }
}

internal sealed class RegularChartListTerminalInput
{
    internal MainChartListRowsApplyRequest RowsRequest { get; set; }
    internal MainChartListColumnSelection ColumnSelection { get; set; }
    internal MainViewUpdateMode Mode { get; set; }
    internal Stopwatch Stopwatch { get; set; }
}

internal readonly struct NormalLibrarySortCacheGenerationSnapshot
{
    internal NormalLibrarySortCacheGenerationSnapshot(long source, long sortKey, long score, long chartInfo, long maintenance, long warning, long installDestination, long referenceTables)
    {
        Source = source;
        SortKey = sortKey;
        Score = score;
        ChartInfo = chartInfo;
        Maintenance = maintenance;
        Warning = warning;
        InstallDestination = installDestination;
        ReferenceTables = referenceTables;
    }
    internal long Source { get; }
    internal long SortKey { get; }
    internal long Score { get; }
    internal long ChartInfo { get; }
    internal long Maintenance { get; }
    internal long Warning { get; }
    internal long InstallDestination { get; }
    internal long ReferenceTables { get; }
    internal NormalLibrarySortCacheKey Create(string columnName, ListSortDirection direction, int rowCount) => new(Source, SortKey, Score, ChartInfo, Maintenance, Warning, InstallDestination, ReferenceTables, columnName, direction, rowCount);
}

internal readonly struct RegularChartListCompletion
{
    internal RegularChartListCompletion(long requestId, long endTimestamp, int threadId, MainViewUpdateMode mode, long elapsedMs)
    {
        RequestId = requestId;
        EndTimestamp = endTimestamp;
        ThreadId = threadId;
        Mode = mode;
        ElapsedMs = elapsedMs;
    }
    internal long RequestId { get; }
    internal long EndTimestamp { get; }
    internal int ThreadId { get; }
    internal MainViewUpdateMode Mode { get; }
    internal long ElapsedMs { get; }
}

internal readonly struct RegularChartListTerminalResult
{
    private RegularChartListTerminalResult(bool wasCommitted, MainChartListRowsApplyResult rowsApply)
    {
        WasCommitted = wasCommitted;
        RowsApply = rowsApply;
    }
    internal bool WasCommitted { get; }
    internal MainChartListRowsApplyResult RowsApply { get; }
    internal static RegularChartListTerminalResult Stale() => new(false, default);
    internal static RegularChartListTerminalResult Committed(MainChartListRowsApplyResult rowsApply) => new(true, rowsApply);
}

internal sealed class RegularChartListTerminalPublishException : Exception
{
    internal RegularChartListTerminalPublishException()
    {
    }

    internal RegularChartListTerminalPublishException(string message)
        : base(message)
    {
    }

    internal RegularChartListTerminalPublishException(Exception innerException)
        : base("Regular chart-list terminal state was committed but publishing notifications failed.", innerException)
    {
    }

    internal RegularChartListTerminalPublishException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private RegularChartListTerminalPublishException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
    }
}

internal readonly struct MainChartListColumnSelection
{
    internal MainChartListColumnSelection(CustomTableColumnSettings columnsSettings, bool reused, long elapsedMs, MainViewUpdateMode? appliedMode, System.Windows.Visibility playlistColumnSettingsVisibility, PlaylistSummaryColumnSettings playlistSummaryColumnsSettings)
    {
        ColumnsSettings = columnsSettings;
        Reused = reused;
        ElapsedMs = elapsedMs;
        AppliedMode = appliedMode;
        PlaylistColumnSettingsVisibility = playlistColumnSettingsVisibility;
        PlaylistSummaryColumnsSettings = playlistSummaryColumnsSettings;
    }
    internal CustomTableColumnSettings ColumnsSettings { get; }
    internal bool Reused { get; }
    internal long ElapsedMs { get; }
    internal MainViewUpdateMode? AppliedMode { get; }
    internal System.Windows.Visibility PlaylistColumnSettingsVisibility { get; }
    internal PlaylistSummaryColumnSettings PlaylistSummaryColumnsSettings { get; }
}

internal static class MainViewBuildRequestSequence
{
    private static long seed;

    internal static long Next() => Interlocked.Increment(ref seed);
}
