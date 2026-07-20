using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

internal enum ElevatedProcessWarningWorkflowOutcome
{
    Suppressed,
    Shown,
    ProbeFailed,
    PresentationFailed,
    Closing
}

internal sealed class ElevatedProcessWarningPresentationRequest
{
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<bool> Completion => completion.Task;

    internal void Complete(bool shown)
    {
        completion.TrySetResult(shown);
    }

    internal void Fail(Exception exception)
    {
        completion.TrySetException(exception ?? new InvalidOperationException("Elevated-process warning presentation failed."));
    }
}

/// <summary>
/// Owns the one-shot elevated-process warning decision while the window remains a presentation adapter.
/// </summary>
internal sealed class ElevatedProcessWarningWorkflowOwner
{
    private readonly object syncRoot = new();

    private readonly Func<bool> processElevationProbe;

    private readonly Action<Exception, string> warningLog;

    private readonly Action<string> infoLog;

    private long generation;

    private bool started;

    private bool active;

    private bool closingRequested;

    private RunContext activeRun;

    private TaskCompletionSource<ElevatedProcessWarningWorkflowOutcome> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal ElevatedProcessWarningWorkflowOwner(
        Func<bool> processElevationProbe,
        Action<Exception, string> warningLog = null,
        Action<string> infoLog = null)
    {
        this.processElevationProbe = processElevationProbe ?? throw new ArgumentNullException(nameof(processElevationProbe));
        this.warningLog = warningLog;
        this.infoLog = infoLog;
    }

    internal event Action<ElevatedProcessWarningPresentationRequest> PresentationRequested;

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

    internal Task<ElevatedProcessWarningWorkflowOutcome> Completion => completion.Task;

    internal Task WaitForIdleAsync()
    {
        lock (syncRoot)
        {
            return active ? completion.Task : Task.CompletedTask;
        }
    }

    internal bool Start(Func<bool> canPresent)
    {
        if (canPresent == null)
        {
            throw new ArgumentNullException(nameof(canPresent));
        }

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
            run = new RunContext(generation, canPresent);
            activeRun = run;
            completion = new TaskCompletionSource<ElevatedProcessWarningWorkflowOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = ExecuteAsync(run);
        return true;
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
            if (!CanPresent(run))
            {
                CompleteAfterPresentationGuard(run);
                return;
            }

            bool isElevated;
            try
            {
                isElevated = processElevationProbe();
            }
            catch (Exception exception)
            {
                LogWarningSafely(exception, "process_elevation_check_failed");
                Complete(run, ElevatedProcessWarningWorkflowOutcome.ProbeFailed);
                return;
            }

            if (!CanPresent(run))
            {
                CompleteAfterPresentationGuard(run);
                return;
            }
            if (!isElevated)
            {
                Complete(run, ElevatedProcessWarningWorkflowOutcome.Suppressed);
                return;
            }

            LogInfoSafely("process_elevated drag_drop_limited_warning_detected=true");
            var request = new ElevatedProcessWarningPresentationRequest();
            if (PresentationRequested == null)
            {
                LogWarningSafely(
                    new InvalidOperationException("Elevated-process warning presentation is not connected."),
                    "process_elevated_warning_presentation_unavailable");
                Complete(run, ElevatedProcessWarningWorkflowOutcome.PresentationFailed);
                return;
            }
            try
            {
                PresentationRequested.Invoke(request);
            }
            catch (Exception exception)
            {
                request.Fail(exception);
            }

            bool shown;
            try
            {
                shown = await request.Completion.ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                LogWarningSafely(exception, "process_elevated drag_drop_limited_warning_failed");
                Complete(run, ElevatedProcessWarningWorkflowOutcome.PresentationFailed);
                return;
            }

            if (!CanPresent(run))
            {
                CompleteAfterPresentationGuard(run);
                return;
            }
            if (shown)
            {
                LogInfoSafely("process_elevated drag_drop_limited_warning_shown=true");
                Complete(run, ElevatedProcessWarningWorkflowOutcome.Shown);
            }
            else
            {
                Complete(run, ElevatedProcessWarningWorkflowOutcome.Suppressed);
            }
        }
        catch (Exception exception)
        {
            LogWarningSafely(exception, "process_elevated_warning_workflow_failed");
            Complete(run, ElevatedProcessWarningWorkflowOutcome.PresentationFailed);
        }
    }

    private bool CanPresent(RunContext run)
    {
        lock (syncRoot)
        {
            if (!active
                || closingRequested
                || generation != run.Generation
                || !ReferenceEquals(activeRun, run))
            {
                return false;
            }
        }

        return run.CanPresent();
    }

    private void CompleteAfterPresentationGuard(RunContext run)
    {
        Complete(
            run,
            IsClosing(run)
                ? ElevatedProcessWarningWorkflowOutcome.Closing
                : ElevatedProcessWarningWorkflowOutcome.Suppressed);
    }

    private bool IsClosing(RunContext run)
    {
        lock (syncRoot)
        {
            return closingRequested
                || generation != run.Generation;
        }
    }

    private void Complete(RunContext run, ElevatedProcessWarningWorkflowOutcome outcome)
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
        completion.TrySetResult(outcome);
    }

    private void LogInfoSafely(string message)
    {
        try
        {
            infoLog?.Invoke(message);
        }
        catch
        {
        }
    }

    private void LogWarningSafely(Exception exception, string context)
    {
        try
        {
            warningLog?.Invoke(exception, context);
        }
        catch
        {
        }
    }

    private sealed class RunContext
    {
        internal RunContext(long generation, Func<bool> canPresent)
        {
            Generation = generation;
            CanPresent = canPresent;
        }

        internal long Generation { get; }

        internal Func<bool> CanPresent { get; }
    }
}
