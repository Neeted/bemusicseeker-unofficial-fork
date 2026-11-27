using System;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Update;

namespace BeMusicSeeker.ViewModels;

internal enum StartupUpdateWorkflowOutcome
{
    NoUpdate,
    Cancelled,
    Applied,
    Failed,
    Closing
}

/// <summary>起動時更新確認が開始前か、実行中か、終端済みかを表します。</summary>
internal enum StartupUpdateWorkflowState
{
    /// <summary>まだ開始していません。閉鎖要求で開始しないまま終わる場合も含みます。</summary>
    NotStarted,
    /// <summary>開始済みで、終端の受領を確定していません。</summary>
    Running,
    /// <summary>終端の受領を確定済みです。</summary>
    Terminal
}

internal sealed class StartupUpdateWorkflowCompletionReceipt
{
    internal StartupUpdateWorkflowCompletionReceipt(
        long generation,
        StartupUpdateWorkflowOutcome outcome,
        Exception exception = null,
        bool shutdownPrepared = false)
    {
        Generation = generation;
        Outcome = outcome;
        Exception = exception;
        ShutdownPrepared = shutdownPrepared;
    }

    internal long Generation { get; }

    internal StartupUpdateWorkflowOutcome Outcome { get; }

    internal Exception Exception { get; }

    internal bool ShutdownPrepared { get; }
}

/// <summary>更新ZIPの取得を、状態表示バーの一行へ投影するときの段階です。</summary>
internal enum StartupUpdateProgressPhase
{
    /// <summary>表示する取得がありません。終端後と、更新ツールの起動を確定した後が該当します。</summary>
    Inactive,
    /// <summary>ZIPを転送しています。</summary>
    Downloading,
    /// <summary>ZIPの検証が済み、起動時の初期化の成功を待っています。</summary>
    WaitingForInitialization
}

/// <summary>
/// 更新ZIPの取得の進捗行を作るための、変更不能な表示値です。管理主体がUIスレッドで発行し、
/// 受け手は保持して行を作ります。段階は後戻りせず、終端後は <see cref="StartupUpdateProgressPhase.Inactive"/> になります。
/// </summary>
/// <param name="Phase">表示する段階。</param>
/// <param name="FileName">取得している資産のファイル名。</param>
/// <param name="ReceivedBytes">受信済み量。</param>
/// <param name="TotalBytes">資産の合計サイズ。</param>
/// <param name="CanCancel">取消操作を出せるか。取消を受け付けた後は、終端まで行を残して偽になります。</param>
internal sealed record StartupUpdateProgressSnapshot(
    StartupUpdateProgressPhase Phase,
    string FileName,
    long ReceivedBytes,
    long TotalBytes,
    bool CanCancel)
{
    /// <summary>表示する取得がない状態です。</summary>
    internal static StartupUpdateProgressSnapshot Inactive { get; } = new(StartupUpdateProgressPhase.Inactive, string.Empty, 0, 0, false);

    /// <summary>行を表示するかを取得します。</summary>
    internal bool IsActive => Phase != StartupUpdateProgressPhase.Inactive;
}

internal sealed class StartupUpdatePresentationRequest
{
    private readonly TaskCompletionSource<UpdateAssetInfo> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal StartupUpdatePresentationRequest(UpdateCheckResult result)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    internal UpdateCheckResult Result { get; }

    internal Task<UpdateAssetInfo> Completion => completion.Task;

    internal void Complete(UpdateAssetInfo selectedAsset)
    {
        completion.TrySetResult(selectedAsset);
    }

    internal void Fail(Exception exception)
    {
        completion.TrySetException(exception ?? new InvalidOperationException("Update presentation failed."));
    }
}

/// <summary>
/// Owns the one-shot startup update workflow while the window remains a typed presentation and shutdown adapter.
/// </summary>
/// <remarks>
/// 更新ダイアログが選択結果を返した直後に、起動時の初期化の完了を待たず更新ZIPの取得を始めます。
/// 更新ツールの起動準備と起動は、ZIPのサイズ・ハッシュ検証の成功と初期化の成功（<see cref="StartupInitializationResult.IsCompletedSuccessfully"/>）が
/// そろった時点で一度だけ行い、どちらが先でも同じです。初期化が成功以外で終わった場合、明示取消、更新ツール起動前の通常終了は、
/// 取得の終わりと一時物・検証済みZIPの回収を待ってから終端（<see cref="StartupUpdateWorkflowOutcome"/>）を一度だけ公開します。
/// 終端の種類は、管理主体のlock内で最初に受け付けた原因で決めます。
/// </remarks>
internal sealed class StartupUpdateWorkflowOwner
{
    private readonly object syncRoot = new();

    private readonly Func<Task<UpdateCheckResult>> checkForUpdates;

    private readonly Func<UpdateAssetInfo, CancellationToken, IProgress<UpdateDownloadProgress>, Task<string>> downloadAndVerify;

    private readonly Func<string, IPreparedUpdaterLaunch> prepareUpdaterLaunch;

    private readonly Action cleanupPreviousWorkDirectory;

    private readonly Action<string> deleteDownloadedPackage;

    private readonly Func<Func<Task>, Task> schedule;

    private readonly Action<Action> dispatchToUi;

