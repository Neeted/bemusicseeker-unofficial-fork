using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 自動リネームの対象確認と変更結果を進捗通知と共に受け渡します。
/// 進捗の発行先には操作の排他権を渡しません。
/// </summary>
internal interface IFolderAutoRenameMutationPort
{
    /// <summary>受理済み操作の明示権限を物理変更へ渡す窓口を返します。呼出元が実処理・後片付けまで元leaseを保持します。</summary>
    IFolderAutoRenameMutationPort ForAcceptedOperation(LibraryFileMutationCapability capability) => this;

    /// <summary>
    /// 実移動計画を固定した窓口を作り、交差する対象別受付Pを取得します。Pが使用中ならfalseを返し、停止も変更も行いません。
    /// 純portの検証では独立した対象判断を維持し、常に受理します。
    /// </summary>
    bool TryPreparePhysicalMutation(BMSLibrary library, ChartFolderAutoRenameRequest request,
        string parentDirectory, bool allFolders, LibraryFileMutationCapability capability,
        out IFolderAutoRenameMutationPort prepared, out LibraryFileMutationLease playlistLease)
    { prepared = this; playlistLease = null; return true; }

    bool HasTargets(BMSLibrary library, string parentDirectory);

    /// <summary>準備済みの実移動が再生停止を必要とするかを返します。独立代替portは従来の停止を維持します。</summary>
    bool RequiresPlaybackStop => true;

    /// <summary>選択した譜面のフォルダを改名します。拒否・失敗の案内は、この実行の収集先へ加えます。</summary>
    FolderAutoRenameExecutionResult RenameSelectedWithProgress(
        BMSLibrary library,
        ChartFolderAutoRenameRequest request,
        IFolderAutoRenameProgressWriter progressWriter,
        LibraryOperationNotices notices);

    /// <summary>範囲内の全フォルダを改名します。拒否・失敗の案内は、この実行の収集先へ加えます。</summary>
    AutoRenameBatchResult RenameAllWithReceiptWithProgress(
        BMSLibrary library,
        string parentDirectory,
        IFolderAutoRenameProgressWriter progressWriter,
        LibraryOperationNotices notices);
}

internal sealed class BmsLibraryFolderAutoRenameMutationPort : IFolderAutoRenameMutationPort
{
    private readonly LibraryFileMutationCapability capability;
    private readonly FolderMoveBatch preparedBatch;
    /// <summary>受理済み操作の明示権限と確定済み実移動を物理変更へ渡す窓口を作ります。元leaseの所有と実終端は呼出元が担当します。</summary>
    internal BmsLibraryFolderAutoRenameMutationPort(LibraryFileMutationCapability capability = null,
        FolderMoveBatch preparedBatch = null)
    { this.capability = capability; this.preparedBatch = preparedBatch; }
    /// <summary>受理済み操作の明示権限を物理変更へ渡す窓口を返します。呼出元が実処理・後片付けまで元leaseを保持します。</summary>
    public IFolderAutoRenameMutationPort ForAcceptedOperation(LibraryFileMutationCapability capability) => new BmsLibraryFolderAutoRenameMutationPort(capability, preparedBatch);

    /// <summary>同じLで計画を捕捉し、交差する物理作用の前にPを取得します。取得失敗ではfalseを返し、停止も変更も行いません。</summary>
    public bool TryPreparePhysicalMutation(BMSLibrary library, ChartFolderAutoRenameRequest request,
        string parentDirectory, bool allFolders, LibraryFileMutationCapability capability,
        out IFolderAutoRenameMutationPort prepared, out LibraryFileMutationLease playlistLease)
    {
        FolderMoveBatch batch = preparedBatch ?? library.PrepareAutoRenameBatch(request?.Charts ?? [], parentDirectory, allFolders);
        if (!library.TryEnterManagedOutputMutation(batch.Moves
            .SelectMany(plan => new[] { plan.SourceDirectory, plan.DestinationDirectory }), true, out playlistLease, capability))
        {
            prepared = null;
            return false;
        }
        prepared = new BmsLibraryFolderAutoRenameMutationPort(capability, batch);
        return true;
    }

    public bool HasTargets(BMSLibrary library, string parentDirectory)
    {
        return preparedBatch != null
            ? preparedBatch.Plans.Any(plan => plan.FailureException != null || (!string.IsNullOrWhiteSpace(plan.SourceDirectory) && !string.IsNullOrWhiteSpace(plan.DestinationDirectory)))
            : library?.HasAutoRenameAllChartFolderTargets(parentDirectory) == true;
    }

    /// <summary>同じ確定済み実移動が一件以上ある場合だけ再生を停止します。</summary>
    public bool RequiresPlaybackStop => preparedBatch?.Moves.Count > 0;

    public FolderAutoRenameExecutionResult RenameSelectedWithProgress(
        BMSLibrary library,
        ChartFolderAutoRenameRequest request,
        IFolderAutoRenameProgressWriter progressWriter,
        LibraryOperationNotices notices)
    {
        ArgumentNullException.ThrowIfNull(progressWriter);
        ArgumentNullException.ThrowIfNull(notices);
        AutoRenameBatchResult result = library?.AutoRenameChartFoldersWithProgress(
            request?.Charts ?? [],
            renameRootFolder: false,
            progressWriter, notices, reportAtTerminal: true, capability: capability, preparedBatch: preparedBatch)
            ?? new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
        return FolderAutoRenameExecutionResult.From(result);
    }

