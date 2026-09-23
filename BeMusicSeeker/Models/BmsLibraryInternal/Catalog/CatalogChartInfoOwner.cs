using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the in-memory chart-info projection and its deferred lifecycle state.
///
/// Durable chart-info facts are submitted to <see cref="CatalogMutationOwner"/>;
/// this owner does not open a database or duplicate scan/LR2 transactions.
/// </summary>
internal sealed class CatalogChartInfoOwner
{

    private readonly ChartInfoBuildService buildService = new();

    private readonly ChartInfoInlineBuildService inlineBuildService;

    private readonly Action<string> propertyChanged;

    private readonly Func<bool> isShutdownRequested;

    private readonly Func<string, string, bool> trySkipForShutdown;

    private readonly Func<Func<string, string, string, Func<Task>, bool>> startupBackgroundTaskSchedulerProvider;

    private readonly Action<string> logPerformance;

    private BmsLibraryDbGateway workflowDbGateway;

    private CatalogMutationOwner workflowMutationOwner;

    private CatalogStorageRowsOwner workflowStorageRowsOwner;

    private CatalogOwnedCollectionOwner workflowOwnedCollectionOwner;

    private Action<string> workflowLogWarning;

    private Action<CatalogChartInfoOwnerEvent> workflowEvent;

    private Func<IDisposable> workflowBeginDigestMutationWindow = () => EmptyDisposable.Instance;

    private Func<IReadOnlyList<LibraryChartDigestChange>, string, Action> workflowPrepareDigestPublication;

    private readonly object backfillGate = new();

    private readonly List<ChartInfoBackfillRequest> backfillRequests = [];

    private readonly object hydrationGate = new();

    private readonly object indexGate = new();

    private readonly object lazyDisplayIndexGate = new();

    private Dictionary<string, LR2SongDBExtended.chart_info> indexBySha256 =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> indexByMd5 =
        new(StringComparer.OrdinalIgnoreCase);

    private bool displayIndexLoaded;

    private bool hydrationRunning;

    private bool hydrationPending;

    private string hydrationPendingReason;

    private bool hydrationPendingQueueBackfill;

    private int hydrationRequestedVersion;

    private int chartInfoBackfillRequestedVersion;

    private int chartInfoBackfillCompletedVersion;

    private int chartInfoBackfillHydrationBypassUntilVersion;

    private ChartInfoHydrationAllCurrentSnapshot hydrationAllCurrentSnapshot;

    private bool chartInfoHydrationRunningValue;

    private int chartInfoHydrationRequestedVersionValue;

    private int chartInfoHydrationCompletedVersionValue;

    private int chartInfoHydrationTotalCountValue;

    private int chartInfoHydrationAppliedCountValue;

    private bool chartInfoBackfillRunningValue;

    private int chartInfoBackfillRequestedVersionValue;

    private int chartInfoBackfillCompletedVersionValue;

    private int chartInfoBackfillTotalCountValue;

    private int chartInfoBackfillProcessedCountValue;

    private int chartInfoBackfillDigestBackfilledCountValue;

    private string chartInfoBackfillCurrentPathValue = string.Empty;

    private bool chartDigestBackfillRunningValue;

    private int chartDigestBackfillRequestedVersionValue;

    private int chartDigestBackfillCompletedVersionValue;

    private int chartDigestBackfillTotalCountValue;

    private int chartDigestBackfillProcessedCountValue;

    private string chartDigestBackfillCurrentPathValue = string.Empty;

    private int chartInfoIndexVersionValue;

    private bool chartInfoIndexHydratedValue;

    internal CatalogChartInfoOwner(
        Action<string> propertyChanged,
        Func<bool> isShutdownRequested,
        Func<string, string, bool> trySkipForShutdown,
        Func<Func<string, string, string, Func<Task>, bool>> startupBackgroundTaskSchedulerProvider,
        Action<string> logPerformance)
    {
        this.propertyChanged = propertyChanged;
        this.isShutdownRequested = isShutdownRequested ?? (() => false);
        this.trySkipForShutdown = trySkipForShutdown ?? ((_, _) => false);
        this.startupBackgroundTaskSchedulerProvider = startupBackgroundTaskSchedulerProvider;
        this.logPerformance = logPerformance;
        inlineBuildService = new(
            buildService,
            FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree());
    }

    /// <summary>
    /// chart-infoの永続化と、commit済みdigest factsの公開処理を接続します。
    /// </summary>
    /// <param name="dbGateway">chart-infoを書き込むDB gateway。</param>
    /// <param name="mutationOwner">catalogの永続化を所有するmutation owner。</param>
    /// <param name="storageRowsOwner">永続化済みstorage rowsのowner。</param>
    /// <param name="ownedCollectionOwner">所持譜面の正本owner。</param>
    /// <param name="logWarning">警告ログ出力。</param>
    /// <param name="workflowEvent">chart-info表示状態の通知。</param>
    /// <param name="beginDigestMutationWindow">digest操作の入力mutation window。</param>
    /// <param name="prepareDigestPublication">commit済みdigest factsからcommon effects公開処理を準備するcallback。</param>
    internal void ConfigureWorkflow(
        BmsLibraryDbGateway dbGateway,
        CatalogMutationOwner mutationOwner,
        CatalogStorageRowsOwner storageRowsOwner,
        CatalogOwnedCollectionOwner ownedCollectionOwner,
        Action<string> logWarning,
        Action<CatalogChartInfoOwnerEvent> workflowEvent,
        Func<IDisposable> beginDigestMutationWindow = null,
        Func<IReadOnlyList<LibraryChartDigestChange>, string, Action> prepareDigestPublication = null)
    {
        workflowDbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        workflowMutationOwner = mutationOwner ?? throw new ArgumentNullException(nameof(mutationOwner));
        workflowStorageRowsOwner = storageRowsOwner ?? throw new ArgumentNullException(nameof(storageRowsOwner));
        workflowOwnedCollectionOwner = ownedCollectionOwner ?? throw new ArgumentNullException(nameof(ownedCollectionOwner));
        workflowLogWarning = logWarning;
        this.workflowEvent = workflowEvent;
        workflowBeginDigestMutationWindow = beginDigestMutationWindow ?? (() => EmptyDisposable.Instance);
        workflowPrepareDigestPublication = prepareDigestPublication;
    }

    internal object BackfillGate => backfillGate;

    internal List<ChartInfoBackfillRequest> BackfillRequests => backfillRequests;

    internal object HydrationGate => hydrationGate;

    internal object IndexGate => indexGate;

    internal object LazyDisplayIndexGate => lazyDisplayIndexGate;

    internal Dictionary<string, LR2SongDBExtended.chart_info> IndexBySha256
    {
        get => indexBySha256;
        set => indexBySha256 = value ?? new(StringComparer.OrdinalIgnoreCase);
    }

    internal Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> IndexByMd5
    {
        get => indexByMd5;
        set => indexByMd5 = value ?? new(StringComparer.OrdinalIgnoreCase);
    }

    internal bool DisplayIndexLoaded
    {
        get => displayIndexLoaded;
        set => displayIndexLoaded = value;
    }

    internal bool HydrationRunningState
    {
        get { lock (hydrationGate) return hydrationRunning; }
        set { lock (hydrationGate) hydrationRunning = value; }
    }

    internal bool HydrationPending
    {
        get { lock (hydrationGate) return hydrationPending; }
        set { lock (hydrationGate) hydrationPending = value; }
    }

    internal string HydrationPendingReason
    {
        get { lock (hydrationGate) return hydrationPendingReason; }
        set { lock (hydrationGate) hydrationPendingReason = value; }
    }

    internal bool HydrationPendingQueueBackfill
    {
        get { lock (hydrationGate) return hydrationPendingQueueBackfill; }
        set { lock (hydrationGate) hydrationPendingQueueBackfill = value; }
    }

    internal int HydrationRequestedVersionState
    {
        get { lock (hydrationGate) return hydrationRequestedVersion; }
        set { lock (hydrationGate) hydrationRequestedVersion = value; }
    }

    internal int ChartInfoBackfillRequestedVersionState
    {
        get { lock (backfillGate) return chartInfoBackfillRequestedVersion; }
        set { lock (backfillGate) chartInfoBackfillRequestedVersion = value; }
    }

