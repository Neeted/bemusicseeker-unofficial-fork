using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Livet;

namespace BeMusicSeeker.ViewModels;

internal sealed class StartupProgressVersionSnapshot
{
    internal int ScoreHydrationCompletedVersion { get; init; }
    internal int ScoreHydrationRequestedVersion { get; init; }
    internal int RankingRefreshCompletedVersion { get; init; }
    internal int RankingRefreshRequestedVersion { get; init; }
    internal int MaintenanceHydrationRequestedVersion { get; init; }
    internal int InstallableMaintenanceDeferredRequestedVersion { get; init; }
    internal int ChartDigestBackfillCompletedVersion { get; init; }
    internal int ChartInfoBackfillCompletedVersion { get; init; }
    internal int ChartInfoHydrationCompletedVersion { get; init; }
    internal int ChartInfoBackfillRequestedVersion { get; init; }
    internal int PlaylistEntriesHydrationCompletedVersion { get; init; }
    internal int LibraryDatabaseLoadCompletedVersion { get; init; }
    internal int LibraryFileEnumerationCompletedVersion { get; init; }
    internal int LibraryFileDiffCompletedVersion { get; init; }
}

public sealed class StartupProgressWorkflowOwner : ViewModel
{
    private readonly Func<StartupProgressVersionSnapshot> versionSnapshotProvider;
    private readonly Action<StartupProgressOperationKind, long> prepareOperation;
    private readonly Action<Action> dispatch;
    private readonly Action<string> log;
    private readonly Action<long> startupInitializationCompleted;
    private readonly Func<bool> backgroundTasksIdle;
    private readonly Func<long, long, bool> backgroundTasksIdleSnapshotCurrent;
    private readonly Func<bool> requiredInitializationSchedulingComplete;
    private readonly Func<Task> completionHideDelay;
    private readonly object backgroundTaskProgressSynchronization;
    private readonly object startupProgressLock = new();
    private StartupProgressState startupProgressState = new();
    private long startupProgressOperationTokenSeed;
    private bool isActive;

    private bool isStartupUiInteractionBlocked;
    private string label = string.Empty;
    private string subLabel = string.Empty;
    private double progressValue;
    private double progressMaximum = 1.0;

    /// <summary>
    /// Initializes the owner with the startup-progress runtime boundaries.
    /// </summary>
    /// <param name="versionSnapshotProvider">Provides the current completion versions when an operation starts.</param>
    /// <param name="prepareOperation">Prepares operation-scoped collaborators for a newly assigned token.</param>
    /// <param name="dispatch">Dispatches presentation notifications to their owning thread.</param>
    /// <param name="log">Writes startup-progress diagnostics.</param>
    /// <param name="startupInitializationCompleted">Publishes completion of required startup initialization.</param>
    /// <param name="backgroundTasksIdle">Reports whether startup background tasks are currently idle.</param>
    /// <param name="backgroundTasksIdleSnapshotCurrent">Validates a scheduler generation and revision as an idle snapshot.</param>
    /// <param name="requiredInitializationSchedulingComplete">Reports whether required task enrollment is closed for the current generation.</param>
    /// <param name="backgroundTaskProgressSynchronization">Synchronizes scheduler enrollment with progress completion.</param>
    /// <param name="completionHideDelay">
    /// Waits before a completed operation is hidden. When omitted, the production two-second delay is used.
    /// </param>
    internal StartupProgressWorkflowOwner(
        Func<StartupProgressVersionSnapshot> versionSnapshotProvider,
        Action<StartupProgressOperationKind, long> prepareOperation,
        Action<Action> dispatch,
        Action<string> log,
        Action<long> startupInitializationCompleted,
        Func<bool> backgroundTasksIdle,
        Func<long, long, bool> backgroundTasksIdleSnapshotCurrent,
        Func<bool> requiredInitializationSchedulingComplete,
        object backgroundTaskProgressSynchronization,
        Func<Task> completionHideDelay = null)
    {
        this.versionSnapshotProvider = versionSnapshotProvider ?? throw new ArgumentNullException(nameof(versionSnapshotProvider));
        this.prepareOperation = prepareOperation ?? throw new ArgumentNullException(nameof(prepareOperation));
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.startupInitializationCompleted = startupInitializationCompleted
            ?? throw new ArgumentNullException(nameof(startupInitializationCompleted));
        this.backgroundTasksIdle = backgroundTasksIdle ?? throw new ArgumentNullException(nameof(backgroundTasksIdle));
        this.backgroundTasksIdleSnapshotCurrent = backgroundTasksIdleSnapshotCurrent
            ?? throw new ArgumentNullException(nameof(backgroundTasksIdleSnapshotCurrent));
        this.requiredInitializationSchedulingComplete = requiredInitializationSchedulingComplete
            ?? throw new ArgumentNullException(nameof(requiredInitializationSchedulingComplete));
        this.completionHideDelay = completionHideDelay
            ?? (() => Task.Delay(TimeSpan.FromSeconds(2)));
        this.backgroundTaskProgressSynchronization = backgroundTaskProgressSynchronization
            ?? throw new ArgumentNullException(nameof(backgroundTaskProgressSynchronization));
    }

    public bool IsActive
    {
        get => isActive;
        private set => SetValue(ref isActive, value, nameof(IsActive));
    }
    public string Label
    {
        get => label;
        private set => SetValue(ref label, value ?? string.Empty, nameof(Label));
    }
    public string SubLabel
    {
        get => subLabel;
        private set => SetValue(ref subLabel, value ?? string.Empty, nameof(SubLabel));
    }
    public double Value
    {
        get => progressValue;
        private set => SetValue(ref progressValue, value, nameof(Value));
    }
    public double Maximum
    {
        get => progressMaximum;
        private set => SetValue(ref progressMaximum, Math.Max(1.0, value), nameof(Maximum));
    }

    /// <summary>
    /// Gets whether startup or terminal shutdown currently blocks shell interaction.
    /// </summary>
    public bool IsStartupUiInteractionBlocked
    {
        get
        {
            lock (startupProgressLock)
            {
                return isStartupUiInteractionBlocked;
            }
        }
    }

