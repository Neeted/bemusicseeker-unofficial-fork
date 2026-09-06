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
internal sealed class StartupUpdateWorkflowOwner
{
    private readonly object syncRoot = new();

    private readonly Func<Task<UpdateCheckResult>> checkForUpdates;

    private readonly Func<UpdateAssetInfo, Task<string>> downloadAndVerify;

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

    private TaskCompletionSource<bool> shutdownPreparationPortCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal StartupUpdateWorkflowOwner(
        Func<Task<UpdateCheckResult>> checkForUpdates,
        Func<UpdateAssetInfo, Task<string>> downloadAndVerify,
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

    internal event Action<Exception> FailurePresentationRequested;

    internal event Action ApplicationShutdownRequested;

    internal event Action<StartupUpdateWorkflowCompletionReceipt> TerminalPublished;

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

    internal bool Start()
    {
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
            run = new RunContext(generation);
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

    internal bool NotifyClosing()
    {
        lock (syncRoot)
        {
            closingRequested = true;
            generation++;
            return active;
        }
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
            if (!IsCurrentForApply(run))
            {
                Complete(run, StartupUpdateWorkflowOutcome.Closing);
                return;
            }
            packagePath = await downloadAndVerify(selectedAsset).ConfigureAwait(false);
            if (!IsCurrentForApply(run))
            {
                Complete(run, StartupUpdateWorkflowOutcome.Closing);
                return;
            }
            IPreparedUpdaterLaunch preparedLaunch = prepareUpdaterLaunch(packagePath)
                ?? throw new InvalidOperationException("Updater launch preparation returned no launch.");
            launchReceipt = preparedLaunch.Start()
                ?? throw new UpdaterLaunchFailureException("Updater process did not start.");
            if (!await TryBeginShutdownPreparationAsync(run).ConfigureAwait(false))
            {
                launchReceipt.Abort();
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
            if (!IsCurrentForApply(run))
            {
                Complete(run, StartupUpdateWorkflowOutcome.Closing);
                return;
            }
            TryDeleteDownloadedPackage(packagePath);
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
            if (!IsCurrentForApply(run))
            {
                Complete(run, StartupUpdateWorkflowOutcome.Closing);
                return;
            }
            if (shutdownPreparationStarted)
            {
                TryDeleteDownloadedPackage(packagePath);
                RequestFailurePresentation(terminalFailure);
                Complete(run, StartupUpdateWorkflowOutcome.Failed, terminalFailure, shutdownPrepared: shutdownPreparationCompleted);
                RequestApplicationShutdown();
                return;
            }

            RequestFailurePresentation(terminalFailure);
            Complete(run, StartupUpdateWorkflowOutcome.Failed, terminalFailure);
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

    private void RequestFailurePresentation(Exception exception)
    {
        DispatchToUi(() =>
        {
            Action<Exception> presenter = FailurePresentationRequested;
            if (presenter == null)
            {
                return;
            }
            try
            {
                presenter(exception);
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
        });
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
        lock (syncRoot)
        {
            if (!active || !ReferenceEquals(activeRun, run))
            {
                return;
            }
            active = false;
            activeRun = null;
        }
        idleCompletion.TrySetResult(true);

        var receipt = new StartupUpdateWorkflowCompletionReceipt(run.Generation, outcome, exception, shutdownPrepared);
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
            throw new ArgumentNullException(nameof(action));

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

    private sealed class RunContext
    {
        internal RunContext(long generation)
        {
            Generation = generation;
        }

        internal long Generation { get; }

        internal bool ShutdownPreparationStarted { get; set; }
    }
}
