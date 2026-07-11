using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views;
using Ribbit.Util;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns request lifetime, derived rows, sort reuse, warmup lifetime, and terminal publication for the regular chart list.
/// </summary>
internal sealed class RegularChartListOwner : IDisposable
{
    internal event EventHandler<MainChartListSortRequestedEventArgs> SortChanged;

    internal event EventHandler<MainChartListSortRequestedEventArgs> SortRefreshRequested;

    internal event EventHandler<RegularChartFolderEditRequestedEventArgs> FolderEditRequested;

    internal event EventHandler<RegularChartInstallDestinationEditRequestedEventArgs> InstallDestinationEditRequested;

    private const int StartupVirtualOrderPrewarmMaxPriority = 3;
    private readonly object syncRoot = new();
    private readonly MainChartListViewModel mainChartList;
    private readonly PlaylistWorkspaceViewModel playlistWorkspace;
    private readonly Action<string> log;
    private readonly Action<string> logWarning;
    private readonly Action<Action> dispatchToUi;
    private readonly Dictionary<NormalLibrarySortCacheKey, List<LibraryChartRow>> sortCache = [];
    private readonly Dictionary<NormalLibrarySortCacheKey, ChartListOrder> virtualOrderCache = [];
    private readonly Dictionary<VirtualChartSubsetSortCacheKey, ChartListOrder> virtualSubsetOrderCache = [];
    private readonly Dictionary<MainViewSummaryCacheKey, int> virtualSummaryCache = [];
    private readonly Dictionary<MainViewSummaryCacheKey, VirtualSummaryWork> virtualSummaryRunning = [];
    private readonly NormalLibraryRowCache rowCache = new();
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
    private RegularNormalLibraryTreeFilter treeFilter;
    private ChartListSortSpecification currentSort;
    private string currentKeywordFilter;
    private RegularChartModeFilter currentModeFilter = RegularChartModeFilter.All;
    private List<ChartListSourceRow> virtualSourceRows;
    private BMSLibrary virtualSourceRowsLibrary;
    private bool virtualSourceRowsLibraryReserved;
    private bool virtualSourceRowsAvailable;
    private long virtualSourceRowsGeneration;
    private bool virtualSourceRowsIncludeBmson;
    private long sourceGeneration;
    private long sortKeyGeneration;
    private long sortRequestRevision;
    private MainChartListSortRequestedEventArgs pendingSortRefresh;
    private bool sortRefreshWorkerActive;
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
        Action<Action> dispatchToUi,
        Action<string> logWarning = null)
    {
        this.mainChartList = mainChartList ?? throw new ArgumentNullException(nameof(mainChartList));
        this.playlistWorkspace = playlistWorkspace ?? throw new ArgumentNullException(nameof(playlistWorkspace));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.logWarning = logWarning ?? log;
        this.mainChartList.AppliedColumnModeCommitted += MainChartListAppliedColumnModeCommitted;
    }

    private void MainChartListAppliedColumnModeCommitted(MainViewUpdateMode mode)
    {
        if (mode == MainViewUpdateMode.PlayHistorySelected)
        {
            ResetDerivedCaches();
        }
    }

    internal ChartListSortSpecification CaptureSort()
    {
        lock (syncRoot)
        {
            return currentSort;
        }
    }

    internal ChartListSortParameters CaptureSortParameters()
    {
        lock (syncRoot)
        {
            return currentSort.HasValue
                ? new ChartListSortParameters
                {
                    ColumnsName = currentSort.RequestedColumnName,
                    Direction = currentSort.Direction
                }
                : null;
        }
    }

    internal bool ResetSortParameters(ChartListSortSpecification expectedSort)
    {
        lock (syncRoot)
        {
            if (!currentSort.HasValue
                || !expectedSort.HasValue
                || !string.Equals(currentSort.RequestedColumnName, expectedSort.RequestedColumnName, StringComparison.Ordinal)
                || currentSort.Direction != expectedSort.Direction)
            {
                return false;
            }

            currentSort = ChartListSortSpecification.Create(
                columnName: null,
                ListSortDirection.Ascending,
                hasValue: false);
            return true;
        }
    }

    internal void SetFilters(string keywordFilter, RegularChartModeFilter modeFilter)
    {
        lock (syncRoot)
        {
            currentKeywordFilter = keywordFilter;
            currentModeFilter = modeFilter;
        }
    }

    private void CaptureFilters(out string keywordFilter, out RegularChartModeFilter modeFilter)
    {
        lock (syncRoot)
        {
            keywordFilter = currentKeywordFilter;
            modeFilter = currentModeFilter;
        }
    }

    internal bool HasTreeFilter
    {
        get
        {
            lock (syncRoot)
            {
                return treeFilter != null;
            }
        }
    }

    internal void SetTreeFilter(RegularNormalLibraryTreeFilter filter)
    {
        lock (syncRoot)
        {
            treeFilter = filter;
        }
    }

    internal RegularNormalLibraryTreeFilter CaptureTreeFilter(bool enabled)
    {
        lock (syncRoot)
        {
            return enabled ? treeFilter : null;
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

    internal bool TryBeginVirtualRequest(BMSLibrary library, out RegularChartListRequestLease lease)
    {
        CancellationTokenSource previousRequest;
        CancellationTokenSource prewarmCancellation = null;
        lock (syncRoot)
        {
            if (disposed)
            {
                lease = null;
                return false;
            }
            if (virtualSourceRowsLibraryReserved
                && !ReferenceEquals(virtualSourceRowsLibrary, library))
            {
                sourceGeneration++;
                ClearAllSortCachesUnsafe(clearSourceRows: true);
                prewarmCancellation = GetActivePrewarmCancellationUnsafe();
            }
            virtualSourceRowsLibrary = library;
            virtualSourceRowsLibraryReserved = true;
            previousRequest = currentCancellation;
            currentCancellation = new CancellationTokenSource();
            currentRequestId = MainViewBuildRequestSequence.Next();
            regularRequestActive = true;
            lease = new RegularChartListRequestLease(currentRequestId, currentCancellation.Token);
        }
        CancelAndDispose(previousRequest);
        Cancel(prewarmCancellation);
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

    internal void QueueSort(MainChartListSortRequestedEventArgs request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (request.Target != MainChartListSortTarget.Regular)
        {
            throw new ArgumentException("A regular chart-list sort request is required.", nameof(request));
        }
        MainChartListSortRequestedEventArgs ownedRequest;
        bool startWorker;
        CancellationTokenSource previousRequest;
        lock (syncRoot)
        {
            ChartListSortSpecification next = ChartListSortSpecification.Create(
                request.ColumnName,
                request.Direction,
                hasValue: true);
            if (currentSort.HasValue
                && string.Equals(currentSort.RequestedColumnName, next.RequestedColumnName, StringComparison.Ordinal)
                && currentSort.Direction == next.Direction)
            {
                return;
            }
            currentSort = next;
            ownedRequest = new MainChartListSortRequestedEventArgs(
                request.ColumnName,
                request.Direction,
                request.Target,
                ++sortRequestRevision);
            previousRequest = InvalidateCurrentRequestUnsafe();
            pendingSortRefresh = ownedRequest;
            startWorker = !sortRefreshWorkerActive;
            sortRefreshWorkerActive = true;
        }
        CancelAndDispose(previousRequest);
        try
        {
            SortChanged?.Invoke(this, ownedRequest);
        }
        finally
        {
            if (startWorker)
            {
                Task.Run(ProcessSortRefreshQueue).Logging("regularChartListSortRequested");
            }
        }
    }

    internal bool CanBeginCellEdit(MainChartListCellEditContext context)
    {
        if (context == null || string.IsNullOrWhiteSpace(context.PropertyName))
        {
            return false;
        }
        if (string.Equals(context.PropertyName, nameof(LibraryChartRow.Folder), StringComparison.Ordinal))
        {
            return GridRowResolver.TryGetFolderEditChartOperationTarget(context.Row, context.SourceScope, out _);
        }
        return string.Equals(context.PropertyName, "instl_dst", StringComparison.Ordinal)
            && IsInstallDestinationEditSection(context.OperationSection)
            && GridRowResolver.TryGetChartOperationTarget(context.Row, context.SourceScope, out ChartOperationTarget target)
            && target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination);
    }

    internal void CompleteCellEdit(MainChartListCellEditEndedEventArgs request)
    {
        if (request == null || !request.Commit)
        {
            return;
        }
        MainChartListCellEditContext context = request.Context;
        if (string.Equals(context.PropertyName, nameof(LibraryChartRow.Folder), StringComparison.Ordinal)
            && GridRowResolver.TryGetFolderEditChartOperationTarget(context.Row, context.SourceScope, out ChartOperationTarget folderTarget)
            && RenameChartFolderRequest.TryCreate(folderTarget, out RenameChartFolderRequest renameRequest))
        {
            FolderEditRequested?.Invoke(this, new RegularChartFolderEditRequestedEventArgs(renameRequest, request.Text));
            return;
        }
        if (string.Equals(context.PropertyName, "instl_dst", StringComparison.Ordinal)
            && IsInstallDestinationEditSection(context.OperationSection)
            && GridRowResolver.TryGetChartOperationTarget(context.Row, context.SourceScope, out ChartOperationTarget installTarget)
            && installTarget.HasCapability(ChartOperationCapabilities.UpdateInstallDestination)
            && PendingInstallDestinationEditRequest.TryCreate(installTarget, out PendingInstallDestinationEditRequest installRequest))
        {
            InstallDestinationEditRequested?.Invoke(
                this,
                new RegularChartInstallDestinationEditRequestedEventArgs(installRequest, request.Text));
        }
    }

    private static bool IsInstallDestinationEditSection(MainViewOperationSection section)
    {
        return section == MainViewOperationSection.InstallPending
            || section == MainViewOperationSection.FullScanCheck;
    }

    private void ProcessSortRefreshQueue()
    {
        bool restartWorker = false;
        try
        {
            while (true)
            {
                MainChartListSortRequestedEventArgs request;
                lock (syncRoot)
                {
                    request = pendingSortRefresh;
                    pendingSortRefresh = null;
                    if (request == null)
                    {
                        return;
                    }
                }
                if (IsCurrentSortRequest(request))
                {
                    SortRefreshRequested?.Invoke(this, request);
                }
            }
        }
        finally
        {
            lock (syncRoot)
            {
                sortRefreshWorkerActive = false;
                if (pendingSortRefresh != null)
                {
                    sortRefreshWorkerActive = true;
                    restartWorker = true;
                }
            }
            if (restartWorker)
            {
                Task.Run(ProcessSortRefreshQueue).Logging("regularChartListSortRequested");
            }
        }
    }

    internal bool IsCurrentSortRequest(MainChartListSortRequestedEventArgs request)
    {
        lock (syncRoot)
        {
            return request.OwnerRevision == sortRequestRevision
                && currentSort.HasValue
                && string.Equals(currentSort.RequestedColumnName, request.ColumnName, StringComparison.Ordinal)
                && currentSort.Direction == request.Direction;
        }
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

    internal IReadOnlyList<LibraryChartRow> SnapshotRows()
    {
        return rowCache.SnapshotRows();
    }

    internal int PruneBmsRows(IEnumerable<BMSFile> currentFiles)
    {
        return rowCache.Count == 0 ? 0 : rowCache.PruneBmsFiles(currentFiles);
    }

    internal int RemoveBmsRows(IEnumerable<BMSFile> removedFiles)
    {
        return rowCache.Count == 0 ? 0 : rowCache.RemoveBmsFiles(removedFiles);
    }

    internal BmsonLibraryRowCacheSyncResult SyncBmsonRows(
        BMSLibrary library,
        OwnedChartStorageOwnerView ownerView = null)
    {
        ownerView ??= library?.CreateNormalLibrarySourceStorageOwnerView();
        mainChartList.RowProjection.PruneTransientStatesToOwnedCharts(library);
        return rowCache.SyncBmsonRows(
            ownerView?.BmsonSongs ?? [],
            row => mainChartList.RowProjection.ConfigureLibraryRow(library, row));
    }

    internal BmsonLibraryRowCacheSyncResult RemoveBmsonRows(
        IEnumerable<LR2SongDBExtended.bmson_song> removedSongs)
    {
        return rowCache.RemoveBmsonSongs(removedSongs);
    }

    internal LibraryChartRow CreateVirtualRow(BMSLibrary library, ChartListSourceRow sourceRow)
    {
        if (sourceRow == null)
        {
            return null;
        }
        LibraryChartRow row = rowCache.GetOrCreate(sourceRow.Chart, null)
            ?? LibraryChartRow.FromChartFile(sourceRow.Chart);
        mainChartList.RowProjection.ConfigureLibraryRow(library, row);
        return row;
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

    internal bool TryBeginVirtualOrderPrewarm(BMSLibrary library, out RegularChartListPrewarmLease lease)
    {
        CancellationTokenSource previousRequest = null;
        CancellationTokenSource activePrewarmToCancel = null;
        CancellationTokenSource completedPrewarmToDispose = null;
        bool started = false;
        lock (syncRoot)
        {
            if (disposed)
            {
                lease = null;
                return false;
            }
            if (virtualSourceRowsLibraryReserved
                && !ReferenceEquals(virtualSourceRowsLibrary, library))
            {
                sourceGeneration++;
                previousRequest = InvalidateCurrentRequestUnsafe();
                ClearAllSortCachesUnsafe(clearSourceRows: true);
                activePrewarmToCancel = GetActivePrewarmCancellationUnsafe();
            }
            virtualSourceRowsLibrary = library;
            virtualSourceRowsLibraryReserved = true;
            if (!virtualOrderPrewarmCompletion.IsCompleted)
            {
                lease = null;
            }
            else
            {
                completedPrewarmToDispose = virtualOrderPrewarmCancellation;
                virtualOrderPrewarmCancellation = new CancellationTokenSource();
                lease = new RegularChartListPrewarmLease(
                    ++virtualOrderPrewarmRunId,
                    virtualOrderPrewarmCancellation.Token);
                virtualOrderPrewarmCompletion = lease.Completion;
                started = true;
            }
        }
        CancelAndDispose(previousRequest);
        Cancel(activePrewarmToCancel);
        CancelAndDispose(completedPrewarmToDispose);
        return started;
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

    internal void RunVirtualOrderPrewarm(
        RegularChartListPrewarmLease lease,
        BMSLibrary library,
        bool includeBmsonRows,
        IReadOnlyList<VirtualNormalLibrarySortDescriptor> descriptors,
        string reason)
    {
        if (lease == null)
        {
            throw new ArgumentNullException(nameof(lease));
        }

        var stopwatch = Stopwatch.StartNew();
        int descriptorCount = descriptors?.Count ?? 0;
        int degree = ResolveVirtualOrderPrewarmDegree(descriptorCount);
        int cacheHitCount = 0;
        int builtCount = 0;
        int staleSkippedCount = 0;
        int rowCount = 0;
        long sourceGenerationAtLookup = 0L;
        long sortKeyGenerationAtLookup = 0L;
        try
        {
            lease.Token.ThrowIfCancellationRequested();
            log("virtual_order_prewarm start reason=" + (reason ?? string.Empty)
                + " runId=" + lease.RunId
                + " descriptorCount=" + descriptorCount
                + " degree=" + degree);
            List<ChartListSourceRow> sourceRows = GetOrCreateVirtualNormalLibrarySourceRows(
                library,
                includeBmsonRows,
                out bool sourceRowsCacheHit,
                out sourceGenerationAtLookup,
                out sortKeyGenerationAtLookup);
            rowCount = sourceRows.Count;
            if (!IsCurrentVirtualGeneration(sourceGenerationAtLookup, sortKeyGenerationAtLookup))
            {
                staleSkippedCount = descriptorCount;
                return;
            }

            foreach (IGrouping<int, VirtualNormalLibrarySortDescriptor> stage in (descriptors ?? [])
                .GroupBy(descriptor => descriptor.PrewarmPriority)
                .OrderBy(group => group.Key))
            {
                VirtualNormalLibrarySortDescriptor[] stageDescriptors = [.. stage];
                int stageCacheHitCount = 0;
                int stageBuiltCount = 0;
                int stageStaleDetected = 0;
                int stageDegree = ResolveVirtualOrderPrewarmDegree(stageDescriptors.Length);
                if (!IsCurrentVirtualGeneration(sourceGenerationAtLookup, sortKeyGenerationAtLookup))
                {
                    staleSkippedCount += stageDescriptors.Length;
                    break;
                }

                Parallel.ForEach(
                    stageDescriptors,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = stageDegree,
                        CancellationToken = lease.Token
                    },
                    (descriptor, loopState) =>
                    {
                        if (!IsCurrentVirtualGeneration(sourceGenerationAtLookup, sortKeyGenerationAtLookup))
                        {
                            Interlocked.Exchange(ref stageStaleDetected, 1);
                            loopState.Stop();
                            return;
                        }
                        _ = GetOrCreateVirtualNormalLibraryOrder(
                            sourceRows,
                            library,
                            descriptor.ColumnName,
                            descriptor.Direction,
                            sourceGenerationAtLookup,
                            sortKeyGenerationAtLookup,
                            CaptureExternalVersions(library, default),
                            useCache: true,
                            out bool cacheHit,
                            out _,
                            out _,
                            out _);
                        if (cacheHit)
                        {
                            Interlocked.Increment(ref stageCacheHitCount);
                        }
                        else
                        {
                            Interlocked.Increment(ref stageBuiltCount);
                        }
                    });
                cacheHitCount += stageCacheHitCount;
                builtCount += stageBuiltCount;
                if (Volatile.Read(ref stageStaleDetected) != 0
                    || !IsCurrentVirtualGeneration(sourceGenerationAtLookup, sortKeyGenerationAtLookup))
                {
                    staleSkippedCount += Math.Max(0, stageDescriptors.Length - stageCacheHitCount - stageBuiltCount);
                    break;
                }
            }

            log("virtual_order_prewarm done reason=" + (reason ?? string.Empty)
                + " runId=" + lease.RunId
                + " descriptorCount=" + descriptorCount
                + " rowCount=" + rowCount
                + " sourceRowsReuse=" + sourceRowsCacheHit
                + " cacheHit=" + cacheHitCount
                + " built=" + builtCount
                + " staleSkipped=" + staleSkippedCount
                + " sourceGeneration=" + sourceGenerationAtLookup
                + " sortKeyGeneration=" + sortKeyGenerationAtLookup
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
        {
            log("virtual_order_prewarm cancelled reason=" + (reason ?? string.Empty)
                + " runId=" + lease.RunId
                + " descriptorCount=" + descriptorCount
                + " rowCount=" + rowCount
                + " cacheHit=" + cacheHitCount
                + " built=" + builtCount
                + " staleSkipped=" + staleSkippedCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            log("virtual_order_prewarm failed reason=" + (reason ?? string.Empty)
                + " runId=" + lease.RunId
                + " descriptorCount=" + descriptorCount
                + " rowCount=" + rowCount
                + " cacheHit=" + cacheHitCount
                + " built=" + builtCount
                + " staleSkipped=" + staleSkippedCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name);
        }
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

    internal RegularVirtualSourceRowsLookup LookupVirtualSourceRows(BMSLibrary library, bool includeBmsonRows)
    {
        lock (syncRoot)
        {
            if (!virtualSourceRowsLibraryReserved)
            {
                virtualSourceRowsLibrary = library;
                virtualSourceRowsLibraryReserved = true;
            }
            bool identityMatches = ReferenceEquals(virtualSourceRowsLibrary, library);
            bool cacheHit = identityMatches
                && virtualSourceRowsAvailable
                && virtualSourceRows != null
                && virtualSourceRowsGeneration == sourceGeneration
                && virtualSourceRowsIncludeBmson == includeBmsonRows;
            return new RegularVirtualSourceRowsLookup(
                library,
                includeBmsonRows,
                identityMatches ? sourceGeneration : -1L,
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
                || !virtualSourceRowsLibraryReserved
                || !ReferenceEquals(virtualSourceRowsLibrary, lookup.Library)
                || sourceGeneration != lookup.SourceGeneration
                || sortKeyGeneration != lookup.SortKeyGeneration)
            {
                return;
            }
            virtualSourceRows = rows;
            virtualSourceRowsLibrary = lookup.Library;
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
        IReadOnlyList<LibraryChartRow> nextModeRows = request.ModeFilter != RegularChartModeFilter.All
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

    internal RegularMaterializedChartListApplyResult TryApplyMaterialized(
        RegularMaterializedChartListApplyRequest request)
    {
        if (request == null)
        {
            throw new ArgumentException("A complete materialized regular chart-list request is required.", nameof(request));
        }
        if (!TryBeginRequest(out RegularChartListRequestLease lease))
        {
            return default;
        }

        Stopwatch stopwatch = request.Stopwatch ?? Stopwatch.StartNew();
        RegularChartListBuildResult build;
        try
        {
            build = Build(
                lease,
                request.RefreshRequest,
                new RegularChartListBuildInput
                {
                    CurrentTreeMode = request.RefreshRequest.CurrentTreeMode,
                    HasVirtualNormalLibraryTreeFilter = HasTreeFilter,
                    IsPlaylistDetailView = false,
                    HasFolderRowsOverride = request.HasFolderRowsOverride,
                    FolderRowsOverride = request.FolderRowsOverride,
                    SortCacheGeneration = CaptureSortGeneration(
                        request.RefreshRequest.SortColumnName,
                        request.ExternalVersions),
                    Stopwatch = stopwatch
                });
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
        {
            return default;
        }

        IList rowsView = build.Sort.RowsView;
        long terminalStageStartMs = stopwatch.ElapsedMilliseconds;
        RegularChartListTerminalResult terminal = TryCommit(
            lease,
            RegularChartListPresentationResult.ForMaterialized(
                build,
                new MainChartListRowsApplyRequest
                {
                    Rows = rowsView,
                    ColumnsSettings = request.ColumnSelection.ColumnsSettings,
                    SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                    Summary = request.PreserveSummary
                        ? MainChartListSummaryUpdate.Preserve()
                        : MainChartListSummaryUpdate.NormalRows(rowsView),
                    ColumnSettingReuse = request.ColumnSelection.Reused,
                    ColumnPreparationMs = request.ColumnSelection.ElapsedMs,
                    TerminalStageStartMs = terminalStageStartMs,
                    Stopwatch = stopwatch
                },
                request.ColumnSelection,
                request.Mode,
                stopwatch));
        return terminal.WasCommitted
            ? new RegularMaterializedChartListApplyResult(terminal.RowsApply, build, request.Mode)
            : default;
    }

    /// <summary>
    /// Cancels regular work before the shell mutates main-view selection or route state.
    /// </summary>
    internal void PrepareForMainViewRefresh()
    {
        InvalidatePendingRequest();
    }

    /// <summary>
    /// Applies a main-library refresh route and owns the regular request construction for that route.
    /// </summary>
    /// <param name="route">The route classified for the main-library workflow.</param>
    /// <param name="library">The current BMS library.</param>
    /// <param name="parameter">The parameter supplied by the refresh trigger.</param>
    /// <param name="treeParameter">The current tree selection parameter.</param>
    /// <param name="preserveSummary">Whether the current table summary should be preserved.</param>
    /// <param name="stopwatch">The stopwatch covering the refresh workflow.</param>
    /// <returns>The regular chart-list entry result.</returns>
    internal RegularChartListEntryResult ApplyMainLibraryView(
        ChartListRefreshRoute route,
        BMSLibrary library,
        object parameter,
        object treeParameter,
        bool preserveSummary,
        Stopwatch stopwatch)
    {
        if (route.Kind != ChartListRefreshRouteKind.ContinueMainLibrary)
        {
            throw new ArgumentException(
                "The regular chart-list owner requires a main-library route.",
                nameof(route));
        }

        return ApplyRegularView(
            new RegularChartListEntryRequest
            {
                Library = library,
                Mode = route.Mode,
                RequestedMode = route.RequestedMode,
                Parameter = parameter,
                CurrentTreeMode = route.CurrentTreeMode,
                TreeParameter = treeParameter,
                IncludeBmsonRows = route.IncludeBmsonRows,
                PreserveSummary = preserveSummary,
                Stopwatch = stopwatch
            });
    }

    internal RegularChartListEntryResult ApplyRegularView(RegularChartListEntryRequest request)
    {
        if (request == null || request.Stopwatch == null)
        {
            throw new ArgumentException("A complete regular chart-list entry request is required.", nameof(request));
        }
        if (request.IncludeBmsonRows && request.Library == null)
        {
            throw new ArgumentException("A library is required when bmson rows are included.", nameof(request));
        }
        ChartListSortSpecification sort = CaptureSort();
        CaptureFilters(out string keywordFilter, out RegularChartModeFilter modeFilter);
        RegularChartListExternalVersions externalVersions = CaptureExternalVersions(request.Library, default);
        ClearPlaylistSourceRowsForRegularView();

        MainViewUpdateMode resolvedMode = ChartListRefreshCoordinator.ResolveMainColumnSettingMode(request.Mode, request.CurrentTreeMode);
        MainViewUpdateMode resolvedTreeMode = ChartListRefreshCoordinator.ResolveMainColumnSettingMode(request.CurrentTreeMode, request.CurrentTreeMode);
        MainChartListColumnSelection columnSelection = mainChartList.ResolveColumnSettingForViewUpdate(resolvedMode, request.CurrentTreeMode);
        MainChartListColumnSelection treeColumnSelection = resolvedMode == resolvedTreeMode
            ? columnSelection
            : mainChartList.ResolveColumnSettingForViewUpdate(resolvedTreeMode, request.CurrentTreeMode);

        SynchronizeBmsonRowsForRefresh(request);
        bool sortWasReset = !TryResolveVirtualSort(
            sort,
            out string virtualSortColumn,
            out ListSortDirection virtualSortDirection);
        if (sortWasReset)
        {
            virtualSortColumn = nameof(LibraryChartRow.Title);
            virtualSortDirection = ListSortDirection.Ascending;
        }

        if (IsDefaultVirtualRequest(request.Mode, request.CurrentTreeMode))
        {
            RegularVirtualNormalLibraryApplyResult result = TryApplyVirtualNormalLibrary(
                new RegularVirtualNormalLibraryApplyRequest
                {
                    Library = request.Library,
                    IncludeBmsonRows = request.IncludeBmsonRows,
                    TreeFilter = CaptureTreeFilter(ShouldApplyDefaultTreeFilter(request.CurrentTreeMode)),
                    KeywordFilter = keywordFilter,
                    ModeFilter = modeFilter,
                    SortColumnName = virtualSortColumn,
                    SortDirection = virtualSortDirection,
                    ExternalVersions = externalVersions,
                    ColumnSelection = columnSelection,
                    PreserveSummary = request.PreserveSummary,
                    Mode = request.Mode,
                    Stopwatch = request.Stopwatch,
                    Reason = request.Mode.ToString()
                });
            LogDefaultVirtualEntry(request, sort, result, sortWasReset);
            if (result.WasCommitted && sortWasReset)
            {
                ResetSortParameters(sort);
            }
            return new RegularChartListEntryResult(result.WasCommitted, RegularChartListEntryRoute.DefaultVirtual, sortWasReset);
        }

        if (IsSubsetVirtualRequest(request.Mode, request.CurrentTreeMode)
            && TryResolveSubsetSource(request, request.Library, out RegularChartListSubsetSource subset))
        {
            RegularVirtualChartSubsetApplyResult result = TryApplyVirtualChartSubset(
                new RegularVirtualChartSubsetApplyRequest
                {
                    Library = request.Library,
                    SourceCharts = subset.SourceCharts,
                    SourceEntries = subset.SourceEntries,
                    SourceProjectionMode = subset.SourceProjectionMode,
                    ApplyResourceHealthProjection = subset.ApplyResourceHealthProjection,
                    TreeMode = request.CurrentTreeMode,
                    SubsetName = subset.Name,
                    KeywordFilter = keywordFilter,
                    ModeFilter = modeFilter,
                    SortColumnName = virtualSortColumn,
                    SortDirection = virtualSortDirection,
                    ExternalVersions = externalVersions,
                    ColumnSelection = columnSelection,
                    PreserveSummary = request.PreserveSummary,
                    Mode = request.Mode,
                    Stopwatch = request.Stopwatch
                });
            LogSubsetVirtualEntry(request, sort, subset.Name, result, sortWasReset);
            if (result.WasCommitted && sortWasReset)
            {
                ResetSortParameters(sort);
            }
            return new RegularChartListEntryResult(result.WasCommitted, RegularChartListEntryRoute.SubsetVirtual, sortWasReset);
        }

        bool virtualSubsetRequiredFailure = IsSubsetVirtualRequired(request.Mode, request.CurrentTreeMode);
        if (virtualSubsetRequiredFailure)
        {
            logWarning("main_view_virtual_required_failed scope=chart_subset"
                + " mode=" + request.Mode
                + " requestedMode=" + request.RequestedMode
                + " treeMode=" + request.CurrentTreeMode);
        }
        bool hasFolderRowsOverride = virtualSubsetRequiredFailure;
        IEnumerable<LibraryChartRow> folderRowsOverride = virtualSubsetRequiredFailure ? [] : null;
        MainViewUpdateMode effectiveMode = request.Mode;
        object effectiveParameter = request.Parameter;
        if (!virtualSubsetRequiredFailure
            && MainViewRefreshDecisionService.ShouldRebuildRegularFolderStage(
                request.Mode,
                HasFolderRows,
                HasKeywordRows,
                HasModeRows,
                request.CurrentTreeMode))
        {
            effectiveMode = request.CurrentTreeMode;
            effectiveParameter = request.TreeParameter;
        }

        var refreshRequest = new RegularChartListRefreshRequest(
            effectiveMode,
            request.RequestedMode,
            effectiveParameter,
            request.CurrentTreeMode,
            request.TreeParameter,
            request.IncludeBmsonRows,
            virtualSubsetRequiredFailure,
            keywordFilter,
            modeFilter,
            sort);
        if (!refreshRequest.VirtualSubsetRequiredFailure
            && (refreshRequest.Mode == MainViewUpdateMode.FolderFilterSelected
                || refreshRequest.Mode == MainViewUpdateMode.FullScanAllChartsFilterSelected))
        {
            hasFolderRowsOverride = true;
            folderRowsOverride = [];
        }

        RegularMaterializedChartListApplyResult materialized = TryApplyMaterialized(
            new RegularMaterializedChartListApplyRequest
            {
                RefreshRequest = refreshRequest,
                HasFolderRowsOverride = hasFolderRowsOverride,
                FolderRowsOverride = folderRowsOverride,
                ExternalVersions = externalVersions,
                ColumnSelection = effectiveMode == request.Mode ? columnSelection : treeColumnSelection,
                PreserveSummary = request.PreserveSummary,
                Mode = effectiveMode,
                Stopwatch = request.Stopwatch
            });
        LogMaterializedEntry(request, effectiveMode, refreshRequest, materialized);
        return new RegularChartListEntryResult(materialized.WasCommitted, RegularChartListEntryRoute.Materialized, sortWasReset: false);
    }

    private void ClearPlaylistSourceRowsForRegularView()
    {
        playlistWorkspace.ClearDetailSourceForRegularView();
    }

    private void SynchronizeBmsonRowsForRefresh(RegularChartListEntryRequest request)
    {
        if (!request.IncludeBmsonRows)
        {
            return;
        }

        BmsonLibraryRowCacheSyncResult result = SyncBmsonRows(request.Library);
        if (result.SortKeyChanged)
        {
            int cacheCount = InvalidateIdentitySortKeys(result.SourceIdentityChanged);
            log("normal_library_sort_cache_invalidate reason=sort_key detail="
                + (result.SourceIdentityChanged ? "bmson_source_identity_changed" : "bmson_sort_key_changed")
                + " cacheCountBefore=" + cacheCount);
        }
        if (result.SourceChanged)
        {
            string reason = result.MembershipChanged
                ? "bmson_membership_changed"
                : result.SourceIdentityChanged
                    ? "bmson_source_identity_changed"
                    : "bmson_source_reference_changed";
            if (TryInvalidateSourceForOwnedCollectionVersion(request.Library.OwnedChartCollectionVersion, out int cacheCount))
            {
                log("normal_library_sort_cache_invalidate reason=source detail=" + reason + " cacheCountBefore=" + cacheCount);
            }
            else
            {
                InvalidateVirtualSourceRows();
            }
        }
    }

    private static bool TryResolveVirtualSort(
        ChartListSortSpecification sort,
        out string columnName,
        out ListSortDirection direction)
    {
        direction = sort.HasValue ? sort.Direction : ListSortDirection.Ascending;
        if (!sort.HasValue)
        {
            columnName = nameof(LibraryChartRow.Title);
            return true;
        }
        return ChartListOrder.TryNormalizeVirtualSortColumn(sort.RequestedColumnName, out columnName);
    }

    internal static bool IsDefaultVirtualRequest(MainViewUpdateMode mode, MainViewUpdateMode treeMode)
    {
        return (treeMode == MainViewUpdateMode.FolderFilterSelected
                || treeMode == MainViewUpdateMode.FullScanAllChartsFilterSelected)
            && (mode == treeMode
                || mode == MainViewUpdateMode.TreeViewFilterNotChanged
                || mode == MainViewUpdateMode.KeywordFilterUpdated
                || mode == MainViewUpdateMode.ModeFilterUpdated
                || mode == MainViewUpdateMode.SortUpdated);
    }

    internal static bool IsSubsetVirtualRequest(MainViewUpdateMode mode, MainViewUpdateMode treeMode)
    {
        return IsSubsetVirtualTreeMode(treeMode)
            && (mode == treeMode
                || mode == MainViewUpdateMode.TreeViewFilterNotChanged
                || mode == MainViewUpdateMode.KeywordFilterUpdated
                || mode == MainViewUpdateMode.ModeFilterUpdated
                || mode == MainViewUpdateMode.SortUpdated);
    }

    internal static bool IsSubsetVirtualRequired(MainViewUpdateMode mode, MainViewUpdateMode treeMode)
    {
        return IsSubsetVirtualRequest(mode, treeMode) || IsSubsetVirtualTreeMode(mode);
    }

    internal static bool IsSubsetVirtualTreeMode(MainViewUpdateMode mode)
    {
        return mode == MainViewUpdateMode.FileMissingFilterSelected
            || mode == MainViewUpdateMode.FileMissingIgnoredFilterSelected
            || mode == MainViewUpdateMode.DuplicateFilterSelected
            || mode == MainViewUpdateMode.GarbledFilterSelected
            || mode == MainViewUpdateMode.GarbleFixedFilterSelected
            || mode == MainViewUpdateMode.UnregisteredFilterSelected
            || mode == MainViewUpdateMode.ZeroNoteFilterSelected
            || mode == MainViewUpdateMode.ChartInfoParseErrorFilterSelected
            || mode == MainViewUpdateMode.NewlyInstalledFolderSelected
            || mode == MainViewUpdateMode.PendingInstallFolderSelected;
    }

    internal static bool ShouldApplyDefaultTreeFilter(MainViewUpdateMode treeMode)
    {
        return treeMode != MainViewUpdateMode.FullScanAllChartsFilterSelected;
    }

    internal static bool ShouldApplyResourceHealthProjection(MainViewUpdateMode treeMode)
    {
        return treeMode == MainViewUpdateMode.FileMissingFilterSelected
            || treeMode == MainViewUpdateMode.FileMissingIgnoredFilterSelected
            || treeMode == MainViewUpdateMode.NewlyInstalledFolderSelected;
    }

    private static bool TryResolveSubsetSource(
        RegularChartListEntryRequest request,
        BMSLibrary library,
        out RegularChartListSubsetSource source)
    {
        object parameter = request.CurrentTreeMode == MainViewUpdateMode.DuplicateFilterSelected
            ? request.TreeParameter ?? request.Parameter
            : request.CurrentTreeMode == MainViewUpdateMode.NewlyInstalledFolderSelected
                || request.CurrentTreeMode == MainViewUpdateMode.PendingInstallFolderSelected
                    ? request.TreeParameter ?? request.Parameter
                    : request.Parameter;
        switch (request.CurrentTreeMode)
        {
            case MainViewUpdateMode.FileMissingFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesNeedResourceFix, "file_missing", applyResourceHealthProjection: true);
                return true;
            case MainViewUpdateMode.FileMissingIgnoredFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesNeedResourceFixIgnored, "file_missing_ignored", applyResourceHealthProjection: true);
                return true;
            case MainViewUpdateMode.DuplicateFilterSelected:
                return TryResolveDuplicateSource(library?.DuplicateChartGroups, parameter, out source);
            case MainViewUpdateMode.GarbledFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesGarbled, "garbled", ChartListSourceProjectionMode.OwnerBacked);
                return true;
            case MainViewUpdateMode.GarbleFixedFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesGarbledFixed, "garble_fixed", ChartListSourceProjectionMode.OwnerBacked);
                return true;
            case MainViewUpdateMode.UnregisteredFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesUnregistered, "unregistered", ChartListSourceProjectionMode.OwnerBacked);
                return true;
            case MainViewUpdateMode.ZeroNoteFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartFilesZeroNote, "zero_note", ChartListSourceProjectionMode.OwnerBacked);
                return true;
            case MainViewUpdateMode.ChartInfoParseErrorFilterSelected:
                source = RegularChartListSubsetSource.ForCharts(library?.ChartInfoParseFailedChartFiles, "chart_info_parse_error");
                return true;
            case MainViewUpdateMode.NewlyInstalledFolderSelected:
                source = ResolvePackageSource(library?.ChartPackagesInstalled, parameter, "newly_installed_all", "newly_installed_package", applyResourceHealthProjection: true);
                return true;
            case MainViewUpdateMode.PendingInstallFolderSelected:
                source = ResolvePackageSource(library?.ChartPackagesPending, parameter, "pending_install_all", "pending_install_package", applyResourceHealthProjection: false);
                return true;
            default:
                source = null;
                return false;
        }
    }

    private static RegularChartListSubsetSource ResolvePackageSource(
        IEnumerable<ChartPackage> packages,
        object parameter,
        string allName,
        string packageName,
        bool applyResourceHealthProjection)
    {
        if (packages == null)
        {
            return RegularChartListSubsetSource.ForEntries([], allName, applyResourceHealthProjection);
        }
        IEnumerable<PackageChartEntry> entries = parameter is ChartPackage package
            ? (package.ChartEntries ?? []).Where(entry => entry?.Chart != null).ToArray()
            : CreatePackageEntrySnapshot(packages);
        return RegularChartListSubsetSource.ForEntries(
            entries,
            parameter is ChartPackage ? packageName : allName,
            applyResourceHealthProjection);
    }

    internal static IReadOnlyList<PackageChartEntry> CreatePackageEntrySnapshot(IEnumerable<ChartPackage> packages)
    {
        List<PackageChartEntry> snapshot = null;
        RetryHelper.RetryIfError(delegate
        {
            snapshot = [];
            foreach (ChartPackage package in packages ?? [])
            {
                try
                {
                    if (package != null)
                    {
                        snapshot.AddRange((package.ChartEntries ?? []).Where(entry => entry?.Chart != null));
                    }
                }
                catch
                {
                }
            }
        }, delegate (Exception ex)
        {
            ExceptionDispatchInfo.Capture(ex).Throw();
        }, delegate
        {
            Thread.Sleep(100);
        }, 100u);
        return snapshot ?? [];
    }

    internal static bool TryResolveDuplicateSource(
        IEnumerable<DuplicateGroup> duplicateGroups,
        object parameter,
        out RegularChartListSubsetSource source)
    {
        if (duplicateGroups == null)
        {
            source = RegularChartListSubsetSource.ForCharts([], "duplicate_empty");
            return true;
        }
        List<DuplicateGroup> groups = [.. (duplicateGroups ?? []).Where(group => group != null)];
        if (parameter == null)
        {
            source = RegularChartListSubsetSource.ForCharts(
                [.. groups.SelectMany(group => group.ChartFiles)],
                "duplicate_all");
            return true;
        }
        if (parameter is DuplicateViewContext context)
        {
            if (context.Kind == DuplicateViewContextKind.GroupHeader)
            {
                DuplicateGroup group = groups.FirstOrDefault(item => string.Equals(item.Header, context.Value, StringComparison.Ordinal));
                IEnumerable<ChartFile> charts = group != null
                    ? group.ChartFiles
                    : [.. groups.SelectMany(item => item.ChartFiles)];
                source = RegularChartListSubsetSource.ForCharts(charts, "duplicate_group");
                return true;
            }
            string prefix = context.Value + Path.DirectorySeparatorChar;
            source = RegularChartListSubsetSource.ForCharts(
                [.. groups.SelectMany(group => group.ChartFiles)
                    .Where(chart => !string.IsNullOrWhiteSpace(chart.Path) && chart.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))],
                "duplicate_folder");
            return true;
        }
        source = null;
        return false;
    }

    private void LogDefaultVirtualEntry(
        RegularChartListEntryRequest request,
        ChartListSortSpecification sort,
        RegularVirtualNormalLibraryApplyResult result,
        bool sortWasReset)
    {
        if (sortWasReset)
        {
            logWarning("main_view_virtual_sort_reset scope=normal_library"
                + " mode=" + request.Mode
                + " requestedMode=" + request.RequestedMode
                + " treeMode=" + request.CurrentTreeMode
                + " requestedSortColumn=" + (sort.RequestedColumnName ?? "(default_title)")
                + " appliedSortColumn=" + nameof(LibraryChartRow.Title)
                + " appliedSortDirection=" + ListSortDirection.Ascending);
        }
        if (result.WasCommitted)
        {
            LogMainSortDetail(ChartListRefreshCoordinator.CreateVirtualSortMetrics(
                result.Order,
                result.SortMs,
                result.SortCacheHit,
                GetSortCacheGenerationForLog(result.SortCacheKey),
                result.OrderCacheLookupMs,
                result.OrderBuildMs));
        }
        MainChartListRowsApplyResult rowsApply = result.Terminal.RowsApply;
        log("main_view_build route=default_virtual"
            + " mode=" + request.Mode
            + " requestedMode=" + request.RequestedMode
            + " committed=" + result.WasCommitted
            + " sortReset=" + sortWasReset
            + " folderMs=" + result.FolderMs
            + " keywordMs=" + result.KeywordMs
            + " modeMs=" + result.ModeMs
            + " sortMs=" + result.SortMs
            + " sortReuse=" + result.SortCacheHit
            + " sortProfile=" + (result.Order?.SortProfile ?? string.Empty)
            + " sortEngine=virtual virtual=True"
            + " sourceRowsMs=" + result.SourceRowsMs
            + " columnMs=" + rowsApply.ColumnStageMs
            + " prepareSwapMs=" + rowsApply.PrepareSwapMs
            + " columnSettingMs=" + rowsApply.ColumnSettingMs
            + " setViewMs=" + rowsApply.SetViewMs
            + " columnSettingReuse=" + rowsApply.ColumnSettingReuse
            + " folderCount=" + result.FolderCount
            + " keywordCount=" + result.KeywordCount
            + " modeCount=" + result.ModeCount
            + " viewCount=" + (result.RowsView?.Count ?? 0)
            + " sourceRows=" + result.SourceRowCount
            + " orderedRows=" + (result.Order?.Count ?? 0)
            + " viewRowsCreated=" + (result.RowsView?.RealizedRowCount ?? 0)
            + " distinctFolderCount=" + result.DistinctFolderCount
            + " summaryFolderCountReuse=" + result.SummaryCacheHit
            + " sourceRowsReuse=" + result.SourceRowsCacheHit
            + " orderCacheLookupMs=" + result.OrderCacheLookupMs
            + " orderBuildMs=" + result.OrderBuildMs
            + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
    }

    private void LogSubsetVirtualEntry(
        RegularChartListEntryRequest request,
        ChartListSortSpecification sort,
        string subsetName,
        RegularVirtualChartSubsetApplyResult result,
        bool sortWasReset)
    {
        if (sortWasReset)
        {
            logWarning("main_view_virtual_subset_sort_reset"
                + " mode=" + request.Mode
                + " requestedMode=" + request.RequestedMode
                + " treeMode=" + request.CurrentTreeMode
                + " requestedSortColumn=" + (sort.RequestedColumnName ?? "(default_title)")
                + " appliedSortColumn=" + nameof(LibraryChartRow.Title)
                + " appliedSortDirection=" + ListSortDirection.Ascending);
        }
        if (result.WasCommitted)
        {
            LogMainSortDetail(ChartListRefreshCoordinator.CreateVirtualSortMetrics(
                result.Order,
                result.SortMs,
                result.SortCacheHit,
                GetSortCacheGenerationForLog(result.SortCacheKey),
                result.OrderCacheLookupMs,
                result.OrderBuildMs));
        }
        if (result.WasCommitted && ShouldApplyResourceHealthProjection(request.CurrentTreeMode))
        {
            LogResourceHealthProjection(request.Library, request.CurrentTreeMode, result.RowsView?.Count ?? 0);
        }
        MainChartListRowsApplyResult rowsApply = result.Terminal.RowsApply;
        log("main_view_build route=subset_virtual"
            + " mode=" + request.Mode
            + " requestedMode=" + request.RequestedMode
            + " treeMode=" + request.CurrentTreeMode
            + " subset=" + subsetName
            + " committed=" + result.WasCommitted
            + " sortReset=" + sortWasReset
            + " keywordMs=" + result.KeywordMs
            + " modeMs=" + result.ModeMs
            + " sortMs=" + result.SortMs
            + " sortReuse=" + result.SortCacheHit
            + " sortProfile=" + (result.Order?.SortProfile ?? string.Empty)
            + " sortEngine=virtual virtual=True"
            + " sourceRowsMs=" + result.SourceRowsMs
            + " columnMs=" + rowsApply.ColumnStageMs
            + " prepareSwapMs=" + rowsApply.PrepareSwapMs
            + " columnSettingMs=" + rowsApply.ColumnSettingMs
            + " setViewMs=" + rowsApply.SetViewMs
            + " columnSettingReuse=" + rowsApply.ColumnSettingReuse
            + " sourceCount=" + result.SourceRowCount
            + " keywordCount=" + result.KeywordCount
            + " modeCount=" + result.ModeCount
            + " viewCount=" + (result.RowsView?.Count ?? 0)
            + " orderedRows=" + (result.Order?.Count ?? 0)
            + " viewRowsCreated=" + (result.RowsView?.RealizedRowCount ?? 0)
            + " distinctFolderCount=" + result.DistinctFolderCount
            + " sourceRowsSignature=" + result.SourceRowsSignature
            + " orderCacheLookupMs=" + result.OrderCacheLookupMs
            + " orderBuildMs=" + result.OrderBuildMs
            + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
    }

    private void LogMaterializedEntry(
        RegularChartListEntryRequest request,
        MainViewUpdateMode effectiveMode,
        RegularChartListRefreshRequest refreshRequest,
        RegularMaterializedChartListApplyResult result)
    {
        MainChartListRowsApplyResult rowsApply = result.RowsApply;
        log("main_view_build route=materialized"
            + " mode=" + effectiveMode
            + " requestedMode=" + request.RequestedMode
            + " committed=" + result.WasCommitted
            + " folderMs=" + result.FolderMs
            + " keywordMs=" + result.KeywordMs
            + " modeMs=" + result.ModeMs
            + " sortMs=" + result.SortMs
            + " sortReuse=" + result.SortReuse
            + " sortProfile=" + result.SortProfile
            + " sortEngine=fast virtual=False"
            + " columnMs=" + rowsApply.ColumnStageMs
            + " prepareSwapMs=" + rowsApply.PrepareSwapMs
            + " columnSettingMs=" + rowsApply.ColumnSettingMs
            + " setViewMs=" + rowsApply.SetViewMs
            + " columnSettingReuse=" + rowsApply.ColumnSettingReuse
            + " folderCount=" + result.FolderCount
            + " keywordCount=" + result.KeywordCount
            + " modeCount=" + result.ModeCount
            + " viewCount=" + result.ViewCount
            + " sortColumn=" + (refreshRequest.HasSortParameters ? refreshRequest.RequestedSortColumnName : "(default_title)")
            + " sortDirection=" + refreshRequest.SortDirection
            + " totalMs=" + request.Stopwatch.ElapsedMilliseconds);
    }

    private void LogMainSortDetail(LibraryChartSortMetrics metrics)
    {
        log("main_sort_detail rowCount=" + metrics.RowCount
            + " columnName=" + (metrics.ColumnName ?? string.Empty)
            + " direction=" + metrics.Direction
            + " propertyType=" + (metrics.PropertyTypeName ?? "(null)")
            + " sortProfile=" + (metrics.SortProfile ?? string.Empty)
            + " stringSortKind=" + (metrics.StringSortKind ?? string.Empty)
            + " sortReuse=" + metrics.SortReuse
            + " sortCacheKey=" + (metrics.SortCacheKey ?? string.Empty)
            + " sortCacheGeneration=" + metrics.SortCacheGeneration
            + " sortCacheHit=" + metrics.SortCacheHit
            + " orderCacheLookupMs=" + metrics.OrderCacheLookupMs
            + " orderBuildMs=" + metrics.OrderBuildMs
            + " sortMs=" + metrics.SortMs);
    }

    private void LogResourceHealthProjection(BMSLibrary library, MainViewUpdateMode mode, int rowCount)
    {
        ResourceHealthIndexSnapshot snapshot = library?.TryGetCurrentResourceHealthIndexSnapshotForView();
        int overlayCount = snapshot == null
            ? 0
            : mode == MainViewUpdateMode.FileMissingFilterSelected
                ? snapshot.ActiveTargets.Count
                : mode == MainViewUpdateMode.FileMissingIgnoredFilterSelected
                    ? snapshot.IgnoredTargets.Count
                    : snapshot.NeedFixCount;
        log("resource_health_projection reason=" + mode
            + " rowCount=" + rowCount
            + " overlayCount=" + overlayCount
            + " ignored=" + (snapshot?.IgnoredCount ?? 0)
            + " version=" + (snapshot?.Version ?? 0));
    }

    private static long GetSortCacheGenerationForLog(NormalLibrarySortCacheKey key)
    {
        if (key.ScoreGeneration != 0) return key.ScoreGeneration;
        if (key.ChartInfoGeneration != 0) return key.ChartInfoGeneration;
        if (key.WarningGeneration != 0) return key.WarningGeneration;
        if (key.InstallDestinationGeneration != 0) return key.InstallDestinationGeneration;
        if (key.ReferenceTablesGeneration != 0) return key.ReferenceTablesGeneration;
        if (key.MaintenanceGeneration != 0) return key.MaintenanceGeneration;
        return key.SortKeyGeneration;
    }

    private static long GetSortCacheGenerationForLog(VirtualChartSubsetSortCacheKey key)
    {
        if (key.ScoreGeneration != 0) return key.ScoreGeneration;
        if (key.ChartInfoGeneration != 0) return key.ChartInfoGeneration;
        if (key.WarningGeneration != 0) return key.WarningGeneration;
        if (key.InstallDestinationGeneration != 0) return key.InstallDestinationGeneration;
        if (key.ReferenceTablesGeneration != 0) return key.ReferenceTablesGeneration;
        if (key.MaintenanceGeneration != 0) return key.MaintenanceGeneration;
        return key.SortKeyGeneration;
    }

    internal RegularChartListTerminalResult TryCommit(
        RegularChartListRequestLease lease,
        RegularChartListPresentationResult presentation)
    {
        if (lease == null || presentation == null || presentation.Kind != RegularChartListPresentationKind.Materialized)
        {
            throw new ArgumentException("A complete regular chart-list terminal request is required.");
        }

        return TryCommitCore(lease, presentation);
    }

    internal RegularChartListTerminalResult TryCommitVirtual(
        RegularChartListRequestLease lease,
        RegularChartListPresentationResult presentation)
    {
        if (lease == null || presentation == null || presentation.Kind != RegularChartListPresentationKind.Virtual)
        {
            throw new ArgumentException("A complete virtual regular chart-list terminal request is required.");
        }

        return TryCommitCore(lease, presentation);
    }

    internal RegularVirtualNormalLibraryApplyResult TryApplyVirtualNormalLibrary(
        RegularVirtualNormalLibraryApplyRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        ResetDerivedCaches();
        if (!TryBeginVirtualRequest(request.Library, out RegularChartListRequestLease lease))
        {
            return RegularVirtualNormalLibraryApplyResult.NotCommitted();
        }

        Stopwatch stopwatch = request.Stopwatch ?? Stopwatch.StartNew();
        mainChartList.RowProjection.CaptureVersions(request.Library);

        long stageStartMs = stopwatch.ElapsedMilliseconds;
        List<ChartListSourceRow> sourceRows = GetOrCreateVirtualNormalLibrarySourceRows(
            request.Library,
            request.IncludeBmsonRows,
            out bool sourceRowsCacheHit,
            out long sourceRowsSourceGeneration,
            out long sourceRowsSortKeyGeneration);
        if (lease.Token.IsCancellationRequested)
        {
            return RegularVirtualNormalLibraryApplyResult.NotCommitted();
        }
        long sourceRowsMs = stopwatch.ElapsedMilliseconds - stageStartMs;

        string filterIdentity = CreateVirtualNormalLibraryFilterIdentity(
            request.TreeFilter?.Identity,
            request.KeywordFilter,
            request.ModeFilter,
            mainChartList.RowProjection.ScoreSnapshotVersion,
            mainChartList.RowProjection.ChartInfoVersion);
        stageStartMs = stopwatch.ElapsedMilliseconds;
        ChartListOrder fullOrder = GetOrCreateVirtualNormalLibraryOrder(
            sourceRows,
            request.Library,
            request.SortColumnName,
            request.SortDirection,
            sourceRowsSourceGeneration,
            sourceRowsSortKeyGeneration,
            request.ExternalVersions,
            useCache: true,
            out bool sortCacheHit,
            out NormalLibrarySortCacheKey sortCacheKey,
            out long orderCacheLookupMs,
            out long orderBuildMs);
        if (lease.Token.IsCancellationRequested)
        {
            return RegularVirtualNormalLibraryApplyResult.NotCommitted();
        }
        long sortStageMs = stopwatch.ElapsedMilliseconds - stageStartMs;

        int[] viewOrderedIndexes = ApplyVirtualNormalLibraryFilters(
            sourceRows,
            fullOrder.Indexes,
            request.TreeFilter,
            GridKeywordSearchQuery.Parse(request.KeywordFilter),
            request.ModeFilter,
            out int folderFilteredCount,
            out int keywordFilteredCount,
            out int modeFilteredCount,
            out long folderStageMs,
            out long keywordStageMs,
            out long modeStageMs);
        if (lease.Token.IsCancellationRequested)
        {
            return RegularVirtualNormalLibraryApplyResult.NotCommitted();
        }

        ChartListOrder order = fullOrder.WithIndexes(viewOrderedIndexes);
        var summaryKey = new MainViewSummaryCacheKey(
            sourceRowsSourceGeneration,
            sourceRowsSortKeyGeneration,
            modeFilteredCount,
            request.IncludeBmsonRows,
            filterIdentity);
        bool summaryCacheHit = TryGetVirtualSummary(summaryKey, out int distinctFolderCount);
        if (!summaryCacheHit)
        {
            distinctFolderCount = -1;
        }

        stageStartMs = stopwatch.ElapsedMilliseconds;
        var rowsView = new ChartListVirtualView(
            sourceRows,
            order,
            row => CreateVirtualRow(request.Library, row),
            distinctFolderCount);
        RegularChartListTerminalResult terminal = TryCommitVirtual(
            lease,
            RegularChartListPresentationResult.ForVirtual(
                new MainChartListRowsApplyRequest
                {
                    Rows = rowsView,
                    ColumnsSettings = request.ColumnSelection.ColumnsSettings,
                    SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                    Summary = request.PreserveSummary
                        ? MainChartListSummaryUpdate.Preserve()
                        : MainChartListSummaryUpdate.NormalCounts(rowsView.Count, distinctFolderCount),
                    ColumnSettingReuse = request.ColumnSelection.Reused,
                    ColumnPreparationMs = request.ColumnSelection.ElapsedMs,
                    TerminalStageStartMs = stageStartMs,
                    Stopwatch = stopwatch
                },
                request.ColumnSelection,
                request.Mode,
                stopwatch));
        if (!terminal.WasCommitted)
        {
            return RegularVirtualNormalLibraryApplyResult.NotCommitted();
        }
        if (!summaryCacheHit)
        {
            ScheduleVirtualSummary(
                lease,
                summaryKey,
                SelectSourceRowsByOrder(sourceRows, viewOrderedIndexes),
                rowsView,
                request.Reason);
        }

        return new RegularVirtualNormalLibraryApplyResult(
            terminal,
            rowsView,
            order,
            sortCacheKey,
            sourceRows.Count,
            folderFilteredCount,
            keywordFilteredCount,
            modeFilteredCount,
            distinctFolderCount,
            sourceRowsCacheHit,
            sortCacheHit,
            summaryCacheHit,
            sourceRowsMs,
            folderStageMs,
            keywordStageMs,
            modeStageMs,
            sortStageMs,
            orderCacheLookupMs,
            orderBuildMs);
    }

    internal RegularVirtualChartSubsetApplyResult TryApplyVirtualChartSubset(
        RegularVirtualChartSubsetApplyRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if ((request.SourceCharts == null) == (request.SourceEntries == null))
        {
            throw new ArgumentException("Exactly one virtual subset source must be provided.", nameof(request));
        }

        ResetDerivedCaches();
        if (!TryBeginVirtualRequest(request.Library, out RegularChartListRequestLease lease))
        {
            return default;
        }

        Stopwatch stopwatch = request.Stopwatch ?? Stopwatch.StartNew();
        mainChartList.RowProjection.CaptureVersions(request.Library);
        NormalLibrarySortCacheGenerationSnapshot generation = CaptureSortGeneration(
            request.SortColumnName,
            request.ExternalVersions);

        long stageStartMs = stopwatch.ElapsedMilliseconds;
        List<ChartListSourceRow> sourceRows = request.SourceEntries != null
            ? mainChartList.RowProjection.BuildPackageSourceRows(
                request.Library,
                request.SourceEntries,
                request.ApplyResourceHealthProjection)
            : mainChartList.RowProjection.BuildStandardSourceRows(
                request.Library,
                request.SourceCharts,
                request.SourceProjectionMode,
                request.ApplyResourceHealthProjection);
        if (lease.Token.IsCancellationRequested)
        {
            return default;
        }
        long sourceRowsMs = stopwatch.ElapsedMilliseconds - stageStartMs;
        long sourceRowsSignature = ComputeVirtualChartSubsetSourceRowsSignature(sourceRows);

        stageStartMs = stopwatch.ElapsedMilliseconds;
        ChartListOrder fullOrder = GetOrCreateVirtualChartSubsetOrder(
            sourceRows,
            request.Library,
            request.SortColumnName,
            request.SortDirection,
            (int)request.TreeMode,
            request.SubsetName,
            sourceRowsSignature,
            generation.Source,
            generation.SortKey,
            request.ExternalVersions,
            out bool sortCacheHit,
            out VirtualChartSubsetSortCacheKey sortCacheKey,
            out long orderCacheLookupMs,
            out long orderBuildMs);
        if (lease.Token.IsCancellationRequested)
        {
            return default;
        }
        long sortStageMs = stopwatch.ElapsedMilliseconds - stageStartMs;

        int[] viewOrderedIndexes = ApplyVirtualChartSubsetFilters(
            sourceRows,
            fullOrder.Indexes,
            GridKeywordSearchQuery.Parse(request.KeywordFilter),
            request.ModeFilter,
            out int keywordCount,
            out int modeCount,
            out long keywordStageMs,
            out long modeStageMs);
        if (lease.Token.IsCancellationRequested)
        {
            return default;
        }

        ChartListOrder order = fullOrder.WithIndexes(viewOrderedIndexes);
        IReadOnlyList<ChartListSourceRow> orderedRows = SelectSourceRowsByOrder(sourceRows, viewOrderedIndexes);
        int distinctFolderCount = CountDistinctFolders(orderedRows);
        if (lease.Token.IsCancellationRequested)
        {
            return default;
        }
        stageStartMs = stopwatch.ElapsedMilliseconds;
        var rowsView = new ChartListVirtualView(
            sourceRows,
            order,
            row => mainChartList.RowProjection.CreateSubsetRow(
                request.Library,
                row,
                request.ApplyResourceHealthProjection),
            distinctFolderCount);
        RegularChartListTerminalResult terminal = TryCommitVirtual(
            lease,
            RegularChartListPresentationResult.ForVirtual(
                new MainChartListRowsApplyRequest
                {
                    Rows = rowsView,
                    ColumnsSettings = request.ColumnSelection.ColumnsSettings,
                    SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                    Summary = request.PreserveSummary
                        ? MainChartListSummaryUpdate.Preserve()
                        : MainChartListSummaryUpdate.NormalCounts(rowsView.Count, distinctFolderCount),
                    ColumnSettingReuse = request.ColumnSelection.Reused,
                    ColumnPreparationMs = request.ColumnSelection.ElapsedMs,
                    TerminalStageStartMs = stageStartMs,
                    Stopwatch = stopwatch
                },
                request.ColumnSelection,
                request.Mode,
                stopwatch));
        if (!terminal.WasCommitted)
        {
            return default;
        }

        return new RegularVirtualChartSubsetApplyResult(
            terminal,
            rowsView,
            order,
            sortCacheKey,
            sourceRows.Count,
            keywordCount,
            modeCount,
            distinctFolderCount,
            sourceRowsSignature,
            sourceRowsMs,
            keywordStageMs,
            modeStageMs,
            sortStageMs,
            sortCacheHit,
            orderCacheLookupMs,
            orderBuildMs);
    }

    private ChartListOrder GetOrCreateVirtualChartSubsetOrder(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        BMSLibrary library,
        string columnName,
        ListSortDirection direction,
        int treeMode,
        string subsetName,
        long sourceRowsSignature,
        long expectedSourceGeneration,
        long expectedSortKeyGeneration,
        RegularChartListExternalVersions externalVersions,
        out bool cacheHit,
        out VirtualChartSubsetSortCacheKey cacheKey,
        out long orderCacheLookupMs,
        out long orderBuildMs)
    {
        var lookupStopwatch = Stopwatch.StartNew();
        int rowCount = sourceRows?.Count ?? 0;
        if (!ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out string normalizedColumnName))
        {
            throw new ArgumentException("Unsupported virtual chart subset sort column.", nameof(columnName));
        }
        cacheKey = CreateVirtualSubsetOrderKey(
            expectedSourceGeneration,
            expectedSortKeyGeneration,
            treeMode,
            subsetName,
            sourceRowsSignature,
            normalizedColumnName,
            direction,
            rowCount,
            CaptureExternalVersions(library, externalVersions));
        if (TryGetVirtualSubsetOrder(cacheKey, out ChartListOrder cachedOrder))
        {
            lookupStopwatch.Stop();
            cacheHit = true;
            orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
            orderBuildMs = 0L;
            return cachedOrder;
        }

        lookupStopwatch.Stop();
        var buildStopwatch = Stopwatch.StartNew();
        if (!ChartListOrder.TryCreate(sourceRows, normalizedColumnName, direction, out ChartListOrder order))
        {
            throw new ArgumentException("Unsupported virtual chart subset sort column.", nameof(columnName));
        }
        buildStopwatch.Stop();
        TryPublishVirtualSubsetOrder(
            cacheKey,
            order,
            CaptureExternalVersions(library, externalVersions));
        cacheHit = false;
        orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
        orderBuildMs = buildStopwatch.ElapsedMilliseconds;
        return order;
    }

    internal static int[] ApplyVirtualChartSubsetFilters(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IReadOnlyList<int> orderedIndexes,
        GridKeywordSearchQuery keywordQuery,
        RegularChartModeFilter modeFilter,
        out int keywordFilteredCount,
        out int modeFilteredCount,
        out long keywordStageMs,
        out long modeStageMs)
    {
        int[] indexes = [.. (orderedIndexes ?? []).Where(index => sourceRows != null && index >= 0 && index < sourceRows.Count)];
        var stageStopwatch = Stopwatch.StartNew();
        if (keywordQuery != null && keywordQuery.HasTokens)
        {
            indexes = indexes.AsParallel().AsOrdered()
                .Where(index => keywordQuery.MatchesChartListSourceRow(sourceRows[index]))
                .ToArray();
        }
        keywordFilteredCount = indexes.Length;
        keywordStageMs = stageStopwatch.ElapsedMilliseconds;
        stageStopwatch.Restart();
        if (modeFilter != RegularChartModeFilter.All)
        {
            HashSet<int?> modeValues = RegularChartListFilterService.CreateModeFilterValueSet(modeFilter);
            indexes = [.. indexes.Where(index => modeValues.Contains(sourceRows[index]?.Mode))];
        }
        modeFilteredCount = indexes.Length;
        modeStageMs = stageStopwatch.ElapsedMilliseconds;
        return indexes;
    }

    internal static long ComputeVirtualChartSubsetSourceRowsSignature(
        IReadOnlyList<ChartListSourceRow> sourceRows)
    {
        unchecked
        {
            long hash = 17L;
            hash = (hash * 397L) ^ (sourceRows?.Count ?? 0);
            if (sourceRows == null)
            {
                return hash;
            }
            foreach (ChartListSourceRow row in sourceRows)
            {
                hash = (hash * 397L) ^ (row?.Kind == ChartFileKind.Bms ? 1 : 2);
                hash = (hash * 397L) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(row?.Path ?? string.Empty);
                hash = (hash * 397L) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(row?.Hash ?? string.Empty);
                hash = (hash * 397L) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(row?.Sha256 ?? string.Empty);
                hash = (hash * 397L) ^ (row?.PackageEntry?.ProjectionVersion ?? 0);
            }
            return hash;
        }
    }

    private List<ChartListSourceRow> GetOrCreateVirtualNormalLibrarySourceRows(
        BMSLibrary library,
        bool includeBmsonRows,
        out bool cacheHit,
        out long sourceGenerationAtLookup,
        out long sortKeyGenerationAtLookup)
    {
        RegularVirtualSourceRowsLookup lookup = LookupVirtualSourceRows(library, includeBmsonRows);
        if (lookup.CacheHit)
        {
            cacheHit = true;
            sourceGenerationAtLookup = lookup.SourceGeneration;
            sortKeyGenerationAtLookup = lookup.SortKeyGeneration;
            return lookup.Rows as List<ChartListSourceRow> ?? [.. lookup.Rows];
        }

        OwnedChartStorageOwnerView sourceOwnerView = library?.CreateNormalLibrarySourceStorageOwnerView();
        List<ChartListSourceRow> sourceRows = mainChartList.RowProjection.BuildNormalSourceRows(
            library,
            sourceOwnerView,
            includeBmsonRows);
        TryPublishVirtualSourceRows(lookup, sourceRows);
        cacheHit = false;
        sourceGenerationAtLookup = lookup.SourceGeneration;
        sortKeyGenerationAtLookup = lookup.SortKeyGeneration;
        return sourceRows;
    }

    private ChartListOrder GetOrCreateVirtualNormalLibraryOrder(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        BMSLibrary library,
        string columnName,
        ListSortDirection direction,
        long expectedSourceGeneration,
        long expectedSortKeyGeneration,
        RegularChartListExternalVersions externalVersions,
        bool useCache,
        out bool cacheHit,
        out NormalLibrarySortCacheKey cacheKey,
        out long orderCacheLookupMs,
        out long orderBuildMs)
    {
        var lookupStopwatch = Stopwatch.StartNew();
        int rowCount = sourceRows?.Count ?? 0;
        if (!ChartListOrder.TryNormalizeVirtualSortColumn(columnName, out string normalizedColumnName))
        {
            throw new ArgumentException("Unsupported virtual normal library sort column.", nameof(columnName));
        }

        if (useCache)
        {
            cacheKey = CreateVirtualOrderKey(
                expectedSourceGeneration,
                expectedSortKeyGeneration,
                normalizedColumnName,
                direction,
                rowCount,
                CaptureExternalVersions(library, externalVersions));
            if (TryGetVirtualOrder(cacheKey, out ChartListOrder cachedOrder))
            {
                lookupStopwatch.Stop();
                cacheHit = true;
                orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
                orderBuildMs = 0L;
                return cachedOrder;
            }
        }
        else
        {
            cacheKey = default;
        }

        lookupStopwatch.Stop();
        var buildStopwatch = Stopwatch.StartNew();
        if (!ChartListOrder.TryCreate(sourceRows, normalizedColumnName, direction, out ChartListOrder order))
        {
            throw new ArgumentException("Unsupported virtual normal library sort column.", nameof(columnName));
        }
        buildStopwatch.Stop();
        if (useCache)
        {
            TryPublishVirtualOrder(cacheKey, order, CaptureExternalVersions(library, externalVersions));
        }
        cacheHit = false;
        orderCacheLookupMs = lookupStopwatch.ElapsedMilliseconds;
        orderBuildMs = buildStopwatch.ElapsedMilliseconds;
        return order;
    }

    private static RegularChartListExternalVersions CaptureExternalVersions(
        BMSLibrary requestLibrary,
        RegularChartListExternalVersions fallback)
    {
        return requestLibrary == null
            ? fallback
            : new RegularChartListExternalVersions(
                requestLibrary.ScoreSnapshotVersion,
                requestLibrary.ChartInfoIndexVersion,
                requestLibrary.MaintenanceHydrationCompletedVersion);
    }

    internal static string CreateVirtualNormalLibraryFilterIdentity(
        string folderFilterIdentity,
        string keywordFilter,
        RegularChartModeFilter modeFilter,
        int scoreSnapshotVersion,
        int chartInfoIndexVersion)
    {
        if (string.IsNullOrEmpty(folderFilterIdentity)
            && string.IsNullOrWhiteSpace(keywordFilter)
            && modeFilter == RegularChartModeFilter.All)
        {
            return "normal_default";
        }

        string normalizedKeywordFilter = keywordFilter ?? string.Empty;
        bool hasKeywordFilter = !string.IsNullOrWhiteSpace(normalizedKeywordFilter);
        string folderIdentity = string.IsNullOrEmpty(folderFilterIdentity) ? "none" : folderFilterIdentity;
        string identity = "normal_filter:folder=" + folderIdentity
            + ";keyword=" + StringComparer.Ordinal.GetHashCode(normalizedKeywordFilter).ToString(CultureInfo.InvariantCulture);
        if (hasKeywordFilter)
        {
            identity += ";score=" + scoreSnapshotVersion.ToString(CultureInfo.InvariantCulture)
                + ";chart=" + chartInfoIndexVersion.ToString(CultureInfo.InvariantCulture);
        }
        return identity + ";mode=" + ((int)modeFilter).ToString(CultureInfo.InvariantCulture);
    }

    internal static int[] ApplyVirtualNormalLibraryFilters(
        IReadOnlyList<ChartListSourceRow> sourceRows,
        IReadOnlyList<int> orderedIndexes,
        RegularNormalLibraryTreeFilter treeFilter,
        GridKeywordSearchQuery keywordQuery,
        RegularChartModeFilter modeFilter,
        out int folderFilteredCount,
        out int keywordFilteredCount,
        out int modeFilteredCount,
        out long folderStageMs,
        out long keywordStageMs,
        out long modeStageMs)
    {
        int[] indexes = [.. (orderedIndexes ?? []).Where(index => sourceRows != null && index >= 0 && index < sourceRows.Count)];
        var stageStopwatch = Stopwatch.StartNew();
        if (treeFilter != null)
        {
            indexes = [.. indexes.Where(index => treeFilter.Matches(sourceRows[index]))];
        }
        folderFilteredCount = indexes.Length;
        folderStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        if (keywordQuery != null && keywordQuery.HasTokens)
        {
            indexes = indexes.AsParallel().AsOrdered()
                .Where(index => keywordQuery.MatchesChartListSourceRow(sourceRows[index]))
                .ToArray();
        }
        keywordFilteredCount = indexes.Length;
        keywordStageMs = stageStopwatch.ElapsedMilliseconds;

        stageStopwatch.Restart();
        if (modeFilter != RegularChartModeFilter.All)
        {
            HashSet<int?> modeValues = RegularChartListFilterService.CreateModeFilterValueSet(modeFilter);
            indexes = [.. indexes.Where(index => modeValues.Contains(sourceRows[index]?.Mode))];
        }
        modeFilteredCount = indexes.Length;
        modeStageMs = stageStopwatch.ElapsedMilliseconds;
        return indexes;
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
        RegularChartListPresentationResult presentation)
    {
        lock (syncRoot)
        {
            if (!IsCurrentUnsafe(lease))
            {
                return RegularChartListTerminalResult.Stale();
            }
        }

        PlaylistColumnPresentationCommit columnCommit = null;
        PlaylistBindingModeCommit bindingModeCommit = null;
        bool CommitRegularPresentation(Action commitRows)
        {
            lock (syncRoot)
            {
                if (!IsCurrentUnsafe(lease))
                {
                    return false;
                }
                commitRows();
                columnCommit = playlistWorkspace.CommitColumnPresentationWithoutNotification(
                    presentation.ColumnSelection.PlaylistColumnSettingsVisibility,
                    presentation.ColumnSelection.PlaylistSummaryColumnsSettings);
                bindingModeCommit = playlistWorkspace.CommitBindingModeWithoutNotification(playlistDetailActive: false);
                if (presentation.Build != null)
                {
                    folderRows = presentation.Build.Stage.FolderRows;
                    keywordRows = presentation.Build.Stage.KeywordRows;
                    modeRows = presentation.Build.Stage.ModeRows;
                    folderSortSourceSnapshot = presentation.Build.Sort.FolderSortSourceSnapshot;
                    folderSortResultSnapshot = presentation.Build.Sort.FolderSortResultSnapshot;
                    folderSortColumnName = presentation.Build.Sort.FolderSortColumnName;
                    folderSortDirection = presentation.Build.Sort.FolderSortDirection;
                    if (presentation.Build.PendingCacheKey.HasValue && presentation.Build.PendingCacheRows != null)
                    {
                        sortCache[presentation.Build.PendingCacheKey.Value] = presentation.Build.PendingCacheRows;
                    }
                }
                mainChartList.CommitAppliedColumnMode(presentation.ColumnSelection.AppliedMode);
                mainChartList.CommitCompletion(new MainChartListCompletion(
                    lease.RequestId,
                    Stopwatch.GetTimestamp(),
                    Thread.CurrentThread.ManagedThreadId,
                    presentation.Mode,
                    presentation.Stopwatch.ElapsedMilliseconds));
                return true;
            }
        }

        void PublishRelatedPresentation()
        {
            var publishExceptions = new List<Exception>();
            try
            {
                playlistWorkspace.PublishColumnPresentation(columnCommit);
            }
            catch (Exception ex)
            {
                publishExceptions.Add(ex);
            }
            try
            {
                playlistWorkspace.PublishBindingMode(bindingModeCommit);
            }
            catch (Exception ex)
            {
                publishExceptions.Add(ex);
            }
            if (publishExceptions.Count > 0)
            {
                throw new AggregateException(publishExceptions);
            }
        }

        MainChartListPresentationApplyResult applied;
        try
        {
            applied = mainChartList.ApplyPresentation(
                presentation.CreateRowsRequest(),
                CommitRegularPresentation,
                PublishRelatedPresentation);
        }
        catch (MainChartListPresentationPublishException ex)
        {
            throw new RegularChartListTerminalPublishException(ex.InnerException ?? ex);
        }
        return applied.WasApplied
            ? RegularChartListTerminalResult.Committed(applied.RowsApply)
            : RegularChartListTerminalResult.Stale();
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
        mainChartList.AppliedColumnModeCommitted -= MainChartListAppliedColumnModeCommitted;
        CancelAndDispose(requestCancellation);
        Cancel(prewarmCancellation);
        return DrainPrewarmAsync(prewarmCompletion, prewarmCancellation);
    }

    public void Dispose()
    {
        mainChartList.AppliedColumnModeCommitted -= MainChartListAppliedColumnModeCommitted;
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
            && request.ModeFilter == RegularChartModeFilter.All
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
            request.Sort,
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

internal sealed class RegularChartListEntryRequest
{
    internal BMSLibrary Library { get; set; }

    internal MainViewUpdateMode Mode { get; set; }

    internal MainViewUpdateMode RequestedMode { get; set; }

    internal object Parameter { get; set; }

    internal MainViewUpdateMode CurrentTreeMode { get; set; }

    internal object TreeParameter { get; set; }

    internal bool IncludeBmsonRows { get; set; }

    internal bool PreserveSummary { get; set; }

    internal Stopwatch Stopwatch { get; set; }
}

internal sealed class RegularChartListSubsetSource
{
    private RegularChartListSubsetSource()
    {
    }

    internal IEnumerable<ChartFile> SourceCharts { get; private set; }

    internal IEnumerable<PackageChartEntry> SourceEntries { get; private set; }

    internal ChartListSourceProjectionMode SourceProjectionMode { get; private set; }

    internal bool ApplyResourceHealthProjection { get; private set; }

    internal string Name { get; private set; }

    internal static RegularChartListSubsetSource ForCharts(
        IEnumerable<ChartFile> charts,
        string name,
        ChartListSourceProjectionMode projectionMode = ChartListSourceProjectionMode.PreserveSourceProjection,
        bool applyResourceHealthProjection = false)
    {
        return new RegularChartListSubsetSource
        {
            SourceCharts = charts ?? [],
            SourceProjectionMode = projectionMode,
            ApplyResourceHealthProjection = applyResourceHealthProjection,
            Name = name ?? string.Empty
        };
    }

    internal static RegularChartListSubsetSource ForEntries(
        IEnumerable<PackageChartEntry> entries,
        string name,
        bool applyResourceHealthProjection)
    {
        return new RegularChartListSubsetSource
        {
            SourceEntries = entries ?? [],
            SourceProjectionMode = ChartListSourceProjectionMode.PreserveSourceProjection,
            ApplyResourceHealthProjection = applyResourceHealthProjection,
            Name = name ?? string.Empty
        };
    }
}

internal enum RegularChartListEntryRoute
{
    DefaultVirtual,
    SubsetVirtual,
    Materialized
}

internal readonly struct RegularChartListEntryResult
{
    internal RegularChartListEntryResult(bool wasCommitted, RegularChartListEntryRoute route, bool sortWasReset)
    {
        WasCommitted = wasCommitted;
        Route = route;
        SortWasReset = sortWasReset;
    }

    internal bool WasCommitted { get; }

    internal RegularChartListEntryRoute Route { get; }

    internal bool SortWasReset { get; }
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

internal enum RegularChartListPresentationKind
{
    Materialized,
    Virtual
}

internal sealed class RegularChartListPresentationResult
{
    private RegularChartListPresentationResult(
        RegularChartListBuildResult build,
        MainChartListRowsApplyRequest rowsRequest,
        MainChartListColumnSelection columnSelection,
        MainViewUpdateMode mode,
        Stopwatch stopwatch,
        RegularChartListPresentationKind kind)
    {
        Build = build;
        if (rowsRequest == null)
        {
            throw new ArgumentNullException(nameof(rowsRequest));
        }
        rows = rowsRequest.Rows;
        columnsSettings = rowsRequest.ColumnsSettings;
        selectionPolicy = rowsRequest.SelectionPolicy;
        summary = rowsRequest.Summary;
        columnSettingReuse = rowsRequest.ColumnSettingReuse;
        columnPreparationMs = rowsRequest.ColumnPreparationMs;
        terminalStageStartMs = rowsRequest.TerminalStageStartMs;
        rowsStopwatch = rowsRequest.Stopwatch;
        rowsAlreadyPrepared = rowsRequest.RowsAlreadyPrepared;
        ColumnSelection = columnSelection;
        Stopwatch = stopwatch ?? throw new ArgumentNullException(nameof(stopwatch));
        Mode = mode;
        Kind = kind;
    }

    internal RegularChartListBuildResult Build { get; }

    internal MainChartListColumnSelection ColumnSelection { get; }

    internal MainViewUpdateMode Mode { get; }

    internal Stopwatch Stopwatch { get; }

    internal RegularChartListPresentationKind Kind { get; }

    private readonly IList rows;

    private readonly CustomTableColumnSettings columnsSettings;

    private readonly MainChartListSelectionPolicy selectionPolicy;

    private readonly MainChartListSummaryUpdate summary;

    private readonly bool columnSettingReuse;

    private readonly long columnPreparationMs;

    private readonly long terminalStageStartMs;

    private readonly Stopwatch rowsStopwatch;

    private readonly bool rowsAlreadyPrepared;

    internal MainChartListRowsApplyRequest CreateRowsRequest()
    {
        return new MainChartListRowsApplyRequest
        {
            Rows = rows,
            ColumnsSettings = columnsSettings,
            SelectionPolicy = selectionPolicy,
            Summary = summary,
            ColumnSettingReuse = columnSettingReuse,
            ColumnPreparationMs = columnPreparationMs,
            TerminalStageStartMs = terminalStageStartMs,
            Stopwatch = rowsStopwatch,
            RowsAlreadyPrepared = rowsAlreadyPrepared
        };
    }

    internal static RegularChartListPresentationResult ForMaterialized(
        RegularChartListBuildResult build,
        MainChartListRowsApplyRequest rowsRequest,
        MainChartListColumnSelection columnSelection,
        MainViewUpdateMode mode,
        Stopwatch stopwatch)
    {
        if (build == null)
        {
            throw new ArgumentNullException(nameof(build));
        }
        if (rowsRequest == null)
        {
            throw new ArgumentNullException(nameof(rowsRequest));
        }
        if (!ReferenceEquals(rowsRequest.Rows, build.Sort.RowsView))
        {
            throw new ArgumentException("Materialized presentation rows must match the build result.", nameof(rowsRequest));
        }
        return new RegularChartListPresentationResult(
            build,
            rowsRequest,
            columnSelection,
            mode,
            stopwatch,
            RegularChartListPresentationKind.Materialized);
    }

    internal static RegularChartListPresentationResult ForVirtual(
        MainChartListRowsApplyRequest rowsRequest,
        MainChartListColumnSelection columnSelection,
        MainViewUpdateMode mode,
        Stopwatch stopwatch)
    {
        return new RegularChartListPresentationResult(
            build: null,
            rowsRequest,
            columnSelection,
            mode,
            stopwatch,
            RegularChartListPresentationKind.Virtual);
    }
}

internal sealed class RegularMaterializedChartListApplyRequest
{
    internal RegularChartListRefreshRequest RefreshRequest { get; set; }
    internal bool HasFolderRowsOverride { get; set; }
    internal IEnumerable<LibraryChartRow> FolderRowsOverride { get; set; }
    internal RegularChartListExternalVersions ExternalVersions { get; set; }
    internal MainChartListColumnSelection ColumnSelection { get; set; }
    internal bool PreserveSummary { get; set; }
    internal MainViewUpdateMode Mode { get; set; }
    internal Stopwatch Stopwatch { get; set; }
}

internal readonly struct RegularMaterializedChartListApplyResult
{
    internal RegularMaterializedChartListApplyResult(
        MainChartListRowsApplyResult rowsApply,
        RegularChartListBuildResult build,
        MainViewUpdateMode appliedMode)
    {
        WasCommitted = true;
        RowsApply = rowsApply;
        AppliedMode = appliedMode;
        FolderCount = build.Stage.FolderCount;
        KeywordCount = build.Stage.KeywordCount;
        ModeCount = build.Stage.ModeCount;
        ViewCount = build.Sort.RowsView?.Count ?? 0;
        FolderMs = build.FolderMs;
        KeywordMs = build.KeywordMs;
        ModeMs = build.ModeMs;
        SortMs = build.SortMs;
        SortReuse = build.Sort.SortReuse;
        SortProfile = build.Sort.SortProfile;
    }

    internal bool WasCommitted { get; }
    internal MainChartListRowsApplyResult RowsApply { get; }
    internal MainViewUpdateMode AppliedMode { get; }
    internal int FolderCount { get; }
    internal int KeywordCount { get; }
    internal int ModeCount { get; }
    internal int ViewCount { get; }
    internal long FolderMs { get; }
    internal long KeywordMs { get; }
    internal long ModeMs { get; }
    internal long SortMs { get; }
    internal bool SortReuse { get; }
    internal string SortProfile { get; }
}

internal sealed class RegularVirtualNormalLibraryApplyRequest
{
    internal BMSLibrary Library { get; set; }
    internal bool IncludeBmsonRows { get; set; }
    internal RegularNormalLibraryTreeFilter TreeFilter { get; set; }
    internal string KeywordFilter { get; set; }
    internal RegularChartModeFilter ModeFilter { get; set; }
    internal string SortColumnName { get; set; }
    internal ListSortDirection SortDirection { get; set; }
    internal RegularChartListExternalVersions ExternalVersions { get; set; }
    internal MainChartListColumnSelection ColumnSelection { get; set; }
    internal bool PreserveSummary { get; set; }
    internal MainViewUpdateMode Mode { get; set; }
    internal Stopwatch Stopwatch { get; set; }
    internal string Reason { get; set; }
}

internal sealed class RegularVirtualChartSubsetApplyRequest
{
    internal BMSLibrary Library { get; set; }
    internal IEnumerable<ChartFile> SourceCharts { get; set; }
    internal IEnumerable<PackageChartEntry> SourceEntries { get; set; }
    internal ChartListSourceProjectionMode SourceProjectionMode { get; set; }
    internal bool ApplyResourceHealthProjection { get; set; }
    internal MainViewUpdateMode TreeMode { get; set; }
    internal string SubsetName { get; set; }
    internal string KeywordFilter { get; set; }
    internal RegularChartModeFilter ModeFilter { get; set; }
    internal string SortColumnName { get; set; }
    internal ListSortDirection SortDirection { get; set; }
    internal RegularChartListExternalVersions ExternalVersions { get; set; }
    internal MainChartListColumnSelection ColumnSelection { get; set; }
    internal bool PreserveSummary { get; set; }
    internal MainViewUpdateMode Mode { get; set; }
    internal Stopwatch Stopwatch { get; set; }
}

internal readonly struct RegularVirtualChartSubsetApplyResult
{
    internal RegularVirtualChartSubsetApplyResult(
        RegularChartListTerminalResult terminal,
        ChartListVirtualView rowsView,
        ChartListOrder order,
        VirtualChartSubsetSortCacheKey sortCacheKey,
        int sourceRowCount,
        int keywordCount,
        int modeCount,
        int distinctFolderCount,
        long sourceRowsSignature,
        long sourceRowsMs,
        long keywordMs,
        long modeMs,
        long sortMs,
        bool sortCacheHit,
        long orderCacheLookupMs,
        long orderBuildMs)
    {
        Terminal = terminal;
        RowsView = rowsView;
        Order = order;
        SortCacheKey = sortCacheKey;
        SourceRowCount = sourceRowCount;
        KeywordCount = keywordCount;
        ModeCount = modeCount;
        DistinctFolderCount = distinctFolderCount;
        SourceRowsSignature = sourceRowsSignature;
        SourceRowsMs = sourceRowsMs;
        KeywordMs = keywordMs;
        ModeMs = modeMs;
        SortMs = sortMs;
        SortCacheHit = sortCacheHit;
        OrderCacheLookupMs = orderCacheLookupMs;
        OrderBuildMs = orderBuildMs;
    }

    internal bool WasCommitted => Terminal.WasCommitted;
    internal RegularChartListTerminalResult Terminal { get; }
    internal ChartListVirtualView RowsView { get; }
    internal ChartListOrder Order { get; }
    internal VirtualChartSubsetSortCacheKey SortCacheKey { get; }
    internal int SourceRowCount { get; }
    internal int KeywordCount { get; }
    internal int ModeCount { get; }
    internal int DistinctFolderCount { get; }
    internal long SourceRowsSignature { get; }
    internal long SourceRowsMs { get; }
    internal long KeywordMs { get; }
    internal long ModeMs { get; }
    internal long SortMs { get; }
    internal bool SortCacheHit { get; }
    internal long OrderCacheLookupMs { get; }
    internal long OrderBuildMs { get; }
}

internal readonly struct RegularVirtualNormalLibraryApplyResult
{
    internal RegularVirtualNormalLibraryApplyResult(
        RegularChartListTerminalResult terminal,
        ChartListVirtualView rowsView,
        ChartListOrder order,
        NormalLibrarySortCacheKey sortCacheKey,
        int sourceRowCount,
        int folderCount,
        int keywordCount,
        int modeCount,
        int distinctFolderCount,
        bool sourceRowsCacheHit,
        bool sortCacheHit,
        bool summaryCacheHit,
        long sourceRowsMs,
        long folderMs,
        long keywordMs,
        long modeMs,
        long sortMs,
        long orderCacheLookupMs,
        long orderBuildMs)
    {
        Terminal = terminal;
        RowsView = rowsView;
        Order = order;
        SortCacheKey = sortCacheKey;
        SourceRowCount = sourceRowCount;
        FolderCount = folderCount;
        KeywordCount = keywordCount;
        ModeCount = modeCount;
        DistinctFolderCount = distinctFolderCount;
        SourceRowsCacheHit = sourceRowsCacheHit;
        SortCacheHit = sortCacheHit;
        SummaryCacheHit = summaryCacheHit;
        SourceRowsMs = sourceRowsMs;
        FolderMs = folderMs;
        KeywordMs = keywordMs;
        ModeMs = modeMs;
        SortMs = sortMs;
        OrderCacheLookupMs = orderCacheLookupMs;
        OrderBuildMs = orderBuildMs;
    }

    internal bool WasCommitted => Terminal.WasCommitted;
    internal RegularChartListTerminalResult Terminal { get; }
    internal ChartListVirtualView RowsView { get; }
    internal ChartListOrder Order { get; }
    internal NormalLibrarySortCacheKey SortCacheKey { get; }
    internal int SourceRowCount { get; }
    internal int FolderCount { get; }
    internal int KeywordCount { get; }
    internal int ModeCount { get; }
    internal int DistinctFolderCount { get; }
    internal bool SourceRowsCacheHit { get; }
    internal bool SortCacheHit { get; }
    internal bool SummaryCacheHit { get; }
    internal long SourceRowsMs { get; }
    internal long FolderMs { get; }
    internal long KeywordMs { get; }
    internal long ModeMs { get; }
    internal long SortMs { get; }
    internal long OrderCacheLookupMs { get; }
    internal long OrderBuildMs { get; }

    internal static RegularVirtualNormalLibraryApplyResult NotCommitted() => default;
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

internal static class MainViewBuildRequestSequence
{
    private static long seed;

    internal static long Next() => Interlocked.Increment(ref seed);
}