    internal void SetStartupUiInteractionBlocked(bool value)
    {
        bool changed;
        lock (startupProgressLock)
        {
            changed = isStartupUiInteractionBlocked != value;
            if (changed)
            {
                isStartupUiInteractionBlocked = value;
            }
        }
        if (changed)
        {
            RaiseStartupProgressPropertyChanged(nameof(IsStartupUiInteractionBlocked));
        }
    }
    internal bool IsOperationActive
    {
        get { lock (startupProgressLock) { return startupProgressState.IsActive; } }
    }
    internal StartupProgressOperationKind CurrentOperationKind
    {
        get { lock (startupProgressLock) { return startupProgressState.OperationKind; } }
    }
    internal bool IsFailed
    {
        get { lock (startupProgressLock) { return startupProgressState.IsFailed; } }
    }
    internal bool IsRetryableFailure
    {
        get { lock (startupProgressLock) { return startupProgressState.IsRetryableFailure; } }
    }
    /// <summary>
    /// Gets whether the active non-failed startup operation temporarily owns the status presentation.
    /// </summary>
    internal bool IsStartupProgressBlockingDedicatedStatus
    {
        get
        {
            lock (startupProgressLock)
            {
                return startupProgressState.IsActive && !startupProgressState.IsFailed;
            }
        }
    }
    internal long GetActiveStartupProgressOperationToken()
    {
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive ? startupProgressState.OperationToken : 0L;
        }
    }
    internal bool IsStartupProgressOperationTokenCurrent(long operationToken)
    {
        if (operationToken == 0L) return true;
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive && startupProgressState.OperationToken == operationToken;
        }
    }

    internal bool IsStartupInitializationRequiredProgressComplete(long operationToken = 0L)
    {
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive
                && !startupProgressState.IsFailed
                && startupProgressState.OperationKind == StartupProgressOperationKind.Startup
                && (operationToken == 0L || startupProgressState.OperationToken == operationToken)
                && AreExpectedStartupProgressPhasesCompleted(startupProgressState);
        }
    }
    internal void TryCompleteStartupBackgroundTasksPhaseIfIdle(
        long operationToken = 0L,
        long schedulerGeneration = 0L,
        long schedulerRevision = 0L)
    {
        bool marked = false;
        lock (backgroundTaskProgressSynchronization)
        {
            lock (startupProgressLock)
            {
                if (!requiredInitializationSchedulingComplete())
                {
                    return;
                }
                if (schedulerGeneration != 0L
                    ? !backgroundTasksIdleSnapshotCurrent(schedulerGeneration, schedulerRevision)
                    : !backgroundTasksIdle())
                {
                    return;
                }
                StartupProgressPhase expectedExceptBackgroundTasks = startupProgressState.ExpectedPhases & ~StartupProgressPhase.StartupBackgroundTasksDone;
                if (startupProgressState.IsActive
                    && (operationToken == 0L || startupProgressState.OperationToken == operationToken)
                    && (startupProgressState.ExpectedPhases & StartupProgressPhase.StartupBackgroundTasksDone) != 0
                    && (startupProgressState.CompletedPhases & StartupProgressPhase.StartupBackgroundTasksDone) == 0
                    && (startupProgressState.CompletedPhases & expectedExceptBackgroundTasks) == expectedExceptBackgroundTasks)
                {
                    startupProgressState.CompletedPhases |= StartupProgressPhase.StartupBackgroundTasksDone;
                    marked = true;
                }
            }
        }
        if (marked)
        {
            RaiseStartupProgressPropertyChanged(nameof(IsStartupProgressBlockingDedicatedStatus));
            RecomputeStartupProgressPresentation();
        }
    }
    internal void ApplyPresentation(bool active, string valueLabel, string valueSubLabel, double progress, double maximum)
    {
        IsActive = active;
        Label = valueLabel;
        SubLabel = valueSubLabel;
        Value = progress;
        Maximum = maximum;
    }
    private void SetValue<T>(ref T storage, T value, string propertyName)
    {
        if (!Equals(storage, value))
        {
            storage = value;
            RaisePropertyChanged(propertyName);
        }
    }

    private void RaiseStartupProgressPropertyChanged(string propertyName)
    {
        dispatch(() => RaisePropertyChanged(propertyName));
    }

    internal long StartStartupProgressOperation(StartupProgressOperationKind operationKind)
    {
        long operationToken = Interlocked.Increment(ref startupProgressOperationTokenSeed);
        StartupProgressVersionSnapshot versionSnapshot = versionSnapshotProvider();
        var state = new StartupProgressState
        {
            OperationKind = operationKind,
            OperationToken = operationToken,
            IsActive = true,
            CompletedPhases = StartupProgressPhase.CoreInitializeStarted,
            ExpectedPhases = GetInitialExpectedStartupProgressPhases(operationKind),
            ScoreHydrationBaselineCompletedVersion = versionSnapshot.ScoreHydrationCompletedVersion,
            ScoreHydrationRequestedBaselineVersion = versionSnapshot.ScoreHydrationRequestedVersion,
            RankingRefreshBaselineCompletedVersion = versionSnapshot.RankingRefreshCompletedVersion,
            RankingRefreshRequestedBaselineVersion = versionSnapshot.RankingRefreshRequestedVersion,
            MaintenanceRequestedBaselineVersion = versionSnapshot.MaintenanceHydrationRequestedVersion,
            InstallableMaintenanceRequestedBaselineVersion = versionSnapshot.InstallableMaintenanceDeferredRequestedVersion,
            ChartDigestBackfillBaselineCompletedVersion = versionSnapshot.ChartDigestBackfillCompletedVersion,
            ChartInfoBackfillBaselineCompletedVersion = versionSnapshot.ChartInfoBackfillCompletedVersion,
            ChartInfoHydrationBaselineCompletedVersion = versionSnapshot.ChartInfoHydrationCompletedVersion,
            PlaylistEntriesHydrationBaselineCompletedVersion = versionSnapshot.PlaylistEntriesHydrationCompletedVersion,
            LibraryDatabaseLoadBaselineCompletedVersion = versionSnapshot.LibraryDatabaseLoadCompletedVersion,
            LibraryFileEnumerationBaselineCompletedVersion = versionSnapshot.LibraryFileEnumerationCompletedVersion,
            LibraryFileDiffBaselineCompletedVersion = versionSnapshot.LibraryFileDiffCompletedVersion
        };
        lock (backgroundTaskProgressSynchronization)
        {
            lock (startupProgressLock)
            {
                startupProgressState = state;
            }
            prepareOperation(operationKind, operationToken);
        }
        RaiseStartupProgressPropertyChanged(nameof(IsOperationActive));
        RaiseStartupProgressPropertyChanged(nameof(IsFailed));
        RaiseStartupProgressPropertyChanged(nameof(IsRetryableFailure));
        RecomputeStartupProgressPresentation(operationToken);
        RaiseStartupProgressPropertyChanged(nameof(IsStartupProgressBlockingDedicatedStatus));
        return state.OperationToken;
    }

    /// <summary>
    /// 起動・リロード進捗を失敗表示へ切り替えます。
    /// </summary>
    /// <param name="subLabel">失敗時に表示する補足文言。</param>
    internal void FailStartupProgressOperation(string subLabel)
    {
        SetStartupUiInteractionBlocked(false);
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return;
            }
            startupProgressState.IsFailed = true;
            startupProgressState.FailureSubLabel = subLabel ?? string.Empty;
            startupProgressState.CompletionHideScheduled = false;
        }
        RaiseStartupProgressPropertyChanged(nameof(IsStartupProgressBlockingDedicatedStatus));
        RecomputeStartupProgressPresentation();
    }

    internal void MarkStartupProgressFailureCleanupComplete(long operationToken)
    {
        bool retryable = false;
        lock (startupProgressLock)
        {
            if (startupProgressState.IsActive
                && startupProgressState.OperationToken == operationToken
                && (startupProgressState.OperationKind == StartupProgressOperationKind.ScoreOnly
                    || startupProgressState.OperationKind == StartupProgressOperationKind.ReloadFileDiff
                    || startupProgressState.OperationKind == StartupProgressOperationKind.FullReinitialize
                    || startupProgressState.OperationKind == StartupProgressOperationKind.Startup)
                && startupProgressState.IsFailed)
            {
                startupProgressState.IsRetryableFailure = true;
                retryable = true;
            }
        }

        if (retryable)
        {
            RaiseStartupProgressPropertyChanged(nameof(IsOperationActive));
            RaiseStartupProgressPropertyChanged(nameof(IsFailed));
            RaiseStartupProgressPropertyChanged(nameof(IsRetryableFailure));
        }
    }

    /// <summary>
    /// 起動・リロード進捗のフェーズを完了済みにします。
    /// </summary>
    /// <param name="phase">完了したフェーズ。</param>
    internal void MarkStartupProgressPhaseCompleted(StartupProgressPhase phase, long operationToken = 0L)
    {
        bool marked = false;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive
                || (operationToken != 0L && startupProgressState.OperationToken != operationToken))
            {
                return;
            }
            startupProgressState.CompletedPhases |= phase;
            if (phase == StartupProgressPhase.RankingRefreshDone
                || phase == StartupProgressPhase.MaintenanceDeferredDone
                || phase == StartupProgressPhase.InstallableMaintenanceDeferredDone)
            {
                startupProgressState.LastCompletedAtUtc = DateTime.UtcNow;
            }
            marked = true;
        }
        if (!marked)
        {
            return;
        }
        RaiseStartupProgressPropertyChanged(nameof(IsStartupProgressBlockingDedicatedStatus));
        RecomputeStartupProgressPresentation(operationToken);
        if (phase != StartupProgressPhase.StartupBackgroundTasksDone)
        {
            TryCompleteStartupBackgroundTasksPhaseIfIdle(operationToken);
        }
    }

    internal void SkipStartupProgressPhaseIfExpected(StartupProgressPhase phase, string reason, long operationToken = 0L)
    {
        bool skipped = false;
        StartupProgressOperationKind operationKind = StartupProgressOperationKind.None;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive
                || (operationToken != 0L && startupProgressState.OperationToken != operationToken)
                || (startupProgressState.ExpectedPhases & phase) == 0
                || (startupProgressState.CompletedPhases & phase) != 0)
            {
                return;
            }
            operationKind = startupProgressState.OperationKind;
            startupProgressState.CompletedPhases |= phase;
            startupProgressState.SkippedPhases |= phase;
            skipped = true;
        }
        if (skipped)
        {
            RaiseStartupProgressPropertyChanged(nameof(IsStartupProgressBlockingDedicatedStatus));
            log?.Invoke("startup_progress_phase_skipped operation=" + operationKind + " phase=" + phase + " reason=" + (reason ?? string.Empty));
            RecomputeStartupProgressPresentation(operationToken);
            if (phase != StartupProgressPhase.StartupBackgroundTasksDone)
            {
                TryCompleteStartupBackgroundTasksPhaseIfIdle(operationToken);
            }
        }
    }

    internal void SkipUnrequestedStartupProgressPhases(string reason, params StartupProgressPhase[] phases)
    {
        SkipUnrequestedStartupProgressPhases(reason, 0L, phases);
    }

    internal void SkipUnrequestedStartupProgressPhases(string reason, long operationToken, params StartupProgressPhase[] phases)
    {
        if (phases == null || phases.Length == 0)
        {
            return;
        }
        foreach (StartupProgressPhase phase in phases)
        {
            bool shouldSkip;
            long currentOperationToken;
            lock (startupProgressLock)
            {
                shouldSkip = startupProgressState.IsActive
                    && (operationToken == 0L || startupProgressState.OperationToken == operationToken)
                    && (startupProgressState.ExpectedPhases & phase) != 0
                    && (startupProgressState.RequestedPhases & phase) == 0
                    && (startupProgressState.CompletedPhases & phase) == 0;
                currentOperationToken = startupProgressState.OperationToken;
            }
            if (shouldSkip)
            {
                SkipStartupProgressPhaseIfExpected(phase, reason, currentOperationToken);
            }
        }
    }

    internal bool TryTrackStartupProgressPhaseRequest(
        StartupProgressPhase phase,
        int version,
        string reason,
        Func<StartupProgressState, bool> isEligible,
        Action<StartupProgressState> updateRequiredVersion,
        long requestedOperationToken = 0L)
    {
        bool ignored = false;
        string ignoredReason = string.Empty;
        bool requestAfterSkip = false;
        StartupProgressOperationKind operationKind = StartupProgressOperationKind.None;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return false;
            }
            if (requestedOperationToken != 0L && startupProgressState.OperationToken != requestedOperationToken)
            {
                return false;
            }
            operationKind = startupProgressState.OperationKind;
            operationToken = startupProgressState.OperationToken;
            if ((startupProgressState.ExpectedPhases & phase) == 0)
            {
                ignored = true;
                ignoredReason = "not_expected";
            }
            else if (isEligible != null && !isEligible(startupProgressState))
            {
                ignored = true;
                ignoredReason = "not_eligible";
            }
            else
            {
                startupProgressState.RequestedPhases |= phase;
                updateRequiredVersion?.Invoke(startupProgressState);
                requestAfterSkip = (startupProgressState.SkippedPhases & phase) != 0;
                if ((startupProgressState.CompletedPhases & phase) == 0)
                {
                    startupProgressState.CompletionHideScheduled = false;
                }
            }
        }
        if (ignored)
        {
            log?.Invoke("startup_progress_request_ignored operation=" + operationKind + " phase=" + phase + " reason=" + ignoredReason + " requestReason=" + (reason ?? string.Empty) + " version=" + version);
            return false;
        }
        RaiseStartupProgressPropertyChanged(nameof(IsStartupProgressBlockingDedicatedStatus));
        if (requestAfterSkip)
        {
            log?.Invoke("startup_progress_request_after_skip operation=" + operationKind + " phase=" + phase + " requestReason=" + (reason ?? string.Empty) + " version=" + version);
        }
        if (!IsStartupProgressOperationTokenCurrent(operationToken))
        {
            return false;
        }
        RecomputeStartupProgressPresentation(operationToken);
        return true;
    }

    internal static bool RequiresStartupProgressRequestBeforeCompletion(StartupProgressPhase phase)
    {
        return phase != StartupProgressPhase.CoreInitializeStarted
            && phase != StartupProgressPhase.StartupReadyData
            && phase != StartupProgressPhase.StartupReadyUi
            && phase != StartupProgressPhase.StartupReadyOperable
            && phase != StartupProgressPhase.LibraryDatabaseLoadDone
            && phase != StartupProgressPhase.LibraryFileEnumerationDone
            && phase != StartupProgressPhase.LibraryFileDiffDone
            && phase != StartupProgressPhase.StartupBackgroundTasksDone;
    }

    internal static bool CanCompleteStartupProgressPhase(StartupProgressState state, StartupProgressPhase phase)
    {
        return (state.ExpectedPhases & phase) != 0
            && (!RequiresStartupProgressRequestBeforeCompletion(phase) || (state.RequestedPhases & phase) != 0);
    }

    /// <summary>
    /// 起動・リロード進捗で deferred playlist 参照適用を待機対象に追加します。
    /// </summary>
    /// <param name="reason">要求理由。</param>
    /// <param name="version">要求版数。</param>
    internal void TrackStartupProgressPlaylistReferenceRequest(string reason, int version, long operationToken = 0L)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.PlaylistReferenceApplied,
            version,
            reason,
            state => ShouldTrackStartupProgressPlaylistReference(reason, state.OperationKind),
            state => state.RequiredPlaylistReferenceVersion = Math.Max(state.RequiredPlaylistReferenceVersion, version),
            operationToken);
    }

    /// <summary>
    /// 起動・リロード進捗で deferred playlist 参照適用完了を反映します。
    /// </summary>
    /// <param name="version">完了版数。</param>
    internal void TryCompleteStartupProgressPlaylistReference(int version, long expectedOperationToken = 0L)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive
                || (expectedOperationToken != 0L && startupProgressState.OperationToken != expectedOperationToken)
                || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.PlaylistReferenceApplied))
            {
                return;
            }
            shouldComplete = version >= startupProgressState.RequiredPlaylistReferenceVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.PlaylistReferenceApplied, operationToken);
        }
    }

    /// <summary>
    /// 起動・リロード進捗で deferred 外部プレイリスト同期を待機対象に追加します。
    /// </summary>
    /// <param name="reason">要求理由。</param>
    /// <param name="version">要求版数。</param>
    internal void TrackStartupProgressExternalSyncRequest(string reason, int version, long operationToken = 0L)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ExternalPlaylistSyncDone,
            version,
            reason,
            state => ShouldTrackStartupProgressExternalSync(reason, state.OperationKind),
            state => state.RequiredExternalSyncVersion = Math.Max(state.RequiredExternalSyncVersion, version),
            operationToken);
    }

    /// <summary>
    /// 起動・リロード進捗で deferred 外部プレイリスト同期完了を反映します。
    /// </summary>
    /// <param name="version">完了版数。</param>
    internal void TryCompleteStartupProgressExternalSync(int version, long expectedOperationToken = 0L)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive
                || (expectedOperationToken != 0L && startupProgressState.OperationToken != expectedOperationToken)
                || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ExternalPlaylistSyncDone))
            {
                return;
            }
            shouldComplete = version >= startupProgressState.RequiredExternalSyncVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ExternalPlaylistSyncDone, operationToken);
        }
    }

    /// <summary>
    /// maintenance deferred 要求を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="requestedVersion">要求版数。</param>
    internal void TrackStartupProgressMaintenanceRequested(int requestedVersion)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.MaintenanceDeferredDone,
            requestedVersion,
            "maintenance_deferred",
            state => requestedVersion > state.MaintenanceRequestedBaselineVersion,
            state => state.RequiredMaintenanceCompletedVersion = Math.Max(state.RequiredMaintenanceCompletedVersion, requestedVersion));
    }

    /// <summary>
    /// installable maintenance deferred 要求を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="requestedVersion">要求版数。</param>
    internal void TrackStartupProgressInstallableMaintenanceRequested(int requestedVersion)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.InstallableMaintenanceDeferredDone,
            requestedVersion,
            "installable_maintenance_deferred",
            state => requestedVersion > state.InstallableMaintenanceRequestedBaselineVersion,
            state => state.RequiredInstallableMaintenanceCompletedVersion = Math.Max(state.RequiredInstallableMaintenanceCompletedVersion, requestedVersion));
    }

    internal void TrackStartupProgressScoreHydrationRequested(int requestedVersion)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ScoreHydrationDone,
            requestedVersion,
            "score_hydration",
            state => requestedVersion > state.ScoreHydrationRequestedBaselineVersion,
            state => state.RequiredScoreHydrationCompletedVersion = Math.Max(state.RequiredScoreHydrationCompletedVersion, requestedVersion));
    }

    internal void TrackStartupProgressRankingRefreshRequested(int requestedVersion)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.RankingRefreshDone,
            requestedVersion,
            "ranking_refresh",
            state => requestedVersion > state.RankingRefreshRequestedBaselineVersion,
            state => state.RequiredRankingRefreshCompletedVersion = Math.Max(state.RequiredRankingRefreshCompletedVersion, requestedVersion));
    }

    internal void TrackStartupProgressChartDigestBackfillRequested(int requestedVersion)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartDigestBackfillDone,
            requestedVersion,
            "chart_digest_backfill",
            state => requestedVersion > state.ChartDigestBackfillBaselineCompletedVersion,
            state => state.RequiredChartDigestBackfillCompletedVersion = Math.Max(state.RequiredChartDigestBackfillCompletedVersion, requestedVersion));
    }

    internal void TrackStartupProgressChartInfoBackfillRequested(int requestedVersion)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartInfoBackfillDone,
            requestedVersion,
            "chart_info_backfill",
            state => requestedVersion > state.ChartInfoBackfillBaselineCompletedVersion,
            state => state.RequiredChartInfoBackfillCompletedVersion = Math.Max(state.RequiredChartInfoBackfillCompletedVersion, requestedVersion));
    }

    internal void TrackStartupProgressChartInfoHydrationRequested(int requestedVersion)
    {
        long operationToken = GetActiveStartupProgressOperationToken();
        int expectedBackfillVersion = versionSnapshotProvider().ChartInfoBackfillRequestedVersion + 1;
        bool shouldTrack = TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartInfoHydrationDone,
            requestedVersion,
            "chart_info_hydration",
            state => requestedVersion > state.ChartInfoHydrationBaselineCompletedVersion,
            state => state.RequiredChartInfoHydrationCompletedVersion = Math.Max(state.RequiredChartInfoHydrationCompletedVersion, requestedVersion),
            operationToken);
        if (!shouldTrack)
        {
            return;
        }
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartInfoBackfillDone,
            expectedBackfillVersion,
            "chart_info_backfill_after_hydration",
            state => true,
            state => state.RequiredChartInfoBackfillCompletedVersion = Math.Max(state.RequiredChartInfoBackfillCompletedVersion, expectedBackfillVersion),
            operationToken);
    }

    internal void TrackStartupProgressPlaylistEntriesHydrationRequested(int requestedVersion, long operationToken = 0L)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            requestedVersion,
            "playlist_entries_hydration",
            state => requestedVersion > state.PlaylistEntriesHydrationBaselineCompletedVersion,
            state =>
            {
                state.PlaylistReferenceFromHydrationRequested = true;
                state.RequiredPlaylistEntriesHydrationCompletedVersion = Math.Max(state.RequiredPlaylistEntriesHydrationCompletedVersion, requestedVersion);
            },
            operationToken);
    }

    internal void TryCompleteStartupProgressPlaylistReferenceFromHydration(int completedVersion, long expectedOperationToken = 0L)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (startupProgressState.IsActive
                && (expectedOperationToken == 0L || startupProgressState.OperationToken == expectedOperationToken)
                && startupProgressState.PlaylistReferenceFromHydrationRequested
                && completedVersion >= startupProgressState.RequiredPlaylistEntriesHydrationCompletedVersion
                && (startupProgressState.ExpectedPhases & StartupProgressPhase.PlaylistReferenceApplied) != 0)
            {
                startupProgressState.RequestedPhases |= StartupProgressPhase.PlaylistReferenceApplied;
                startupProgressState.RequiredPlaylistReferenceVersion = Math.Max(startupProgressState.RequiredPlaylistReferenceVersion, completedVersion);
                startupProgressState.CompletionHideScheduled = false;
                operationToken = startupProgressState.OperationToken;
                shouldComplete = true;
            }
        }
        if (!shouldComplete)
        {
            return;
        }
        RaiseStartupProgressPropertyChanged(nameof(IsStartupProgressBlockingDedicatedStatus));
        RecomputeStartupProgressPresentation(operationToken);
        MarkStartupProgressPhaseCompleted(StartupProgressPhase.PlaylistReferenceApplied, operationToken);
    }

    internal void TrackStartupProgressPlaylistEntriesHydrationDirectRequest(int requestedVersion, string reason, long operationToken = 0L)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            requestedVersion,
            reason,
            state => requestedVersion > state.PlaylistEntriesHydrationBaselineCompletedVersion,
            state => state.RequiredPlaylistEntriesHydrationCompletedVersion = Math.Max(state.RequiredPlaylistEntriesHydrationCompletedVersion, requestedVersion),
            operationToken);
    }

    internal void UpdateStartupProgressChartDigestBackfillStatus(int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartDigestBackfillDone))
            {
                return;
            }
            startupProgressState.ChartDigestBackfillTotalCount = totalCount;
            startupProgressState.ChartDigestBackfillProcessedCount = processedCount;
            startupProgressState.ChartDigestBackfillCurrentPath = currentPath ?? string.Empty;
        }
        RecomputeStartupProgressPresentation();
    }

    internal void UpdateStartupProgressLibraryInitializationStatus(BMSLibrary.LibraryInitializationProgressStage stage, string scannerLabel, int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return;
            }
            startupProgressState.LibraryInitializationProgressStage = stage;
            startupProgressState.LibraryInitializationProgressScannerLabel = scannerLabel ?? string.Empty;
            startupProgressState.LibraryInitializationProgressTotalCount = Math.Max(0, totalCount);
            startupProgressState.LibraryInitializationProgressProcessedCount = Math.Max(0, processedCount);
            startupProgressState.LibraryInitializationProgressCurrentPath = currentPath ?? string.Empty;
        }
        RecomputeStartupProgressPresentation();
    }

    internal void UpdateStartupProgressChartInfoBackfillStatus(int totalCount, int processedCount, string currentPath)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoBackfillDone))
            {
                return;
            }
            startupProgressState.ChartInfoBackfillTotalCount = totalCount;
            startupProgressState.ChartInfoBackfillProcessedCount = processedCount;
            startupProgressState.ChartInfoBackfillCurrentPath = currentPath ?? string.Empty;
        }
        RecomputeStartupProgressPresentation();
    }

    internal void UpdateStartupProgressChartInfoHydrationStatus(int totalCount, int appliedCount)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoHydrationDone))
            {
                return;
            }
            startupProgressState.ChartInfoHydrationTotalCount = totalCount;
            startupProgressState.ChartInfoHydrationAppliedCount = appliedCount;
        }
        RecomputeStartupProgressPresentation();
    }

    internal void TryCompleteStartupProgressLibraryDatabaseLoad(int completedVersion)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.LibraryDatabaseLoadDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion > startupProgressState.LibraryDatabaseLoadBaselineCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryDatabaseLoadDone, operationToken);
        }
    }

    internal void TryCompleteStartupProgressLibraryFileEnumeration(int completedVersion)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.LibraryFileEnumerationDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion > startupProgressState.LibraryFileEnumerationBaselineCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryFileEnumerationDone, operationToken);
        }
    }

    internal void TryCompleteStartupProgressLibraryFileDiff(int completedVersion)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || (startupProgressState.ExpectedPhases & StartupProgressPhase.LibraryFileDiffDone) == 0)
            {
                return;
            }
            shouldComplete = completedVersion > startupProgressState.LibraryFileDiffBaselineCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.LibraryFileDiffDone, operationToken);
        }
    }

    internal void TryCompleteStartupProgressChartDigestBackfill(int completedVersion)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartDigestBackfillDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredChartDigestBackfillCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ChartDigestBackfillDone, operationToken);
        }
    }

    internal void TryCompleteStartupProgressChartInfoBackfill(int completedVersion)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoBackfillDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredChartInfoBackfillCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ChartInfoBackfillDone, operationToken);
        }
    }

    internal void TryCompleteStartupProgressChartInfoHydration(int completedVersion)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoHydrationDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredChartInfoHydrationCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ChartInfoHydrationDone, operationToken);
        }
    }

    internal void TryCompleteStartupProgressPlaylistEntriesHydration(int completedVersion, long expectedOperationToken = 0L)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive
                || (expectedOperationToken != 0L && startupProgressState.OperationToken != expectedOperationToken)
                || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.PlaylistEntriesHydrationDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredPlaylistEntriesHydrationCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.PlaylistEntriesHydrationDone, operationToken);
        }
    }

    /// <summary>
    /// maintenance deferred 完了を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    internal void TryCompleteStartupProgressMaintenance(int completedVersion)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.MaintenanceDeferredDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredMaintenanceCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.MaintenanceDeferredDone, operationToken);
        }
    }

    /// <summary>
    /// installable maintenance deferred 完了を起動・リロード進捗へ反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    internal void TryCompleteStartupProgressInstallableMaintenance(int completedVersion)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.InstallableMaintenanceDeferredDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredInstallableMaintenanceCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.InstallableMaintenanceDeferredDone, operationToken);
        }
    }

    /// <summary>
    /// 起動・リロード進捗で deferred score hydration 完了を反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    internal void TryCompleteStartupProgressScoreHydration(int completedVersion)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ScoreHydrationDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredScoreHydrationCompletedVersion
                && completedVersion > startupProgressState.ScoreHydrationBaselineCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.ScoreHydrationDone, operationToken);
        }
    }

    /// <summary>
    /// 起動・リロード進捗で deferred ranking refresh 完了を反映します。
    /// </summary>
    /// <param name="completedVersion">完了版数。</param>
    internal void TryCompleteStartupProgressRankingRefresh(int completedVersion)
    {
        bool shouldComplete = false;
        long operationToken = 0L;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.RankingRefreshDone))
            {
                return;
            }
            shouldComplete = completedVersion >= startupProgressState.RequiredRankingRefreshCompletedVersion
                && completedVersion > startupProgressState.RankingRefreshBaselineCompletedVersion;
            operationToken = startupProgressState.OperationToken;
        }
        if (shouldComplete)
        {
            MarkStartupProgressPhaseCompleted(StartupProgressPhase.RankingRefreshDone, operationToken);
        }
    }

    /// <summary>
    /// 起動・リロード進捗の表示を現在の内部状態から再計算します。
    /// </summary>
    internal void RecomputeStartupProgressPresentation(long expectedOperationToken = 0L)
    {
        bool isActive;
        string label;
        string subLabel;
        double value;
        double maximum;
        bool shouldHideLater = false;
        long hideOperationToken = 0L;
        long reflectOperationToken = 0L;
        bool operationCompletedForLog = false;
        StartupProgressOperationKind operationKindForLog = StartupProgressOperationKind.None;
        lock (startupProgressLock)
        {
            StartupProgressState state = startupProgressState;
            if (expectedOperationToken != 0L
                && (!state.IsActive || state.OperationToken != expectedOperationToken))
            {
                return;
            }
            isActive = state.IsActive;
            reflectOperationToken = state.OperationToken;
            operationKindForLog = state.OperationKind;
            if (!isActive)
            {
                label = string.Empty;
                subLabel = string.Empty;
                value = 0.0;
                maximum = 1.0;
            }
            else
            {
                maximum = Math.Max(1.0, CountExpectedStartupProgressPhases(state));
                value = CountCompletedExpectedStartupProgressPhases(state);
                bool operableCompleted = (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
                bool operationCompleted = !state.IsFailed && AreExpectedStartupProgressPhasesCompleted(state);
                operationCompletedForLog = operationCompleted;
                if (state.IsFailed)
                {
                    label = GetStartupProgressFailedLabel(state.OperationKind);
                    subLabel = !string.IsNullOrWhiteSpace(state.FailureSubLabel) ? state.FailureSubLabel : GetStartupProgressSubLabel(state);
                }
                else if (operationCompleted)
                {
                    label = GetStartupProgressCompletedLabel(state.OperationKind);
                    subLabel = string.Empty;
                    if (!state.CompletionHideScheduled)
                    {
                        state.CompletionHideScheduled = true;
                        startupProgressState = state;
                        shouldHideLater = true;
                        hideOperationToken = state.OperationToken;
                    }
                }
                else if (operableCompleted)
                {
                    label = BeMusicSeeker.Properties.Resources.Statusbar_progress_operable_background;
                    subLabel = GetStartupProgressSubLabel(state);
                }
                else
                {
                    label = GetStartupProgressRunningLabel(state.OperationKind);
                    subLabel = GetStartupProgressSubLabel(state);
                }
            }
        }
        if (operationCompletedForLog)
        {
            startupInitializationCompleted(reflectOperationToken);
        }
        Action reflect = delegate
        {
            lock (startupProgressLock)
            {
                if (isActive)
                {
                    if (!startupProgressState.IsActive || startupProgressState.OperationToken != reflectOperationToken)
                    {
                        return;
                    }
                }
                else if (startupProgressState.IsActive)
                {
                    return;
                }
            }
            ApplyPresentation(isActive, label, subLabel, value, maximum);
        };
        dispatch(reflect);
        if (shouldHideLater)
        {
            ScheduleStartupProgressHide(hideOperationToken);
        }
    }
    internal void ScheduleStartupProgressHide(long operationToken)
    {
        Task.Run(async delegate
        {
            await completionHideDelay().ConfigureAwait(false);
            bool shouldClear = false;
            lock (startupProgressLock)
            {
                if (startupProgressState.IsActive && !startupProgressState.IsFailed && startupProgressState.OperationToken == operationToken && startupProgressState.CompletionHideScheduled)
                {
                    startupProgressState = new StartupProgressState();
                    shouldClear = true;
                }
            }
            if (shouldClear)
            {
                RaiseStartupProgressPropertyChanged(nameof(IsOperationActive));
                RaiseStartupProgressPropertyChanged(nameof(IsFailed));
                RaiseStartupProgressPropertyChanged(nameof(IsRetryableFailure));
                RecomputeStartupProgressPresentation();
            }
        });
    }

    /// <summary>
    /// operation 種別に応じた進行中ラベルを返します。
    /// </summary>
    /// <param name="operationKind">operation 種別。</param>
    /// <returns>進行中ラベル。</returns>
    internal static string GetStartupProgressRunningLabel(StartupProgressOperationKind operationKind)
    {
        return operationKind switch
        {
            StartupProgressOperationKind.ReloadFileDiff => BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_files,
            StartupProgressOperationKind.ScoreOnly => BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_scores,
            StartupProgressOperationKind.FullReinitialize => BeMusicSeeker.Properties.Resources.Statusbar_progress_full_reinitialize,
            StartupProgressOperationKind.ReloadTables => BeMusicSeeker.Properties.Resources.Statusbar_progress_reload_tables,
            _ => BeMusicSeeker.Properties.Resources.Statusbar_progress_startup,
        };
    }

    /// <summary>
    /// operation 種別に応じた完了ラベルを返します。
    /// </summary>
    /// <param name="operationKind">operation 種別。</param>
    /// <returns>完了ラベル。</returns>
    internal static string GetStartupProgressCompletedLabel(StartupProgressOperationKind operationKind)
    {
        if (operationKind == StartupProgressOperationKind.Startup)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete;
        }
        if (operationKind == StartupProgressOperationKind.FullReinitialize)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete_reinitialize;
        }
        if (operationKind == StartupProgressOperationKind.ScoreOnly)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete_scores;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_complete_reload;
    }

    /// <summary>
    /// operation 種別に応じた失敗ラベルを返します。
    /// </summary>
    /// <param name="operationKind">operation 種別。</param>
    /// <returns>失敗ラベル。</returns>
    internal static string GetStartupProgressFailedLabel(StartupProgressOperationKind operationKind)
    {
        if (operationKind == StartupProgressOperationKind.Startup)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed;
        }
        if (operationKind == StartupProgressOperationKind.FullReinitialize)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed_reinitialize;
        }
        if (operationKind == StartupProgressOperationKind.ScoreOnly)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed_scores;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_failed_reload;
    }

    /// <summary>
    /// 現在の未完了フェーズに対応するサブラベルを返します。
    /// </summary>
    /// <param name="state">進捗状態。</param>
    /// <returns>サブラベル。</returns>
    internal static string GetStartupProgressSubLabel(StartupProgressState state)
    {
        if (!IsStartupProgressLibraryLoadCompleted(state))
        {
            return GetStartupProgressLibraryLoadSubLabel(state);
        }
        if (!IsStartupProgressUiPrepareCompleted(state))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ui_prepare;
        }
        if ((state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) == 0)
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ui_prepare;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.PlaylistEntriesHydrationDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_playlist_load;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartInfoHydrationDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info_load;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartInfoBackfillDone))
        {
            string fileName = string.IsNullOrWhiteSpace(state.ChartInfoBackfillCurrentPath) ? string.Empty : Path.GetFileName(state.ChartInfoBackfillCurrentPath);
            return FormatStartupProgressCountLabel(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info, state.ChartInfoBackfillProcessedCount, state.ChartInfoBackfillTotalCount, fileName);
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartDigestBackfillDone))
        {
            string fileName = string.IsNullOrWhiteSpace(state.ChartDigestBackfillCurrentPath) ? string.Empty : Path.GetFileName(state.ChartDigestBackfillCurrentPath);
            return FormatStartupProgressCountLabel(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info, state.ChartDigestBackfillProcessedCount, state.ChartDigestBackfillTotalCount, fileName);
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.PlaylistReferenceApplied) || !IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ExternalPlaylistSyncDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_playlist_ref;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ScoreHydrationDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_score_hydration;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.RankingRefreshDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ranking_refresh;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.MaintenanceDeferredDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_maintenance;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.InstallableMaintenanceDeferredDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_installable_maintenance;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_background;
    }

    internal static string GetStartupProgressLibraryLoadSubLabel(StartupProgressState state)
    {
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.LibraryDatabaseLoadDone))
        {
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_library_db_load;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.LibraryFileEnumerationDone))
        {
            string scanner = state.LibraryInitializationProgressScannerLabel;
            if (!string.IsNullOrWhiteSpace(scanner))
            {
                return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_enumeration + " (" + scanner + ")";
            }
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_enumeration;
        }
        if (!IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.LibraryFileDiffDone))
        {
            if (state.LibraryInitializationProgressTotalCount > 0)
            {
                string fileName = string.IsNullOrWhiteSpace(state.LibraryInitializationProgressCurrentPath) ? string.Empty : Path.GetFileName(state.LibraryInitializationProgressCurrentPath);
                return FormatStartupProgressCountLabel(BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_diff, state.LibraryInitializationProgressProcessedCount, state.LibraryInitializationProgressTotalCount, fileName);
            }
            return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_diff;
        }
        return BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_library_load;
    }

    internal static string FormatStartupProgressCountLabel(string phaseLabel, int processedCount, int totalCount, string fileName)
    {
        string prefix = "[" + Math.Max(0, processedCount) + "/" + Math.Max(0, totalCount) + "] " + (phaseLabel ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return prefix;
        }
        return prefix + " " + fileName;
    }

    /// <summary>
    /// ライブラリ読込フェーズが完了済みかどうかを返します。
    /// </summary>
    internal static bool IsStartupProgressLibraryLoadCompleted(StartupProgressState state)
    {
        if (state.OperationKind == StartupProgressOperationKind.Startup)
        {
            return (state.CompletedPhases & StartupProgressPhase.StartupReadyData) != 0;
        }
        return (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
    }

    /// <summary>
    /// 画面準備フェーズが完了済みかどうかを返します。
    /// </summary>
    internal static bool IsStartupProgressUiPrepareCompleted(StartupProgressState state)
    {
        if (state.OperationKind == StartupProgressOperationKind.Startup)
        {
            return (state.CompletedPhases & StartupProgressPhase.StartupReadyUi) != 0;
        }
        return (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) != 0;
    }

    /// <summary>
    /// プレイリスト参照/保守更新フェーズが完了済みかどうかを返します。
    /// </summary>
    internal static bool IsStartupProgressReferencePhaseCompleted(StartupProgressState state)
    {
        return IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.PlaylistReferenceApplied)
            && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ExternalPlaylistSyncDone)
            && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.MaintenanceDeferredDone)
            && IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.InstallableMaintenanceDeferredDone);
    }

    internal static int CountExpectedStartupProgressPhases(StartupProgressState state)
    {
        int count = 0;
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.CoreInitializeStarted, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryDatabaseLoadDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileEnumerationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileDiffDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyData, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyUi, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyOperable, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistReferenceApplied, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ExternalPlaylistSyncDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistEntriesHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.MaintenanceDeferredDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.InstallableMaintenanceDeferredDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartDigestBackfillDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoBackfillDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ScoreHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.RankingRefreshDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.StartupBackgroundTasksDone, ref count);
        return count;
    }

    internal static StartupProgressPhase GetInitialExpectedStartupProgressPhases(StartupProgressOperationKind operationKind)
    {
        return operationKind switch
        {
            StartupProgressOperationKind.Startup => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.LibraryDatabaseLoadDone
                                | StartupProgressPhase.LibraryFileEnumerationDone
                                | StartupProgressPhase.LibraryFileDiffDone
                                | StartupProgressPhase.StartupReadyData
                                | StartupProgressPhase.StartupReadyUi
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.ScoreHydrationDone
                                | StartupProgressPhase.ChartDigestBackfillDone
                                | StartupProgressPhase.ChartInfoBackfillDone
                                | StartupProgressPhase.ChartInfoHydrationDone
                                | StartupProgressPhase.PlaylistEntriesHydrationDone
                                | StartupProgressPhase.StartupBackgroundTasksDone,
            StartupProgressOperationKind.FullReinitialize => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.LibraryDatabaseLoadDone
                                | StartupProgressPhase.LibraryFileEnumerationDone
                                | StartupProgressPhase.LibraryFileDiffDone
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.ScoreHydrationDone
                                | StartupProgressPhase.RankingRefreshDone
                                | StartupProgressPhase.MaintenanceDeferredDone
                                | StartupProgressPhase.InstallableMaintenanceDeferredDone
                                | StartupProgressPhase.ChartDigestBackfillDone
                                | StartupProgressPhase.ChartInfoBackfillDone
                                | StartupProgressPhase.ChartInfoHydrationDone
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressOperationKind.ReloadFileDiff => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.LibraryFileEnumerationDone
                                | StartupProgressPhase.LibraryFileDiffDone
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressOperationKind.ScoreOnly => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.ScoreHydrationDone
                                | StartupProgressPhase.RankingRefreshDone,
            StartupProgressOperationKind.ReloadTables => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.ExternalPlaylistSyncDone
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            _ => StartupProgressPhase.CoreInitializeStarted | StartupProgressPhase.StartupReadyOperable,
        };
    }

    internal static int CountCompletedExpectedStartupProgressPhases(StartupProgressState state)
    {
        int count = 0;
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.CoreInitializeStarted, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryDatabaseLoadDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileEnumerationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.LibraryFileDiffDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyData, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyUi, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupReadyOperable, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistReferenceApplied, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ExternalPlaylistSyncDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.PlaylistEntriesHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.MaintenanceDeferredDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.InstallableMaintenanceDeferredDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartDigestBackfillDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoBackfillDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ScoreHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.RankingRefreshDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.StartupBackgroundTasksDone, ref count);
        return count;
    }

    internal static void CountExpectedStartupProgressPhase(StartupProgressState state, StartupProgressPhase phase, ref int count)
    {
        if (IsStartupProgressPhaseExpected(state, phase))
        {
            count++;
        }
    }

    internal static void CountCompletedExpectedStartupProgressPhase(StartupProgressState state, StartupProgressPhase phase, ref int count)
    {
        if (IsStartupProgressPhaseExpected(state, phase) && (state.CompletedPhases & phase) != 0)
        {
            count++;
        }
    }

    internal static bool AreExpectedStartupProgressPhasesCompleted(StartupProgressState state)
    {
        StartupProgressPhase expected = state.ExpectedPhases;
        return expected == StartupProgressPhase.None || (state.CompletedPhases & expected) == expected;
    }

    /// <summary>
    /// 指定フェーズが待機対象かどうかを返します。
    /// </summary>
    internal static bool IsStartupProgressPhaseExpected(StartupProgressState state, StartupProgressPhase phase)
    {
        return (state.ExpectedPhases & phase) != 0;
    }

    /// <summary>
    /// 指定フェーズが完了済み、または待機対象外かどうかを返します。
    /// </summary>
    internal static bool IsStartupProgressPhaseCompletedOrNotExpected(StartupProgressState state, StartupProgressPhase phase)
    {
        return !IsStartupProgressPhaseExpected(state, phase) || (state.CompletedPhases & phase) != 0;
    }

    /// <summary>
    /// deferred playlist 参照要求を現在の operation 進捗へ関連付けるかどうかを返します。
    /// </summary>
    internal static bool ShouldTrackStartupProgressPlaylistReference(string reason, StartupProgressOperationKind operationKind)
    {
        return operationKind switch
        {
            StartupProgressOperationKind.Startup => string.Equals(reason, "Initialize", StringComparison.Ordinal) || string.Equals(reason, "DeferredExternalSync:Initialize", StringComparison.Ordinal) || string.Equals(reason, "PlaylistEntriesHydration", StringComparison.Ordinal),
            StartupProgressOperationKind.ReloadFileDiff => string.Equals(reason, "ReloadFileDiff", StringComparison.Ordinal) || string.Equals(reason, "PlaylistEntriesHydration", StringComparison.Ordinal),
            StartupProgressOperationKind.FullReinitialize => string.Equals(reason, "FullReinitialize", StringComparison.Ordinal) || string.Equals(reason, "PlaylistEntriesHydration", StringComparison.Ordinal),
            StartupProgressOperationKind.ReloadTables => string.Equals(reason, "DeferredExternalSync:ReloadTables", StringComparison.Ordinal) || string.Equals(reason, "PlaylistEntriesHydration", StringComparison.Ordinal),
            _ => false,
        };
    }

    /// <summary>
    /// deferred 外部プレイリスト同期要求を現在の operation 進捗へ関連付けるかどうかを返します。
    /// </summary>
    internal static bool ShouldTrackStartupProgressExternalSync(string reason, StartupProgressOperationKind operationKind)
    {
        return operationKind switch
        {
            StartupProgressOperationKind.Startup => string.Equals(reason, "Initialize", StringComparison.Ordinal),
            StartupProgressOperationKind.ReloadTables => string.Equals(reason, "ReloadTables", StringComparison.Ordinal),
            _ => false,
        };
    }
}
internal enum StartupProgressOperationKind
{
    None,
    Startup,
    ReloadFileDiff,
    ScoreOnly,
    FullReinitialize,
    ReloadTables
}

