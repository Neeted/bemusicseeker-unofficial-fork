using System;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

internal sealed class ShellShutdownWorkflowCompletionReceipt
{
    internal ShellShutdownWorkflowCompletionReceipt(
        string reason,
        bool preparationSucceeded,
        Exception exception = null)
    {
        Reason = reason ?? "window_close";
        PreparationSucceeded = preparationSucceeded;
        Exception = exception;
    }

    internal string Reason { get; }

    internal bool PreparationSucceeded { get; }

    internal Exception Exception { get; }

    internal bool CloseAllowed => true;
}

/// <summary>
/// Owns the shell close/update preparation arbitration while WPF remains the terminal shutdown host.
/// </summary>
internal sealed class ShellShutdownWorkflowOwner
{
    private readonly object syncRoot = new();

    private readonly StartupUpdateWorkflowOwner startupUpdateWorkflow;

    private readonly ElevatedProcessWarningWorkflowOwner elevatedProcessWarningWorkflow;

    private readonly Func<string, Task<ShutdownPreparationResult>> prepareShutdown;

    private readonly Action<string> markCoordinatedShutdownStarted;

    private readonly Func<Func<Task>, Task> dispatchToUi;

    private readonly Action<Exception, string> logWarning;

    private Task<ShutdownPreparationResult> preparationTask;

    private Task<ShellShutdownWorkflowCompletionReceipt> windowCloseTask;

    private bool closingOrClosed;

    private bool preparationStarted;

    private bool preparationRunning;

    private bool preparationCompleted;

    private bool preparationWasUpdate;

    private bool closeAllowed;

    private bool updatePreparationFailurePending;

    internal ShellShutdownWorkflowOwner(
        StartupUpdateWorkflowOwner startupUpdateWorkflow,
        ElevatedProcessWarningWorkflowOwner elevatedProcessWarningWorkflow,
        Func<string, Task<ShutdownPreparationResult>> prepareShutdown,
        Action<string> markCoordinatedShutdownStarted,
        Func<Func<Task>, Task> dispatchToUi,
        Action<Exception, string> logWarning = null)
    {
        this.startupUpdateWorkflow = startupUpdateWorkflow ?? throw new ArgumentNullException(nameof(startupUpdateWorkflow));
        this.elevatedProcessWarningWorkflow = elevatedProcessWarningWorkflow ?? throw new ArgumentNullException(nameof(elevatedProcessWarningWorkflow));
        this.prepareShutdown = prepareShutdown ?? throw new ArgumentNullException(nameof(prepareShutdown));
        this.markCoordinatedShutdownStarted = markCoordinatedShutdownStarted ?? throw new ArgumentNullException(nameof(markCoordinatedShutdownStarted));
        this.dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
        this.logWarning = logWarning;

        startupUpdateWorkflow.BindShutdownPreparation(PrepareForStartupUpdateAsync);
    }

    internal bool IsClosingOrClosed
    {
        get
        {
            lock (syncRoot)
            {
                return closingOrClosed;
            }
        }
    }

    internal bool IsShutdownPreparationStarted
    {
        get
        {
            lock (syncRoot)
            {
                return preparationStarted;
            }
        }
    }

    internal bool IsShutdownPreparationRunning
    {
        get
        {
            lock (syncRoot)
            {
                return preparationRunning;
            }
        }
    }

    internal bool IsShutdownPrepared
    {
        get
        {
            lock (syncRoot)
            {
                return preparationCompleted;
            }
        }
    }

    internal bool IsCloseAllowed
    {
        get
        {
            lock (syncRoot)
            {
                return closeAllowed;
            }
        }
    }

    internal Task<ShutdownPreparationResult> PrepareForStartupUpdateAsync(string reason)
    {
        Task<ShutdownPreparationResult> preparation = EnsurePreparationAsync(reason, updatePreparation: true);
        bool preparationBelongsToUpdate;
        lock (syncRoot)
        {
            preparationBelongsToUpdate = preparationWasUpdate;
        }
        if (preparationBelongsToUpdate)
        {
            _ = AllowCloseAfterStartupUpdateTerminalAsync();
        }
        return preparation;
    }

    internal bool ConsumeUpdatePreparationFailure()
    {
        lock (syncRoot)
        {
            if (!updatePreparationFailurePending)
            {
                return false;
            }
            updatePreparationFailurePending = false;
            return true;
        }
    }

    internal Task<ShellShutdownWorkflowCompletionReceipt> RequestWindowCloseAsync()
    {
        TryBeginWindowCloseRequest(out Task<ShellShutdownWorkflowCompletionReceipt> request);
        return request;
    }

    internal bool TryBeginWindowCloseRequest(out Task<ShellShutdownWorkflowCompletionReceipt> request)
    {
        lock (syncRoot)
        {
            if (windowCloseTask != null)
            {
                request = windowCloseTask;
                return false;
            }

            closingOrClosed = true;
            var completion = new TaskCompletionSource<ShellShutdownWorkflowCompletionReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            windowCloseTask = completion.Task;
            _ = CompleteWindowCloseAsync(completion);
            request = windowCloseTask;
            return true;
        }
    }