    private readonly Action<string> logInfo;

    private readonly Action<Exception> logWarning;

    private readonly Action<Exception> logError;

    private Func<string, Task<ShutdownPreparationResult>> shutdownPreparationPort;

    private long generation;

    private bool started;

    private bool active;

    private bool closingRequested;

    private RunContext activeRun;

    private bool shutdownPreparationStartedForRun;

    private TaskCompletionSource<bool> idleCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource<bool> terminalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool terminalPending;

    private StartupUpdateWorkflowCompletionReceipt terminalReceipt;

    private TaskCompletionSource<bool> shutdownPreparationPortCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool progressPublishPending;

    internal StartupUpdateWorkflowOwner(
        Func<Task<UpdateCheckResult>> checkForUpdates,
        Func<UpdateAssetInfo, CancellationToken, IProgress<UpdateDownloadProgress>, Task<string>> downloadAndVerify,
        Func<string, IPreparedUpdaterLaunch> prepareUpdaterLaunch,
        Action cleanupPreviousWorkDirectory,
        Action<string> deleteDownloadedPackage,
        Func<Func<Task>, Task> schedule,
        Action<Action> dispatchToUi,
        Action<string> logInfo = null,
        Action<Exception> logWarning = null,
        Action<Exception> logError = null)
    {
        this.checkForUpdates = checkForUpdates ?? throw new ArgumentNullException(nameof(checkForUpdates));
        this.downloadAndVerify = downloadAndVerify ?? throw new ArgumentNullException(nameof(downloadAndVerify));
        this.prepareUpdaterLaunch = prepareUpdaterLaunch ?? throw new ArgumentNullException(nameof(prepareUpdaterLaunch));
        this.cleanupPreviousWorkDirectory = cleanupPreviousWorkDirectory ?? throw new ArgumentNullException(nameof(cleanupPreviousWorkDirectory));
        this.deleteDownloadedPackage = deleteDownloadedPackage ?? throw new ArgumentNullException(nameof(deleteDownloadedPackage));
        this.schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.logInfo = logInfo;
        this.logWarning = logWarning;
        this.logError = logError;
    }

    internal event Action<StartupUpdatePresentationRequest> PresentationRequested;

    /// <summary>更新の失敗の表示を要求します。受け手は表示の完了で終わる Task を返し、既読化は表示が成功した後にだけ行います。</summary>
    internal event Func<Exception, Task> FailurePresentationRequested;

    internal event Action ApplicationShutdownRequested;

    internal event Action<StartupUpdateWorkflowCompletionReceipt> TerminalPublished;

    /// <summary>
    /// 更新ZIPの取得の進捗行の表示値を、注入されたUIへの配送を通して発行します。同時に配送を待つのは一件までで、
    /// 配送先ではその時点の状態から値を作るため、古い通知で段階が戻ったり終端後に行が復活したりしません。
    /// </summary>
    internal event Action<StartupUpdateProgressSnapshot> ProgressChanged;

    internal void BindShutdownPreparation(Func<string, Task<ShutdownPreparationResult>> port)
    {
        if (port == null)
        {
            throw new ArgumentNullException(nameof(port));
        }
        lock (syncRoot)
        {
            if (active)
            {
                throw new InvalidOperationException("Startup update shutdown preparation cannot be rebound while active.");
            }
            if (shutdownPreparationPort != null && !shutdownPreparationPort.Equals(port))
            {
                throw new InvalidOperationException("Startup update shutdown preparation is already bound.");
            }
            shutdownPreparationPort = port;
        }
    }

    internal bool IsActive
    {
        get
        {
            lock (syncRoot)
            {
                return active;
            }
        }
    }

    internal bool IsIdle => !IsActive;

    /// <summary>
    /// 更新確認の開始状態と、終端していればその実receiptを同じlock下で一貫して返します。
    /// </summary>
    /// <remarks>
    /// 受領はlock下で確定してから終端通知を発行するため、<see cref="TerminalPublished"/>の購読時機に依存せず、
    /// 購読より先に終端した更新確認も後から観測できます。検証起動の制御経路が、開始前の状態を
    /// 終端種別で補わずに失敗として扱うために使います。終端した受領は後続の呼出しでも変わりません。
    /// </remarks>
    /// <param name="receipt">状態が<see cref="StartupUpdateWorkflowState.Terminal"/>のときだけ非nullの実receiptです。</param>
    /// <returns>現在の開始状態です。</returns>
    internal StartupUpdateWorkflowState CaptureState(out StartupUpdateWorkflowCompletionReceipt receipt)
    {
        lock (syncRoot)
        {
            receipt = terminalReceipt;
            return terminalReceipt != null
                ? StartupUpdateWorkflowState.Terminal
                : started ? StartupUpdateWorkflowState.Running : StartupUpdateWorkflowState.NotStarted;
        }
    }

    internal bool IsShutdownPreparationStarted
    {
        get
        {
            lock (syncRoot)
            {
                return shutdownPreparationStartedForRun || activeRun?.ShutdownPreparationStarted == true;
            }
        }
    }