    public AutoRenameBatchResult RenameAllWithReceiptWithProgress(
        BMSLibrary library,
        string parentDirectory,
        IFolderAutoRenameProgressWriter progressWriter,
        LibraryOperationNotices notices)
    {
        ArgumentNullException.ThrowIfNull(progressWriter);
        ArgumentNullException.ThrowIfNull(notices);
        return library?.AutoRenameAllChartFoldersWithProgress(
            parentDirectory,
            progressWriter, notices, reportAtTerminal: true, capability: capability, preparedBatch: preparedBatch)
            ?? new AutoRenameBatchResult(false, 0, LibraryMutationSessionReceipt.Empty);
    }
}

internal sealed class FolderAutoRenameRefreshSuppressionChangedEventArgs : EventArgs
{
    internal FolderAutoRenameRefreshSuppressionChangedEventArgs(bool isSuppressed)
    {
        IsSuppressed = isSuppressed;
    }

    internal bool IsSuppressed { get; }
}

internal sealed class FolderAutoRenameProgressSnapshot
{
    internal int TotalCount { get; init; }

    internal int ProcessedCount { get; init; }

    internal string CurrentPath { get; init; } = string.Empty;

    internal bool IsCompleted { get; init; }
}

internal sealed class FolderAutoRenameExecutionResult
{
    internal bool RefreshRequired { get; init; }

    internal AutoRenameBatchResult MutationResult { get; init; }

    internal bool HasDurableCommit => MutationResult?.HasDurableCommit == true;

    /// <summary>
    /// Gets whether the auto-rename command reached durable state but failed
    /// while finalizing it.
    /// </summary>
    internal bool HasDurableFinalizationFailure => MutationResult?.HasDurableFinalizationFailure == true;

    internal bool ManualRecoveryRequired => MutationResult?.ManualRecoveryRequired == true;

    internal bool CompletedWithCleanupFailure => MutationResult?.CompletedWithCleanupFailure == true;

    internal IReadOnlyList<string> RecoveryPaths => MutationResult?.RecoveryPaths ?? [];

    internal static FolderAutoRenameExecutionResult From(AutoRenameBatchResult result)
    {
        return new FolderAutoRenameExecutionResult
        {
            RefreshRequired = result?.HasDurableCommit == true
                && result?.HasDurableFinalizationFailure != true,
            MutationResult = result
        };
    }
}

internal sealed class FolderAutoRenameCompletionReceipt
{
    /// <summary>Captures completed mutation facts separately from the existing refresh decision.</summary>
    internal FolderAutoRenameCompletionReceipt(long generation, bool allFolders, FolderAutoRenameExecutionResult result)
    {
        Generation = generation;
        AllFolders = allFolders;
        RefreshRequired = result?.RefreshRequired == true;
        HasDurableCommit = result?.HasDurableCommit == true;
        HasDurableFinalizationFailure = result?.HasDurableFinalizationFailure == true;
        ManualRecoveryRequired = result?.ManualRecoveryRequired == true;
        CompletedWithCleanupFailure = result?.CompletedWithCleanupFailure == true;
        RecoveryPaths = result?.RecoveryPaths ?? [];
        SessionReceipt = result?.MutationResult?.SessionReceipt;
    }

    internal long Generation { get; }

    /// <summary>Retains all operation facts for the terminal report, independently of refresh.</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; }

    internal bool AllFolders { get; }

    internal bool RefreshRequired { get; }

    internal bool HasDurableCommit { get; }

    /// <summary>
    /// Gets whether finalization failed after the requested move became durable.
    /// </summary>
    internal bool HasDurableFinalizationFailure { get; }

    internal bool ManualRecoveryRequired { get; }

    internal bool CompletedWithCleanupFailure { get; }

    internal IReadOnlyList<string> RecoveryPaths { get; }
}

internal sealed class FolderAutoRenameFailure
{
    /// <summary>
    /// Creates an immutable failure publication, retaining the mutation facts
    /// when the command produced a typed execution result before failing.
    /// </summary>
    /// <param name="generation">The workflow generation that failed.</param>
    /// <param name="allFolders">Whether the failed request covered all folders.</param>
    /// <param name="exception">The primary workflow failure.</param>
    /// <param name="executionResult">The partial mutation result, when available.</param>
    internal FolderAutoRenameFailure(
        long generation,
        bool allFolders,
        Exception exception,
        FolderAutoRenameExecutionResult executionResult = null)
    {
        Generation = generation;
        AllFolders = allFolders;
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        ExecutionResult = executionResult;
    }

    internal long Generation { get; }

    internal bool AllFolders { get; }

    /// <summary>
    /// Gets the immutable execution facts produced before the failure, when a
    /// mutation reached the typed result boundary.
    /// </summary>
    internal FolderAutoRenameExecutionResult ExecutionResult { get; }

    /// <summary>
    /// Gets the underlying immutable auto-rename batch facts, when available.
    /// </summary>
    internal AutoRenameBatchResult MutationResult => ExecutionResult?.MutationResult;

    internal Exception Exception { get; }

    /// <summary>
    /// Gets whether the failed mutation reached durable filesystem and DB
    /// state.
    /// </summary>
    internal bool HasDurableCommit => ExecutionResult?.HasDurableCommit == true;

    /// <summary>
    /// Gets whether finalization failed after the mutation became durable.
    /// </summary>
    internal bool HasDurableFinalizationFailure =>
        ExecutionResult?.HasDurableFinalizationFailure == true;

    /// <summary>
    /// Gets whether cleanup also reported a cleanup-only terminal failure.
    /// </summary>
    internal bool CompletedWithCleanupFailure => ExecutionResult?.CompletedWithCleanupFailure == true;