/// <summary>
/// 起動・リロード進捗の内部フェーズ集合です。
/// </summary>
[Flags]
internal enum StartupProgressPhase
{
    None = 0,
    CoreInitializeStarted = 1,
    StartupReadyData = 2,
    StartupReadyUi = 4,
    StartupReadyOperable = 8,
    PlaylistReferenceApplied = 16,
    ExternalPlaylistSyncDone = 32,
    ScoreHydrationDone = 64,
    RankingRefreshDone = 128,
    MaintenanceDeferredDone = 256,
    ChartDigestBackfillDone = 512,
    ChartInfoBackfillDone = 1024,
    ChartInfoHydrationDone = 2048,
    PlaylistEntriesHydrationDone = 4096,
    LibraryDatabaseLoadDone = 8192,
    LibraryFileEnumerationDone = 16384,
    LibraryFileDiffDone = 32768,
    InstallableMaintenanceDeferredDone = 65536,
    StartupBackgroundTasksDone = 262144
}

/// <summary>
/// 起動・リロード進捗の内部状態です。
/// UI 表示は Completed/Expected フェーズ集合から再計算します。
/// </summary>
internal sealed class StartupProgressState
{
    internal long OperationToken;

    internal StartupProgressOperationKind OperationKind;

    internal bool IsActive;

