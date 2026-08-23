using System;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;
using System.Windows;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal sealed class MaintenanceRescanCompletionReceipt : EventArgs
{
    internal MaintenanceRescanCompletionReceipt(long generation, bool canceled, MaintenanceWorkflowResult result)
    {
        Generation = generation;
        Canceled = canceled;
        Result = MaintenanceWorkflowResultFacts.From(result);
    }

    internal long Generation { get; }

    internal bool Canceled { get; }

    internal MaintenanceWorkflowResultFacts Result { get; }
}

internal sealed class MaintenanceRescanFailure : EventArgs
{
    internal MaintenanceRescanFailure(long generation, Exception exception)
    {
        Generation = generation;
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    internal long Generation { get; }

    internal Exception Exception { get; }
}

internal enum MaintenanceRescanStartStatus
{
    Started,
    Rejected,
    NotStarted,
    Failed
}

internal sealed class MaintenanceRescanStartResult
{
    private MaintenanceRescanStartResult(MaintenanceRescanStartStatus status, Exception failure)
    {
        Status = status;
        Failure = failure;
    }

    internal MaintenanceRescanStartStatus Status { get; }

    internal Exception Failure { get; }

    internal bool Started => Status == MaintenanceRescanStartStatus.Started;

    internal static MaintenanceRescanStartResult StartedResult { get; } =
        new(MaintenanceRescanStartStatus.Started, null);

    internal static MaintenanceRescanStartResult Rejected { get; } =
        new(MaintenanceRescanStartStatus.Rejected, null);

    internal static MaintenanceRescanStartResult NotStarted { get; } =
        new(MaintenanceRescanStartStatus.NotStarted, null);

