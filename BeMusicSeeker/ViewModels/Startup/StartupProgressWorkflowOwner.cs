using System;
using System.Collections.Generic;
using System.Linq;
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
    internal int ChartInfoHydrationCompletedVersion { get; init; }
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
    private readonly Func<Task> completionHideDelay;
    private readonly object backgroundTaskProgressSynchronization;
    private readonly object startupProgressLock = new();
    private StartupProgressState startupProgressState = new();
    private long startupProgressOperationTokenSeed;
    private bool isActive;
    private IReadOnlyList<BMSLibrary.LibraryInitializationProgressSnapshot> libraryInitializationStatuses = Array.Empty<BMSLibrary.LibraryInitializationProgressSnapshot>();

    private bool isStartupUiInteractionBlocked;
    private string label = string.Empty;
    private string subLabel = string.Empty;
    private double progressValue;
    private double progressMaximum = 1.0;

    /// <summary>
    /// 既存の実行・表示境界を起動進捗へ接続します。
    /// </summary>
    /// <param name="versionSnapshotProvider">操作開始時の現在の完了版を返す境界。</param>
    /// <param name="prepareOperation">新しい操作tokenの構成要素を準備する境界。</param>
    /// <param name="dispatch">表示を所有するスレッドへ通知する境界。</param>
    /// <param name="log">進捗診断の出力。</param>
    /// <param name="startupInitializationCompleted">必須初期化の完了を記録する境界。</param>
    /// <param name="backgroundTaskProgressSynchronization">操作準備とscheduler登録の既存同期。</param>
    /// <param name="completionHideDelay">
    /// 完了の余韻表示を消すまで待ちます。省略時は既存の2秒待機を使います。
    /// </param>
    internal StartupProgressWorkflowOwner(
        Func<StartupProgressVersionSnapshot> versionSnapshotProvider,
        Action<StartupProgressOperationKind, long> prepareOperation,
        Action<Action> dispatch,
        Action<string> log,
        Action<long> startupInitializationCompleted,
        object backgroundTaskProgressSynchronization,
        Func<Task> completionHideDelay = null)
    {
        this.versionSnapshotProvider = versionSnapshotProvider ?? throw new ArgumentNullException(nameof(versionSnapshotProvider));
        this.prepareOperation = prepareOperation ?? throw new ArgumentNullException(nameof(prepareOperation));
        this.dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        this.log = log ?? throw new ArgumentNullException(nameof(log));
        this.startupInitializationCompleted = startupInitializationCompleted
            ?? throw new ArgumentNullException(nameof(startupInitializationCompleted));
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
    /// <summary>操作別の固定名と、完了または失敗時の状態を取得します。</summary>
    public string Label
    {
        get => label;
        private set => SetValue(ref label, value ?? string.Empty, nameof(Label));
    }
    /// <summary>明示された失敗理由を取得します。通常の処理詳細は子行に表示します。</summary>
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
    internal long GetActiveStartupProgressOperationToken()
    {
        lock (startupProgressLock)
        {
            return startupProgressState.IsActive ? startupProgressState.OperationToken : 0L;
        }
    }
    internal bool IsStartupProgressOperationTokenCurrent(long operationToken)
    {
        if (operationToken == 0L)
        {
            return true;
        }

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
    /// <summary>直接待った必須起動と親受付の解放後に完了します。後続schedulerのidleを成功条件にしません。</summary>
    internal void CompleteRequiredInitialization(long operationToken)
    {
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive || startupProgressState.OperationToken != operationToken
                || startupProgressState.IsFailed) { return; }
            startupProgressState.CompletedPhases |= startupProgressState.ExpectedPhases;
        }
        RecomputeStartupProgressPresentation(operationToken);
    }

    internal void ApplyPresentation(bool active, string valueLabel, string valueSubLabel, double progress, double maximum)
    {
        IsActive = active;
        Label = valueLabel;
        SubLabel = valueSubLabel;
        Value = progress;
        Maximum = maximum;
        RaisePropertyChanged(nameof(DetailRows));
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
                libraryInitializationStatuses = Array.Empty<BMSLibrary.LibraryInitializationProgressSnapshot>();
            }
            prepareOperation(operationKind, operationToken);
        }
        RaiseStartupProgressPropertyChanged(nameof(IsOperationActive));
        RaiseStartupProgressPropertyChanged(nameof(IsFailed));
        RaiseStartupProgressPropertyChanged(nameof(IsRetryableFailure));
        RecomputeStartupProgressPresentation(operationToken);
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
            marked = true;
        }
        if (!marked)
        {
            return;
        }
        RecomputeStartupProgressPresentation(operationToken);
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
            log?.Invoke("startup_progress_phase_skipped operation=" + operationKind + " phase=" + phase + " reason=" + (reason ?? string.Empty));
            RecomputeStartupProgressPresentation(operationToken);
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
            && phase != StartupProgressPhase.LibraryFileDiffDone;
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
    /// <param name="request">表示用の発生元と実要求版。既存の最大必要版・完了条件とは別に保持します。</param>
    internal void TrackStartupProgressPlaylistReferenceRequest(string reason, int version, long operationToken = 0L, OperationProgressRequest request = null)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.PlaylistReferenceApplied,
            version,
            reason,
            state => ShouldTrackStartupProgressPlaylistReference(reason, state.OperationKind),
            state =>
            {
                state.RequiredPlaylistReferenceVersion = Math.Max(state.RequiredPlaylistReferenceVersion, version);
                state.RequiredPlaylistReferenceProgressRequest = request;
            },
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
    /// <param name="request">表示用の発生元と実要求版。既存の最大必要版・完了条件とは別に保持します。</param>
    internal void TrackStartupProgressExternalSyncRequest(string reason, int version, long operationToken = 0L, OperationProgressRequest request = null)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ExternalPlaylistSyncDone,
            version,
            reason,
            state => ShouldTrackStartupProgressExternalSync(reason, state.OperationKind),
            state =>
            {
                state.RequiredExternalSyncVersion = Math.Max(state.RequiredExternalSyncVersion, version);
                state.RequiredExternalSyncProgressRequest = request;
            },
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

    /// <summary>スコア要求の既存必要版と捕捉した表示識別を記録します。</summary>
    /// <param name="requestedVersion">既存の完了計算へ使う要求版。</param>
    /// <param name="request">受付時の発生元と同主体の実要求版。未捕捉なら親行一致とは扱いません。</param>
    internal void TrackStartupProgressScoreHydrationRequested(int requestedVersion, OperationProgressRequest request = null)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ScoreHydrationDone,
            requestedVersion,
            "score_hydration",
            state => requestedVersion > state.ScoreHydrationRequestedBaselineVersion,
            state =>
            {
                state.RequiredScoreHydrationCompletedVersion = Math.Max(state.RequiredScoreHydrationCompletedVersion, requestedVersion);
                state.RequiredScoreHydrationProgressRequest = request;
            });
    }

    /// <summary>現在の親が実際に待つ要求の発生元と版が一致する場合だけ子行へ所属させます。</summary>
    /// <param name="request">受付時に捕捉した要求識別。</param>
    /// <returns>予定・追跡中の同じ要求であり、失敗していない親に所属する場合にtrue。</returns>
    internal bool IsExecutionProgressPartOfStartup(OperationProgressRequest request)
    {
        lock (startupProgressLock)
        {
            StartupProgressState state = startupProgressState;
            if (!state.IsActive || state.IsFailed || request == null || request.OperationToken == 0
                || request.OperationToken != state.OperationToken)
            {
                return false;
            }
            return request.Source switch
            {
                "score_hydration_deferred" => request == state.RequiredScoreHydrationProgressRequest,
                "chart_info_hydration" => request == state.RequiredChartInfoHydrationProgressRequest,
                "playlist_bmt_output" or "playlist_custom_folder_output_repair"
                    => state.OperationKind is StartupProgressOperationKind.Startup or StartupProgressOperationKind.FullReinitialize,
                "lr2_song_db_sync" => state.OperationKind is StartupProgressOperationKind.Startup
                    or StartupProgressOperationKind.FullReinitialize or StartupProgressOperationKind.ReloadFileDiff,
                "playlist_entries_hydration" => request == state.RequiredPlaylistEntriesHydrationProgressRequest
                    || request == state.RequiredPlaylistReferenceProgressRequest,
                "playlist_ref_apply" => request == state.RequiredPlaylistReferenceProgressRequest
                    || request == state.RequiredPlaylistEntriesHydrationProgressRequest,
                "external_playlist_sync" => request == state.RequiredExternalSyncProgressRequest
                    || request == state.RequiredPlaylistReferenceProgressRequest,
                _ => false
            };
        }
    }

    /// <summary>必須の保存済み譜面情報読込みの必要版・表示識別だけを記録します。</summary>
    /// <param name="requestedVersion">既存の完了計算へ使う要求版。</param>
    /// <param name="request">受付時の発生元と同主体の実要求版。未捕捉なら親行一致とは扱いません。</param>
    internal void TrackStartupProgressChartInfoHydrationRequested(int requestedVersion, OperationProgressRequest request = null)
    {
        long operationToken = GetActiveStartupProgressOperationToken();
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.ChartInfoHydrationDone,
            requestedVersion,
            "chart_info_hydration",
            state => requestedVersion > state.ChartInfoHydrationBaselineCompletedVersion,
            state =>
            {
                state.RequiredChartInfoHydrationCompletedVersion = Math.Max(state.RequiredChartInfoHydrationCompletedVersion, requestedVersion);
                state.RequiredChartInfoHydrationProgressRequest = request;
            },
            operationToken);

    }

    /// <summary>項目読込みの必要版と捕捉した表示識別を、既存の完了条件へ記録します。</summary>
    /// <param name="requestedVersion">既存の完了計算へ使う要求版。</param>
    /// <param name="request">受付時の発生元と同主体の実要求版。未捕捉なら親行一致とは扱いません。</param>
    internal void TrackStartupProgressPlaylistEntriesHydrationRequested(int requestedVersion, long operationToken = 0L, OperationProgressRequest request = null)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            requestedVersion,
            "playlist_entries_hydration",
            state => requestedVersion > state.PlaylistEntriesHydrationBaselineCompletedVersion,
            state =>
            {
                state.PlaylistReferenceFromHydrationRequested = true;
                state.RequiredPlaylistEntriesHydrationProgressRequest = request;
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
                startupProgressState.RequiredPlaylistReferenceProgressRequest = startupProgressState.RequiredPlaylistEntriesHydrationProgressRequest;
                startupProgressState.CompletionHideScheduled = false;
                operationToken = startupProgressState.OperationToken;
                shouldComplete = true;
            }
        }
        if (!shouldComplete)
        {
            return;
        }
        RecomputeStartupProgressPresentation(operationToken);
        MarkStartupProgressPhaseCompleted(StartupProgressPhase.PlaylistReferenceApplied, operationToken);
    }

    /// <summary>参照更新等が実際に待つ項目読込みの必要版と表示元を記録します。</summary>
    /// <param name="requestedVersion">既存の完了計算へ使う要求版。</param>
    /// <param name="request">受付時の発生元と同主体の実要求版。未捕捉なら親行一致とは扱いません。</param>
    internal void TrackStartupProgressPlaylistEntriesHydrationDirectRequest(int requestedVersion, string reason, long operationToken = 0L, OperationProgressRequest request = null)
    {
        TryTrackStartupProgressPhaseRequest(
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            requestedVersion,
            reason,
            state => requestedVersion > state.PlaylistEntriesHydrationBaselineCompletedVersion,
            state =>
            {
                state.RequiredPlaylistEntriesHydrationCompletedVersion = Math.Max(state.RequiredPlaylistEntriesHydrationCompletedVersion, requestedVersion);
                state.RequiredPlaylistEntriesHydrationProgressRequest = request;
            },
            operationToken);
    }

    /// <summary>現在の操作が実行している独立した詳細処理を、親の段階数とは別に取得します。</summary>
    public IReadOnlyList<OperationProgressRow> DetailRows
    {
        get
        {
            lock (startupProgressLock)
            {
                var rows = new List<OperationProgressRow>();
                StartupProgressState state = startupProgressState;
                if (!state.IsActive || state.IsFailed)
                {
                    return rows;
                }

                if (state.OperationKind == StartupProgressOperationKind.Startup
                    && (state.CompletedPhases & StartupProgressPhase.StartupReadyData) != 0
                    && (state.CompletedPhases & StartupProgressPhase.StartupReadyOperable) == 0)
                {
                    AddDetail(rows, "task:ui_prepare", BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_ui_prepare,
                        0, 0, string.Empty);
                }

                foreach (BMSLibrary.LibraryInitializationProgressSnapshot status in libraryInitializationStatuses.OrderBy(s => s.Stage).ThenBy(s => s.ScannerLabel, StringComparer.Ordinal))
                {
                    StartupProgressPhase phase = status.Stage switch
                    {
                        BMSLibrary.LibraryInitializationProgressStage.DatabaseLoad => StartupProgressPhase.LibraryDatabaseLoadDone,
                        BMSLibrary.LibraryInitializationProgressStage.FileEnumeration => StartupProgressPhase.LibraryFileEnumerationDone,
                        _ => StartupProgressPhase.LibraryFileDiffDone
                    };
                    if (status.Stage == BMSLibrary.LibraryInitializationProgressStage.None
                        || IsStartupProgressPhaseCompletedOrNotExpected(state, phase))
                    {
                        continue;
                    }

                    string label = status.Stage switch
                    {
                        BMSLibrary.LibraryInitializationProgressStage.DatabaseLoad => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_library_db_load,
                        BMSLibrary.LibraryInitializationProgressStage.FileEnumeration => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_enumeration,
                        BMSLibrary.LibraryInitializationProgressStage.Lr2FolderFileCheck => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_lr2_folder_file_check,
                        _ => BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_file_diff
                    };
                    if (!string.IsNullOrWhiteSpace(status.ScannerLabel))
                    {
                        label += " (" + status.ScannerLabel + ")";
                    }

                    AddDetail(rows, "library:" + status.Stage + ":" + status.ScannerLabel, label,
                        status.TotalCount, status.ProcessedCount, status.CurrentPath);
                }
                if (state.ChartInfoHydrationTotalCount > 0 && !IsStartupProgressPhaseCompletedOrNotExpected(state, StartupProgressPhase.ChartInfoHydrationDone))
                {
                    AddDetail(rows, "task:chart_info_hydration", BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_chart_info_load,
                        state.ChartInfoHydrationTotalCount, state.ChartInfoHydrationAppliedCount, string.Empty);
                }

                return rows;
            }
        }
    }

    private static void AddDetail(List<OperationProgressRow> rows, string key, string label, int total, int processed, string path)
    {
        bool indeterminate = total <= 0;
        rows.Add(new(key, indeterminate ? label : FormatStartupProgressCountLabel(label, processed, total, string.Empty),
            path ?? string.Empty, Math.Max(0, processed), Math.Max(1, total), indeterminate, ParentKey: "startup"));
    }

    /// <summary>送出時に捕捉された操作識別が一致する処理別最新値だけを反映します。</summary>
    internal void UpdateStartupProgressLibraryInitializationStatuses(IReadOnlyList<BMSLibrary.LibraryInitializationProgressSnapshot> snapshots)
    {
        long token;
        lock (startupProgressLock)
        {
            if (!startupProgressState.IsActive)
            {
                return;
            }
            token = startupProgressState.OperationToken;
            BMSLibrary.LibraryInitializationProgressSnapshot[] current = snapshots.Where(status => status.OperationToken == token).ToArray();
            if (current.Length == 0 && snapshots.Count > 0)
            {
                return;
            }
            libraryInitializationStatuses = current;
        }
        RecomputeStartupProgressPresentation(token);
    }

    /// <summary>現在の親操作が待つ要求版と一致する読込み件数だけを反映します。</summary>
    internal void UpdateStartupProgressChartInfoHydrationStatus(ChartInfoWorkflowProgressSnapshot snapshot)
    {
        lock (startupProgressLock)
        {
            if (snapshot == null || !startupProgressState.IsActive
                || snapshot.Request != startupProgressState.RequiredChartInfoHydrationProgressRequest
                || snapshot.RequestVersion != startupProgressState.RequiredChartInfoHydrationCompletedVersion
                || !CanCompleteStartupProgressPhase(startupProgressState, StartupProgressPhase.ChartInfoHydrationDone))
            {
                return;
            }

            startupProgressState.ChartInfoHydrationTotalCount = snapshot.TotalCount;
            startupProgressState.ChartInfoHydrationAppliedCount = snapshot.ProcessedCount;
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
                bool operationCompleted = !state.IsFailed && AreExpectedStartupProgressPhasesCompleted(state);
                operationCompletedForLog = operationCompleted;
                if (state.IsFailed)
                {
                    label = GetStartupProgressFailedLabel(state.OperationKind);
                    subLabel = state.FailureSubLabel;
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
                else
                {
                    label = GetStartupProgressRunningLabel(state.OperationKind);
                    subLabel = string.Empty;
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
        return string.Format(BeMusicSeeker.Properties.Resources.Statusbar_progress_operation_completed_format,
            GetStartupProgressRunningLabel(operationKind));
    }

    /// <summary>
    /// operation 種別に応じた失敗ラベルを返します。
    /// </summary>
    /// <param name="operationKind">operation 種別。</param>
    /// <returns>失敗ラベル。</returns>
    internal static string GetStartupProgressFailedLabel(StartupProgressOperationKind operationKind)
    {
        return string.Format(BeMusicSeeker.Properties.Resources.Statusbar_progress_operation_failed_format,
            GetStartupProgressRunningLabel(operationKind));
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
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountExpectedStartupProgressPhase(state, StartupProgressPhase.ScoreHydrationDone, ref count);
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
                                | StartupProgressPhase.ChartInfoHydrationDone
                                | StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressOperationKind.FullReinitialize => StartupProgressPhase.CoreInitializeStarted
                                | StartupProgressPhase.LibraryDatabaseLoadDone
                                | StartupProgressPhase.LibraryFileEnumerationDone
                                | StartupProgressPhase.LibraryFileDiffDone
                                | StartupProgressPhase.StartupReadyOperable
                                | StartupProgressPhase.PlaylistReferenceApplied
                                | StartupProgressPhase.ScoreHydrationDone
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
                                | StartupProgressPhase.ScoreHydrationDone,
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
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ChartInfoHydrationDone, ref count);
        CountCompletedExpectedStartupProgressPhase(state, StartupProgressPhase.ScoreHydrationDone, ref count);
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
    ChartInfoHydrationDone = 2048,
    PlaylistEntriesHydrationDone = 4096,
    LibraryDatabaseLoadDone = 8192,
    LibraryFileEnumerationDone = 16384,
    LibraryFileDiffDone = 32768,
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

    internal OperationProgressRequest RequiredScoreHydrationProgressRequest;

    internal int RequiredScoreHydrationCompletedVersion;

    internal int ChartInfoHydrationBaselineCompletedVersion;

    internal int PlaylistEntriesHydrationBaselineCompletedVersion;

    internal int LibraryDatabaseLoadBaselineCompletedVersion;

    internal int LibraryFileEnumerationBaselineCompletedVersion;

    internal int LibraryFileDiffBaselineCompletedVersion;

    internal OperationProgressRequest RequiredPlaylistReferenceProgressRequest;

    internal int RequiredPlaylistReferenceVersion;

    internal bool PlaylistReferenceFromHydrationRequested;

    internal OperationProgressRequest RequiredExternalSyncProgressRequest;

    internal int RequiredExternalSyncVersion;

    internal OperationProgressRequest RequiredPlaylistEntriesHydrationProgressRequest;

    internal int RequiredPlaylistEntriesHydrationCompletedVersion;

    internal OperationProgressRequest RequiredChartInfoHydrationProgressRequest;

    internal int RequiredChartInfoHydrationCompletedVersion;

    internal int ChartInfoHydrationTotalCount;

    internal int ChartInfoHydrationAppliedCount;

    internal bool CompletionHideScheduled;

    internal DateTime? LastCompletedAtUtc;
}