    /// <summary>
    /// 起動時更新確認を一度だけ開始します。
    /// </summary>
    /// <param name="initialization">
    /// 起動時の初期化の観測結果です。成功以外（失敗・設定要求・終了要求・取消・例外・完了未公開など）で終わると、
    /// 更新ツールは起動せず、取得中の更新は取り消します。この <see cref="Task"/> の終端は待たず、初期化そのものは止めません。
    /// </param>
    /// <returns>開始した場合は true。開始済み、または閉鎖要求の後は false です。</returns>
    internal bool Start(Task<StartupInitializationResult> initialization)
    {
        ArgumentNullException.ThrowIfNull(initialization);
        RunContext run;
        lock (syncRoot)
        {
            if (started || closingRequested)
            {
                return false;
            }
            started = true;
            active = true;
            generation++;
            run = new RunContext(generation, initialization);
            activeRun = run;
            shutdownPreparationStartedForRun = false;
            idleCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminalCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminalPending = true;
            shutdownPreparationPortCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            Task scheduled = schedule(() => ExecuteAsync(run));
            if (scheduled == null)
            {
                throw new InvalidOperationException("Startup update scheduler returned no task.");
            }
            scheduled.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        CompleteFailure(run, task.Exception?.GetBaseException() ?? new InvalidOperationException("Startup update scheduler failed."), preShutdown: true);
                    }
                    else if (task.IsCanceled)
                    {
                        CompleteFailure(run, new InvalidOperationException("Startup update scheduler canceled."), preShutdown: true);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }
        catch (Exception exception)
        {
            CompleteFailure(run, exception, preShutdown: true);
            return true;
        }
    }

    internal Task WaitForIdleAsync()
    {
        lock (syncRoot)
        {
            return active ? idleCompletion.Task : Task.CompletedTask;
        }
    }

    internal Task WaitForTerminalAsync()
    {
        lock (syncRoot)
        {
            return terminalPending ? terminalCompletion.Task : Task.CompletedTask;
        }
    }

    internal Task WaitForShutdownPreparationRequestAsync()
    {
        lock (syncRoot)
        {
            return shutdownPreparationStartedForRun
                ? shutdownPreparationPortCompletion.Task
                : Task.CompletedTask;
        }
    }

    /// <summary>
    /// 終了の通知を受け付けます。実行中の取得があれば、終了を原因として取消を発行します。
    /// </summary>
    /// <remarks>
    /// 呼出し元は終了側のlockを保持したままのことがあるため、取消は <see cref="CancellationTokenSource.CancelAsync"/> で発行し、
    /// その完了は待ちません。回収・終端の公開・UIへの配送は、取消の登録処理が動くスレッドで進み、呼出し元のスレッドでは動きません。
    /// 進捗行の更新も、同じ理由でここでは配送せず、終端の公開で行を除きます。
    /// </remarks>
    /// <returns>実行中の更新確認があれば true。</returns>
    internal bool NotifyClosing()
    {
        CancellationTokenSource cancellation = null;
        bool wasActive;
        lock (syncRoot)
        {
            closingRequested = true;
            generation++;
            wasActive = active;
            if (active && activeRun is { } run && run.Stage != RunStage.LaunchCommitted)
            {
                if (run.Cause == StopCause.None)
                {
                    run.Cause = StopCause.Closing;
                }
                if (run.Stage is RunStage.Transferring or RunStage.WaitingForInitialization)
                {
                    cancellation = run.Cancellation;
                }
            }
        }
        CancelWithoutWaiting(cancellation);
        return wasActive;
    }

    /// <summary>
    /// 進捗行の取消操作に対応する明示取消を受け付けます。転送中と初期化待ちのどちらでも受け付け、
    /// 更新ツールの起動を確定した後、取消や終了の原因をすでに受け付けた後、取得を始める前は効果がありません。
    /// </summary>
    /// <returns>この呼出しで取消を受け付けた場合は true。</returns>
    internal bool RequestCancel()
    {
        CancellationTokenSource cancellation;
        lock (syncRoot)
        {
            if (!active
                || activeRun is not { } run
                || run.Stage is not (RunStage.Transferring or RunStage.WaitingForInitialization)
                || run.Cause != StopCause.None)
            {
                return false;
            }
            run.Cause = StopCause.Cancelled;
            cancellation = run.Cancellation;
        }
        SchedulePublishProgress();
        CancelWithoutWaiting(cancellation);
        return true;
    }

    private async Task ExecuteAsync(RunContext run)
    {
        try
        {
            try
            {
                cleanupPreviousWorkDirectory();
            }
            catch (Exception exception)
            {
                CompleteFailure(run, exception, preShutdown: true);
                return;
            }

            UpdateCheckResult result;
            try
            {
                result = await checkForUpdates().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                CompleteFailure(run, exception, preShutdown: true);
                return;
            }

            if (result?.IsUpdateAvailable != true)
            {
                Complete(run, IsCurrentBeforePresentation(run)
                    ? StartupUpdateWorkflowOutcome.NoUpdate
                    : StartupUpdateWorkflowOutcome.Closing);
                return;
            }

            UpdateAssetInfo selectedAsset;
            try
            {
                selectedAsset = await PresentAsync(run, result).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                CompleteFailure(run, exception, preShutdown: true);
                return;
            }

            if (selectedAsset == null)
            {
                Complete(run, IsCurrentBeforePresentation(run)
                    ? StartupUpdateWorkflowOutcome.Cancelled
                    : StartupUpdateWorkflowOutcome.Closing);
                return;
            }

            await ApplyAsync(run, selectedAsset).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompleteFailure(run, exception, preShutdown: true);
        }
    }