    internal int ChartInfoBackfillCompletedVersionState
    {
        get { lock (backfillGate) return chartInfoBackfillCompletedVersion; }
        set { lock (backfillGate) chartInfoBackfillCompletedVersion = value; }
    }

    internal int ChartInfoBackfillHydrationBypassUntilVersion
    {
        get { lock (backfillGate) return chartInfoBackfillHydrationBypassUntilVersion; }
        set { lock (backfillGate) chartInfoBackfillHydrationBypassUntilVersion = value; }
    }

    internal ChartInfoHydrationAllCurrentSnapshot HydrationAllCurrentSnapshot
    {
        get { lock (hydrationGate) return hydrationAllCurrentSnapshot; }
        set { lock (hydrationGate) hydrationAllCurrentSnapshot = value; }
    }

    internal bool ChartInfoHydrationRunning
    {
        get => chartInfoHydrationRunningValue;
        set => Set(ref chartInfoHydrationRunningValue, value, nameof(BMSLibrary.ChartInfoHydrationRunning));
    }

    internal int ChartInfoHydrationRequestedVersion
    {
        get => chartInfoHydrationRequestedVersionValue;
        set => Set(ref chartInfoHydrationRequestedVersionValue, value, nameof(BMSLibrary.ChartInfoHydrationRequestedVersion));
    }

    internal int ChartInfoHydrationCompletedVersion
    {
        get => chartInfoHydrationCompletedVersionValue;
        set => Set(ref chartInfoHydrationCompletedVersionValue, value, nameof(BMSLibrary.ChartInfoHydrationCompletedVersion));
    }

    internal int ChartInfoHydrationTotalCount
    {
        get => chartInfoHydrationTotalCountValue;
        set => Set(ref chartInfoHydrationTotalCountValue, value, nameof(BMSLibrary.ChartInfoHydrationTotalCount));
    }

    internal int ChartInfoHydrationAppliedCount
    {
        get => chartInfoHydrationAppliedCountValue;
        set => Set(ref chartInfoHydrationAppliedCountValue, value, nameof(BMSLibrary.ChartInfoHydrationAppliedCount));
    }

    internal bool ChartInfoBackfillRunning
    {
        get => chartInfoBackfillRunningValue;
        set => Set(ref chartInfoBackfillRunningValue, value, nameof(BMSLibrary.ChartInfoBackfillRunning));
    }

    internal int ChartInfoBackfillRequestedVersion
    {
        get => chartInfoBackfillRequestedVersionValue;
        set => Set(ref chartInfoBackfillRequestedVersionValue, value, nameof(BMSLibrary.ChartInfoBackfillRequestedVersion));
    }

    internal int ChartInfoBackfillCompletedVersion
    {
        get => chartInfoBackfillCompletedVersionValue;
        set => Set(ref chartInfoBackfillCompletedVersionValue, value, nameof(BMSLibrary.ChartInfoBackfillCompletedVersion));
    }

    internal int ChartInfoBackfillTotalCount
    {
        get => chartInfoBackfillTotalCountValue;
        set => Set(ref chartInfoBackfillTotalCountValue, value, nameof(BMSLibrary.ChartInfoBackfillTotalCount));
    }

    internal int ChartInfoBackfillProcessedCount
    {
        get => chartInfoBackfillProcessedCountValue;
        set => Set(ref chartInfoBackfillProcessedCountValue, value, nameof(BMSLibrary.ChartInfoBackfillProcessedCount));
    }

    internal int ChartInfoBackfillDigestBackfilledCount
    {
        get => chartInfoBackfillDigestBackfilledCountValue;
        set => Set(ref chartInfoBackfillDigestBackfilledCountValue, value, nameof(BMSLibrary.ChartInfoBackfillDigestBackfilledCount));
    }

    internal string ChartInfoBackfillCurrentPath
    {
        get => chartInfoBackfillCurrentPathValue;
        set => Set(ref chartInfoBackfillCurrentPathValue, value ?? string.Empty, nameof(BMSLibrary.ChartInfoBackfillCurrentPath));
    }

    internal bool ChartDigestBackfillRunning
    {
        get => chartDigestBackfillRunningValue;
        set => Set(ref chartDigestBackfillRunningValue, value, nameof(BMSLibrary.ChartDigestBackfillRunning));
    }

    internal int ChartDigestBackfillRequestedVersion
    {
        get => chartDigestBackfillRequestedVersionValue;
        set => Set(ref chartDigestBackfillRequestedVersionValue, value, nameof(BMSLibrary.ChartDigestBackfillRequestedVersion));
    }

    internal int ChartDigestBackfillCompletedVersion
    {
        get => chartDigestBackfillCompletedVersionValue;
        set => Set(ref chartDigestBackfillCompletedVersionValue, value, nameof(BMSLibrary.ChartDigestBackfillCompletedVersion));
    }

    internal int ChartDigestBackfillTotalCount
    {
        get => chartDigestBackfillTotalCountValue;
        set => Set(ref chartDigestBackfillTotalCountValue, value, nameof(BMSLibrary.ChartDigestBackfillTotalCount));
    }

    internal int ChartDigestBackfillProcessedCount
    {
        get => chartDigestBackfillProcessedCountValue;
        set => Set(ref chartDigestBackfillProcessedCountValue, value, nameof(BMSLibrary.ChartDigestBackfillProcessedCount));
    }

    internal string ChartDigestBackfillCurrentPath
    {
        get => chartDigestBackfillCurrentPathValue;
        set => Set(ref chartDigestBackfillCurrentPathValue, value ?? string.Empty, nameof(BMSLibrary.ChartDigestBackfillCurrentPath));
    }

    internal int ChartInfoIndexVersion
    {
        get { lock (indexGate) return chartInfoIndexVersionValue; }
        set { lock (indexGate) chartInfoIndexVersionValue = value; }
    }

    internal bool ChartInfoIndexHydrated
    {
        get { lock (indexGate) return chartInfoIndexHydratedValue; }
        set { lock (indexGate) chartInfoIndexHydratedValue = value; }
    }

    internal bool HydrationReadyForInstallableMaintenance
    {
        get
        {
            lock (hydrationGate)
            {
                return hydrationRequestedVersion > 0
                    && chartInfoHydrationCompletedVersionValue >= hydrationRequestedVersion
                    && !hydrationRunning;
            }
        }
    }

    internal void ClearHydrationState()
    {
        lock (hydrationGate)
        {
            hydrationPending = false;
            hydrationPendingReason = null;
            hydrationPendingQueueBackfill = false;
            hydrationRunning = false;
        }
    }

    internal void SetHydrationQueueState(
        int requestVersion,
        string reason,
        bool queueBackfillAfterHydration,
        bool running)
    {
        lock (hydrationGate)
        {
            hydrationRequestedVersion = requestVersion;
            hydrationPending = true;
            hydrationPendingReason = reason;
            hydrationPendingQueueBackfill = hydrationPendingQueueBackfill || queueBackfillAfterHydration;
            hydrationRunning = running;
        }
    }