    internal bool IsFailed;

    internal bool IsRetryableFailure;

    internal string FailureSubLabel = string.Empty;

    internal StartupProgressPhase CompletedPhases;

    internal StartupProgressPhase ExpectedPhases;

    internal StartupProgressPhase RequestedPhases;

    internal StartupProgressPhase SkippedPhases;

    internal int ScoreHydrationBaselineCompletedVersion;

    internal int ScoreHydrationRequestedBaselineVersion;

    internal int RequiredScoreHydrationCompletedVersion;

    internal int RankingRefreshBaselineCompletedVersion;

    internal int RankingRefreshRequestedBaselineVersion;

    internal int RequiredRankingRefreshCompletedVersion;

    internal int MaintenanceRequestedBaselineVersion;

    internal int InstallableMaintenanceRequestedBaselineVersion;

    internal int ChartDigestBackfillBaselineCompletedVersion;

    internal int ChartInfoBackfillBaselineCompletedVersion;

    internal int ChartInfoHydrationBaselineCompletedVersion;

    internal int PlaylistEntriesHydrationBaselineCompletedVersion;

    internal int LibraryDatabaseLoadBaselineCompletedVersion;

    internal int LibraryFileEnumerationBaselineCompletedVersion;

    internal int LibraryFileDiffBaselineCompletedVersion;