    private async Task<UpdateAssetInfo> PresentAsync(RunContext run, UpdateCheckResult result)
    {
        if (!IsCurrentBeforePresentation(run) || PresentationRequested == null)
        {
            return null;
        }

        var request = new StartupUpdatePresentationRequest(result);
        if (!DispatchToUi(() =>
        {
            if (!IsCurrentBeforePresentation(run))
            {
                request.Complete(null);
                return;
            }
            try
            {
                PresentationRequested?.Invoke(request);
            }
            catch (Exception exception)
            {
                request.Fail(exception);
            }
        }))
        {
            return null;
        }
        return await request.Completion.ConfigureAwait(false);
    }

    private async Task ApplyAsync(RunContext run, UpdateAssetInfo selectedAsset)
    {
        string packagePath = null;
        UpdaterLaunchReceipt launchReceipt = null;
        bool shutdownPreparationStarted = false;
        bool shutdownPreparationCompleted = false;
        try
        {
            // 取得の開始から、検証の成功と初期化の成功がそろう起動の確定までを一つに合流させる。
            // 確定できなかった場合は、一時物の回収を終えて終端を公開済みなので、ここでは何もしない。
            packagePath = await AcquirePackageAndCommitLaunchAsync(run, selectedAsset).ConfigureAwait(false);
            if (packagePath == null)
            {
                return;
            }

            IPreparedUpdaterLaunch preparedLaunch = prepareUpdaterLaunch(packagePath)
                ?? throw new InvalidOperationException("Updater launch preparation returned no launch.");
            launchReceipt = preparedLaunch.Start()
                ?? throw new UpdaterLaunchFailureException("Updater process did not start.");
            if (!await TryBeginShutdownPreparationAsync(run).ConfigureAwait(false))
            {
                launchReceipt.Abort();
                TryDeleteDownloadedPackage(packagePath);
                Complete(run, StartupUpdateWorkflowOutcome.Closing);
                return;
            }
            shutdownPreparationStarted = true;
            ShutdownPreparationResult shutdownResult = await RequestShutdownPreparationAsync().ConfigureAwait(false);
            shutdownPreparationCompleted = true;
            LogInfoSafely("startup_update shutdown prepared " + shutdownResult.ToLogFields());

            launchReceipt.Proceed();
            Complete(run, StartupUpdateWorkflowOutcome.Applied, shutdownPrepared: true);
            RequestApplicationShutdown();
        }
        catch (UpdaterLaunchFailureException exception)
        {
            Exception terminalFailure = AbortLaunchOrCombine(launchReceipt, exception);
            LogErrorSafely(terminalFailure, "startup_update updater launch failed");
            TryDeleteDownloadedPackage(packagePath);
            if (!IsCurrentForApply(run))
            {
                Complete(run, StartupUpdateWorkflowOutcome.Closing);
                return;
            }
            RequestFailurePresentation(terminalFailure);
            Complete(run, StartupUpdateWorkflowOutcome.Failed, terminalFailure, shutdownPrepared: shutdownPreparationCompleted);
            if (shutdownPreparationStarted)
            {
                RequestApplicationShutdown();
            }
        }
        catch (Exception exception)
        {
            Exception terminalFailure = AbortLaunchOrCombine(launchReceipt, exception);
            LogErrorSafely(terminalFailure, "startup_update apply failed");
            // 更新ツールを起動しない経路では、検証済みZIPを終端の公開より前に破棄する。
            TryDeleteDownloadedPackage(packagePath);
            if (!IsCurrentForApply(run))
            {
                Complete(run, StartupUpdateWorkflowOutcome.Closing);
                return;
            }
            if (shutdownPreparationStarted)
            {
                RequestFailurePresentation(terminalFailure);
                Complete(run, StartupUpdateWorkflowOutcome.Failed, terminalFailure, shutdownPrepared: shutdownPreparationCompleted);
                RequestApplicationShutdown();
                return;
            }

            RequestFailurePresentation(terminalFailure);
            Complete(run, StartupUpdateWorkflowOutcome.Failed, terminalFailure);
        }
    }