    /// <summary>
    /// Gets the recovery paths retained by the failed mutation.
    /// </summary>
    internal IReadOnlyList<string> RecoveryPaths => ExecutionResult?.RecoveryPaths ?? [];
}

/// <summary>フォルダ自動改名の明示要求の受付結果です。</summary>
internal enum FolderAutoRenameRequestStatus
{
    /// <summary>対象なし、終了要求後、ライブラリ未接続、受付の閉鎖などで、通知せずに開始しませんでした。</summary>
    NotStarted,

    /// <summary>利用者が確認を取り消しました。</summary>
    Cancelled,

    /// <summary>実行を受理し、背景処理を予定しました。終端は進捗・完了・失敗の通知で公開します。</summary>
    Started,

    /// <summary>自分の実行中または共通受付Lの使用中で、副作用前に拒否し、受付使用中の警告を一度だけ表示しました。</summary>
    RejectedBusy,
}

/// <summary>
/// Owns the scheduled shell lifecycle for selected and all-folder auto-rename
/// requests while the library remains responsible for filesystem/catalog mutation.
/// </summary>
internal sealed class FolderAutoRenameWorkflowOwner
{
    private readonly object syncRoot = new();

    private readonly IFolderAutoRenameMutationPort mutationPort;

    private readonly ChartFileOperationSynchronizer chartFileOperations;

    private readonly ChartMutationActivityOwner chartMutationActivity;

    private readonly IChartMutationPlaybackPort playback;

    private readonly Func<Func<Task>, Task> schedule;

    private readonly Action<Action> dispatchToUi;

    private readonly IUiDialogService dialogs;

    private readonly Action<string> logInfo;

    private readonly Action<Exception> reportWorkflowFailure;

    private readonly Action<Exception> reportNotificationFailure;

    private BMSLibrary library;

    private long generation;

    private long statusVersion;

    private RunContext activeRun;

    private ActiveProgressPublication activeProgressPublication;

    private bool shutdownRequested;

    internal FolderAutoRenameWorkflowOwner(
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IFolderAutoRenameMutationPort mutationPort,
        IChartMutationPlaybackPort playback,
        Func<Func<Task>, Task> schedule,
        Action<Action> dispatchToUi,
        IUiDialogService dialogs,
        Action<string> logInfo = null,
        Action<Exception> reportNotificationFailure = null,
        Action<Exception> reportWorkflowFailure = null)
    {
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.mutationPort = mutationPort ?? throw new ArgumentNullException(nameof(mutationPort));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.logInfo = logInfo;
        this.reportNotificationFailure = reportNotificationFailure;
        this.reportWorkflowFailure = reportWorkflowFailure;
    }

    internal event Action<FolderAutoRenameProgressSnapshot> ProgressChanged;

    internal event Action<FolderAutoRenameCompletionReceipt> CompletionPublished;

    internal event Action<FolderAutoRenameFailure> FailurePublished;

    internal event Action TerminalPublished;

    internal event EventHandler<FolderAutoRenameRefreshSuppressionChangedEventArgs> RefreshSuppressionChanged;

    internal bool IsActive
    {
        get
        {
            lock (syncRoot)
            {
                return activeRun != null;
            }
        }
    }

    internal bool IsIdle => !IsActive;

    /// <summary>
    /// Returns a receipt for the currently owned run, completing after terminal
    /// observer delivery, dispatcher rejection, or stale-generation retirement.
    /// </summary>
    internal Task WaitForIdleAsync()
    {
        lock (syncRoot)
        {
            return activeRun?.IdleCompletion.Task ?? Task.CompletedTask;
        }
    }

    internal void AttachLibrary(BMSLibrary nextLibrary)
    {
        RunContext run;
        long resetGeneration;
        lock (syncRoot)
        {
            if (ReferenceEquals(library, nextLibrary))
            {
                return;
            }
            library = nextLibrary;
            generation++;
            statusVersion++;
            run = activeRun;
            resetGeneration = generation;
        }
        if (run != null)
        {
            DispatchNotification(() =>
            {
                lock (syncRoot)
                {
                    if (generation != resetGeneration || !ReferenceEquals(library, nextLibrary))
                    {
                        return;
                    }
                }
                ProgressChanged?.Invoke(CreateResetProgress());
            });
        }
    }

    /// <summary>
    /// 選択譜面のフォルダ自動改名を共通受付Lの取得後に開始します。自分の実行中または共通受付Lの使用中は、実行・進捗・通知を作らずに
    /// 受付使用中の警告を一度だけ表示して <see cref="FolderAutoRenameRequestStatus.RejectedBusy"/> を返します。
    /// 対象なし・終了要求後・ライブラリ未接続・受付の閉鎖は通知せず <see cref="FolderAutoRenameRequestStatus.NotStarted"/> です。
    /// </summary>
    internal async Task<FolderAutoRenameRequestStatus> RequestStartSelectedAsync(IReadOnlyList<ChartOperationTarget> targets)
    {
        if (!ChartFolderAutoRenameRequest.TryCreate(targets, out ChartFolderAutoRenameRequest request))
        {
            return FolderAutoRenameRequestStatus.NotStarted;
        }
        FolderAutoRenameRequestStatus admission = TryAcceptExplicitRequest(out IDisposable operationGate);
        if (admission != FolderAutoRenameRequestStatus.Started)
        {
            return await CompleteRejectedRequestAsync(admission).ConfigureAwait(false);
        }
        RunContext run;
        lock (syncRoot)
        {
            if (!TryCreateRunUnsafe(allFolders: false, request, parentDirectory: null, out run))
            {
                operationGate.Dispose();
                return FolderAutoRenameRequestStatus.NotStarted;
            }
            run.OperationGate = operationGate;
        }
        Schedule(run);
        return FolderAutoRenameRequestStatus.Started;
    }

