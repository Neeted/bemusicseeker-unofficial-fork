using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal interface IFolderAutoRenameMutationPort
{
    bool HasTargets(BMSLibrary library, string parentDirectory);

    FolderAutoRenameExecutionResult RenameSelected(
        BMSLibrary library,
        ChartFolderAutoRenameRequest request,
        Action<int, int, string> progressReporter);

    bool RenameAll(
        BMSLibrary library,
        string parentDirectory,
        Action<int, int, string> progressReporter);
}

internal interface IFolderAutoRenameTerminalMutationPort
{
    AutoRenameBatchResult RenameAllWithReceipt(
        BMSLibrary library,
        string parentDirectory,
        Action<int, int, string> progressReporter);
}

/// <summary>
/// Progress-aware auto-rename mutation seam. The producer receives only an
/// immutable, non-blocking writer while the workflow owner retains the
/// operation lease.
/// </summary>
internal interface IFolderAutoRenameProgressMutationPort
{
    FolderAutoRenameExecutionResult RenameSelectedWithProgress(
        BMSLibrary library,
        ChartFolderAutoRenameRequest request,
        IFolderAutoRenameProgressWriter progressWriter);

    bool RenameAllWithProgress(
        BMSLibrary library,
        string parentDirectory,
        IFolderAutoRenameProgressWriter progressWriter);

    AutoRenameBatchResult RenameAllWithReceiptWithProgress(
        BMSLibrary library,
        string parentDirectory,
        IFolderAutoRenameProgressWriter progressWriter);
}

internal sealed class BmsLibraryFolderAutoRenameMutationPort :
    IFolderAutoRenameMutationPort,
    IFolderAutoRenameTerminalMutationPort,
    IFolderAutoRenameProgressMutationPort
{
    public bool HasTargets(BMSLibrary library, string parentDirectory)
    {
        return library?.HasAutoRenameAllChartFolderTargets(parentDirectory) == true;
    }

    public FolderAutoRenameExecutionResult RenameSelected(
        BMSLibrary library,
        ChartFolderAutoRenameRequest request,
        Action<int, int, string> progressReporter)
    {
        AutoRenameBatchResult result = library?.AutoRenameChartFoldersWithResult(
            request?.Charts ?? [],
            progressReporter: progressReporter);
        return FolderAutoRenameExecutionResult.From(result);
    }

    public bool RenameAll(
        BMSLibrary library,
        string parentDirectory,
        Action<int, int, string> progressReporter)
    {
        return library?.AutoRenameAllChartFolders(parentDirectory, progressReporter) == true;
    }

    public AutoRenameBatchResult RenameAllWithReceipt(
        BMSLibrary library,
        string parentDirectory,
        Action<int, int, string> progressReporter)
    {
        return library?.AutoRenameAllChartFoldersWithResult(parentDirectory, progressReporter)
            ?? new AutoRenameBatchResult(false, 0, new FileDbMutationBatchReceipt([]));
    }

    public FolderAutoRenameExecutionResult RenameSelectedWithProgress(
        BMSLibrary library,
        ChartFolderAutoRenameRequest request,
        IFolderAutoRenameProgressWriter progressWriter)
    {
        ArgumentNullException.ThrowIfNull(progressWriter);
        AutoRenameBatchResult result = library?.AutoRenameChartFoldersWithProgress(
            request?.Charts ?? [],
            renameRootFolder: false,
            progressWriter)
            ?? new AutoRenameBatchResult(false, 0, new FileDbMutationBatchReceipt([]));
        return FolderAutoRenameExecutionResult.From(result);
    }

    public bool RenameAllWithProgress(
        BMSLibrary library,
        string parentDirectory,
        IFolderAutoRenameProgressWriter progressWriter)
    {
        ArgumentNullException.ThrowIfNull(progressWriter);
        return library?.AutoRenameAllChartFoldersWithProgress(
            parentDirectory,
            progressWriter)?.HasActionablePlan == true;
    }

    public AutoRenameBatchResult RenameAllWithReceiptWithProgress(
        BMSLibrary library,
        string parentDirectory,
        IFolderAutoRenameProgressWriter progressWriter)
    {
        ArgumentNullException.ThrowIfNull(progressWriter);
        return library?.AutoRenameAllChartFoldersWithProgress(
            parentDirectory,
            progressWriter)
            ?? new AutoRenameBatchResult(false, 0, new FileDbMutationBatchReceipt([]));
    }
}

