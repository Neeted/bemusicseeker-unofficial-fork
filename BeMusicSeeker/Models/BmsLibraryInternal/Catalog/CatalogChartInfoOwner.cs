using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>受付時の表示識別を既存要求版とともに捕捉します。</summary>
    internal Func<string, long, OperationProgressRequest> ProgressRequestFactory { get; set; }

    /// <summary>受付時の表示通知先を捕捉します。</summary>
    internal Action<OperationProgressRequest, bool> RequestProgressReporter { get; set; }

    private OperationProgressRequest backfillProgressRequest;
    private OperationProgressRequest hydrationProgressRequest;
    private Action<OperationProgressRequest, bool> backfillExecutionReporter;
    private Action<OperationProgressRequest, bool> hydrationExecutionReporter;

    /// <summary>直近の補完要求に捕捉した発生元です。</summary>
    internal OperationProgressRequest BackfillProgressRequest { get { lock (backfillGate) { return backfillProgressRequest; } } }

    /// <summary>直近の読込み要求に捕捉した発生元です。</summary>
    internal OperationProgressRequest HydrationProgressRequest { get { lock (hydrationGate) { return hydrationProgressRequest; } } }

    private readonly ChartInfoBuildService buildService;

    private readonly ChartInfoInlineBuildService inlineBuildService;

    private ChartInfoWorkflowProgressSnapshot backfillProgressSnapshot = new(0, 0, 0, string.Empty);

    private ChartInfoWorkflowProgressSnapshot hydrationProgressSnapshot = new(0, 0, 0, string.Empty);

    /// <summary>補完workerが捕捉した要求版と件数を、一つの変更不能な値で返します。</summary>
    internal ChartInfoWorkflowProgressSnapshot BackfillProgressSnapshot => Volatile.Read(ref backfillProgressSnapshot);

    /// <summary>読込みworkerが捕捉した要求版と適用数を、一つの変更不能な値で返します。</summary>
    internal ChartInfoWorkflowProgressSnapshot HydrationProgressSnapshot => Volatile.Read(ref hydrationProgressSnapshot);

    private readonly Action<string> propertyChanged;

    private readonly Func<bool> isShutdownRequested;

    private readonly Func<string, string, bool> trySkipForShutdown;

    private readonly Func<Func<string, string, string, Func<Task>, bool>> startupBackgroundTaskSchedulerProvider;

    private readonly Action<string> logPerformance;

    private readonly ChartFileOperationSynchronizer operationAdmission;

    private BmsLibraryDbGateway workflowDbGateway;

    private CatalogMutationOwner workflowMutationOwner;


    private CatalogOwnedCollectionOwner workflowOwnedCollectionOwner;

    private Action<string> workflowLogWarning;

    private Action<CatalogChartInfoOwnerEvent> workflowEvent;

    private Func<IDisposable> workflowBeginDigestMutationWindow = () => EmptyDisposable.Instance;

    private Func<IReadOnlyList<LibraryChartDigestChange>, string, CatalogCommitReceipt, Action> workflowPrepareDigestPublication;

    private readonly object backfillGate = new();

    private readonly List<ChartInfoBackfillRequest> backfillRequests = [];
    private int inputCoverageParserVersion;
    private long inputCoverageTimeoutMs;

    private void CaptureInputCoverageConditions()
    {
        inputCoverageParserVersion = BmsLibraryDbGateway.CurrentChartInfoParserVersion;
        inputCoverageTimeoutMs = Math.Max(0L, (long)Math.Ceiling(buildService.CurrentParseTimeout.TotalMilliseconds));
    }

    private readonly object hydrationGate = new();

    private readonly object indexGate = new();

    private readonly object lazyDisplayIndexGate = new();

    private Dictionary<string, BeMusicSeeker.Models.ChartDetails> indexBySha256 =
        new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, SortedDictionary<string, BeMusicSeeker.Models.ChartDetails>> indexByMd5 =
        new(StringComparer.OrdinalIgnoreCase);

    private bool displayIndexLoaded;

    private bool hydrationRunning;

    private bool hydrationPending;

    private string hydrationPendingReason;

    private bool hydrationPendingQueueBackfill;

    private int hydrationRequestedVersion;

    private int chartInfoBackfillRequestedVersion;

    private int chartInfoBackfillCompletedVersion;

    private TaskCompletionSource<bool> hydrationCompletion;

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

    /// <summary>推定入力の必須背景更新を、構成が共有する論理受付へ接続します。</summary>
    /// <param name="operationAdmission">先行操作の実終端を非同期で待つ共有受付。</param>
    /// <param name="buildService">構成時に共有する実解析・保存service。省略時は既定の実装を使用します。</param>
    internal CatalogChartInfoOwner(
        Action<string> propertyChanged,
        Func<bool> isShutdownRequested,
        Func<string, string, bool> trySkipForShutdown,
        Func<Func<string, string, string, Func<Task>, bool>> startupBackgroundTaskSchedulerProvider,
        Action<string> logPerformance,
        ChartFileOperationSynchronizer operationAdmission = null,
        ChartInfoBuildService buildService = null)
    {
        this.propertyChanged = propertyChanged;
        this.isShutdownRequested = isShutdownRequested ?? (() => false);
        this.trySkipForShutdown = trySkipForShutdown ?? ((_, _) => false);
        this.startupBackgroundTaskSchedulerProvider = startupBackgroundTaskSchedulerProvider;
        this.logPerformance = logPerformance;
        this.operationAdmission = operationAdmission ?? new ChartFileOperationSynchronizer();
        this.buildService = buildService ?? new ChartInfoBuildService();
        inlineBuildService = new(
            this.buildService,
            FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree());
    }

    /// <summary>
    /// chart-infoの永続化と、commit済みdigest factsの公開処理を接続します。
    /// </summary>
    /// <param name="dbGateway">chart-infoを書き込むDB gateway。</param>
    /// <param name="mutationOwner">catalogの永続化を所有するmutation owner。</param>
    /// <param name="ownedCollectionOwner">所持譜面の正本owner。</param>
    /// <param name="logWarning">警告ログ出力。</param>
    /// <param name="workflowEvent">chart-info表示状態の通知。</param>
    /// <param name="beginDigestMutationWindow">digest操作の入力mutation window。</param>
    /// <param name="prepareDigestPublication">commit済みdigest factsからcommon effects公開処理を準備するcallback。</param>
    internal void ConfigureWorkflow(
        BmsLibraryDbGateway dbGateway,
        CatalogMutationOwner mutationOwner,
        CatalogOwnedCollectionOwner ownedCollectionOwner,
        Action<string> logWarning,
        Action<CatalogChartInfoOwnerEvent> workflowEvent,
        Func<IDisposable> beginDigestMutationWindow = null,
        Func<IReadOnlyList<LibraryChartDigestChange>, string, CatalogCommitReceipt, Action> prepareDigestPublication = null)
    {
        workflowDbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        workflowMutationOwner = mutationOwner ?? throw new ArgumentNullException(nameof(mutationOwner));
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

    internal Dictionary<string, BeMusicSeeker.Models.ChartDetails> IndexBySha256
    {
        get => indexBySha256;
        set => indexBySha256 = value ?? new(StringComparer.OrdinalIgnoreCase);
    }

    internal Dictionary<string, SortedDictionary<string, BeMusicSeeker.Models.ChartDetails>> IndexByMd5
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
        get
        {
            lock (hydrationGate)
            {
                return hydrationRunning;
            }
        }
        set
        {
            lock (hydrationGate)
            {
                hydrationRunning = value;
            }
        }
    }

    internal bool HydrationPending
    {
        get
        {
            lock (hydrationGate)
            {
                return hydrationPending;
            }
        }
        set
        {
            lock (hydrationGate)
            {
                hydrationPending = value;
            }
        }
    }

    internal string HydrationPendingReason
    {
        get
        {
            lock (hydrationGate)
            {
                return hydrationPendingReason;
            }
        }
        set
        {
            lock (hydrationGate)
            {
                hydrationPendingReason = value;
            }
        }
    }

    internal bool HydrationPendingQueueBackfill
    {
        get
        {
            lock (hydrationGate)
            {
                return hydrationPendingQueueBackfill;
            }
        }
        set
        {
            lock (hydrationGate)
            {
                hydrationPendingQueueBackfill = value;
            }
        }
    }

    internal int HydrationRequestedVersionState
    {
        get
        {
            lock (hydrationGate)
            {
                return hydrationRequestedVersion;
            }
        }
        set
        {
            lock (hydrationGate)
            {
                hydrationRequestedVersion = value;
            }
        }
    }

    internal int ChartInfoBackfillRequestedVersionState
    {
        get
        {
            lock (backfillGate)
            {
                return chartInfoBackfillRequestedVersion;
            }
        }
        set
        {
            lock (backfillGate)
            {
                chartInfoBackfillRequestedVersion = value;
            }
        }
    }

    internal int ChartInfoBackfillCompletedVersionState
    {
        get
        {
            lock (backfillGate)
            {
                return chartInfoBackfillCompletedVersion;
            }
        }
        set
        {
            lock (backfillGate)
            {
                chartInfoBackfillCompletedVersion = value;
            }
        }
    }

    internal ChartInfoHydrationAllCurrentSnapshot HydrationAllCurrentSnapshot
    {
        get
        {
            lock (hydrationGate)
            {
                return hydrationAllCurrentSnapshot;
            }
        }
        set
        {
            lock (hydrationGate)
            {
                hydrationAllCurrentSnapshot = value;
            }
        }
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
        get
        {
            lock (indexGate)
            {
                return chartInfoIndexVersionValue;
            }
        }
        set
        {
            lock (indexGate)
            {
                chartInfoIndexVersionValue = value;
            }
        }
    }

    internal bool ChartInfoIndexHydrated
    {
        get
        {
            lock (indexGate)
            {
                return chartInfoIndexHydratedValue;
            }
        }
        set
        {
            lock (indexGate)
            {
                chartInfoIndexHydratedValue = value;
            }
        }
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
            hydrationCompletion?.TrySetResult(true);
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

    /// <summary>必須出力に使う索引を同じ親操作内で読込み、DB失敗を隠さず返します。後続補完は解禁後に実行します。</summary>
    internal RequiredChartInfoHydrationResult HydrateRequiredForStartup(string reason, OperationProgressRequest originatingRequest)
    {
        EnsureWorkflowConfigured();
        int version = Interlocked.Increment(ref hydrationRequestedVersion);
        OperationProgressRequest request = originatingRequest == null
            ? ProgressRequestFactory?.Invoke("chart_info_hydration", version) ?? new(0, 0, "chart_info_hydration", version)
            : new(originatingRequest.Generation, originatingRequest.OperationToken, "chart_info_hydration", version);
        lock (hydrationGate) { hydrationProgressRequest = request; }
        ChartInfoHydrationRequestedVersion = version;
        RequestProgressReporter?.Invoke(request, true);
        ChartInfoHydrationRunning = true;
        try
        {
            ChartInfoHydrationResult result = HydrateChartInfos(reason, propagateLoadFailure: true);
            ChartInfoHydrationTotalCount = result.TotalRows;
            ChartInfoHydrationAppliedCount = result.AppliedBmsCount + result.AppliedBmsonCount;
            PublishProgressSnapshot(ref hydrationProgressSnapshot,
                new(version, result.TotalRows, ChartInfoHydrationAppliedCount, string.Empty, request),
                nameof(BMSLibrary.ChartInfoHydrationProgressSnapshot));
            ChartInfoHydrationCompletedVersion = version;
            return new(result.Succeeded, result.OwnerCount, result.BackfillCandidateOwnerCount, request);
        }
        finally
        {
            ChartInfoHydrationRunning = false;
            RequestProgressReporter?.Invoke(request, false);
        }
    }

    /// <summary>必須読込みの結果を既存補完の後続へ渡し、候補要約のDB読取りも親受付解放後に実行します。</summary>
    internal void ScheduleRequiredHydrationBackfill(string reason, RequiredChartInfoHydrationResult result)
    {
        if (TrySkip("chart_info_backfill", reason)) { return; }
        Func<Task> acceptedWork = async () =>
        {
            await WaitForHydrationIdleAsync().ConfigureAwait(false);
            using IDisposable lease = await operationAdmission.EnterAcceptedBackgroundAsync().ConfigureAwait(false);
            if (TrySkip("chart_info_backfill", reason)) { return; }
            QueueBackfill(reason, processSynchronously: true,
                hydrationResult: new() { Succeeded = result.Succeeded, OwnerCount = result.OwnerCount, BackfillCandidateOwnerCount = result.CandidateOwnerCount },
                originatingRequest: result.ProgressRequest);
        };
        Func<string, string, string, Func<Task>, bool> scheduler = SchedulerProvider?.Invoke();
        if (scheduler != null)
        {
            // 既存の補完laneへ直接登録する。同じlaneの完了へ依存する追加stageは持たない。
            scheduler("chart_info_backfill", reason ?? "required_hydration", null, acceptedWork);
            return;
        }
        Task.Run(acceptedWork).ObserveFault("ProcessChartInfoBackfillAfterRequiredHydration");
    }

    internal void EnsureHydratedForLr2(string reason)
    {
        EnsureWorkflowConfigured();
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

    /// <summary>受理済み読込みの終端を、論理受付を取得する前に非同期で待ちます。</summary>
    private async Task WaitForHydrationIdleAsync()
    {
        while (true)
        {
            Task completion;
            lock (hydrationGate)
            {
                if (!hydrationRunning) { return; }
                completion = hydrationCompletion.Task;
            }
            await completion.ConfigureAwait(false);
        }
    }

    /// <summary>統合パイプラインの補完要求を受付け、実行周ごとに発生元を保持します。候補取得・必要処理の失敗は呼出元へ返します。</summary>
    /// <param name="reason">既存の受付理由。</param>
    /// <param name="processSynchronously">既存の同期補完許可。</param>
    /// <param name="hydrationResult">直前の読込みによる既存の解析要否情報。</param>
    /// <param name="originatingRequest">読込みから起こす補完の表示発生元。機能要求版は補完側で発行します。</param>
    internal void QueueBackfill(
        string reason,
        bool processSynchronously = false,
        ChartInfoHydrationResult hydrationResult = null, OperationProgressRequest originatingRequest = null)
    {
        EnsureWorkflowConfigured();
        ChartInfoHydrationResult currentAllCurrentResult = null;
        currentAllCurrentResult = CreateCurrentHydrationAllCurrentResult();
        if (currentAllCurrentResult != null)
        {
            int skippedVersion = CompleteSkippedBackfillRequestIfIdle(originatingRequest);
            ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance, () => "chart_info_backfill skipped reason=hydration_all_current"
                + " version=" + skippedVersion
                + " requestReason=" + (reason ?? "unknown")
                + " ownerCount=" + currentAllCurrentResult.OwnerCount
                + " currentChartInfo=" + currentAllCurrentResult.CurrentChartInfoOwnerCount
                + " currentParseFailure=" + currentAllCurrentResult.CurrentParseFailureOwnerCount
                + " candidates=" + currentAllCurrentResult.BackfillCandidateOwnerCount);
            NotifyBackfillCompletionBestEffort(() => PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Checkpoint("chart_info_backfill", "skipped")));
            return;
        }

        if (inputCoverageParserVersion != BmsLibraryDbGateway.CurrentChartInfoParserVersion
            || inputCoverageTimeoutMs != Math.Max(0L, (long)Math.Ceiling(buildService.CurrentParseTimeout.TotalMilliseconds)))
        {
            workflowOwnedCollectionOwner.ResetChartInfoInputCoverage();
        }

        if (workflowOwnedCollectionOwner.TryCapturePendingChartInfoInputs(out IReadOnlyList<ChartFile> changedInputs))
        {
            if (changedInputs.Count == 0)
            {
                CompleteSkippedBackfillRequestIfIdle(originatingRequest);
                NotifyBackfillCompletionBestEffort(() => PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Checkpoint("chart_info_backfill", "skipped")));
                return;
            }
            QueueBackfillRequest(ChartInfoBackfillRequest.Targets(reason,
                System.Array.AsReadOnly(changedInputs.Select(chart => LibraryChartRef.FromChartFile(chart)).ToArray())), processSynchronously, originatingRequest);
            return;
        }

        var candidateSummaryStopwatch = Stopwatch.StartNew();
        ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance, () => "chart_info_backfill candidate_summary_start reason=" + (reason ?? "unknown"));
        ChartInfoBackfillCandidateSummary summary = GetBackfillCandidateSummary(workflowDbGateway, buildService.CurrentParseTimeout);
        candidateSummaryStopwatch.Stop();
        ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance, () => "chart_info_backfill candidate_summary_done reason=" + (reason ?? "unknown")
            + " elapsedMs=" + candidateSummaryStopwatch.ElapsedMilliseconds
            + " bmsOwners=" + summary.BmsOwnerCount
            + " bmsonOwners=" + summary.BmsonOwnerCount
            + " candidates=" + summary.CandidateOwnerCount
            + " missingDigest=" + summary.MissingDigestOwnerCount
            + " missingChartInfo=" + summary.MissingChartInfoOwnerCount
            + " staleChartInfo=" + summary.StaleChartInfoOwnerCount
            + " currentParseFailure=" + summary.CurrentParseFailureOwnerCount
            + " currentChartInfo=" + summary.CurrentChartInfoOwnerCount);
        if (summary.CandidateOwnerCount <= 0)
        {
            int skippedVersion = CompleteSkippedBackfillRequestIfIdle(originatingRequest);
            ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance, () => "chart_info_backfill skipped reason=no_candidates"
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
            NotifyBackfillCompletionBestEffort(() => PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Checkpoint("chart_info_backfill", "skipped")));
            return;
        }
        ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance, () => "chart_info_backfill candidates"
            + " reason=" + (reason ?? "unknown")
            + " bmsOwners=" + summary.BmsOwnerCount
            + " bmsonOwners=" + summary.BmsonOwnerCount
            + " candidates=" + summary.CandidateOwnerCount
            + " missingDigest=" + summary.MissingDigestOwnerCount
            + " missingChartInfo=" + summary.MissingChartInfoOwnerCount
            + " staleChartInfo=" + summary.StaleChartInfoOwnerCount
            + " currentParseFailure=" + summary.CurrentParseFailureOwnerCount
            + " currentChartInfo=" + summary.CurrentChartInfoOwnerCount);
        QueueBackfillRequest(ChartInfoBackfillRequest.Full(reason), processSynchronously, originatingRequest);
    }

    /// <summary>純表示の進捗値を先に確定し、購読者の通常故障で処理結果を変えません。</summary>
    private void PublishProgressSnapshot(
        ref ChartInfoWorkflowProgressSnapshot target,
        ChartInfoWorkflowProgressSnapshot snapshot,
        string propertyName)
    {
        Volatile.Write(ref target, snapshot);
        try
        {
            propertyChanged?.Invoke(propertyName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 既存の純表示購読者の普通故障で保存・取消・終端を変えません。
            ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance,
                () => "chart_info_progress_observer_failed property=" + propertyName + " exception=" + ex.GetType().Name);
        }
    }

    /// <summary>受理済み要求を終端まで処理し、必要処理の最初の通常失敗を所有Taskへ返します。完了版は保存成功を意味しません。</summary>
    private void ProcessBackfillRequests()
    {
        EnsureWorkflowConfigured();
        lock (backfillGate)
        {
            if (!ChartInfoBackfillRunning) { return; }
        }
        ExceptionDispatchInfo firstFailure = null;
        void recordFailure(Exception failure, int version, string phase)
        {
            firstFailure ??= ExceptionDispatchInfo.Capture(failure);
            ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance,
                () => "chart_info_backfill failed version=" + version + " phase=" + phase + " exception=" + failure);
        }
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
                    chartInfoBackfillCompletedVersionValue = shutdownRequestVersion;
                    chartInfoBackfillRunningValue = false;
                    chartInfoBackfillCurrentPathValue = string.Empty;
                }
                NotifyBackfillCompletionBestEffort(() => propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoBackfillRunning)));
                NotifyBackfillCompletionBestEffort(() => propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoBackfillCompletedVersion)));
                NotifyBackfillCompletionBestEffort(() => propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoBackfillCurrentPath)));
                ChartInfoBackfillTotalCount = 0;
                ChartInfoBackfillProcessedCount = 0;
                ChartInfoBackfillDigestBackfilledCount = 0;
                ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance,
                    () => "chart_info_backfill skipped version=" + shutdownRequestVersion + " reason=shutdown_requested");
                break;
            }
            int requestVersion;
            ChartInfoBackfillRequest[] requests;
            OperationProgressRequest progressRequest;
            Action<OperationProgressRequest, bool> progressReporter;
            lock (backfillGate)
            {
                progressRequest = backfillProgressRequest;
                progressReporter = backfillExecutionReporter;
                requestVersion = chartInfoBackfillRequestedVersion;
                requests = backfillRequests.ToArray();
                backfillRequests.Clear();
            }
            bool completedLatestRequest = false;
            bool fullRequest = requests.Any(request => request.IsFull);
            int committedDigestCount = 0;
            List<ChartFile> chartSnapshot = null;
            Dictionary<string, BeMusicSeeker.Models.ChartDetails> existingRowsSnapshot = null;
            List<Action> publicationEffects = [];
            try
            {
                try
                {
                    progressReporter?.Invoke(progressRequest, true);
                    if (fullRequest)
                    {
                        using (workflowOwnedCollectionOwner.WriteGate.GetReaderGuard())
                        {
                            lock (workflowOwnedCollectionOwner.Gate)
                            {
                                chartSnapshot = workflowOwnedCollectionOwner.Collection.CreateSnapshot(includeWarningSnapshot: false, includeScoreSnapshot: false);
                                workflowOwnedCollectionOwner.BeginChartInfoInputCoverage(chartSnapshot);
                            }
                        }
                    }
                    else
                    {
                        CatalogReadSnapshot inputs = workflowOwnedCollectionOwner.CaptureReadSnapshot(requests.SelectMany(request => request.Charts));
                        chartSnapshot = inputs.Charts.Values.ToList();
                    }
                    using (workflowBeginDigestMutationWindow())
                    {
                        CaptureInputCoverageConditions();
                        ChartInfoBackfillTotalCount = 0;
                        ChartInfoBackfillProcessedCount = 0;
                        ChartInfoBackfillCurrentPath = string.Empty;
                        void reportProgress(int total, int processed, string currentPath)
                        {
                            ChartInfoBackfillTotalCount = total;
                            ChartInfoBackfillProcessedCount = processed;
                            ChartInfoBackfillCurrentPath = currentPath ?? string.Empty;
                            PublishProgressSnapshot(ref backfillProgressSnapshot,
                                new(requestVersion, total, processed, currentPath, progressRequest),
                                nameof(BMSLibrary.ChartInfoBackfillProgressSnapshot));
                        }
                        existingRowsSnapshot = fullRequest ? CreateSha256Snapshot() : new(StringComparer.OrdinalIgnoreCase);
                        if (!fullRequest)
                        {
                            foreach (ChartFile chart in chartSnapshot)
                            {
                                ChartDetails row = ResolveChartInfoFromIndex(chart.Sha256, chart.Md5);
                                if (!string.IsNullOrWhiteSpace(row?.sha256))
                                {
                                    existingRowsSnapshot[row.sha256] = row;
                                }
                            }
                        }

                        ChartInfoBackfillResult result = buildService.BackfillChartInfos(
                            workflowDbGateway,
                            chartSnapshot,
                            reportProgress,
                            LogPerformance,
                            workflowLogWarning,
                            publication =>
                            {
                                // callback以後の必要反映が失敗しても、既に確定したdigest件数は失いません。
                                committedDigestCount += publication.DigestChanges.Count;
                                PublishCommittedStorageApplication(
                                    publication.DigestChanges,
                                    publication.AppliedRows,
                                    publication.ParseFailureChanged,
                                    "chart_info_backfill",
                                    publicationEffects.Add, publication.CurrentValues, publication.ChangedFailureMd5s);
                            },
                            existingRowsSnapshot,
                            workflowMutationOwner.ApplyChartInfoStorageWrite,
                            subset: !fullRequest,
                            inputCompleted: values => workflowOwnedCollectionOwner.CompleteChartInfoInputs(values, full: false));

                        ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance, () => "chart_info_backfill done version=" + requestVersion
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
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    recordFailure(ex, requestVersion, "build");
                }
                // 既に準備した確定後効果は、mutation window解放後にそれぞれ試みます。
                for (int publicationIndex = 0; publicationIndex < publicationEffects.Count; publicationIndex++)
                {
                    try
                    {
                        publicationEffects[publicationIndex]?.Invoke();
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        recordFailure(ex, requestVersion, "publication_" + publicationIndex);
                    }
                }
            }
            finally
            {
                chartSnapshot?.Clear();
                existingRowsSnapshot?.Clear();
                // observerに渡す前に、普通故障を含む要求の終端事実を全て確定します。
                lock (backfillGate)
                {
                    chartInfoBackfillCompletedVersion = requestVersion;
                    chartInfoBackfillCompletedVersionValue = requestVersion;
                    chartInfoBackfillCurrentPathValue = string.Empty;
                    chartInfoBackfillDigestBackfilledCountValue = committedDigestCount;
                    if (requestVersion == chartInfoBackfillRequestedVersion)
                    {
                        chartInfoBackfillRunningValue = false;
                        completedLatestRequest = true;
                    }
                }
                NotifyBackfillCompletionBestEffort(() => propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoBackfillCurrentPath)));
                NotifyBackfillCompletionBestEffort(() => propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoBackfillDigestBackfilledCount)));
                NotifyBackfillCompletionBestEffort(() => propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoBackfillCompletedVersion)));
                if (completedLatestRequest)
                {
                    NotifyBackfillCompletionBestEffort(() => propertyChanged?.Invoke(nameof(BMSLibrary.ChartInfoBackfillRunning)));
                }
                NotifyBackfillCompletionBestEffort(() => PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Checkpoint("chart_info_backfill", "after_release")));
                NotifyBackfillCompletionBestEffort(() => progressReporter?.Invoke(progressRequest, false));
            }
            if (completedLatestRequest) { break; }
        }
        firstFailure?.Throw();
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
            OperationProgressRequest progressRequest;
            Action<OperationProgressRequest, bool> progressReporter;
            bool queueBackfillAfterHydration;
            lock (hydrationGate)
            {
                progressRequest = hydrationProgressRequest;
                progressReporter = hydrationExecutionReporter;
                requestVersion = hydrationRequestedVersion;
                reason = hydrationPendingReason;
                queueBackfillAfterHydration = hydrationPendingQueueBackfill;
                hydrationPending = false;
                hydrationPendingReason = null;
                hydrationPendingQueueBackfill = false;
            }

            ChartInfoHydrationResult result;
            progressReporter?.Invoke(progressRequest, true);
            try
            {
                result = HydrateChartInfos(reason);
            }
            catch (Exception ex)
            {
                result = new ChartInfoHydrationResult();
                LogPerformance?.Invoke("chart_info_hydration failed reason=" + (reason ?? "unknown") + " message=" + ex.Message);
            }
            progressReporter?.Invoke(progressRequest, false);
            ChartInfoHydrationTotalCount = result.TotalRows;
            ChartInfoHydrationAppliedCount = result.AppliedBmsCount + result.AppliedBmsonCount;
            PublishProgressSnapshot(ref hydrationProgressSnapshot,
                new(requestVersion, result.TotalRows, result.AppliedBmsCount + result.AppliedBmsonCount, string.Empty, progressRequest),
                nameof(BMSLibrary.ChartInfoHydrationProgressSnapshot));
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
                        QueueBackfill(reason, processSynchronously: true, hydrationResult: result, originatingRequest: progressRequest);
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
                            hydrationCompletion?.TrySetResult(true);
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

    private ChartInfoHydrationResult HydrateChartInfos(string reason, bool propagateLoadFailure = false)
    {
        EnsureWorkflowConfigured();
        var result = new ChartInfoHydrationResult();
        var totalStopwatch = Stopwatch.StartNew();
        LogPerformance?.Invoke("chart_info_hydration start reason=" + (reason ?? "unknown"));
        Dictionary<string, BeMusicSeeker.Models.ChartDetails> chartInfoMap;
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
            if (propagateLoadFailure) { throw; }
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

        int catalogRevisionAtSummary = 0;
        var ownerApplyStopwatch = Stopwatch.StartNew();
        using (workflowOwnedCollectionOwner.WriteGate.GetReaderGuard())
        {

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
            catalogRevisionAtSummary = workflowOwnedCollectionOwner.CatalogRevision;
            lock (workflowOwnedCollectionOwner.Gate)
            {
                result.Succeeded = true;
                CaptureHydrationAllCurrentSnapshot(result, catalogRevisionAtSummary);
            }
        }
        ownerApplyStopwatch.Stop();
        result.OwnerApplyMs = result.ApplyMs = ownerApplyStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        result.TotalMs = totalStopwatch.ElapsedMilliseconds;
        result.Succeeded = true;
        return result;
    }

    private void QueueBackfillRequest(ChartInfoBackfillRequest request, bool processSynchronously, OperationProgressRequest originatingRequest = null)
    {
        int requestVersion;
        OperationProgressRequest progressRequest;
        bool shouldStartWorker = false;
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
            progressRequest = backfillProgressRequest = originatingRequest == null ? ProgressRequestFactory?.Invoke("chart_info_backfill", requestVersion)
                : originatingRequest with { Source = "chart_info_backfill", Version = requestVersion };
            backfillExecutionReporter = RequestProgressReporter;
            backfillRequests.Add(request);
            if (!ChartInfoBackfillRunning)
            {
                shouldStartWorker = true;
                ChartInfoBackfillRunning = true;
            }

        }
        ChartInfoBackfillRequestedVersion = requestVersion;
        PublishProgressSnapshot(ref backfillProgressSnapshot,
            new(requestVersion, 0, 0, string.Empty, progressRequest),
            nameof(BMSLibrary.ChartInfoBackfillProgressSnapshot));
        ChartInfoBackfillTotalCount = 0;
        ChartInfoBackfillProcessedCount = 0;
        ChartInfoBackfillDigestBackfilledCount = 0;
        ChartInfoBackfillCurrentPath = string.Empty;
        ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance, () => "chart_info_backfill queue"
            + " reason=" + (request.Reason ?? "unknown")
            + " mode=full"
            + " version=" + requestVersion);
        if (processSynchronously)
        {
            // 同じ受理済み入力更新の継続として実行する。待機中の旧workerは終端後に完了済み要求を再実行しない。
            ProcessBackfillRequests();
            return;
        }
        if (shouldStartWorker)
        {
            Task.Run(async () =>
            {
                await WaitForHydrationIdleAsync().ConfigureAwait(false);
                using IDisposable lease = await operationAdmission.EnterAcceptedBackgroundAsync().ConfigureAwait(false);
                ProcessBackfillRequests();
            }).ObserveFault("ProcessChartInfoBackfillRequests");
        }
    }

    private int CompleteSkippedBackfillRequestIfIdle(OperationProgressRequest originatingRequest = null)
    {
        int requestVersion = 0;
        OperationProgressRequest progressRequest;
        lock (backfillGate)
        {
            if (ChartInfoBackfillRunning)
            {
                return chartInfoBackfillRequestedVersion;
            }
            chartInfoBackfillRequestedVersion++;
            requestVersion = chartInfoBackfillRequestedVersion;
            chartInfoBackfillCompletedVersion = requestVersion;
            backfillProgressRequest = progressRequest = originatingRequest == null ? ProgressRequestFactory?.Invoke("chart_info_backfill", requestVersion)
                : originatingRequest with { Source = "chart_info_backfill", Version = requestVersion };
        }
        ChartInfoBackfillRequestedVersion = requestVersion;
        PublishProgressSnapshot(ref backfillProgressSnapshot,
            new(requestVersion, 0, 0, string.Empty, progressRequest),
            nameof(BMSLibrary.ChartInfoBackfillProgressSnapshot));
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

    private void CaptureHydrationAllCurrentSnapshot(ChartInfoHydrationResult result, int catalogRevision)
    {
        ChartInfoHydrationAllCurrentSnapshot snapshot = null;
        if (result != null
            && result.Succeeded
            && result.OwnerCount > 0
            && result.BackfillCandidateOwnerCount <= 0)
        {
            workflowOwnedCollectionOwner.CompleteChartInfoInputs([], full: true, expectedCatalogRevision: catalogRevision);
            inputCoverageParserVersion = BmsLibraryDbGateway.CurrentChartInfoParserVersion;
            inputCoverageTimeoutMs = Math.Max(0L, (long)Math.Ceiling(buildService.CurrentParseTimeout.TotalMilliseconds));
            snapshot = new ChartInfoHydrationAllCurrentSnapshot
            {
                CatalogRevision = catalogRevision,

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
        if (snapshot == null
            || !workflowOwnedCollectionOwner.TryCapturePendingChartInfoInputs(out IReadOnlyList<ChartFile> pendingInputs) || pendingInputs.Count != 0
            || workflowOwnedCollectionOwner.CaptureStateSnapshot().BmsRowCount + workflowOwnedCollectionOwner.CaptureStateSnapshot().BmsonRowCount != snapshot.OwnerCount
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
            || workflowOwnedCollectionOwner == null)
        {
            throw new InvalidOperationException("Chart-info workflow owner is not configured.");
        }
    }

    private void PublishWorkflowEvent(CatalogChartInfoOwnerEvent ownerEvent)
    {
        workflowEvent?.Invoke(ownerEvent);
    }

    internal void PublishWarningPresentationChanged(string reason, IReadOnlyList<ChartFile> charts = null, IReadOnlyList<string> md5s = null)
    {
        PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Warning(reason, charts, md5s));
    }

    internal BeMusicSeeker.Models.ChartDetails ResolveChartInfo(
        string sha256,
        string md5,
        Func<bool> shouldLazyLoad,
        Action<string> lazyLoad)
    {
        BeMusicSeeker.Models.ChartDetails row = ResolveChartInfoFromIndex(sha256, md5);
        if (row == null && shouldLazyLoad?.Invoke() == true)
        {
            lazyLoad?.Invoke("resolve_chart_info");
            row = ResolveChartInfoFromIndex(sha256, md5);
        }
        return row;
    }

    internal BeMusicSeeker.Models.ChartDetails ResolveChartInfo(string sha256, string md5)
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

    internal Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateLr2ResolverSnapshot()
    {
        Dictionary<string, BeMusicSeeker.Models.ChartDetails> bySha256;
        Dictionary<string, BeMusicSeeker.Models.ChartDetails> byMd5;
        lock (indexGate)
        {
            bySha256 = new Dictionary<string, BeMusicSeeker.Models.ChartDetails>(
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
            if (!string.IsNullOrWhiteSpace(row.Sha256)
                && bySha256.TryGetValue(row.Sha256, out BeMusicSeeker.Models.ChartDetails bySha256Row))
            {
                return bySha256Row;
            }
            if (!string.IsNullOrWhiteSpace(row.Md5)
                && byMd5.TryGetValue(row.Md5, out BeMusicSeeker.Models.ChartDetails byMd5Row))
            {
                return byMd5Row;
            }
            return null;
        };
    }

    internal ChartInfoIndexUpdateResult ReplaceIndex(
        IEnumerable<BeMusicSeeker.Models.ChartDetails> rows,
        bool hydrated)
    {
        var bySha256 = new Dictionary<string, BeMusicSeeker.Models.ChartDetails>(StringComparer.OrdinalIgnoreCase);
        var byMd5 = new Dictionary<string, SortedDictionary<string, BeMusicSeeker.Models.ChartDetails>>(StringComparer.OrdinalIgnoreCase);
        int inputRows = 0;
        foreach (BeMusicSeeker.Models.ChartDetails row in rows ?? [])
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
            ByMd5Count = byMd5.Count,
            ChangedMd5s = [.. byMd5.Keys],
            ChangedSha256s = [.. bySha256.Keys]
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
        PublishWorkflowEvent(CatalogChartInfoOwnerEvent.IndexChanged("chart_info_index_snapshot", result));
        return result;
    }

    internal ChartInfoIndexUpdateResult UpsertIndex(
        IEnumerable<BeMusicSeeker.Models.ChartDetails> rows,
        string reason,
        bool dispatchPresentation = true,
        bool publishEffects = true)
    {
        List<BeMusicSeeker.Models.ChartDetails> rowList = [.. (rows ?? [])
            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.sha256))];
        if (rowList.Count == 0)
        {
            return new ChartInfoIndexUpdateResult();
        }
        var result = new ChartInfoIndexUpdateResult
        {
            InputRows = rowList.Count,
            ChangedMd5s = [.. rowList.Select(row => row.md5).Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.OrdinalIgnoreCase)],
            ChangedSha256s = [.. rowList.Select(row => row.sha256).Distinct(StringComparer.OrdinalIgnoreCase)]
        };
        lock (indexGate)
        {
            foreach (BeMusicSeeker.Models.ChartDetails row in rowList)
            {
                if (!TryGetSha256(row, out string sha256))
                {
                    continue;
                }
                if (indexBySha256.TryGetValue(sha256, out BeMusicSeeker.Models.ChartDetails previousRow))
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
            PublishWorkflowEvent(CatalogChartInfoOwnerEvent.IndexChanged("chart_info_index_delta", result));
        }
        logPerformance?.Invoke("chart_info_index_delta upserted=" + upsertedRowCount
            + " bySha256=" + result.BySha256Count
            + " byMd5=" + result.ByMd5Count
            + " version=" + result.Version
            + " reason=" + (reason ?? "unknown"));
    }

    internal Dictionary<string, BeMusicSeeker.Models.ChartDetails> CreateSha256Snapshot()
    {
        lock (indexGate)
        {
            if (!chartInfoIndexHydratedValue)
            {
                return null;
            }
            return new Dictionary<string, BeMusicSeeker.Models.ChartDetails>(indexBySha256, StringComparer.OrdinalIgnoreCase);
        }
    }

    internal Dictionary<string, BeMusicSeeker.Models.ChartDetails> LoadChartInfoMapSnapshot(Func<Dictionary<string, BeMusicSeeker.Models.ChartDetails>> loader)
    {
        return loader?.Invoke();
    }

    internal bool IsIndexHydrated
    {
        get
        {
            lock (indexGate)
            {
                return chartInfoIndexHydratedValue;
            }
        }
    }

    private BeMusicSeeker.Models.ChartDetails ResolveChartInfoFromIndex(string sha256, string md5)
    {
        lock (indexGate)
        {
            if (!string.IsNullOrWhiteSpace(sha256)
                && indexBySha256.TryGetValue(sha256, out BeMusicSeeker.Models.ChartDetails bySha256))
            {
                return bySha256;
            }
            if (!string.IsNullOrWhiteSpace(md5)
                && indexByMd5.TryGetValue(md5, out SortedDictionary<string, BeMusicSeeker.Models.ChartDetails> candidates)
                && candidates.Count > 0)
            {
                return candidates.First().Value;
            }
        }
        return null;
    }

    private static bool TryGetSha256(BeMusicSeeker.Models.ChartDetails row, out string sha256)
    {
        sha256 = row?.sha256?.Trim();
        if (string.IsNullOrWhiteSpace(sha256))
        {
            sha256 = null;
            return false;
        }
        return true;
    }

    private static bool TryGetMd5(BeMusicSeeker.Models.ChartDetails row, out string md5)
    {
        md5 = row?.md5?.Trim();
        if (string.IsNullOrWhiteSpace(md5))
        {
            md5 = null;
            return false;
        }
        return true;
    }

    private static bool IsCurrentChartInfoRow(BeMusicSeeker.Models.ChartDetails row)
    {
        return row != null && row.parser_version >= BmsLibraryDbGateway.CurrentChartInfoParserVersion;
    }

    private static void AddMd5Candidate(
        IDictionary<string, SortedDictionary<string, BeMusicSeeker.Models.ChartDetails>> byMd5,
        BeMusicSeeker.Models.ChartDetails row,
        string sha256)
    {
        if (!TryGetMd5(row, out string md5) || string.IsNullOrWhiteSpace(sha256))
        {
            return;
        }
        if (!byMd5.TryGetValue(md5, out SortedDictionary<string, BeMusicSeeker.Models.ChartDetails> candidates))
        {
            candidates = new(StringComparer.OrdinalIgnoreCase);
            byMd5[md5] = candidates;
        }
        candidates[sha256] = row;
    }

    private static void RemoveMd5Candidate(
        IDictionary<string, SortedDictionary<string, BeMusicSeeker.Models.ChartDetails>> byMd5,
        BeMusicSeeker.Models.ChartDetails row,
        string sha256)
    {
        if (!TryGetMd5(row, out string md5)
            || !byMd5.TryGetValue(md5, out SortedDictionary<string, BeMusicSeeker.Models.ChartDetails> candidates))
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
        OperationProgressRequest progressRequest;
        bool shouldStartWorker = false;
        lock (hydrationGate)
        {
            hydrationRequestedVersion++;
            requestVersion = hydrationRequestedVersion;
            progressRequest = hydrationProgressRequest = ProgressRequestFactory?.Invoke("chart_info_hydration", requestVersion);
            hydrationExecutionReporter = RequestProgressReporter;
            hydrationPending = true;
            hydrationPendingReason = reason;
            hydrationPendingQueueBackfill = hydrationPendingQueueBackfill || queueBackfillAfterHydration;
            if (!hydrationRunning)
            {
                hydrationRunning = true;
                hydrationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                shouldStartWorker = true;
            }
        }
        ChartInfoHydrationRequestedVersion = requestVersion;
        PublishProgressSnapshot(ref hydrationProgressSnapshot,
            new(requestVersion, 0, 0, string.Empty, progressRequest),
            nameof(BMSLibrary.ChartInfoHydrationProgressSnapshot));
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

        Func<Task> acceptedWork = async () =>
        {
            using IDisposable lease = await operationAdmission.EnterAcceptedBackgroundAsync().ConfigureAwait(false);
            await process().ConfigureAwait(false);
        };
        Func<string, string, string, Func<Task>, bool> scheduler = SchedulerProvider?.Invoke();
        if (scheduler != null)
        {
            if (scheduler("chart_info_hydration", reason ?? "queue", null, acceptedWork))
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
        Task.Run(acceptedWork).ObserveFault("ProcessDeferredChartInfoHydrationRequests");
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
            hydrationCompletion?.TrySetResult(true);
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
        PublishWorkflowEvent(CatalogChartInfoOwnerEvent.Warning("chart_info_parse_failure_remove", md5s: normalizedMd5s));
    }

    internal static string[] NormalizeParseFailureMd5s(IEnumerable<string> md5s)
    {
        return [.. (md5s ?? [])
            .Where(md5 => !string.IsNullOrWhiteSpace(md5))
            .Select(md5 => md5.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal Dictionary<string, BeMusicSeeker.Models.ChartParseFailure> LoadCurrentParseFailureMap(
        BmsLibraryDbGateway dbGateway,
        TimeSpan parseTimeout)
    {
        return dbGateway?.LoadCurrentChartInfoParseFailureMap(parseTimeout)
            ?? new Dictionary<string, BeMusicSeeker.Models.ChartParseFailure>(StringComparer.OrdinalIgnoreCase);
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

    internal Dictionary<string, BeMusicSeeker.Models.ChartDetails> LoadChartInfoMap(
        BmsLibraryDbGateway dbGateway)
    {
        return dbGateway?.LoadChartInfoMap()
            ?? new Dictionary<string, BeMusicSeeker.Models.ChartDetails>(StringComparer.OrdinalIgnoreCase);
    }

    internal Dictionary<string, BeMusicSeeker.Models.ChartDetails> LoadChartInfosBySha256(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<string> sha256s)
    {
        return dbGateway?.LoadChartInfosBySha256(sha256s)
            ?? new Dictionary<string, BeMusicSeeker.Models.ChartDetails>(StringComparer.OrdinalIgnoreCase);
    }

    internal Dictionary<string, BeMusicSeeker.Models.ChartDetails> LoadChartInfosByMd5(
        BmsLibraryDbGateway dbGateway,
        IEnumerable<string> md5s)
    {
        return dbGateway?.LoadChartInfosByMd5(md5s)
            ?? new Dictionary<string, BeMusicSeeker.Models.ChartDetails>(StringComparer.OrdinalIgnoreCase);
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
        ChartFile[] updatedCharts = [.. result.StorageApplications.Select(application => application.CreateCurrentValue())];
        CatalogChartInfoStorageWriteReceipt writeReceipt = workflowMutationOwner.ApplyChartInfoStorageWrite(
            new CatalogChartInfoStorageWriteRequest(
                updatedCharts,
                new CatalogChartInfoWriteRequest(
                    chartInfoRows: result.ChartInfoRows,
                    parseFailureRows: result.ParseFailureRows,
                    parseFailureDeleteMd5s: result.ParseFailureDeleteMd5s)));
        if ((updatedCharts.Length > 0
            || result.ChartInfoRows.Count > 0
            || result.ParseFailureRows.Count > 0
            || result.ParseFailureDeleteMd5s.Count > 0)
            && !writeReceipt.Applied)
        {
            throw new InvalidOperationException("Inline chart-info persistence returned no receipt.");
        }
        int songRowChartInfoApplied = 0;
        var committedCurrentValues = new List<ChartFile>();
        lock (workflowOwnedCollectionOwner.Gate)
        {
            foreach (ChartInfoStorageApplication application in result.StorageApplications)
            {
                ChartFile current = workflowOwnedCollectionOwner.Collection.ResolveCurrentChart(
                    LibraryChartRef.FromChartFile(application.Chart));
                if (current == null)
                {
                    continue;
                }
                ChartFile next = application.CreateCurrentValue(current);
                var digestChange = new LibraryChartDigestChange(
                    current.Kind,
                    current.Path, current.Md5, current.Sha256, next.Md5, next.Sha256);
                committedCurrentValues.Add(next);
                if (digestChange.HasDigestChange)
                {
                    result.DigestChanges.Add(digestChange);
                }
                if (application.Row != null)
                {
                    songRowChartInfoApplied++;
                }
            }
        }
        PublishCommittedStorageApplication(
            result.DigestChanges,
            result.AppliedRows,
            result.ParseFailureRows.Count > 0 || result.ParseFailureDeleteMd5s.Count > 0,
            reason ?? "install_package_inline",
            deferPublication, committedCurrentValues, [.. result.ParseFailureRows.Select(value => value.md5).Concat(result.ParseFailureDeleteMd5s)]);
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
    /// 詳細情報の永続確定後、共通現在値・ハッシュ由来索引・詳細索引の順で反映します。
    /// 準備した共通通知は内部反映と排他解放の後にだけ発行します。
    /// </summary>
    private void PublishCommittedStorageApplication(
        IEnumerable<LibraryChartDigestChange> digestChanges,
        IReadOnlyList<BeMusicSeeker.Models.ChartDetails> appliedRows,
        bool parseFailureChanged,
        string reason,
        Action<Action> deferPublication = null,
        IReadOnlyList<ChartFile> currentValues = null,
        IReadOnlyList<string> changedFailureMd5s = null)
    {
        LibraryChartDigestChange[] committedDigestChanges = [.. (digestChanges ?? [])
            .Where(change => change != null)];
        BeMusicSeeker.Models.ChartDetails[] committedRows = [.. (appliedRows ?? [])
            .Where(row => row != null)];

        CatalogCommitReceipt currentCommit = workflowOwnedCollectionOwner.CommitCurrentValues(currentValues, (current, value) =>
            ChartFileProjection.WithChartInfo(current, value.ChartInfo) with
            {
                Md5 = value.Md5,
                Sha256 = value.Sha256,
                LastWriteTimeUtc = value.LastWriteTimeUtc,
                Level = value.Level,
                LevelText = value.LevelText,
                Difficulty = value.Difficulty,
                Mode = value.Mode
            });
        List<ChartFile> changedCurrentValues = [.. currentCommit.Changes.Where(change => change.After != null).Select(change => change.After)];

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
                reason + "_digest", currentCommit);
        }


        if (committedRows.Length > 0)
        {
            ChartInfoIndexUpdateResult indexResult = UpsertIndex(
                committedRows,
                reason,
                publishEffects: false);
            // ハッシュ変更の共通通知には最新の基本値も含まれるため、集合版の公開を二重に行わない。
            indexResult.ChangedCharts = committedDigestChanges.Length == 0 ? changedCurrentValues : [];
            if (deferPublication == null)
            {
                PublishIndexUpsertEffects(indexResult, committedRows.Length, reason, dispatchPresentation: true);
            }

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
                reason + "_parse_failure", md5s: changedFailureMd5s);
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

    /// <summary>補完の終端表示・終了進捗と計測checkpointの普通故障を局所化し、要求終端を保持します。必要な索引・digest公開には使いません。</summary>
    private void NotifyBackfillCompletionBestEffort(Action notify)
    {
        try
        {
            notify();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ChartInfoBuildService.ReportDiagnosticBestEffort(LogPerformance,
                () => "chart_info_observer_failed exception=" + ex.GetType().Name);
        }
    }
}