    internal int RequiredPlaylistReferenceVersion;

    internal bool PlaylistReferenceFromHydrationRequested;

    internal int RequiredExternalSyncVersion;

    internal int RequiredPlaylistEntriesHydrationCompletedVersion;

    internal int RequiredMaintenanceCompletedVersion;

    internal int RequiredInstallableMaintenanceCompletedVersion;

    internal int RequiredChartDigestBackfillCompletedVersion;

    internal int RequiredChartInfoBackfillCompletedVersion;

    internal int RequiredChartInfoHydrationCompletedVersion;

    internal int ChartDigestBackfillTotalCount;

    internal int ChartDigestBackfillProcessedCount;

    internal string ChartDigestBackfillCurrentPath = string.Empty;

    internal int ChartInfoBackfillTotalCount;

    internal int ChartInfoBackfillProcessedCount;

    internal string ChartInfoBackfillCurrentPath = string.Empty;

    internal int ChartInfoHydrationTotalCount;

    internal int ChartInfoHydrationAppliedCount;

    internal BMSLibrary.LibraryInitializationProgressStage LibraryInitializationProgressStage;

    internal string LibraryInitializationProgressScannerLabel = string.Empty;

    internal int LibraryInitializationProgressTotalCount;

    internal int LibraryInitializationProgressProcessedCount;

    internal string LibraryInitializationProgressCurrentPath = string.Empty;

    internal bool CompletionHideScheduled;

    internal DateTime? LastCompletedAtUtc;
}