internal interface IFolderAutoRenamePlaybackPort
{
    void StopPlaybackForCharts(IReadOnlyList<ChartFile> charts);

    void StopPlaybackForFolderMutation();
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

    internal bool ManualRecoveryRequired => MutationResult?.ManualRecoveryRequired == true;

    internal bool CompletedWithCleanupFailure => MutationResult?.CompletedWithCleanupFailure == true;

    internal IReadOnlyList<string> RecoveryPaths => MutationResult?.RecoveryPaths ?? [];

    internal static FolderAutoRenameExecutionResult From(AutoRenameBatchResult result)
    {
        return new FolderAutoRenameExecutionResult
        {
            RefreshRequired = result?.HasDurableCommit == true,
            MutationResult = result
        };
    }
}

internal sealed class FolderAutoRenameCompletionReceipt
{
    internal FolderAutoRenameCompletionReceipt(long generation, bool allFolders, FolderAutoRenameExecutionResult result)
    {
        Generation = generation;
        AllFolders = allFolders;
        RefreshRequired = result?.RefreshRequired == true;
        HasDurableCommit = result?.HasDurableCommit == true;
        ManualRecoveryRequired = result?.ManualRecoveryRequired == true;
        CompletedWithCleanupFailure = result?.CompletedWithCleanupFailure == true;
        RecoveryPaths = result?.RecoveryPaths ?? [];
    }

    internal long Generation { get; }

    internal bool AllFolders { get; }

    internal bool RefreshRequired { get; }

    internal bool HasDurableCommit { get; }

    internal bool ManualRecoveryRequired { get; }

    internal bool CompletedWithCleanupFailure { get; }

    internal IReadOnlyList<string> RecoveryPaths { get; }
}