    /// <summary>
    /// 範囲内の全フォルダ自動改名を、共通受付Lの取得後に確認してから開始します。Lは確認の前に取得し、確認中も保持します。
    /// 自分の実行中または共通受付Lの使用中は、確認を出さずに受付使用中の警告を一度だけ表示して <see cref="FolderAutoRenameRequestStatus.RejectedBusy"/> を返します。
    /// 利用者の取消は <see cref="FolderAutoRenameRequestStatus.Cancelled"/>、終了要求後・ライブラリ未接続・受付の閉鎖は通知せず未開始です。
    /// </summary>
    internal async Task<FolderAutoRenameRequestStatus> RequestStartAllAsync(string parentDirectory)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return FolderAutoRenameRequestStatus.NotStarted;
        }
        BMSLibrary requestedLibrary;
        lock (syncRoot)
        {
            requestedLibrary = library;
        }
        FolderAutoRenameRequestStatus admission = TryAcceptExplicitRequest(out IDisposable operationGate);
        if (admission != FolderAutoRenameRequestStatus.Started)
        {
            return await CompleteRejectedRequestAsync(admission).ConfigureAwait(false);
        }

        bool operationGateTransferred = false;
        try
        {
            UiDialogResult confirmation = await dialogs.ConfirmAsync(new UiConfirmationRequest(
                BeMusicSeeker.Properties.Resources.Msg_rename_folders,
                BeMusicSeeker.Properties.Resources.Confirm,
                UiDialogButton.OKCancel,
                UiDialogIcon.Question,
                UiDialogDefaultResult.Cancel));
            UiDialogRoute.ThrowIfNotShown(confirmation, "folderAutoRenameAllConfirmation");
            if (!confirmation.IsAccepted)
            {
                return FolderAutoRenameRequestStatus.Cancelled;
            }
            if (!LongPathFileSystem.DirectoryExists(parentDirectory))
            {
                return FolderAutoRenameRequestStatus.NotStarted;
            }

            RunContext run;
            lock (syncRoot)
            {
                if (!ReferenceEquals(library, requestedLibrary)
                    || !TryCreateRunUnsafe(allFolders: true, request: null, parentDirectory, out run))
                {
                    return FolderAutoRenameRequestStatus.NotStarted;
                }
                run.OperationGate = operationGate;
                operationGateTransferred = true;
            }
            Schedule(run);
            return FolderAutoRenameRequestStatus.Started;
        }
        finally
        {
            if (!operationGateTransferred)
            {
                operationGate.Dispose();
            }
        }
    }

    /// <summary>
    /// 明示要求の受付を判定し、受理なら共通受付Lを返します。終了要求後・ライブラリ未接続・受付の閉鎖は未開始、
    /// 自分の実行中と共通受付Lの使用中は受付使用中です。
    /// </summary>
    private FolderAutoRenameRequestStatus TryAcceptExplicitRequest(out IDisposable operationGate)
    {
        operationGate = null;
        lock (syncRoot)
        {
            if (shutdownRequested || library == null)
            {
                return FolderAutoRenameRequestStatus.NotStarted;
            }
            if (activeRun != null)
            {
                return FolderAutoRenameRequestStatus.RejectedBusy;
            }
        }
        if (chartFileOperations.TryEnter(out operationGate))
        {
            return FolderAutoRenameRequestStatus.Started;
        }
        return chartFileOperations.IsAdmissionClosed
            ? FolderAutoRenameRequestStatus.NotStarted
            : FolderAutoRenameRequestStatus.RejectedBusy;
    }

    /// <summary>受付使用中の明示要求だけに警告を一度表示します。表示失敗は呼出元の失敗として返します。</summary>
    private async Task<FolderAutoRenameRequestStatus> CompleteRejectedRequestAsync(FolderAutoRenameRequestStatus status)
    {
        if (status == FolderAutoRenameRequestStatus.RejectedBusy)
        {
            await ShowBusyWarningAsync().ConfigureAwait(false);
        }
        return status;
    }

    private async Task ShowBusyWarningAsync()
    {
        UiDialogResult result = await dialogs.ShowMessageAsync(UiMessageRequest.CreateWarning(
            BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy,
            BeMusicSeeker.Properties.Resources.Warning)).ConfigureAwait(false);
        UiDialogRoute.ThrowIfNotShown(result, "Folder auto-rename Busy notification");
    }

    internal void RequestShutdown()
    {
        lock (syncRoot)
        {
            shutdownRequested = true;
            generation++;
            statusVersion++;
        }
        DispatchNotification(() => ProgressChanged?.Invoke(CreateResetProgress()));
    }

    private bool TryCreateRunUnsafe(
        bool allFolders,
        ChartFolderAutoRenameRequest request,
        string parentDirectory,
        out RunContext run)
    {
        if (shutdownRequested || library == null || activeRun != null)
        {
            run = null;
            return false;
        }
        generation++;
        run = new RunContext(generation, library, allFolders, request, parentDirectory);
        run.ProgressWriter = new FolderAutoRenameProgressWriter(this, run);
        activeRun = run;
        return true;
    }

    private bool Schedule(RunContext run)
    {
        try
        {
            Task scheduled = schedule(() => Execute(run));
            if (scheduled == null)
            {
                throw new InvalidOperationException("Folder auto-rename scheduler returned no task.");
            }
            scheduled.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        CompleteFailure(run, task.Exception?.GetBaseException() ?? new InvalidOperationException("Folder auto-rename scheduler failed."));
                    }
                    else if (task.IsCanceled)
                    {
                        CompleteFailure(run, new InvalidOperationException("Folder auto-rename scheduler canceled."));
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }
        catch (Exception exception)
        {
            CompleteFailure(run, exception);
            return true;
        }
    }

    private async Task Execute(RunContext run)
    {
        FolderAutoRenameExecutionResult result = null;
        try
        {
            if (!IsCurrentGeneration(run))
            {
                CompleteStale(run);
                return;
            }
            bool physicalAdmitted;
            IFolderAutoRenameMutationPort preparedPort;
            using (LibraryFileMutationCapability preparationCapability = chartFileOperations.CreateMutationCapability(run.OperationGate))
            {
                physicalAdmitted = mutationPort.TryPreparePhysicalMutation(run.Library, run.SelectedRequest,
                    run.ParentDirectory, run.AllFolders, preparationCapability, out preparedPort, out run.PlaylistLease);
            }
            if (!physicalAdmitted)
            {
                await CompletePhysicalAdmissionBusyAsync(run).ConfigureAwait(false);
                return;
            }
            run.PreparedPort = preparedPort;
            if (run.AllFolders)
            {
                bool hasTargets;
                try
                {
                    hasTargets = false;
                    if (!await ExecuteMutation(
                        run,
                        stopPlayback: false,
                        mutation: operationPort => hasTargets = operationPort.HasTargets(run.Library, run.ParentDirectory),
                        refreshSuppression: false))
                    {
                        CompleteStale(run);
                        return;
                    }
                }
                catch (Exception exception)
                {
                    LogInfoSafely("folder_auto_rename preflight_failed message=" + FormatExceptionMessage(exception));
                    CompleteFailure(run, exception);
                    return;
                }
                if (!hasTargets)
                {
                    CompleteSuccess(run, new FolderAutoRenameExecutionResult());
                    return;
                }
            }
            run.ProgressWriter.TryWrite(new FolderAutoRenameProgressUpdate(1, 0, string.Empty));
            LogInfoSafely("folder_auto_rename start scope=" + (run.AllFolders ? "all" : "selected"));
            if (run.AllFolders)
            {
                if (!await ExecuteMutation(
                    run,
                    stopPlayback: run.PreparedPort.RequiresPlaybackStop,
                    operationPort =>
                    {
                        result = FolderAutoRenameExecutionResult.From(
                            operationPort.RenameAllWithReceiptWithProgress(
                                run.Library,
                                run.ParentDirectory,
                                run.ProgressWriter,
                                run.Notices));
                    },
                    refreshSuppression: true))
                {
                    CompleteStale(run);
                    return;
                }
            }
            else
            {
                if (!await ExecuteMutation(
                    run,
                    stopPlayback: run.PreparedPort.RequiresPlaybackStop,
                    operationPort =>
                    {
                        result = operationPort.RenameSelectedWithProgress(
                            run.Library,
                            run.SelectedRequest,
                            run.ProgressWriter,
                            run.Notices);
                    },
                    refreshSuppression: true))
                {
                    CompleteStale(run);
                    return;
                }
            }
            if (result == null)
            {
                throw new InvalidOperationException("Folder auto-rename executor returned no result.");
            }
            LogInfoSafely("folder_auto_rename done scope=" + (run.AllFolders ? "all" : "selected") + " refreshRequired=" + result.RefreshRequired);
            if (result.HasDurableFinalizationFailure || result.MutationResult?.PrimaryFailure != null)
            {
                CompleteFailure(
                    run,
                    result.MutationResult?.PrimaryFailure?.SourceException
                        ?? new IOException("Folder move finalization failed."),
                    result);
                return;
            }
            CompleteSuccess(run, result);
        }
        catch (Exception exception)
        {
            LogInfoSafely("folder_auto_rename failed scope=" + (run.AllFolders ? "all" : "selected") + " message=" + FormatExceptionMessage(exception));
            CompleteFailure(run, exception, result);
        }
    }

    private async Task<bool> ExecuteMutation(
        RunContext run,
        bool stopPlayback,
        Action<IFolderAutoRenameMutationPort> mutation,
        bool refreshSuppression)
    {
        BMSLibrary library = run.Library;
        IDisposable activityLease = null;
        // 受付は要求の受理時に取得済みで、実行の終端まで run が所有します。
        IDisposable operationGate = run.OperationGate
            ?? throw new InvalidOperationException("Folder auto-rename run has no accepted operation gate.");
        LibraryFileMutationCapability capability = null;
        bool suppressionStarted = false;
        bool mutationAllowed = true;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            activityLease = chartMutationActivity.Enter();
            if (!IsCurrentGeneration(run))
            {
                mutationAllowed = false;
            }
            else
            {
                if (stopPlayback) { await playback.StopPlaybackForMutationAsync().ConfigureAwait(false); }
                suppressionStarted = refreshSuppression;
                if (suppressionStarted)
                {
                    PublishRefreshSuppressionChanged(isSuppressed: true);
                }
                capability = chartFileOperations.CreateMutationCapability(operationGate);
                using LibraryFileMutationCapability playlistCapability = run.PlaylistLease?.CreateMutationCapability();
                using LibraryFileMutationCapability combinedCapability = playlistCapability == null ? null : capability.WithPlaylistCapability(playlistCapability);
                mutation((run.PreparedPort ?? mutationPort).ForAcceptedOperation(combinedCapability ?? capability));
            }
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            // 抑制解除で流れる失効ツリーの反映からの重複選択を準備へ合流させるため、解除の通知より前に登録する。
            Task acceptedPreparation = capability != null && refreshSuppression
                ? library.CompleteAcceptedDuplicateDataAsync(capability)
                : Task.CompletedTask;
            if (suppressionStarted)
            {
                CaptureCleanupFailure(() => PublishRefreshSuppressionChanged(isSuppressed: false), failures);
            }
            if (activityLease != null)
            {
                CaptureCleanupFailure(activityLease.Dispose, failures);
            }
            try { await acceptedPreparation.ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(ExceptionDispatchInfo.Capture(failure)); }
            CaptureCleanupFailure(() => capability?.Dispose(), failures);
        }
        switch (failures.Count)
        {
            case 0:
                return mutationAllowed;
            case 1:
                failures[0].Throw();
                return false;
            default:
                throw new AggregateException(failures.Select(failure => failure.SourceException));
        }
    }

    private void PublishRefreshSuppressionChanged(bool isSuppressed)
    {
        RefreshSuppressionChanged?.Invoke(
            this,
            new FolderAutoRenameRefreshSuppressionChangedEventArgs(isSuppressed));
    }

    private static void CaptureCleanupFailure(Action cleanup, List<ExceptionDispatchInfo> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
    }

    private void PublishProgress(RunContext run, FolderAutoRenameProgressSnapshot progress)
    {
        if (progress == null)
        {
            return;
        }
        PublishProgress(
            run,
            new FolderAutoRenameProgressUpdate(
                progress.TotalCount,
                progress.ProcessedCount,
                progress.CurrentPath));
    }

    private void PublishProgress(RunContext run, FolderAutoRenameProgressUpdate progress)
    {
        FolderAutoRenameProgressSnapshot snapshot = new()
        {
            TotalCount = progress.TotalCount,
            ProcessedCount = progress.ProcessedCount,
            CurrentPath = progress.CurrentPath ?? string.Empty
        };
        bool schedule;
        ActiveProgressPublication publication;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run)
                || !IsCurrentGenerationUnsafe(run)
                || run.ProgressWriter.IsSealed)
            {
                return;
            }
            snapshot = CloneProgress(snapshot);
            run.LastProgress = snapshot;
            run.ProgressStarted = true;
            statusVersion++;
            publication = activeProgressPublication;
            if (publication == null || !ReferenceEquals(publication.Run, run))
            {
                publication = new ActiveProgressPublication(run);
                activeProgressPublication = publication;
            }
            publication.Snapshot = snapshot;
            publication.HasSnapshot = true;
            schedule = !publication.DispatchScheduled;
            if (schedule)
            {
                publication.DispatchScheduled = true;
            }
        }
        if (!schedule)
        {
            return;
        }
        if (!DispatchNotification(() => DrainActiveProgress(publication)))
        {
            lock (syncRoot)
            {
                if (ReferenceEquals(activeProgressPublication, publication))
                {
                    publication.HasSnapshot = false;
                    publication.DispatchScheduled = false;
                }
            }
        }
    }

    private void DrainActiveProgress(ActiveProgressPublication publication)
    {
        FolderAutoRenameProgressSnapshot snapshot;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeProgressPublication, publication)
                || publication.Sealed
                || !publication.HasSnapshot)
            {
                if (ReferenceEquals(activeProgressPublication, publication))
                {
                    publication.DispatchScheduled = false;
                }
                return;
            }
            snapshot = publication.Snapshot;
            publication.HasSnapshot = false;
            publication.IsDraining = true;
        }

        InvokeObserverSafely(() => ProgressChanged?.Invoke(CloneProgress(snapshot)));

        bool schedule;
        lock (syncRoot)
        {
            publication.IsDraining = false;
            if (!ReferenceEquals(activeProgressPublication, publication) || publication.Sealed)
            {
                publication.HasSnapshot = false;
                publication.DispatchScheduled = false;
                return;
            }
            schedule = publication.HasSnapshot;
            if (!schedule)
            {
                publication.DispatchScheduled = false;
            }
        }
        if (schedule && !DispatchNotification(() => DrainActiveProgress(publication)))
        {
            lock (syncRoot)
            {
                if (ReferenceEquals(activeProgressPublication, publication))
                {
                    publication.HasSnapshot = false;
                    publication.DispatchScheduled = false;
                }
            }
        }
    }

    private void CompleteSuccess(RunContext run, FolderAutoRenameExecutionResult result)
    {
        BeginProgressTerminalization(run);
        long terminalStatusVersion;
        bool publish;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run))
            {
                return;
            }
            statusVersion++;
            terminalStatusVersion = statusVersion;
            publish = IsCurrentGenerationUnsafe(run);
            if (publish)
            {
                run.TerminalPending = true;
            }
        }
        if (!publish)
        {
            CompleteIdle(run);
            return;
        }
        FolderAutoRenameProgressSnapshot terminalProgress = CreateTerminalProgress(run);
        var receipt = new FolderAutoRenameCompletionReceipt(run.Generation, run.AllFolders, result);
        bool dispatched = DispatchNotification(() =>
        {
            bool staleTerminal;
            lock (syncRoot)
            {
                staleTerminal = !IsCurrentGenerationUnsafe(run) || statusVersion != terminalStatusVersion;
            }
            if (staleTerminal)
            {
                FinishTerminalPublication(run);
                return;
            }
            if (run.ProgressStarted)
            {
                InvokeObserverSafely(() => ProgressChanged?.Invoke(CloneProgress(terminalProgress)));
            }
            InvokeObserverSafely(() => CompletionPublished?.Invoke(receipt));
            ReleaseAndPublishTerminal(run);
        });
        if (!dispatched)
        {
            FinishTerminalPublication(run);
        }
    }

    private void CompleteFailure(
        RunContext run,
        Exception exception,
        FolderAutoRenameExecutionResult executionResult = null)
    {
        BeginProgressTerminalization(run);
        long terminalStatusVersion;
        bool publish;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run))
            {
                return;
            }
            statusVersion++;
            terminalStatusVersion = statusVersion;
            publish = IsCurrentGenerationUnsafe(run);
            if (publish)
            {
                run.TerminalPending = true;
            }
        }
        ReportWorkflowFailure(exception);
        if (!publish)
        {
            CompleteIdle(run);
            return;
        }
        FolderAutoRenameProgressSnapshot terminalProgress = CreateTerminalProgress(run);
        var failure = new FolderAutoRenameFailure(
            run.Generation,
            run.AllFolders,
            exception,
            executionResult);
        bool dispatched = DispatchNotification(() =>
        {
            bool staleTerminal;
            lock (syncRoot)
            {
                staleTerminal = !IsCurrentGenerationUnsafe(run) || statusVersion != terminalStatusVersion;
            }
            if (staleTerminal)
            {
                FinishTerminalPublication(run);
                return;
            }
            if (run.ProgressStarted)
            {
                InvokeObserverSafely(() => ProgressChanged?.Invoke(CloneProgress(terminalProgress)));
            }
            InvokeObserverSafely(() => FailurePublished?.Invoke(failure));
            ReleaseAndPublishTerminal(run);
        });
        if (!dispatched)
        {
            FinishTerminalPublication(run);
        }
    }

    /// <summary>
    /// 完了・失敗の通知（受付を保持したまま発行）の後に、実行の論理Lと対象別Pを解放し、集めた案内の表示を依頼してから、終端を発行します。
    /// 案内の依頼は UI の外から行い、抑止の判定と最初の組の依頼が済むまでを待ちます。表示の完了は待たないので、終端は表示の保留中にも発行されます。
    /// 案内がなければ待たずに、同じ UI の配送の中で終端を発行します。UI スレッドは依頼の発行を同期で待たず、継続で終端を配送します。
    /// </summary>
    private void ReleaseAndPublishTerminal(RunContext run)
    {
        ReleaseOperationGate(run);
        Task requested = FileDbMutationReport.BeginRequestOperationNotices(
            dialogs, run.Notices, () => !IsCurrentGeneration(run) || run.Library.IsShutdownRequested, "folderAutoRename");
        if (requested.IsCompleted)
        {
            PublishTerminal(run);
            return;
        }
        _ = requested.ContinueWith(
            _ =>
            {
                if (!DispatchNotification(() => PublishTerminal(run)))
                {
                    FinishTerminalPublication(run);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishTerminal(RunContext run)
    {
        InvokeObserverSafely(() => TerminalPublished?.Invoke());
        FinishTerminalPublication(run);
    }

    private void FinishTerminalPublication(RunContext run)
    {
        bool completed = false;
        lock (syncRoot)
        {
            if (ReferenceEquals(activeRun, run) && run.TerminalPending)
            {
                run.TerminalPending = false;
                if (ReferenceEquals(activeProgressPublication?.Run, run))
                {
                    activeProgressPublication = null;
                }
                statusVersion++;
                completed = true;
            }
        }
        if (completed)
        {
            CompleteIdle(run);
        }
    }

    /// <summary>
    /// 確認後に対象別受付Pを取得できなかった実行を、進捗・完了・失敗・終端の通知なしで終えます。
    /// LとPを解放してから、現在の世代で終了要求前なら受付使用中の警告を一度だけ表示し、その後にidleを公開します。
    /// 警告の表示失敗は通知失敗として報告し、一般の失敗へ置き換えません。
    /// </summary>
    private async Task CompletePhysicalAdmissionBusyAsync(RunContext run)
    {
        BeginProgressTerminalization(run);
        ReleaseOperationGate(run);
        try
        {
            if (IsCurrentGeneration(run) && !run.Library.IsShutdownRequested)
            {
                await ShowBusyWarningAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            ReportNotificationFailure(exception);
        }
        finally
        {
            CompleteIdle(run);
        }
    }

    private void CompleteStale(RunContext run)
    {
        BeginProgressTerminalization(run);
        CompleteIdle(run);
    }

    private void BeginProgressTerminalization(RunContext run)
    {
        if (run == null)
        {
            return;
        }
        run.ProgressWriter.Seal();
        lock (syncRoot)
        {
            if (ReferenceEquals(activeProgressPublication?.Run, run))
            {
                activeProgressPublication.Sealed = true;
                activeProgressPublication.HasSnapshot = false;
            }
        }
    }

    /// <summary>受付解放後にのみidleを公開し、次要求と終端Taskの観測を同じ境界へ揃えます。</summary>
    private void CompleteIdle(RunContext run)
    {
        ReleaseOperationGate(run);
        lock (syncRoot)
        {
            if (ReferenceEquals(activeRun, run))
            {
                activeRun = null;
                statusVersion++;
            }
            run.IdleCompletion.TrySetResult(true);
        }
    }

    private static void ReleaseOperationGate(RunContext run)
    {
        Interlocked.Exchange(ref run.PlaylistLease, null)?.Dispose();
        IDisposable operationGate = Interlocked.Exchange(ref run.OperationGate, null);
        operationGate?.Dispose();
    }

    private bool IsCurrentGeneration(RunContext run)
    {
        lock (syncRoot)
        {
            return IsCurrentGenerationUnsafe(run);
        }
    }

    private bool IsCurrentGenerationUnsafe(RunContext run)
    {
        return !shutdownRequested
            && generation == run.Generation
            && ReferenceEquals(library, run.Library);
    }

    private bool DispatchNotification(Action notification)
    {
        try
        {
            dispatchToUi(() =>
            {
                try
                {
                    notification();
                }
                catch (Exception exception)
                {
                    ReportNotificationFailure(exception);
                }
            });
            return true;
        }
        catch (Exception exception)
        {
            ReportNotificationFailure(exception);
            return false;
        }
    }

    private void ReportWorkflowFailure(Exception exception)
    {
        try
        {
            reportWorkflowFailure?.Invoke(exception);
        }
        catch
        {
        }
    }

    private void ReportNotificationFailure(Exception exception)
    {
        try
        {
            reportNotificationFailure?.Invoke(exception);
        }
        catch
        {
        }
    }

    private void InvokeObserverSafely(Action observer)
    {
        try
        {
            observer?.Invoke();
        }
        catch (Exception exception)
        {
            ReportNotificationFailure(exception);
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

    private static string FormatExceptionMessage(Exception exception)
    {
        return (exception?.Message ?? string.Empty).Replace(Environment.NewLine, " ");
    }

    private static FolderAutoRenameProgressSnapshot CloneProgress(FolderAutoRenameProgressSnapshot source)
    {
        return new FolderAutoRenameProgressSnapshot
        {
            TotalCount = source?.TotalCount ?? 0,
            ProcessedCount = source?.ProcessedCount ?? 0,
            CurrentPath = source?.CurrentPath ?? string.Empty,
            IsCompleted = source?.IsCompleted == true
        };
    }

    private static FolderAutoRenameProgressSnapshot CreateTerminalProgress(RunContext run)
    {
        FolderAutoRenameProgressSnapshot progress = CloneProgress(run.LastProgress ?? new FolderAutoRenameProgressSnapshot { TotalCount = 1 });
        return new FolderAutoRenameProgressSnapshot
        {
            TotalCount = progress.TotalCount,
            ProcessedCount = progress.ProcessedCount,
            CurrentPath = string.Empty,
            IsCompleted = true
        };
    }

    private static FolderAutoRenameProgressSnapshot CreateResetProgress()
    {
        return new FolderAutoRenameProgressSnapshot
        {
            TotalCount = 1,
            ProcessedCount = 0,
            CurrentPath = string.Empty,
            IsCompleted = true
        };
    }

    private sealed class ActiveProgressPublication
    {
        internal ActiveProgressPublication(RunContext run)
        {
            Run = run ?? throw new ArgumentNullException(nameof(run));
        }

        internal RunContext Run { get; }

        internal FolderAutoRenameProgressSnapshot Snapshot { get; set; }

        internal bool HasSnapshot { get; set; }

        internal bool DispatchScheduled { get; set; }

        internal bool IsDraining { get; set; }

        internal bool Sealed { get; set; }
    }

    /// <summary>
    /// Feature-local progress writer. It exposes immutable facts to the owner
    /// while keeping scheduling and observer delivery on the consumer side.
    /// </summary>
    private sealed class FolderAutoRenameProgressWriter : IFolderAutoRenameProgressWriter
    {
        private readonly FolderAutoRenameWorkflowOwner owner;

        private readonly RunContext run;

        private int sealedState;

        internal FolderAutoRenameProgressWriter(
            FolderAutoRenameWorkflowOwner owner,
            RunContext run)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            this.run = run ?? throw new ArgumentNullException(nameof(run));
        }

        internal bool IsSealed => Volatile.Read(ref sealedState) != 0;

        public void TryWrite(FolderAutoRenameProgressUpdate update)
        {
            if (IsSealed)
            {
                return;
            }
            try
            {
                owner.PublishProgress(run, update.Normalize());
            }
            catch (Exception exception)
            {
                owner.ReportNotificationFailure(exception);
            }
        }

        internal void Seal()
        {
            Interlocked.Exchange(ref sealedState, 1);
        }
    }

    private sealed class RunContext
    {
        internal RunContext(
            long generation,
            BMSLibrary library,
            bool allFolders,
            ChartFolderAutoRenameRequest selectedRequest,
            string parentDirectory)
        {
            Generation = generation;
            Library = library;
            AllFolders = allFolders;
            SelectedRequest = selectedRequest;
            ParentDirectory = parentDirectory;
        }

        internal long Generation { get; }

        internal BMSLibrary Library { get; }

        internal bool AllFolders { get; }

        internal ChartFolderAutoRenameRequest SelectedRequest { get; }

        internal string ParentDirectory { get; }

        internal FolderAutoRenameProgressWriter ProgressWriter { get; set; }

        internal IDisposable OperationGate;
        internal LibraryFileMutationLease PlaylistLease;
        internal IFolderAutoRenameMutationPort PreparedPort;

        /// <summary>この実行が集める案内の収集先です。事前処理と本体の両方の案内が、一つの列として終端の解放後に表示されます。</summary>
        internal LibraryOperationNotices Notices { get; } = new();

        internal FolderAutoRenameProgressSnapshot LastProgress { get; set; }

        internal bool ProgressStarted { get; set; }

        internal bool TerminalPending { get; set; }

        internal TaskCompletionSource<bool> IdleCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