    /// <summary>
    /// 更新ZIPの転送を始め、検証の成功と初期化の成功がそろったときに、更新ツールの起動を一度だけ確定します。
    /// </summary>
    /// <remarks>
    /// 初期化の完了の待ちは、この実行の取消で解ける <see cref="Task.WaitAsync(CancellationToken)"/> で行い、初期化の <see cref="Task"/> 自体の終端は待ちません。
    /// 取得の終わり、取消付きの待ちの終わり、一時物と検証済みZIPの回収の後にだけ終端を公開します。
    /// </remarks>
    /// <returns>起動を確定した場合は検証済みZIPのパス。確定できず、終端を公開済みの場合は null。</returns>
    private async Task<string> AcquirePackageAndCommitLaunchAsync(RunContext run, UpdateAssetInfo selectedAsset)
    {
        switch (TryEnterTransfer(run, selectedAsset, out StartupUpdateWorkflowOutcome entryOutcome))
        {
            case TransitionDecision.Stale:
                return null;
            case TransitionDecision.Stop:
                Complete(run, entryOutcome);
                return null;
        }
        SchedulePublishProgress();

        CancellationToken token = run.Cancellation.Token;
        Task<string> download = StartDownload(run, selectedAsset);
        Task initializationWait = run.Initialization.WaitAsync(token);
        bool initializationObserved = false;
        while (!download.IsCompleted)
        {
            if (initializationObserved)
            {
                await ((Task)download).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                break;
            }
            await Task.WhenAny(download, initializationWait).ConfigureAwait(false);
            if (initializationWait.IsCompleted)
            {
                initializationObserved = true;
                ObserveInitialization(run);
            }
        }

        string verifiedPath = null;
        Exception failure = null;
        if (download.IsCompletedSuccessfully)
        {
            verifiedPath = string.IsNullOrWhiteSpace(download.Result) ? null : download.Result;
            failure = verifiedPath == null ? new InvalidOperationException("Update download returned no package path.") : null;
        }
        else
        {
            Exception exception = download.Exception?.GetBaseException() ?? new OperationCanceledException(token);
            // 自分の取消による中断だけを、失敗の経路から外す。それ以外の取消は取得の失敗として扱う。
            failure = exception is OperationCanceledException && token.IsCancellationRequested ? null : exception;
        }

        if (verifiedPath != null)
        {
            MarkDownloadVerified(run);
            if (!initializationObserved)
            {
                await ((Task)initializationWait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                ObserveInitialization(run);
            }
            TransitionDecision commit = TryCommitLaunch(run, out StartupUpdateWorkflowOutcome stopOutcome);
            if (commit == TransitionDecision.Proceed)
            {
                SchedulePublishProgress();
                return verifiedPath;
            }
            TryDeleteDownloadedPackage(verifiedPath);
            if (commit == TransitionDecision.Stop)
            {
                Complete(run, stopOutcome);
            }
            return null;
        }

        if (failure == null)
        {
            // 自分の取消による中断。原因を受け付けた時点で取消を発行済みなので、取消付きの待ちはすぐ終わる。
            await ((Task)initializationWait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Complete(run, GetStopOutcome(run));
            return null;
        }

        TransitionDecision failureDecision = TryRecordFailure(run, out StartupUpdateWorkflowOutcome failureStopOutcome);
        if (failureDecision == TransitionDecision.Stale)
        {
            return null;
        }
        if (failureDecision == TransitionDecision.Proceed)
        {
            // 取得の失敗は初期化の完了を待たず公開する。初期化の待ちだけを自分の取消で解き、初期化そのものは止めない。
            CancelWithoutWaiting(run.Cancellation);
        }
        await ((Task)initializationWait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (failureDecision == TransitionDecision.Stop)
        {
            LogWarningSafely(failure, "startup_update download ended after the update was stopped");
            Complete(run, failureStopOutcome);
            return null;
        }
        LogErrorSafely(failure, "startup_update apply failed");
        RequestFailurePresentation(failure);
        Complete(run, StartupUpdateWorkflowOutcome.Failed, failure);
        return null;
    }

    private Task<string> StartDownload(RunContext run, UpdateAssetInfo selectedAsset)
    {
        try
        {
            return downloadAndVerify(selectedAsset, run.Cancellation.Token, new TransferProgress(this, run))
                ?? Task.FromException<string>(new InvalidOperationException("Update download returned no task."));
        }
        catch (Exception exception)
        {
            return Task.FromException<string>(exception);
        }
    }

    /// <summary>
    /// 取得を始められるかを、lock内で一度だけ決めます。終了や取消を受け付け済み、または初期化がすでに成功以外で終わっている場合は、
    /// 転送を始めず、その原因の終端を返します。
    /// </summary>
    private TransitionDecision TryEnterTransfer(RunContext run, UpdateAssetInfo selectedAsset, out StartupUpdateWorkflowOutcome stopOutcome)
    {
        stopOutcome = StartupUpdateWorkflowOutcome.Cancelled;
        lock (syncRoot)
        {
            if (!active || !ReferenceEquals(activeRun, run))
            {
                return TransitionDecision.Stale;
            }
            if (TryGetStopOutcome(run, out stopOutcome))
            {
                return TransitionDecision.Stop;
            }
            Task<StartupInitializationResult> initialization = run.Initialization;
            if (initialization.IsCompleted)
            {
                run.InitializationResolved = true;
                run.InitializationSucceeded = IsInitializationSucceeded(initialization);
                if (!run.InitializationSucceeded)
                {
                    run.Cause = StopCause.Cancelled;
                    stopOutcome = StartupUpdateWorkflowOutcome.Cancelled;
                    return TransitionDecision.Stop;
                }
            }
            run.Stage = RunStage.Transferring;
            run.FileName = selectedAsset.FileName ?? string.Empty;
            run.TotalBytes = Math.Max(0, selectedAsset.SizeBytes);
            run.ReceivedBytes = 0;
            return TransitionDecision.Proceed;
        }
    }

    private static bool IsInitializationSucceeded(Task<StartupInitializationResult> initialization)
        => initialization.IsCompletedSuccessfully && initialization.Result?.IsCompletedSuccessfully == true;

    /// <summary>
    /// 完了した初期化の結果を一度だけ採用します。成功以外なら、まだ原因がなく取得中か初期化待ちであれば、
    /// 初期化が成功以外で終わったことを原因にして取消を発行します。初期化の結果そのものには手を加えません。
    /// </summary>
    private void ObserveInitialization(RunContext run)
    {
        Task<StartupInitializationResult> initialization = run.Initialization;
        if (!initialization.IsCompleted)
        {
            return;
        }
        bool succeeded = IsInitializationSucceeded(initialization);
        CancellationTokenSource cancellation = null;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run) || run.InitializationResolved)
            {
                return;
            }
            run.InitializationResolved = true;
            run.InitializationSucceeded = succeeded;
            if (!succeeded
                && run.Cause == StopCause.None
                && run.Stage is RunStage.Transferring or RunStage.WaitingForInitialization)
            {
                run.Cause = StopCause.Cancelled;
                cancellation = run.Cancellation;
            }
        }
        if (cancellation != null)
        {
            SchedulePublishProgress();
            CancelWithoutWaiting(cancellation);
        }
    }