    internal static MaintenanceRescanStartResult Failed(Exception failure)
    {
        return new(
            MaintenanceRescanStartStatus.Failed,
            failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

/// <summary>
/// Owns the shell lifecycle of the all-owned maintenance rescan while the library
/// remains responsible for the durable/live catalog mutation boundary.
/// </summary>
internal sealed class MaintenanceRescanWorkflowOwner
{
    private readonly object syncRoot = new();

    private readonly Func<BMSLibrary, Action<MaintenanceWorkflowProgress>, CancellationToken, MaintenanceWorkflowResult> execute;

    private readonly Func<Action, Task> schedule;

    private readonly Action<Action> dispatchToUi;

    private readonly Action<string> logInfo;

    private readonly Action<Exception> reportWorkflowFailure;

    private readonly Action<Exception> reportNotificationFailure;

    private readonly IUiDialogService dialogs;

    private BMSLibrary library;

    private long generation;

    private long statusVersion;

    private RunContext activeRun;

    private bool shutdownRequested;

    internal MaintenanceRescanWorkflowOwner(
        Func<BMSLibrary, Action<MaintenanceWorkflowProgress>, CancellationToken, MaintenanceWorkflowResult> execute,
        Func<Action, Task> schedule,
        Action<Action> dispatchToUi,
        Action<string> logInfo = null,
        Action<Exception> reportNotificationFailure = null,
        Action<Exception> reportWorkflowFailure = null,
        IUiDialogService dialogs = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.logInfo = logInfo;
        this.reportWorkflowFailure = reportWorkflowFailure;
        this.reportNotificationFailure = reportNotificationFailure;
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    internal event Action<MaintenanceWorkflowProgress> ProgressChanged;

    internal event Action<MaintenanceRescanCompletionReceipt> CompletionPublished;

    internal event Action<MaintenanceRescanFailure> FailurePublished;

    /// <summary>
    /// Reports that the maintenance-rescan owner received a cancellation request.
    /// </summary>
    internal event Action CancellationRequested;

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
    /// Returns a receipt for the currently owned run, completing when the worker
    /// reaches success, failure, or stale-generation retirement.  The receipt is
    /// independent of the UI progress notification queue.
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
            if (run != null)
            {
                run.CancelRequested = true;
            }
        }
        CancelRun(run);
        DispatchNotification(() => ProgressChanged?.Invoke(CreateResetProgress()));
    }

    internal async Task<MaintenanceRescanStartResult> RequestStartAsync()
    {
        try
        {
            UiDialogResult result = await dialogs.ConfirmAsync(new UiConfirmationRequest(
                BeMusicSeeker.Properties.Resources.Msg_rescan_all_charts_confirm,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel));
            if (result == null)
            {
                return MaintenanceRescanStartResult.Failed(
                    new InvalidOperationException("Maintenance rescan confirmation returned no result."));
            }
            if (result.Status is UiDialogStatus.Rejected
                or UiDialogStatus.CancelledByUser
                or UiDialogStatus.ClosedByUser)
            {
                return MaintenanceRescanStartResult.Rejected;
            }
            if (result.Status != UiDialogStatus.Accepted)
            {
                return MaintenanceRescanStartResult.Failed(
                    result.Exception ?? new InvalidOperationException(
                        "Maintenance rescan confirmation could not be displayed (" + result.Status + ")."));
            }
            return StartCore()
                ? MaintenanceRescanStartResult.StartedResult
                : MaintenanceRescanStartResult.NotStarted;
        }
        catch (Exception exception)
        {
            return MaintenanceRescanStartResult.Failed(exception);
        }
    }

    private bool StartCore()
    {
        RunContext run;
        lock (syncRoot)
        {
            if (shutdownRequested || library == null || activeRun != null)
            {
                return false;
            }
            generation++;
            run = new RunContext(generation, library, new CancellationTokenSource());
            activeRun = run;
        }

        PublishProgress(run, new MaintenanceWorkflowProgress
        {
            TotalCount = 1,
            ProcessedCount = 0
        });
        try
        {
            Task scheduled = schedule(() => Execute(run));
            if (scheduled == null)
            {
                throw new InvalidOperationException("Maintenance rescan scheduler returned no task.");
            }
            scheduled.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        CompleteFailure(run, task.Exception?.GetBaseException() ?? new InvalidOperationException("Maintenance rescan scheduler failed."));
                    }
                    else if (task.IsCanceled)
                    {
                        CompleteFailure(run, new InvalidOperationException("Maintenance rescan scheduler canceled without a cancellation request."));
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

    internal void Cancel()
    {
        InvokeObserverSafely(() => CancellationRequested?.Invoke());
        RunContext run;
        MaintenanceWorkflowProgress progress;
        long progressStatusVersion;
        lock (syncRoot)
        {
            run = activeRun;
            if (run == null)
            {
                return;
            }
            run.CancelRequested = true;
            progress = CloneProgress(run.LastProgress ?? new MaintenanceWorkflowProgress { TotalCount = 1 });
            progress.IsCanceled = true;
            progress.IsCompleted = false;
            statusVersion++;
            progressStatusVersion = statusVersion;
        }
        // Cancellation callbacks may synchronously re-enter this owner.  Invoke
        // them outside syncRoot so a library progress callback cannot deadlock
        // the lifecycle lock while the UI requests cancellation.
        CancelRun(run);
        PublishProgressSnapshot(run, progress, progressStatusVersion);
    }

    internal void RequestShutdown()
    {
        RunContext run;
        lock (syncRoot)
        {
            shutdownRequested = true;
            generation++;
            statusVersion++;
            run = activeRun;
            if (run != null)
            {
                run.CancelRequested = true;
            }
        }
        CancelRun(run);
        DispatchNotification(() => ProgressChanged?.Invoke(CreateResetProgress()));
    }

    private void Execute(RunContext run)
    {
        MaintenanceWorkflowResult result;
        try
        {
            if (!IsCurrentGeneration(run))
            {
                CompleteStale(run);
                return;
            }
            LogInfoSafely("maintenance_rescan start scope=all_owned");
            result = execute(
                    run.Library,
                    progress =>
                    {
                        if (progress != null && !progress.IsCompleted)
                        {
                            LogInfoSafely(
                                "maintenance_rescan progress processed="
                                + progress.ProcessedCount
                                + "/"
                                + progress.TotalCount
                                + " path="
                                + (progress.CurrentPath ?? string.Empty));
                        }
                        PublishProgress(run, progress);
                    },
                    run.CancellationTokenSource.Token);
            if (result == null)
            {
                throw new InvalidOperationException("Maintenance rescan executor returned no result.");
            }
            bool canceled = result.Canceled || run.CancellationTokenSource.IsCancellationRequested;
            LogInfoSafely("maintenance_rescan " + (canceled ? "canceled" : "done") + " scope=all_owned");
            CompleteSuccess(run, result, canceled);
        }
        catch (OperationCanceledException) when (run.CancelRequested || run.CancellationTokenSource.IsCancellationRequested)
        {
            CompleteSuccess(run, new MaintenanceWorkflowResult { Canceled = true }, canceled: true);
        }
        catch (OperationCanceledException exception)
        {
            CompleteFailure(run, exception);
        }
        catch (Exception exception)
        {
            LogInfoSafely("maintenance_rescan failed scope=all_owned message=" + (exception.Message ?? string.Empty).Replace(Environment.NewLine, " "));
            CompleteFailure(run, exception);
        }
        finally
        {
            run.DisposeCancellationTokenSource();
        }
    }

    private void PublishProgress(RunContext run, MaintenanceWorkflowProgress progress)
    {
        if (progress == null)
        {
            return;
        }
        MaintenanceWorkflowProgress snapshot;
        long snapshotStatusVersion;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run) || !IsCurrentGenerationUnsafe(run))
            {
                return;
            }
            snapshot = CloneProgress(progress);
            if (run.CancelRequested)
            {
                snapshot.IsCanceled = true;
            }
            run.LastProgress = snapshot;
            statusVersion++;
            snapshotStatusVersion = statusVersion;
        }
        PublishProgressSnapshot(run, snapshot, snapshotStatusVersion);
    }

    private void PublishProgressSnapshot(RunContext run, MaintenanceWorkflowProgress snapshot, long expectedStatusVersion)
    {
        DispatchNotification(() =>
        {
            lock (syncRoot)
            {
                if (!IsCurrentGenerationUnsafe(run) || statusVersion != expectedStatusVersion)
                {
                    return;
                }
            }
            ProgressChanged?.Invoke(CloneProgress(snapshot));
        });
    }

    private void CompleteSuccess(RunContext run, MaintenanceWorkflowResult result, bool canceled)
    {
        long terminalStatusVersion;
        bool publish;
        bool finalCanceled;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run))
            {
                return;
            }
            finalCanceled = canceled
                || run.CancelRequested
                || run.CancellationTokenSource.IsCancellationRequested;
            activeRun = null;
            statusVersion++;
            terminalStatusVersion = statusVersion;
            publish = IsCurrentGenerationUnsafe(run);
        }
        run.DisposeCancellationTokenSource();
        run.IdleCompletion.TrySetResult(true);
        if (!publish)
        {
            return;
        }
        MaintenanceWorkflowProgress terminalProgress = CreateTerminalProgress(run, finalCanceled);
        var receipt = new MaintenanceRescanCompletionReceipt(run.Generation, finalCanceled, result);
        DispatchNotification(() =>
        {
            lock (syncRoot)
            {
                if (!IsCurrentGenerationUnsafe(run) || statusVersion != terminalStatusVersion)
                {
                    return;
                }
            }
            InvokeObserverSafely(() => ProgressChanged?.Invoke(CloneProgress(terminalProgress)));
            InvokeObserverSafely(() => CompletionPublished?.Invoke(receipt));
        });
    }

    private void CompleteFailure(RunContext run, Exception exception)
    {
        long terminalStatusVersion;
        bool publish;
        bool canceled;
        lock (syncRoot)
        {
            if (!ReferenceEquals(activeRun, run))
            {
                return;
            }
            canceled = run.CancelRequested || run.CancellationTokenSource.IsCancellationRequested;
            activeRun = null;
            statusVersion++;
            terminalStatusVersion = statusVersion;
            publish = IsCurrentGenerationUnsafe(run);
        }
        run.DisposeCancellationTokenSource();
        if (!canceled)
        {
            ReportWorkflowFailure(exception);
        }
        run.IdleCompletion.TrySetResult(true);
        if (!publish)
        {
            return;
        }
        if (canceled)
        {
            MaintenanceWorkflowProgress canceledProgress = CreateTerminalProgress(run, canceled: true);
            var canceledReceipt = new MaintenanceRescanCompletionReceipt(
                run.Generation,
                canceled: true,
                new MaintenanceWorkflowResult { Canceled = true });
            DispatchNotification(() =>
            {
                lock (syncRoot)
                {
                    if (!IsCurrentGenerationUnsafe(run) || statusVersion != terminalStatusVersion)
                    {
                        return;
                    }
                }
                InvokeObserverSafely(() => ProgressChanged?.Invoke(CloneProgress(canceledProgress)));
                InvokeObserverSafely(() => CompletionPublished?.Invoke(canceledReceipt));
            });
            return;
        }
        MaintenanceWorkflowProgress terminalProgress = CreateTerminalProgress(run, canceled: true);
        var failure = new MaintenanceRescanFailure(run.Generation, exception);
        DispatchNotification(() =>
        {
            lock (syncRoot)
            {
                if (!IsCurrentGenerationUnsafe(run) || statusVersion != terminalStatusVersion)
                {
                    return;
                }
            }
            InvokeObserverSafely(() => ProgressChanged?.Invoke(CloneProgress(terminalProgress)));
            InvokeObserverSafely(() => FailurePublished?.Invoke(failure));
        });
    }

    private void CompleteStale(RunContext run)
    {
        lock (syncRoot)
        {
            if (ReferenceEquals(activeRun, run))
            {
                activeRun = null;
                statusVersion++;
            }
        }
        run.IdleCompletion.TrySetResult(true);
    }

    private MaintenanceWorkflowProgress CreateTerminalProgress(RunContext run, bool canceled)
    {
        lock (syncRoot)
        {
            MaintenanceWorkflowProgress progress = CloneProgress(run.LastProgress ?? new MaintenanceWorkflowProgress { TotalCount = 1 });
            progress.IsCompleted = true;
            progress.IsCanceled = canceled;
            progress.CurrentPath = string.Empty;
            return progress;
        }
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

    private void DispatchNotification(Action notification)
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
        }
        catch (Exception exception)
        {
            ReportNotificationFailure(exception);
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

    private static void CancelRun(RunContext run)
    {
        if (run == null)
        {
            return;
        }
        try
        {
            run.CancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion won the race.  A disposed source is already unable
            // to deliver useful cancellation to the finished execution.
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

    private static MaintenanceWorkflowProgress CloneProgress(MaintenanceWorkflowProgress source)
    {
        return new MaintenanceWorkflowProgress
        {
            TotalCount = source?.TotalCount ?? 0,
            ProcessedCount = source?.ProcessedCount ?? 0,
            EvaluatedCount = source?.EvaluatedCount ?? 0,
            CompletedCount = source?.CompletedCount ?? 0,
            CurrentPath = source?.CurrentPath ?? string.Empty,
            IsCompleted = source?.IsCompleted == true,
            IsCanceled = source?.IsCanceled == true
        };
    }

    private static MaintenanceWorkflowProgress CreateResetProgress()
    {
        return new MaintenanceWorkflowProgress
        {
            TotalCount = 1,
            ProcessedCount = 0,
            IsCompleted = true,
            IsCanceled = true
        };
    }

    private sealed class RunContext
    {
        internal RunContext(long generation, BMSLibrary library, CancellationTokenSource cancellationTokenSource)
        {
            Generation = generation;
            Library = library;
            CancellationTokenSource = cancellationTokenSource;
        }

        internal long Generation { get; }

        internal BMSLibrary Library { get; }

        internal CancellationTokenSource CancellationTokenSource { get; }

        internal MaintenanceWorkflowProgress LastProgress { get; set; }

        internal bool CancelRequested { get; set; }

        internal TaskCompletionSource<bool> IdleCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int cancellationTokenSourceDisposed;

        internal void DisposeCancellationTokenSource()
        {
            if (Interlocked.Exchange(ref cancellationTokenSourceDisposed, 1) == 0)
            {
                CancellationTokenSource.Dispose();
            }
        }
    }
}