    private async Task CompleteWindowCloseAsync(TaskCompletionSource<ShellShutdownWorkflowCompletionReceipt> completion)
    {
        try
        {
            elevatedProcessWarningWorkflow.NotifyClosing();
            bool startupUpdateWasActive = startupUpdateWorkflow.NotifyClosing();
            Task startupUpdateTerminal = startupUpdateWorkflow.WaitForTerminalAsync();
            bool preparationAlreadyStarted = IsShutdownPreparationStarted;
            if (startupUpdateWasActive
                && startupUpdateWorkflow.IsShutdownPreparationStarted
                && !preparationAlreadyStarted)
            {
                await startupUpdateWorkflow.WaitForShutdownPreparationRequestAsync().ConfigureAwait(false);
                preparationAlreadyStarted = IsShutdownPreparationStarted;
            }
            if (startupUpdateWasActive && !preparationAlreadyStarted)
            {
                await startupUpdateWorkflow.WaitForIdleAsync().ConfigureAwait(false);
                await startupUpdateTerminal.ConfigureAwait(false);
            }

            Task<ShutdownPreparationResult> preparation = EnsurePreparationAsync("window_close", updatePreparation: false);
            try
            {
                await preparation.ConfigureAwait(false);
                if (startupUpdateWasActive && preparationAlreadyStarted)
                {
                    await startupUpdateWorkflow.WaitForIdleAsync().ConfigureAwait(false);
                }
                await startupUpdateTerminal.ConfigureAwait(false);
                MarkCloseAllowed();
                completion.TrySetResult(new ShellShutdownWorkflowCompletionReceipt("window_close", preparationSucceeded: true));
            }
            catch (Exception exception)
            {
                LogWarningSafely(exception, "shell_shutdown preparation failed");
                if (startupUpdateWasActive && preparationAlreadyStarted)
                {
                    try
                    {
                        await startupUpdateWorkflow.WaitForIdleAsync().ConfigureAwait(false);
                    }
                    catch (Exception drainException)
                    {
                        LogWarningSafely(drainException, "shell_shutdown startup update drain failed");
                    }
                    try
                    {
                        await startupUpdateTerminal.ConfigureAwait(false);
                    }
                    catch (Exception drainException)
                    {
                        LogWarningSafely(drainException, "shell_shutdown startup update terminal drain failed");
                    }
                }
                else
                {
                    try
                    {
                        await startupUpdateTerminal.ConfigureAwait(false);
                    }
                    catch (Exception drainException)
                    {
                        LogWarningSafely(drainException, "shell_shutdown startup update terminal drain failed");
                    }
                }
                MarkCloseAllowed();
                completion.TrySetResult(new ShellShutdownWorkflowCompletionReceipt("window_close", preparationSucceeded: false, exception));
            }
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "shell_shutdown close arbitration failed");
            MarkPreparationCompleted();
            MarkCloseAllowed();
            completion.TrySetResult(new ShellShutdownWorkflowCompletionReceipt("window_close", preparationSucceeded: false, exception));
        }
    }

    private Task<ShutdownPreparationResult> EnsurePreparationAsync(string reason, bool updatePreparation)
    {
        TaskCompletionSource<ShutdownPreparationResult> completion;
        string normalizedReason = reason ?? (updatePreparation ? "update" : "window_close");
        lock (syncRoot)
        {
            if (preparationTask != null)
            {
                return preparationTask;
            }

            preparationStarted = true;
            preparationRunning = true;
            closingOrClosed = true;
            preparationWasUpdate = updatePreparation;
            completion = new TaskCompletionSource<ShutdownPreparationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            preparationTask = completion.Task;
        }

        try
        {
            Task dispatchTask = dispatchToUi(() => RunPreparationAsync(completion, normalizedReason, updatePreparation));
            if (dispatchTask == null)
            {
                throw new InvalidOperationException("Shutdown preparation UI dispatch returned no task.");
            }
            _ = ObservePreparationDispatchAsync(dispatchTask, completion, updatePreparation);
        }
        catch (Exception exception)
        {
            CompletePreparationFailure(completion, exception, updatePreparation);
        }
        return completion.Task;
    }

    private async Task ObservePreparationDispatchAsync(
        Task dispatchTask,
        TaskCompletionSource<ShutdownPreparationResult> completion,
        bool updatePreparation)
    {
        try
        {
            await dispatchTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompletePreparationFailure(completion, exception, updatePreparation);
        }
    }

    private async Task RunPreparationAsync(
        TaskCompletionSource<ShutdownPreparationResult> completion,
        string reason,
        bool updatePreparation)
    {
        try
        {
            markCoordinatedShutdownStarted(reason);
            ShutdownPreparationResult result = await prepareShutdown(reason).ConfigureAwait(false);
            MarkPreparationCompleted();
            completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            CompletePreparationFailure(completion, exception, updatePreparation);
        }
    }

    private void CompletePreparationFailure(
        TaskCompletionSource<ShutdownPreparationResult> completion,
        Exception exception,
        bool updatePreparation)
    {
        lock (syncRoot)
        {
            preparationRunning = false;
            preparationCompleted = true;
            if (updatePreparation)
            {
                updatePreparationFailurePending = true;
            }
        }
        completion.TrySetException(exception);
    }

    private void MarkPreparationCompleted()
    {
        lock (syncRoot)
        {
            preparationRunning = false;
            preparationCompleted = true;
        }
    }

    private async Task AllowCloseAfterStartupUpdateTerminalAsync()
    {
        await startupUpdateWorkflow.WaitForTerminalAsync().ConfigureAwait(false);
        MarkCloseAllowed();
    }

    private void MarkCloseAllowed()
    {
        lock (syncRoot)
        {
            closeAllowed = true;
        }
    }

    private void LogWarningSafely(Exception exception, string context)
    {
        try
        {
            logWarning?.Invoke(exception, context);
        }
        catch
        {
        }
    }
}