    private void MarkDownloadVerified(RunContext run)
    {
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run) || run.Stage != RunStage.Transferring)
            {
                return;
            }
            run.Stage = RunStage.WaitingForInitialization;
            run.ReceivedBytes = run.TotalBytes;
        }
        SchedulePublishProgress();
    }

    /// <summary>
    /// 検証の成功と初期化の成功がそろった時点で、起動を一度だけ確定します。確定後の明示取消は効果がなく、
    /// 終了通知は従来の終了準備の先着判定で扱います。
    /// </summary>
    private TransitionDecision TryCommitLaunch(RunContext run, out StartupUpdateWorkflowOutcome stopOutcome)
    {
        lock (syncRoot)
        {
            stopOutcome = StartupUpdateWorkflowOutcome.Cancelled;
            if (!active || !ReferenceEquals(activeRun, run))
            {
                return TransitionDecision.Stale;
            }
            if (TryGetStopOutcome(run, out stopOutcome))
            {
                return TransitionDecision.Stop;
            }
            if (!run.InitializationResolved || !run.InitializationSucceeded)
            {
                run.Cause = StopCause.Cancelled;
                stopOutcome = StartupUpdateWorkflowOutcome.Cancelled;
                return TransitionDecision.Stop;
            }
            run.Stage = RunStage.LaunchCommitted;
            return TransitionDecision.Proceed;
        }
    }

    /// <summary>
    /// 取得の失敗を、他の原因より先に受け付けられるかを決めます。取消や終了を先に受け付け済みなら、その原因の終端にします。
    /// </summary>
    private TransitionDecision TryRecordFailure(RunContext run, out StartupUpdateWorkflowOutcome stopOutcome)
    {
        lock (syncRoot)
        {
            stopOutcome = StartupUpdateWorkflowOutcome.Cancelled;
            if (!active || !ReferenceEquals(activeRun, run))
            {
                return TransitionDecision.Stale;
            }
            if (TryGetStopOutcome(run, out stopOutcome))
            {
                return TransitionDecision.Stop;
            }
            run.Cause = StopCause.Failed;
            return TransitionDecision.Proceed;
        }
    }

    /// <summary>先に受け付けた原因から終端の種類を決めます。終了の通知が先なら <see cref="StartupUpdateWorkflowOutcome.Closing"/> です。</summary>
    private bool TryGetStopOutcome(RunContext run, out StartupUpdateWorkflowOutcome outcome)
    {
        // 先に受け付けた原因が取消なら、その後の終了の通知で種類を変えない。
        if (run.Cause == StopCause.Cancelled)
        {
            outcome = StartupUpdateWorkflowOutcome.Cancelled;
            return true;
        }
        outcome = StartupUpdateWorkflowOutcome.Closing;
        return run.Cause == StopCause.Closing || generation != run.Generation;
    }

    private StartupUpdateWorkflowOutcome GetStopOutcome(RunContext run)
    {
        lock (syncRoot)
        {
            return TryGetStopOutcome(run, out StartupUpdateWorkflowOutcome outcome)
                ? outcome
                : StartupUpdateWorkflowOutcome.Cancelled;
        }
    }

    private void ReportTransfer(RunContext run, UpdateDownloadProgress value)
    {
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run) || run.Stage != RunStage.Transferring)
            {
                return;
            }
            long received = Math.Max(0, value.ReceivedBytes);
            if (run.TotalBytes > 0)
            {
                received = Math.Min(received, run.TotalBytes);
            }
            if (received <= run.ReceivedBytes)
            {
                return;
            }
            run.ReceivedBytes = received;
        }
        SchedulePublishProgress();
    }

    /// <summary>
    /// 進捗の配送を一件にまとめて依頼します。配送先ではその時点の状態から値を作るため、未配送の間に重なった更新は最新の値だけが届きます。
    /// </summary>
    private void SchedulePublishProgress()
    {
        lock (syncRoot)
        {
            if (progressPublishPending)
            {
                return;
            }
            progressPublishPending = true;
        }
        if (!DispatchToUi(PublishProgress))
        {
            lock (syncRoot)
            {
                progressPublishPending = false;
            }
        }
    }

    private void PublishProgress()
    {
        StartupUpdateProgressSnapshot snapshot;
        lock (syncRoot)
        {
            progressPublishPending = false;
            snapshot = activeRun is { } run && active
                && (run.Stage is RunStage.Transferring or RunStage.WaitingForInitialization)
                ? new StartupUpdateProgressSnapshot(
                    run.Stage == RunStage.Transferring
                        ? StartupUpdateProgressPhase.Downloading
                        : StartupUpdateProgressPhase.WaitingForInitialization,
                    run.FileName,
                    run.ReceivedBytes,
                    run.TotalBytes,
                    run.Cause == StopCause.None)
                : StartupUpdateProgressSnapshot.Inactive;
        }
        try
        {
            ProgressChanged?.Invoke(snapshot);
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "startup_update progress notification failed");
        }
    }

    /// <summary>
    /// 取消を発行し、その完了は待ちません。登録された処理は呼出し元のスレッドでは動かないため、
    /// 呼出し元が保持するlockの下で、回収・終端の公開・UIへの配送が走りません。
    /// </summary>
    private void CancelWithoutWaiting(CancellationTokenSource cancellation)
    {
        if (cancellation != null)
        {
            _ = ObserveCancellationAsync(cancellation);
        }
    }

    private async Task ObserveCancellationAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "startup_update cancellation callback failed");
        }
    }

    private static Exception AbortLaunchOrCombine(
        UpdaterLaunchReceipt launchReceipt,
        Exception primaryFailure)
    {
        if (launchReceipt == null)
        {
            return primaryFailure;
        }
        try
        {
            launchReceipt.Abort();
            return primaryFailure;
        }
        catch (Exception abortFailure)
        {
            return new AggregateException(
                "Updater workflow failed and the updater process abort also failed.",
                primaryFailure,
                abortFailure);
        }
    }

    private async Task<ShutdownPreparationResult> RequestShutdownPreparationAsync()
    {
        Func<string, Task<ShutdownPreparationResult>> port;
        lock (syncRoot)
        {
            port = shutdownPreparationPort;
        }
        if (port == null)
        {
            throw new InvalidOperationException("Startup update shutdown preparation is not configured.");
        }
        Task<ShutdownPreparationResult> preparation;
        try
        {
            preparation = port("update");
            if (preparation == null)
            {
                throw new InvalidOperationException("Startup update shutdown preparation returned no task.");
            }
        }
        finally
        {
            shutdownPreparationPortCompletion.TrySetResult(true);
        }
        return await preparation.ConfigureAwait(false);
    }

    /// <summary>
    /// 失敗の表示を UI へ依頼します。表示の要求は UI へ渡した処理の中で最初の await より前に行い、
    /// 表示の完了は待たずに、終端の公開と終了の要求の順を保ちます。
    /// </summary>
    private void RequestFailurePresentation(Exception exception)
    {
        DispatchToUi(() => _ = PresentFailureAsync(exception));
    }

    /// <summary>表示が成功した後にだけ、永続した失敗の記録を既読にします。表示の失敗は診断へ残し、既読にしません。戻り値の Task は失敗しません。</summary>
    private async Task PresentFailureAsync(Exception exception)
    {
        Func<Exception, Task> presenter = FailurePresentationRequested;
        if (presenter == null)
        {
            return;
        }
        try
        {
            await (presenter(exception) ?? throw new InvalidOperationException("Startup update failure presentation returned no task."));
            if (exception is UpdateFailureReceiptException receipt
                && receipt.ShouldAcknowledgeAfterPresentation)
            {
                receipt.Acknowledge();
            }
        }
        catch (Exception notificationException)
        {
            LogWarningSafely(notificationException, "startup_update failure presentation failed");
        }
    }

    private void RequestApplicationShutdown()
    {
        DispatchToUi(() =>
        {
            try
            {
                ApplicationShutdownRequested?.Invoke();
            }
            catch (Exception exception)
            {
                LogErrorSafely(exception, "startup_update application shutdown request failed");
            }
        });
    }

    private void CompleteFailure(RunContext run, Exception exception, bool preShutdown)
    {
        LogErrorSafely(exception, "startup_update workflow failed");
        if (preShutdown)
        {
            RequestFailurePresentation(exception);
        }
        Complete(run, StartupUpdateWorkflowOutcome.Failed, exception);
    }

    private void Complete(
        RunContext run,
        StartupUpdateWorkflowOutcome outcome,
        Exception exception = null,
        bool shutdownPrepared = false)
    {
        StartupUpdateWorkflowCompletionReceipt receipt;
        bool progressRowShown;
        lock (syncRoot)
        {
            if (!active || !ReferenceEquals(activeRun, run))
            {
                return;
            }
            progressRowShown = run.Stage is RunStage.Transferring or RunStage.WaitingForInitialization;
            active = false;
            activeRun = null;
            // 終端通知より先に、観測用の保持値を同じlock下で確定します。
            receipt = new StartupUpdateWorkflowCompletionReceipt(run.Generation, outcome, exception, shutdownPrepared);
            terminalReceipt = receipt;
        }
        idleCompletion.TrySetResult(true);
        if (progressRowShown)
        {
            // 行を除く配送を、終端の通知より先に並べる。
            SchedulePublishProgress();
        }

        if (!DispatchToUi(() =>
        {
            try
            {
                TerminalPublished?.Invoke(receipt);
            }
            catch (Exception notificationException)
            {
                LogWarningSafely(notificationException, "startup_update terminal notification failed");
            }
            CompleteTerminalNotification();
        }))
        {
            CompleteTerminalNotification();
        }
    }

    private void CompleteTerminalNotification()
    {
        lock (syncRoot)
        {
            terminalPending = false;
        }
        terminalCompletion.TrySetResult(true);
    }

    private bool TryBeginShutdownPreparation(RunContext run)
    {
        lock (syncRoot)
        {
            if (!active || !ReferenceEquals(activeRun, run))
            {
                return false;
            }
            if (closingRequested && generation != run.Generation)
            {
                return false;
            }
            run.ShutdownPreparationStarted = true;
            shutdownPreparationStartedForRun = true;
            return true;
        }
    }

    private async Task<bool> TryBeginShutdownPreparationAsync(RunContext run)
    {
        bool accepted = false;
        await DispatchToUiAsync(
            () =>
            {
                accepted = TryBeginShutdownPreparation(run);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        return accepted;
    }

    private Task DispatchToUiAsync(Func<Task> action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            dispatchToUi(() => _ = CompleteDispatchedActionAsync(action, completion));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        return completion.Task;
    }

    private static async Task CompleteDispatchedActionAsync(
        Func<Task> action,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await action().ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private bool IsCurrentForApply(RunContext run)
    {
        lock (syncRoot)
        {
            return active
                && ReferenceEquals(activeRun, run)
                && (generation == run.Generation || run.ShutdownPreparationStarted);
        }
    }

    private bool IsCurrentBeforePresentation(RunContext run)
    {
        lock (syncRoot)
        {
            return active
                && !closingRequested
                && generation == run.Generation
                && ReferenceEquals(activeRun, run);
        }
    }

    private bool DispatchToUi(Action action)
    {
        try
        {
            dispatchToUi(action);
            return true;
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "startup_update UI dispatch failed");
            return false;
        }
    }

    private void TryDeleteDownloadedPackage(string packagePath)
    {
        try
        {
            deleteDownloadedPackage(packagePath);
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "startup_update package cleanup failed");
        }
    }

    private void LogInfoSafely(string message)
    {
        try
        {
            logInfo?.Invoke(message);
        }
        catch
        {
        }
    }

    private void LogWarningSafely(Exception exception, string context)
    {
        try
        {
            logWarning?.Invoke(exception);
            logInfo?.Invoke(context + " message=" + FormatExceptionMessage(exception));
        }
        catch
        {
        }
    }

    private void LogErrorSafely(Exception exception, string context)
    {
        try
        {
            logError?.Invoke(exception);
            logInfo?.Invoke(context + " message=" + FormatExceptionMessage(exception));
        }
        catch
        {
        }
    }

    private static string FormatExceptionMessage(Exception exception)
    {
        return (exception?.Message ?? string.Empty).Replace(Environment.NewLine, " ");
    }

    private enum RunStage
    {
        /// <summary>取得を始める前です。更新確認・ダイアログの提示中も含みます。</summary>
        BeforeTransfer,
        Transferring,
        WaitingForInitialization,
        /// <summary>更新ツールの起動を確定しました。以後の明示取消は効果がありません。</summary>
        LaunchCommitted
    }

    /// <summary>取得を止める原因です。管理主体のlock内で最初に記録した一つだけを採用します。</summary>
    private enum StopCause
    {
        None,
        Closing,
        /// <summary>明示取消、または初期化が成功以外で終わったことによる取消です。</summary>
        Cancelled,
        Failed
    }

    private enum TransitionDecision
    {
        Proceed,
        Stop,
        Stale
    }

    private sealed class TransferProgress(StartupUpdateWorkflowOwner owner, RunContext run) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => owner.ReportTransfer(run, value);
    }

    /// <summary>
    /// 一回の実行が所有する状態です。可変の値は管理主体のlock内でだけ読み書きします。
    /// 取消元は、<see cref="CancellationTokenSource.Dispose()"/> が必要な資源（タイマー・待機ハンドル）を持たないため破棄せず、
    /// 破棄済みの取消元へ取消を発行する競合を作りません。
    /// </summary>
    private sealed class RunContext
    {
        internal RunContext(long generation, Task<StartupInitializationResult> initialization)
        {
            Generation = generation;
            Initialization = initialization;
        }

        internal long Generation { get; }

        internal Task<StartupInitializationResult> Initialization { get; }

        internal CancellationTokenSource Cancellation { get; } = new();

        internal bool ShutdownPreparationStarted { get; set; }

        internal RunStage Stage { get; set; }

        internal StopCause Cause { get; set; }

        internal bool InitializationResolved { get; set; }

        internal bool InitializationSucceeded { get; set; }

        internal string FileName { get; set; } = string.Empty;

        internal long ReceivedBytes { get; set; }

        internal long TotalBytes { get; set; }
    }
}