    internal void NotifyHydrationState()
    {
        propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoHydrationRunning));
        propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoHydrationRequestedVersion));
        propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoHydrationCompletedVersion));
    }

    internal void NotifyBackfillState()
    {
        propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoBackfillRunning));
        propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoBackfillRequestedVersion));
        propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoBackfillCompletedVersion));
    }

    internal bool TrySkip(string operation, string reason) => trySkipForShutdown(operation, reason);

    internal bool IsShutdownRequested => isShutdownRequested();

    internal Func<Func<string, string, string, Func<Task>, bool>> SchedulerProvider => startupBackgroundTaskSchedulerProvider;

    internal Action<string> LogPerformance => logPerformance;

    internal ChartInfoBuildService BuildService => buildService;

    internal void QueueDeferredHydration(string reason, bool queueFullBackfillAfterHydration)
    {
        EnsureWorkflowConfigured();
        QueueHydration(
            reason,
            queueFullBackfillAfterHydration,
            () =>
            {
                ProcessDeferredHydrationRequests();
                return Task.CompletedTask;
            });
    }

    internal void EnsureHydratedForLr2(string reason)
    {
        EnsureWorkflowConfigured();
        WaitForHydrationIdle();
        if (IsIndexHydrated)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        ChartInfoHydrationResult result = HydrateChartInfos(
            "lr2_song_db_sync_" + (string.IsNullOrWhiteSpace(reason) ? "sync" : reason));
        stopwatch.Stop();
        LogPerformance?.Invoke("lr2_song_db_sync_chart_info_hydration ensured"
            + " reason=" + (reason ?? "unknown")
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds
            + " succeeded=" + result.Succeeded.ToString().ToLowerInvariant()
            + " totalRows=" + result.TotalRows
            + " dbLoadMs=" + result.DbLoadMs
            + " indexBuildMs=" + result.IndexBuildMs);
    }

    internal void WaitForHydrationIdle()
    {
        EnsureWorkflowConfigured();
        while (true)
        {
            lock (hydrationGate)
            {
                if (!hydrationRunning)
                {
                    return;
                }
            }
            lock (backfillGate)
            {
                if (chartInfoBackfillHydrationBypassUntilVersion > chartInfoBackfillCompletedVersion)
                {
                    return;
                }
            }
            Thread.Sleep(50);
        }
    }

    internal void QueueBackfill(
        string reason,
        bool processSynchronously = false,
        ChartInfoHydrationResult hydrationResult = null)
    {
        EnsureWorkflowConfigured();
        ChartInfoHydrationResult currentAllCurrentResult = null;
        if (hydrationResult != null
            && hydrationResult.Succeeded
            && hydrationResult.OwnerCount > 0
            && hydrationResult.BackfillCandidateOwnerCount <= 0)
        {
            currentAllCurrentResult = CreateCurrentHydrationAllCurrentResult();
        }
        if (currentAllCurrentResult != null && currentAllCurrentResult.OwnerCount == hydrationResult.OwnerCount)
        {
            int skippedVersion = CompleteSkippedBackfillRequestIfIdle();
            LogPerformance?.Invoke("chart_info_backfill skipped reason=hydration_all_current"
                + " version=" + skippedVersion
                + " requestReason=" + (reason ?? "unknown")
                + " ownerCount=" + currentAllCurrentResult.OwnerCount
                + " currentChartInfo=" + currentAllCurrentResult.CurrentChartInfoOwnerCount
                + " currentParseFailure=" + currentAllCurrentResult.CurrentParseFailureOwnerCount
                + " candidates=" + currentAllCurrentResult.BackfillCandidateOwnerCount);
            PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Checkpoint("chart_info_backfill", "skipped"));
            return;
        }

        ChartInfoBackfillCandidateSummary summary = null;
        var candidateSummaryStopwatch = Stopwatch.StartNew();
        try
        {
            LogPerformance?.Invoke("chart_info_backfill candidate_summary_start reason=" + (reason ?? "unknown"));
            summary = GetBackfillCandidateSummary(workflowDbGateway, buildService.CurrentParseTimeout);
            candidateSummaryStopwatch.Stop();
            LogPerformance?.Invoke("chart_info_backfill candidate_summary_done reason=" + (reason ?? "unknown")
                + " elapsedMs=" + candidateSummaryStopwatch.ElapsedMilliseconds
                + " bmsOwners=" + summary.BmsOwnerCount
                + " bmsonOwners=" + summary.BmsonOwnerCount
                + " candidates=" + summary.CandidateOwnerCount
                + " missingDigest=" + summary.MissingDigestOwnerCount
                + " missingChartInfo=" + summary.MissingChartInfoOwnerCount
                + " staleChartInfo=" + summary.StaleChartInfoOwnerCount
                + " currentParseFailure=" + summary.CurrentParseFailureOwnerCount
                + " currentChartInfo=" + summary.CurrentChartInfoOwnerCount);
        }
        catch (Exception ex)
        {
            candidateSummaryStopwatch.Stop();
            LogPerformance?.Invoke("chart_info_backfill candidate_summary_failed reason=" + (reason ?? "unknown")
                + " elapsedMs=" + candidateSummaryStopwatch.ElapsedMilliseconds
                + " message=" + ex.Message);
        }
        if (summary != null && summary.CandidateOwnerCount <= 0)
        {
            int skippedVersion = CompleteSkippedBackfillRequestIfIdle();
            LogPerformance?.Invoke("chart_info_backfill skipped reason=no_candidates"
                + " version=" + skippedVersion
                + " requestReason=" + (reason ?? "unknown")
                + " bmsOwners=" + summary.BmsOwnerCount
                + " bmsonOwners=" + summary.BmsonOwnerCount
                + " candidates=" + summary.CandidateOwnerCount
                + " missingDigest=" + summary.MissingDigestOwnerCount
                + " missingChartInfo=" + summary.MissingChartInfoOwnerCount
                + " staleChartInfo=" + summary.StaleChartInfoOwnerCount
                + " currentParseFailure=" + summary.CurrentParseFailureOwnerCount
                + " currentChartInfo=" + summary.CurrentChartInfoOwnerCount);
            PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Checkpoint("chart_info_backfill", "skipped"));
            return;
        }
        if (summary != null)
        {
            LogPerformance?.Invoke("chart_info_backfill candidates"
                + " reason=" + (reason ?? "unknown")
                + " bmsOwners=" + summary.BmsOwnerCount
                + " bmsonOwners=" + summary.BmsonOwnerCount
                + " candidates=" + summary.CandidateOwnerCount
                + " missingDigest=" + summary.MissingDigestOwnerCount
                + " missingChartInfo=" + summary.MissingChartInfoOwnerCount
                + " staleChartInfo=" + summary.StaleChartInfoOwnerCount
                + " currentParseFailure=" + summary.CurrentParseFailureOwnerCount
                + " currentChartInfo=" + summary.CurrentChartInfoOwnerCount);
        }
        QueueBackfillRequest(ChartInfoBackfillRequest.Full(reason), processSynchronously);
    }

    internal void ProcessBackfillRequests(bool waitForHydrationIdle = true)
    {
        EnsureWorkflowConfigured();
        while (true)
        {
            if (IsShutdownRequested)
            {
                int shutdownRequestVersion;
                lock (backfillGate)
                {
                    shutdownRequestVersion = chartInfoBackfillRequestedVersion;
                    backfillRequests.Clear();
                    chartInfoBackfillCompletedVersion = shutdownRequestVersion;
                    chartInfoBackfillHydrationBypassUntilVersion = 0;
                    ChartInfoBackfillRunning = false;
                }
                ChartInfoBackfillCompletedVersion = shutdownRequestVersion;
                ChartInfoBackfillTotalCount = 0;
                ChartInfoBackfillProcessedCount = 0;
                ChartInfoBackfillDigestBackfilledCount = 0;
                ChartInfoBackfillCurrentPath = string.Empty;
                LogPerformance?.Invoke("chart_info_backfill skipped version=" + shutdownRequestVersion + " reason=shutdown_requested");
                return;
            }
            if (waitForHydrationIdle)
            {
                WaitForHydrationIdle();
            }
            int requestVersion;
            lock (backfillGate)
            {
                requestVersion = chartInfoBackfillRequestedVersion;
                backfillRequests.Clear();
            }
            List<ChartFile> chartSnapshot;
            using (workflowStorageRowsOwner.WriteGate.GetReaderGuard())
            {
                workflowOwnedCollectionOwner.EnsureCurrent(workflowStorageRowsOwner);
                lock (workflowOwnedCollectionOwner.Gate)
                {
                    chartSnapshot = workflowOwnedCollectionOwner.Collection.CreateSnapshot(
                        includeWarningSnapshot: false,
                        includeResourceReferences: false,
                        includeScoreSnapshot: false);
                }
            }
            int snapshotCount = chartSnapshot.Count;
            bool completedLatestRequest = false;
            Dictionary<string, LR2SongDBExtended.chart_info> existingRowsSnapshot = null;
            ChartInfoBackfillResult result = null;
            List<Action> publicationEffects = [];
            using (workflowBeginDigestMutationWindow())
            {
                try
                {
                    ChartInfoBackfillRunning = true;
                    ChartInfoBackfillTotalCount = 0;
                    ChartInfoBackfillProcessedCount = 0;
                    ChartInfoBackfillCurrentPath = string.Empty;
                    void reportProgress(int total, int processed, string currentPath)
                    {
                        ChartInfoBackfillTotalCount = total;
                        ChartInfoBackfillProcessedCount = processed;
                        ChartInfoBackfillCurrentPath = currentPath ?? string.Empty;
                    }
                    existingRowsSnapshot = CreateSha256Snapshot();
                    result = buildService.BackfillChartInfos(
                        workflowDbGateway,
                        chartSnapshot,
                        reportProgress,
                        LogPerformance,
                        workflowLogWarning,
                        publication => PublishCommittedStorageApplication(
                            publication.DigestChanges,
                            publication.AppliedRows,
                            publication.ParseFailureChanged,
                            "chart_info_backfill",
                            publicationEffects.Add),
                        existingRowsSnapshot,
                        request =>
                        {
                            return workflowMutationOwner.ApplyChartInfoStorageWrite(request);
                        });
                    LogPerformance?.Invoke("chart_info_backfill done version=" + requestVersion
                        + " mode=full total=" + result.TargetCount
                        + " success=" + result.BackfilledCount
                        + " failed=" + result.FailedCount
                        + " timeoutFailed=" + result.TimeoutFailedCount
                        + " digestBackfilled=" + result.DigestBackfilledCount
                        + " digestFailed=" + result.DigestFailedCount
                        + " fileReadCount=" + result.FileReadCount
                        + " fileReadBytes=" + result.FileReadBytes
                        + " currentRowSkipped=" + result.CurrentRowSkippedCount
                        + " parseFailureSkipped=" + result.FailureSkippedCount
                        + " songProjectionRequested=" + result.SongProjectionRequestedCount
                        + " songProjectionMatched=" + result.SongProjectionMatchedCount
                        + " songProjectionChanged=" + result.SongProjectionChangedCount
                        + " songProjectionMissing=" + result.SongProjectionMissingCount);
                }
                catch (Exception ex)
                {
                    LogPerformance?.Invoke("chart_info_backfill failed version=" + requestVersion + " message=" + ex.Message);
                }
                finally
                {
                    ChartInfoBackfillCurrentPath = string.Empty;
                    ChartInfoBackfillDigestBackfilledCount = result?.DigestBackfilledCount ?? 0;
                    chartSnapshot?.Clear();
                    existingRowsSnapshot?.Clear();
                }
            }
            for (int publicationIndex = 0; publicationIndex < publicationEffects.Count; publicationIndex++)
            {
                try
                {
                    publicationEffects[publicationIndex]?.Invoke();
                }
                catch (Exception ex)
                {
                    LogPerformance?.Invoke("chart_info_backfill publication failed version="
                        + requestVersion + " index=" + publicationIndex + " message=" + ex.Message);
                }
            }
            ChartInfoBackfillCompletedVersion = requestVersion;
            lock (backfillGate)
            {
                chartInfoBackfillCompletedVersion = requestVersion;
                if (requestVersion == chartInfoBackfillRequestedVersion)
                {
                    ChartInfoBackfillRunning = false;
                    completedLatestRequest = true;
                }
            }
            PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Checkpoint("chart_info_backfill", "after_release"));
            if (completedLatestRequest)
            {
                return;
            }
        }
    }

    private void ProcessDeferredHydrationRequests()
    {
        EnsureWorkflowConfigured();
        while (true)
        {
            if (IsShutdownRequested)
            {
                CompleteHydrationForShutdown("shutdown_requested");
                return;
            }
            int requestVersion;
            string reason;
            bool queueBackfillAfterHydration;
            lock (hydrationGate)
            {
                requestVersion = hydrationRequestedVersion;
                reason = hydrationPendingReason;
                queueBackfillAfterHydration = hydrationPendingQueueBackfill;
                hydrationPending = false;
                hydrationPendingReason = null;
                hydrationPendingQueueBackfill = false;
            }

            ChartInfoHydrationResult result;
            try
            {
                result = HydrateChartInfos(reason);
            }
            catch (Exception ex)
            {
                result = new ChartInfoHydrationResult();
                LogPerformance?.Invoke("chart_info_hydration failed reason=" + (reason ?? "unknown") + " message=" + ex.Message);
            }
            ChartInfoHydrationTotalCount = result.TotalRows;
            ChartInfoHydrationAppliedCount = result.AppliedBmsCount + result.AppliedBmsonCount;
            ChartInfoHydrationCompletedVersion = requestVersion;
            LogPerformance?.Invoke("chart_info_hydration done version=" + requestVersion
                + " reason=" + (reason ?? "unknown")
                + " totalRows=" + result.TotalRows
                + " chartInfoRows=" + result.ChartInfoRows
                + " appliedBms=" + result.AppliedBmsCount
                + " appliedBmson=" + result.AppliedBmsonCount
                + " ownerApplyUpdated=" + result.OwnerApplyUpdatedCount
                + " ownerApplySkipped=" + result.OwnerApplySkippedCount
                + " ownerApplySilent=" + result.OwnerApplySilentCount
                + " ownerApplyNotified=" + result.OwnerApplyNotifiedCount
                + " ownerCount=" + result.OwnerCount
                + " currentChartInfoOwners=" + result.CurrentChartInfoOwnerCount
                + " currentParseFailureOwners=" + result.CurrentParseFailureOwnerCount
                + " backfillCandidateOwners=" + result.BackfillCandidateOwnerCount
                + " dbMode=" + (string.IsNullOrWhiteSpace(result.DbMaterializeMode) ? "unknown" : result.DbMaterializeMode)
                + " dbLoadMs=" + result.DbLoadMs
                + " dbMaterializeMs=" + result.DbMaterializeMs
                + " rawRows=" + result.DbRawRows
                + " rawReadMs=" + result.DbRawReadMs
                + " rawObjectMs=" + result.DbRawObjectMs
                + " readOnly=" + result.DbReadOnly.ToString().ToLowerInvariant()
                + " dbLockWaitMs=" + result.DbLockWaitMs
                + " parseFailureRows=" + result.ParseFailureRows
                + " indexBuildMs=" + result.IndexBuildMs
                + " ownerApplyMs=" + result.OwnerApplyMs
                + " totalMs=" + result.TotalMs);
            PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Checkpoint("chart_info_hydration", "after"));

            bool completedLatestRequest = false;
            bool shouldQueueBackfillAfterCompletion = false;
            lock (hydrationGate)
            {
                if (!hydrationPending)
                {
                    completedLatestRequest = true;
                    shouldQueueBackfillAfterCompletion = queueBackfillAfterHydration;
                }
                else if (queueBackfillAfterHydration)
                {
                    hydrationPendingQueueBackfill = true;
                }
            }
            if (completedLatestRequest)
            {
                bool shouldReturnAfterCompletion = false;
                try
                {
                    if (shouldQueueBackfillAfterCompletion)
                    {
                        QueueBackfill(reason, processSynchronously: true, hydrationResult: result);
                    }
                }
                finally
                {
                    lock (hydrationGate)
                    {
                        if (!hydrationPending)
                        {
                            hydrationRunning = false;
                            ChartInfoHydrationRunning = false;
                            shouldReturnAfterCompletion = true;
                        }
                    }
                }
                if (shouldReturnAfterCompletion)
                {
                    return;
                }
            }
        }
    }

    private ChartInfoHydrationResult HydrateChartInfos(string reason)
    {
        EnsureWorkflowConfigured();
        var result = new ChartInfoHydrationResult();
        var totalStopwatch = Stopwatch.StartNew();
        LogPerformance?.Invoke("chart_info_hydration start reason=" + (reason ?? "unknown"));
        Dictionary<string, LR2SongDBExtended.chart_info> chartInfoMap;
        HashSet<string> currentChartInfoSha256s;
        HashSet<string> currentParseFailureMd5s;
        var loadStopwatch = Stopwatch.StartNew();
        try
        {
            ChartInfoHydrationLoadResult loadResult = LoadHydrationData(
                workflowDbGateway,
                buildService.CurrentParseTimeout);
            chartInfoMap = loadResult.ChartInfoBySha256;
            currentChartInfoSha256s = loadResult.CurrentChartInfoSha256s;
            currentParseFailureMd5s = loadResult.CurrentParseFailureMd5s;
            result.ParseFailureRows = loadResult.ParseFailureRows;
            result.ChartInfoRows = loadResult.ChartInfoRows;
            result.DbMaterializeMode = loadResult.MaterializeMode;
            result.DbRawRows = loadResult.RawRows;
            result.DbRawReadMs = loadResult.RawReadMs;
            result.DbRawObjectMs = loadResult.RawObjectMs;
            result.DbMaterializeMs = loadResult.MaterializeMs;
            result.DbLoadMs = loadResult.DbReadMs;
            result.DbReadOnly = loadResult.ReadOnly;
            result.DbLockWaitMs = loadResult.DbLockWaitMs;
        }
        catch (Exception ex)
        {
            loadStopwatch.Stop();
            totalStopwatch.Stop();
            result.DbLoadMs = result.DbLoadMs == 0 ? loadStopwatch.ElapsedMilliseconds : result.DbLoadMs;
            result.LoadMs = result.DbLoadMs;
            result.TotalMs = totalStopwatch.ElapsedMilliseconds;
            LogPerformance?.Invoke("chart_info_hydration failed reason=" + (reason ?? "unknown") + " message=" + ex.Message);
            return result;
        }
        loadStopwatch.Stop();
        if (result.DbLoadMs == 0)
        {
            result.DbLoadMs = loadStopwatch.ElapsedMilliseconds;
        }
        result.LoadMs = result.DbLoadMs;
        result.TotalRows = chartInfoMap.Count;
        if (result.ChartInfoRows == 0)
        {
            result.ChartInfoRows = chartInfoMap.Count;
        }
        var indexStopwatch = Stopwatch.StartNew();
        ChartInfoIndexUpdateResult indexUpdateResult = ReplaceIndex(chartInfoMap.Values, hydrated: true);
        indexStopwatch.Stop();
        result.IndexBuildMs = indexStopwatch.ElapsedMilliseconds;
        LogPerformance?.Invoke("chart_info_index_hydrated rows=" + indexUpdateResult.InputRows
            + " bySha256=" + indexUpdateResult.BySha256Count
            + " byMd5=" + indexUpdateResult.ByMd5Count
            + " version=" + indexUpdateResult.Version
            + " indexBuildMs=" + result.IndexBuildMs);

        int ownedCollectionVersionAtSummary = 0;
        int bmsRowsVersionAtSummary = 0;
        int bmsonRowsVersionAtSummary = 0;
        var ownerApplyStopwatch = Stopwatch.StartNew();
        using (workflowStorageRowsOwner.WriteGate.GetReaderGuard())
        {
            workflowOwnedCollectionOwner.EnsureCurrent(workflowStorageRowsOwner);
            ChartInfoHydrationOwnerSummary ownerSummary;
            lock (workflowOwnedCollectionOwner.Gate)
            {
                ownerSummary = workflowOwnedCollectionOwner.Collection.CreateChartInfoHydrationOwnerSummary(
                    currentChartInfoSha256s,
                    currentParseFailureMd5s);
            }
            result.OwnerCount = ownerSummary.OwnerCount;
            result.CurrentChartInfoOwnerCount = ownerSummary.CurrentChartInfoOwnerCount;
            result.CurrentParseFailureOwnerCount = ownerSummary.CurrentParseFailureOwnerCount;
            result.BackfillCandidateOwnerCount = ownerSummary.BackfillCandidateOwnerCount;
            result.OwnerApplySkippedCount = ownerSummary.OwnerApplySkippedCount;
            StorageRowsVersionSnapshot rowsSnapshot = workflowStorageRowsOwner.CaptureVersionSnapshot();
            ownedCollectionVersionAtSummary = workflowOwnedCollectionOwner.CollectionVersion;
            bmsRowsVersionAtSummary = rowsSnapshot.BmsRowsVersion;
            bmsonRowsVersionAtSummary = rowsSnapshot.BmsonRowsVersion;
        }
        ownerApplyStopwatch.Stop();
        result.OwnerApplyMs = result.ApplyMs = ownerApplyStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        result.Succeeded = true;
        CaptureHydrationAllCurrentSnapshot(
            result,
            ownedCollectionVersionAtSummary,
            bmsRowsVersionAtSummary,
            bmsonRowsVersionAtSummary);
        return result;
    }

    private void QueueBackfillRequest(ChartInfoBackfillRequest request, bool processSynchronously)
    {
        int requestVersion;
        bool shouldStartWorker = false;
        bool shouldWaitForCompletion = false;
        if (request == null)
        {
            return;
        }
        if (TrySkip("chart_info_backfill", request.Reason))
        {
            return;
        }
        lock (backfillGate)
        {
            chartInfoBackfillRequestedVersion++;
            requestVersion = chartInfoBackfillRequestedVersion;
            backfillRequests.Add(request);
            if (!ChartInfoBackfillRunning)
            {
                shouldStartWorker = true;
                ChartInfoBackfillRunning = true;
            }
            shouldWaitForCompletion = processSynchronously && !shouldStartWorker;
            if (shouldWaitForCompletion)
            {
                chartInfoBackfillHydrationBypassUntilVersion = Math.Max(
                    chartInfoBackfillHydrationBypassUntilVersion,
                    requestVersion);
            }
        }
        ChartInfoBackfillRequestedVersion = requestVersion;
        ChartInfoBackfillTotalCount = 0;
        ChartInfoBackfillProcessedCount = 0;
        ChartInfoBackfillDigestBackfilledCount = 0;
        ChartInfoBackfillCurrentPath = string.Empty;
        LogPerformance?.Invoke("chart_info_backfill queue"
            + " reason=" + (request.Reason ?? "unknown")
            + " mode=full"
            + " version=" + requestVersion);
        if (shouldStartWorker)
        {
            if (processSynchronously)
            {
                ProcessBackfillRequests(waitForHydrationIdle: false);
                return;
            }
            Task.Run(() => ProcessBackfillRequests()).ObserveFault("ProcessChartInfoBackfillRequests");
        }
        if (shouldWaitForCompletion)
        {
            WaitForBackfillVersion(requestVersion);
        }
    }

    private void WaitForBackfillVersion(int requestVersion)
    {
        try
        {
            while (true)
            {
                bool completed;
                lock (backfillGate)
                {
                    completed = chartInfoBackfillCompletedVersion >= requestVersion;
                }
                if (completed)
                {
                    return;
                }
                Thread.Sleep(50);
            }
        }
        finally
        {
            lock (backfillGate)
            {
                if (chartInfoBackfillHydrationBypassUntilVersion <= requestVersion)
                {
                    chartInfoBackfillHydrationBypassUntilVersion = 0;
                }
            }
        }
    }

    private int CompleteSkippedBackfillRequestIfIdle()
    {
        int requestVersion = 0;
        lock (backfillGate)
        {
            if (ChartInfoBackfillRunning)
            {
                return chartInfoBackfillRequestedVersion;
            }
            chartInfoBackfillRequestedVersion++;
            requestVersion = chartInfoBackfillRequestedVersion;
            chartInfoBackfillCompletedVersion = requestVersion;
        }
        ChartInfoBackfillRequestedVersion = requestVersion;
        ChartInfoBackfillTotalCount = 0;
        ChartInfoBackfillProcessedCount = 0;
        ChartInfoBackfillDigestBackfilledCount = 0;
        ChartInfoBackfillCurrentPath = string.Empty;
        ChartInfoBackfillCompletedVersion = requestVersion;
        ChartInfoBackfillRunning = false;
        return requestVersion;
    }

    internal void ClearHydrationAllCurrentSnapshot(string reason)
    {
        bool cleared = false;
        lock (hydrationGate)
        {
            if (hydrationAllCurrentSnapshot != null)
            {
                hydrationAllCurrentSnapshot = null;
                cleared = true;
            }
        }
        if (cleared)
        {
            LogPerformance?.Invoke("chart_info_hydration_all_current cleared reason=" + (reason ?? "unknown"));
        }
    }

    private void CaptureHydrationAllCurrentSnapshot(
        ChartInfoHydrationResult result,
        int ownedCollectionVersion,
        int bmsRowsVersion,
        int bmsonRowsVersion)
    {
        ChartInfoHydrationAllCurrentSnapshot snapshot = null;
        if (result != null
            && result.Succeeded
            && result.OwnerCount > 0
            && result.BackfillCandidateOwnerCount <= 0)
        {
            snapshot = new ChartInfoHydrationAllCurrentSnapshot
            {
                OwnedCollectionVersion = ownedCollectionVersion,
                BmsRowsVersion = bmsRowsVersion,
                BmsonRowsVersion = bmsonRowsVersion,
                OwnerCount = result.OwnerCount,
                CurrentChartInfoOwnerCount = result.CurrentChartInfoOwnerCount,
                CurrentParseFailureOwnerCount = result.CurrentParseFailureOwnerCount,
                ParserVersion = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                ParseTimeoutMs = Math.Max(0L, (long)Math.Ceiling(buildService.CurrentParseTimeout.TotalMilliseconds))
            };
        }
        lock (hydrationGate)
        {
            hydrationAllCurrentSnapshot = snapshot;
        }
    }

    internal ChartInfoHydrationResult CreateCurrentHydrationAllCurrentResult()
    {
        ChartInfoHydrationAllCurrentSnapshot snapshot;
        lock (hydrationGate)
        {
            snapshot = hydrationAllCurrentSnapshot;
        }
        StorageRowsVersionSnapshot rows = workflowStorageRowsOwner.CaptureVersionSnapshot();
        if (snapshot == null
            || snapshot.OwnedCollectionVersion != workflowOwnedCollectionOwner.CollectionVersion
            || snapshot.BmsRowsVersion != rows.BmsRowsVersion
            || snapshot.BmsonRowsVersion != rows.BmsonRowsVersion
            || snapshot.ParserVersion != BmsLibraryDbGateway.CurrentChartInfoParserVersion
            || snapshot.ParseTimeoutMs != Math.Max(0L, (long)Math.Ceiling(buildService.CurrentParseTimeout.TotalMilliseconds)))
        {
            return null;
        }
        return new ChartInfoHydrationResult
        {
            Succeeded = true,
            OwnerCount = snapshot.OwnerCount,
            CurrentChartInfoOwnerCount = snapshot.CurrentChartInfoOwnerCount,
            CurrentParseFailureOwnerCount = snapshot.CurrentParseFailureOwnerCount,
            BackfillCandidateOwnerCount = 0
        };
    }

    private void EnsureWorkflowConfigured()
    {
        if (workflowDbGateway == null
            || workflowMutationOwner == null
            || workflowStorageRowsOwner == null
            || workflowOwnedCollectionOwner == null)
        {
            throw new InvalidOperationException("Chart-info workflow owner is not configured.");
        }
    }

    private void PublishWorkflowEvent(CatalogChartInfoOwnerEvent ownerEvent)
    {
        workflowEvent?.Invoke(ownerEvent);
    }

    internal void PublishWarningPresentationChanged(string reason)
    {
        PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Warning(reason));
    }

    internal LR2SongDBExtended.chart_info ResolveChartInfo(
        string sha256,
        string md5,
        Func<bool> shouldLazyLoad,
        Action<string> lazyLoad)
    {
        LR2SongDBExtended.chart_info row = ResolveChartInfoFromIndex(sha256, md5);
        if (row == null && shouldLazyLoad?.Invoke() == true)
        {
            lazyLoad?.Invoke("resolve_chart_info");
            row = ResolveChartInfoFromIndex(sha256, md5);
        }
        return row;
    }

    internal LR2SongDBExtended.chart_info ResolveChartInfo(string sha256, string md5)
    {
        return ResolveChartInfo(
            sha256,
            md5,
            ShouldLazyLoadDisplayIndex,
            EnsureDisplayIndexLoadedForLazyResolve);
    }

    internal bool ShouldLazyLoadDisplayIndex()
    {
        lock (indexGate)
        {
            if (chartInfoIndexHydratedValue || displayIndexLoaded)
            {
                return false;
            }
        }
        return CreateCurrentHydrationAllCurrentResult() != null;
    }

    internal void EnsureDisplayIndexLoadedForLazyResolve(string reason)
    {
        EnsureWorkflowConfigured();
        if (!ShouldLazyLoadDisplayIndex())
        {
            return;
        }
        lock (lazyDisplayIndexGate)
        {
            if (!ShouldLazyLoadDisplayIndex())
            {
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            LogPerformance?.Invoke("chart_info_lazy_display_index_load start reason=" + (reason ?? "unknown"));
            try
            {
                ChartInfoHydrationLoadResult loadResult = LoadHydrationData(
                    workflowDbGateway,
                    buildService.CurrentParseTimeout);
                ChartInfoIndexUpdateResult indexUpdateResult = UpsertIndex(
                    loadResult.ChartInfoBySha256.Values,
                    "lazy_display_index",
                    dispatchPresentation: false);
                lock (indexGate)
                {
                    displayIndexLoaded = true;
                }
                stopwatch.Stop();
                LogPerformance?.Invoke("chart_info_lazy_display_index_load done reason=" + (reason ?? "unknown")
                    + " rows=" + loadResult.ChartInfoRows
                    + " dbMode=" + (string.IsNullOrWhiteSpace(loadResult.MaterializeMode) ? "unknown" : loadResult.MaterializeMode)
                    + " dbLoadMs=" + loadResult.DbReadMs
                    + " rawRows=" + loadResult.RawRows
                    + " rawReadMs=" + loadResult.RawReadMs
                    + " rawObjectMs=" + loadResult.RawObjectMs
                    + " indexVersion=" + indexUpdateResult.Version
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                ClearHydrationAllCurrentSnapshot("lazy_display_index_load_failed");
                workflowLogWarning?.Invoke("chart_info_lazy_display_index_load failed reason=" + (reason ?? "unknown")
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " message=" + ex.Message.Replace(Environment.NewLine, " | "));
            }
        }
    }

    internal Func<BMSFile, LR2SongDBExtended.chart_info> CreateLr2ResolverSnapshot()
    {
        Dictionary<string, LR2SongDBExtended.chart_info> bySha256;
        Dictionary<string, LR2SongDBExtended.chart_info> byMd5;
        lock (indexGate)
        {
            bySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(
                indexBySha256
                    .Where(pair => IsCurrentChartInfoRow(pair.Value))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            byMd5 = indexByMd5
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null && pair.Value.Count > 0)
                .Select(pair => new
                {
                    pair.Key,
                    Row = pair.Value.Values.FirstOrDefault(IsCurrentChartInfoRow)
                })
                .Where(pair => pair.Row != null)
                .ToDictionary(pair => pair.Key, pair => pair.Row, StringComparer.OrdinalIgnoreCase);
        }
        return row =>
        {
            if (row == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(row.sha256)
                && bySha256.TryGetValue(row.sha256, out LR2SongDBExtended.chart_info bySha256Row))
            {
                return bySha256Row;
            }
            if (!string.IsNullOrWhiteSpace(row.hash)
                && byMd5.TryGetValue(row.hash, out LR2SongDBExtended.chart_info byMd5Row))
            {
                return byMd5Row;
            }
            return null;
        };
    }

    internal ChartInfoOwnerVersionSnapshot CaptureOwnerVersionSnapshot()
    {
        EnsureWorkflowConfigured();
        using (workflowStorageRowsOwner.WriteGate.GetReaderGuard())
        {
            CatalogStorageRowsStateSnapshot rows = workflowStorageRowsOwner.CaptureStateSnapshot();
            lock (workflowOwnedCollectionOwner.Gate)
            {
                return new ChartInfoOwnerVersionSnapshot
                {
                    OwnedCollectionVersion = workflowOwnedCollectionOwner.CollectionVersion,
                    BmsRowsVersion = rows.BmsRowsVersion,
                    BmsonRowsVersion = rows.BmsonRowsVersion,
                    BmsOwnerCount = rows.BmsRowCount,
                    BmsonOwnerCount = rows.BmsonRowCount
                };
            }
        }
    }

    internal ChartInfoIndexUpdateResult ReplaceIndex(
        IEnumerable<LR2SongDBExtended.chart_info> rows,
        bool hydrated)
    {
        var bySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        var byMd5 = new Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>>(StringComparer.OrdinalIgnoreCase);
        int inputRows = 0;
        foreach (LR2SongDBExtended.chart_info row in rows ?? [])
        {
            if (!TryGetSha256(row, out string sha256))
            {
                continue;
            }
            inputRows++;
            bySha256[sha256] = row;
            AddMd5Candidate(byMd5, row, sha256);
        }
        var result = new ChartInfoIndexUpdateResult
        {
            InputRows = inputRows,
            BySha256Count = bySha256.Count,
            ByMd5Count = byMd5.Count
        };
        lock (indexGate)
        {
            indexBySha256 = bySha256;
            indexByMd5 = byMd5;
            result.HydrationChanged = chartInfoIndexHydratedValue != hydrated;
            chartInfoIndexHydratedValue = hydrated;
            displayIndexLoaded = hydrated;
            chartInfoIndexVersionValue++;
            result.Version = chartInfoIndexVersionValue;
        }
        propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoIndexVersion));
        if (result.HydrationChanged)
        {
            propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoIndexHydrated));
        }
        PublishWorkflowEvent(CatalogChartInfoOwnerEvent.IndexChanged("chart_info_index_snapshot"));
        return result;
    }

    internal ChartInfoIndexUpdateResult UpsertIndex(
        IEnumerable<LR2SongDBExtended.chart_info> rows,
        string reason,
        bool dispatchPresentation = true,
        bool publishEffects = true)
    {
        List<LR2SongDBExtended.chart_info> rowList = [.. (rows ?? [])
            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.sha256))];
        if (rowList.Count == 0)
        {
            return new ChartInfoIndexUpdateResult();
        }
        var result = new ChartInfoIndexUpdateResult { InputRows = rowList.Count };
        lock (indexGate)
        {
            foreach (LR2SongDBExtended.chart_info row in rowList)
            {
                if (!TryGetSha256(row, out string sha256))
                {
                    continue;
                }
                if (indexBySha256.TryGetValue(sha256, out LR2SongDBExtended.chart_info previousRow))
                {
                    RemoveMd5Candidate(indexByMd5, previousRow, sha256);
                }
                indexBySha256[sha256] = row;
                AddMd5Candidate(indexByMd5, row, sha256);
            }
            chartInfoIndexVersionValue++;
            result.Version = chartInfoIndexVersionValue;
            result.BySha256Count = indexBySha256.Count;
            result.ByMd5Count = indexByMd5.Count;
        }
        if (publishEffects)
        {
            PublishIndexUpsertEffects(result, rowList.Count, reason, dispatchPresentation);
        }
        return result;
    }

    internal void PublishIndexUpsertEffects(
        ChartInfoIndexUpdateResult result,
        int upsertedRowCount,
        string reason,
        bool dispatchPresentation)
    {
        propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoIndexVersion));
        if (dispatchPresentation)
        {
            PublishWorkflowEvent(CatalogChartInfoOwnerEvent.IndexChanged("chart_info_index_delta"));
        }
        logPerformance?.Invoke("chart_info_index_delta upserted=" + upsertedRowCount
            + " bySha256=" + result.BySha256Count
            + " byMd5=" + result.ByMd5Count
            + " version=" + result.Version
            + " reason=" + (reason ?? "unknown"));
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> CreateSha256Snapshot()
    {
        lock (indexGate)
        {
            if (!chartInfoIndexHydratedValue)
            {
                return null;
            }
            return new Dictionary<string, LR2SongDBExtended.chart_info>(indexBySha256, StringComparer.OrdinalIgnoreCase);
        }
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfoMapSnapshot(Func<Dictionary<string, LR2SongDBExtended.chart_info>> loader)
    {
        return loader?.Invoke();
    }

    internal bool IsIndexHydrated
    {
        get { lock (indexGate) return chartInfoIndexHydratedValue; }
    }

    private LR2SongDBExtended.chart_info ResolveChartInfoFromIndex(string sha256, string md5)
    {
        lock (indexGate)
        {
            if (!string.IsNullOrWhiteSpace(sha256)
                && indexBySha256.TryGetValue(sha256, out LR2SongDBExtended.chart_info bySha256))
            {
                return bySha256;
            }
            if (!string.IsNullOrWhiteSpace(md5)
                && indexByMd5.TryGetValue(md5, out SortedDictionary<string, LR2SongDBExtended.chart_info> candidates)
                && candidates.Count > 0)
            {
                return candidates.First().Value;
            }
        }
        return null;
    }

    private static bool TryGetSha256(LR2SongDBExtended.chart_info row, out string sha256)
    {
        sha256 = row?.sha256?.Trim();
        if (string.IsNullOrWhiteSpace(sha256))
        {
            sha256 = null;
            return false;
        }
        return true;
    }

    private static bool TryGetMd5(LR2SongDBExtended.chart_info row, out string md5)
    {
        md5 = row?.md5?.Trim();
        if (string.IsNullOrWhiteSpace(md5))
        {
            md5 = null;
            return false;
        }
        return true;
    }

    private static bool IsCurrentChartInfoRow(LR2SongDBExtended.chart_info row)
    {
        return row != null && row.parser_version >= BmsLibraryDbGateway.CurrentChartInfoParserVersion;
    }

    private static void AddMd5Candidate(
        IDictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> byMd5,
        LR2SongDBExtended.chart_info row,
        string sha256)
    {
        if (!TryGetMd5(row, out string md5) || string.IsNullOrWhiteSpace(sha256))
        {
            return;
        }
        if (!byMd5.TryGetValue(md5, out SortedDictionary<string, LR2SongDBExtended.chart_info> candidates))
        {
            candidates = new(StringComparer.OrdinalIgnoreCase);
            byMd5[md5] = candidates;
        }
        candidates[sha256] = row;
    }

    private static void RemoveMd5Candidate(
        IDictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>> byMd5,
        LR2SongDBExtended.chart_info row,
        string sha256)
    {
        if (!TryGetMd5(row, out string md5)
            || !byMd5.TryGetValue(md5, out SortedDictionary<string, LR2SongDBExtended.chart_info> candidates))
        {
            return;
        }
        candidates.Remove(sha256);
        if (candidates.Count == 0)
        {
            byMd5.Remove(md5);
        }
    }

    internal void QueueHydration(
        string reason,
        bool queueBackfillAfterHydration,
        Func<Task> process)
    {
        if (TrySkip("chart_info_hydration", reason))
        {
            return;
        }

        int requestVersion;
        bool shouldStartWorker = false;
        lock (hydrationGate)
        {
            hydrationRequestedVersion++;
            requestVersion = hydrationRequestedVersion;
            hydrationPending = true;
            hydrationPendingReason = reason;
            hydrationPendingQueueBackfill = hydrationPendingQueueBackfill || queueBackfillAfterHydration;
            if (!hydrationRunning)
            {
                hydrationRunning = true;
                shouldStartWorker = true;
            }
        }
        ChartInfoHydrationRequestedVersion = requestVersion;
        ChartInfoHydrationTotalCount = 0;
        ChartInfoHydrationAppliedCount = 0;
        ChartInfoHydrationRunning = true;
        LogPerformance?.Invoke("chart_info_hydration queue reason=" + (reason ?? "unknown")
            + " version=" + requestVersion
            + " queueBackfill=" + queueBackfillAfterHydration.ToString().ToLowerInvariant());
        if (!shouldStartWorker || process == null)
        {
            return;
        }

        Func<string, string, string, Func<Task>, bool> scheduler = SchedulerProvider?.Invoke();
        if (scheduler != null)
        {
            if (scheduler("chart_info_hydration", reason ?? "queue", null, process))
            {
                return;
            }
            CompleteHydrationForShutdown("startup_scheduler_rejected");
            return;
        }
        if (IsShutdownRequested)
        {
            CompleteHydrationForShutdown("shutdown_requested");
            return;
        }
        Task.Run(process).ObserveFault("ProcessDeferredChartInfoHydrationRequests");
    }

    internal void CompleteHydrationForShutdown(string reason)
    {
        int requestVersion;
        lock (hydrationGate)
        {
            requestVersion = hydrationRequestedVersion;
            hydrationPending = false;
            hydrationPendingReason = null;
            hydrationPendingQueueBackfill = false;
            hydrationRunning = false;
        }
        ChartInfoHydrationCompletedVersion = requestVersion;
        ChartInfoHydrationRunning = false;
        LogPerformance?.Invoke("chart_info_hydration skipped version=" + requestVersion
            + " reason=" + (reason ?? "shutdown_requested"));
    }

    internal void TryImportMetadataBundle(
        string baseDirectoryPath,
        BmsLibraryDbGateway dbGateway)
    {
        ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(
            baseDirectoryPath,
            dbGateway,
            LogPerformance);
    }

    internal void RemoveParseFailuresByMd5(IEnumerable<string> md5s)
    {
        EnsureWorkflowConfigured();
        string[] normalizedMd5s = NormalizeParseFailureMd5s(md5s);
        if (normalizedMd5s.Length == 0)
        {
            return;
        }

        CatalogChartInfoWriteReceipt receipt = workflowMutationOwner.ApplyChartInfoWrite(
            new CatalogChartInfoWriteRequest(parseFailureDeleteMd5s: normalizedMd5s));
        if (!receipt.Applied)
        {
            throw new InvalidOperationException("Chart-info parse-failure delete returned no receipt.");
        }
        PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Warning("chart_info_parse_failure_remove"));
    }

    internal static string[] NormalizeParseFailureMd5s(IEnumerable<string> md5s)
    {
        return [.. (md5s ?? [])
            .Where(md5 => !string.IsNullOrWhiteSpace(md5))
            .Select(md5 => md5.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> LoadCurrentParseFailureMap(
        BmsLibraryDbGateway dbGateway,
        TimeSpan parseTimeout)
    {
        return dbGateway?.LoadCurrentChartInfoParseFailureMap(parseTimeout)
            ?? new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase);
    }

    internal ChartInfoHydrationLoadResult LoadHydrationData(
        BmsLibraryDbGateway dbGateway,
        TimeSpan parseTimeout)
    {
        return dbGateway?.LoadChartInfoHydrationData(parseTimeout) ?? new ChartInfoHydrationLoadResult();
    }

    internal ChartInfoBackfillCandidateSummary GetBackfillCandidateSummary(
        BmsLibraryDbGateway dbGateway,
        TimeSpan parseTimeout)
    {
        return dbGateway?.GetChartInfoBackfillCandidateSummary(parseTimeout);
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfoMap(
        BmsLibraryDbGateway dbGateway)
    {
        return dbGateway?.LoadChartInfoMap()
            ?? new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfosBySha256(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<string> sha256s)
    {
        return dbGateway?.LoadChartInfosBySha256(sha256s)
            ?? new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
    }

    internal Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfosByMd5(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<string> md5s)
    {
        return dbGateway?.LoadChartInfosByMd5(md5s)
            ?? new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 所有済み譜面のchart-infoを構築し、確定したdigestとsession indexを反映します。
    /// </summary>
    /// <param name="reason">ログと通知の理由。</param>
    /// <param name="charts">構築対象の譜面。</param>
    /// <param name="deferPublication">DBとindex更新後の公開処理を呼び出し元の解放境界へ渡すcallback。</param>
    /// <param name="logOverride">通常ログの任意override。</param>
    /// <param name="warningLogOverride">警告ログの任意override。</param>
    /// <returns>構築結果と確定したdigest facts。</returns>
    internal ChartInfoInlineBuildResult BuildInline(
        string reason,
        IEnumerable<ChartFile> charts,
        Action<Action> deferPublication = null,
        Action<string> logOverride = null,
        Action<string> warningLogOverride = null)
    {
        EnsureWorkflowConfigured();
        Action<string> effectiveLog = logOverride ?? LogPerformance;
        Action<string> effectiveWarningLog = warningLogOverride ?? workflowLogWarning;
        List<ChartFile> targetCharts = [.. (charts ?? []).Where(chart => chart != null)];
        var result = new ChartInfoInlineBuildResult();
        if (targetCharts.Count == 0)
        {
            effectiveLog?.Invoke("chart_info_inline_install reason=" + (reason ?? "unknown")
                + " target=0 success=0 currentSkipped=0 failureSkipped=0 parseFailed=0 failurePersisted=0 failureCleared=0 readFailed=0 parseMs=0");
            return result;
        }

        result = inlineBuildService.BuildForExistingCharts(
            workflowDbGateway,
            targetCharts,
            effectiveLog,
            effectiveWarningLog);
        List<BMSFile> bmsRows = [.. result.StorageApplications
            .Select(application => application.CreateBmsPersistenceCopy())
            .Where(row => row != null)];
        List<LR2SongDBExtended.bmson_song> bmsonRows = [.. result.StorageApplications
            .Select(application => application.CreateBmsonPersistenceCopy())
            .Where(row => row != null)];
        CatalogChartInfoStorageWriteReceipt writeReceipt = workflowMutationOwner.ApplyChartInfoStorageWrite(
            new CatalogChartInfoStorageWriteRequest(
                bmsRows,
                bmsonRows,
                new CatalogChartInfoWriteRequest(
                    chartInfoRows: result.ChartInfoRows,
                    parseFailureRows: result.ParseFailureRows,
                    parseFailureDeleteMd5s: result.ParseFailureDeleteMd5s)));
        if ((bmsRows.Count > 0
            || bmsonRows.Count > 0
            || result.ChartInfoRows.Count > 0
            || result.ParseFailureRows.Count > 0
            || result.ParseFailureDeleteMd5s.Count > 0)
            && !writeReceipt.Applied)
        {
            throw new InvalidOperationException("Inline chart-info persistence returned no receipt.");
        }
        int songRowChartInfoApplied = 0;
        foreach (ChartInfoStorageApplication application in result.StorageApplications)
        {
            songRowChartInfoApplied += application.ApplyCommitted(result.DigestChanges);
        }
        PublishCommittedStorageApplication(
            result.DigestChanges,
            result.AppliedRows,
            result.ParseFailureRows.Count > 0 || result.ParseFailureDeleteMd5s.Count > 0,
            reason ?? "install_package_inline",
            deferPublication);
        effectiveLog?.Invoke(
            "chart_info_inline_install owner_applied=" + songRowChartInfoApplied
            + " target=" + result.TargetCount
            + " success=" + result.SuccessCount
            + " currentSkipped=" + result.CurrentSkippedCount
            + " failureSkipped=" + result.FailureSkippedCount
            + " parseFailed=" + result.ParseFailedCount
            + " failurePersisted=" + result.FailurePersistedCount
            + " failureCleared=" + result.FailureClearedCount
            + " readFailed=" + result.ReadFailedCount
            + " parseMs=" + result.ParseMs);
        return result;
    }

    /// <summary>
    /// Publishes facts from a successful chart-info storage receipt in catalog dependency order.
    /// Digest-derived lookup state is prepared before the chart-info session index, and the
    /// prepared common effects are published only after the session index has been updated.
    /// </summary>
    private void PublishCommittedStorageApplication(
        IEnumerable<LibraryChartDigestChange> digestChanges,
        IReadOnlyList<LR2SongDBExtended.chart_info> appliedRows,
        bool parseFailureChanged,
        string reason,
        Action<Action> deferPublication = null)
    {
        LibraryChartDigestChange[] committedDigestChanges = [.. (digestChanges ?? [])
            .Where(change => change != null)];
        LR2SongDBExtended.chart_info[] committedRows = [.. (appliedRows ?? [])
            .Where(row => row != null)];

        Action digestPublication = null;
        if (committedDigestChanges.Length > 0)
        {
            CatalogDigestMutationRequest digestRequest =
                workflowMutationOwner.CreateDigestMutationRequest(committedDigestChanges);
            CatalogDigestMutationReceipt digestReceipt = workflowMutationOwner.ApplyDigestMutation(digestRequest);
            if (!digestReceipt.Applied)
            {
                throw new InvalidOperationException("Committed chart-info digest mutation returned no receipt.");
            }
            digestPublication = workflowPrepareDigestPublication?.Invoke(
                committedDigestChanges,
                reason + "_digest");
        }

        if (committedRows.Length > 0)
        {
            ChartInfoIndexUpdateResult indexResult = UpsertIndex(
                committedRows,
                reason,
                publishEffects: deferPublication == null);
            if (deferPublication != null)
            {
                deferPublication(() => PublishIndexUpsertEffects(
                    indexResult,
                    committedRows.Length,
                    reason,
                    dispatchPresentation: true));
            }
        }

        if (parseFailureChanged)
        {
            var warningEvent = CatalogChartInfoOwnerEvent.Warning(
                reason + "_parse_failure");
            if (deferPublication == null)
            {
                PublishWorkflowEvent(warningEvent);
            }
            else
            {
                deferPublication(() => PublishWorkflowEvent(warningEvent));
            }
        }
        if (digestPublication != null)
        {
            if (deferPublication == null)
            {
                digestPublication();
            }
            else
            {
                deferPublication(digestPublication);
            }
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        internal static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private void Set<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        propertyChanged?.Invoke(propertyName);
    }
}