internal sealed class FolderAutoRenameFailure
{
    internal FolderAutoRenameFailure(long generation, bool allFolders, Exception exception)
    {
        Generation = generation;
        AllFolders = allFolders;
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    internal long Generation { get; }

    internal bool AllFolders { get; }

    internal Exception Exception { get; }
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

    private readonly IFolderAutoRenamePlaybackPort playback;

    private readonly Func<Action, Task> schedule;

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
        IFolderAutoRenamePlaybackPort playback,
        Func<Action, Task> schedule,
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

    internal bool RequestStartSelected(IReadOnlyList<ChartOperationTarget> targets)
    {
        if (!ChartFolderAutoRenameRequest.TryCreate(targets, out ChartFolderAutoRenameRequest request))
        {
            return false;
        }
        RunContext run;
        lock (syncRoot)
        {
            if (!TryCreateRunUnsafe(allFolders: false, request, parentDirectory: null, out run))
            {
                return false;
            }
        }
        if (!chartFileOperations.TryEnter(out run.OperationGate))
        {
            CompleteFailure(run, new InvalidOperationException("A chart-file operation is already active."));
            return true;
        }
        return Schedule(run);
    }

    internal async Task RequestStartAllAsync(string parentDirectory)
    {
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return;
        }
        BMSLibrary requestedLibrary;
        lock (syncRoot)
        {
            requestedLibrary = library;
            if (shutdownRequested || requestedLibrary == null || activeRun != null)
            {
                return;
            }
        }

        if (!chartFileOperations.TryEnter(out IDisposable operationGate))
        {
            RunContext rejectedRun;
            lock (syncRoot)
            {
                if (!TryCreateRunUnsafe(allFolders: true, request: null, parentDirectory, out rejectedRun))
                {
                    return;
                }
            }
            CompleteFailure(rejectedRun, new InvalidOperationException("A chart-file operation is already active."));
            return;
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
            if (!confirmation.IsAccepted || !LongPathFileSystem.DirectoryExists(parentDirectory))
            {
                return;
            }

            RunContext run;
            lock (syncRoot)
            {
                if (!ReferenceEquals(library, requestedLibrary)
                    || !TryCreateRunUnsafe(allFolders: true, request: null, parentDirectory, out run))
                {
                    return;
                }
                run.OperationGate = operationGate;
                operationGateTransferred = true;
            }
            Schedule(run);
        }
        finally
        {
            if (!operationGateTransferred)
            {
                operationGate.Dispose();
            }
        }
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

    private void Execute(RunContext run)
    {
        try
        {
            if (!IsCurrentGeneration(run))
            {
                CompleteStale(run);
                return;
            }
            if (run.AllFolders)
            {
                bool hasTargets;
                try
                {
                    hasTargets = false;
                    if (!ExecuteMutation(
                        run,
                        stopPlayback: null,
                        mutation: () => hasTargets = mutationPort.HasTargets(run.Library, run.ParentDirectory),
                        refreshSuppression: false,
                        releaseAcquiredOperationGate: false))
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
            Action<int, int, string> progressReporter = (total, processed, currentPath) => PublishProgress(run, new FolderAutoRenameProgressSnapshot
            {
                TotalCount = total,
                ProcessedCount = processed,
                CurrentPath = currentPath ?? string.Empty
            });
            FolderAutoRenameExecutionResult result = null;
            if (run.AllFolders)
            {
                if (!ExecuteMutation(
                    run,
                    playback.StopPlaybackForFolderMutation,
                    () =>
                    {
                        if (mutationPort is IFolderAutoRenameProgressMutationPort progressMutationPort)
                        {
                            result = FolderAutoRenameExecutionResult.From(
                                progressMutationPort.RenameAllWithReceiptWithProgress(
                                    run.Library,
                                    run.ParentDirectory,
                                    run.ProgressWriter));
                        }
                        else if (mutationPort is IFolderAutoRenameTerminalMutationPort terminalMutationPort)
                        {
                            result = FolderAutoRenameExecutionResult.From(
                                terminalMutationPort.RenameAllWithReceipt(
                                    run.Library,
                                    run.ParentDirectory,
                                    progressReporter));
                        }
                        else
                        {
                            bool changed = mutationPort.RenameAll(run.Library, run.ParentDirectory, progressReporter);
                            result = new FolderAutoRenameExecutionResult { RefreshRequired = changed };
                        }
                    },
                    refreshSuppression: true,
                    releaseAcquiredOperationGate: false))
                {
                    CompleteStale(run);
                    return;
                }
            }
            else
            {
                if (!ExecuteMutation(
                    run,
                    () => playback.StopPlaybackForCharts(run.SelectedRequest.Charts),
                    () =>
                    {
                        if (mutationPort is IFolderAutoRenameProgressMutationPort progressMutationPort)
                        {
                            result = progressMutationPort.RenameSelectedWithProgress(
                                run.Library,
                                run.SelectedRequest,
                                run.ProgressWriter);
                        }
                        else
                        {
                            result = mutationPort.RenameSelected(run.Library, run.SelectedRequest, progressReporter);
                        }
                    },
                    refreshSuppression: true,
                    releaseAcquiredOperationGate: false))
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
            CompleteSuccess(run, result);
        }
        catch (Exception exception)
        {
            LogInfoSafely("folder_auto_rename failed scope=" + (run.AllFolders ? "all" : "selected") + " message=" + FormatExceptionMessage(exception));
            CompleteFailure(run, exception);
        }
    }

    private bool ExecuteMutation(
        RunContext run,
        Action stopPlayback,
        Action mutation,
        bool refreshSuppression,
        bool releaseAcquiredOperationGate = true)
    {
        BMSLibrary library = run.Library;
        BMSLibrary.OperationDialogScope dialogScope = null;
        IDisposable activityLease = null;
        IDisposable operationGate = run.OperationGate;
        bool ownsOperationGate = operationGate == null;
        bool suppressionStarted = false;
        bool mutationAllowed = true;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            if (ownsOperationGate && !chartFileOperations.TryEnter(out operationGate))
            {
                throw new InvalidOperationException("A chart-file operation is already active.");
            }
            dialogScope = library.BeginOperationDialogScope();
            activityLease = chartMutationActivity.Enter();
            if (!IsCurrentGeneration(run))
            {
                mutationAllowed = false;
            }
            else
            {
                stopPlayback?.Invoke();
                suppressionStarted = refreshSuppression;
                if (suppressionStarted)
                {
                    PublishRefreshSuppressionChanged(isSuppressed: true);
                }
                mutation();
            }
        }
        catch (Exception exception)
        {
            failures.Add(ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            if (suppressionStarted)
            {
                CaptureCleanupFailure(() => PublishRefreshSuppressionChanged(isSuppressed: false), failures);
            }
            if (ownsOperationGate && operationGate != null && releaseAcquiredOperationGate)
            {
                CaptureCleanupFailure(operationGate.Dispose, failures);
            }
            if (activityLease != null)
            {
                CaptureCleanupFailure(activityLease.Dispose, failures);
            }
            if (dialogScope != null)
            {
                CaptureCleanupFailure(dialogScope.Dispose, failures);
                CaptureCleanupFailure(dialogScope.Flush, failures);
            }
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
        ReleaseOperationGate(run);
        long terminalStatusVersion;
        bool publish;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run))
            {
                return;
            }
            activeRun = null;
            statusVersion++;
            terminalStatusVersion = statusVersion;
            publish = IsCurrentGenerationUnsafe(run);
            if (publish)
            {
                activeRun = run;
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
            InvokeObserverSafely(() => TerminalPublished?.Invoke());
            FinishTerminalPublication(run);
        });
        if (!dispatched)
        {
            FinishTerminalPublication(run);
        }
    }

    private void CompleteFailure(RunContext run, Exception exception)
    {
        BeginProgressTerminalization(run);
        ReleaseOperationGate(run);
        long terminalStatusVersion;
        bool publish;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run))
            {
                return;
            }
            activeRun = null;
            statusVersion++;
            terminalStatusVersion = statusVersion;
            publish = IsCurrentGenerationUnsafe(run);
            if (publish)
            {
                activeRun = run;
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
        var failure = new FolderAutoRenameFailure(run.Generation, run.AllFolders, exception);
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
            InvokeObserverSafely(() => TerminalPublished?.Invoke());
            FinishTerminalPublication(run);
        });
        if (!dispatched)
        {
            FinishTerminalPublication(run);
        }
    }

    private void FinishTerminalPublication(RunContext run)
    {
        bool completed = false;
        lock (syncRoot)
        {
            if (ReferenceEquals(activeRun, run) && run.TerminalPending)
            {
                activeRun = null;
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

    private void CompleteStale(RunContext run)
    {
        BeginProgressTerminalization(run);
        ReleaseOperationGate(run);
        lock (syncRoot)
        {
            if (ReferenceEquals(activeRun, run))
            {
                activeRun = null;
                statusVersion++;
            }
        }
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

    private static void CompleteIdle(RunContext run)
    {
        run.IdleCompletion.TrySetResult(true);
    }

    private static void ReleaseOperationGate(RunContext run)
    {
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

        internal FolderAutoRenameProgressSnapshot LastProgress { get; set; }

        internal bool ProgressStarted { get; set; }

        internal bool TerminalPending { get; set; }

        internal TaskCompletionSource<bool> IdleCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
