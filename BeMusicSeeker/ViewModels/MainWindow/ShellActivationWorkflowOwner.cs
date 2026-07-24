using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the one-shot activation sequence for the composed main shell.
/// </summary>
internal sealed class ShellActivationWorkflowOwner
{
    private readonly StartupUpdateWorkflowOwner startupUpdateWorkflow;

    private readonly ElevatedProcessWarningWorkflowOwner elevatedProcessWarningWorkflow;

    private readonly Func<Task<bool>> initialize;

    private int constructedShellActivated;

    private int renderedShellActivated;

    internal ShellActivationWorkflowOwner(
        StartupUpdateWorkflowOwner startupUpdateWorkflow,
        ElevatedProcessWarningWorkflowOwner elevatedProcessWarningWorkflow,
        Func<Task<bool>> initialize)
    {
        this.startupUpdateWorkflow = startupUpdateWorkflow
            ?? throw new ArgumentNullException(nameof(startupUpdateWorkflow));
        this.elevatedProcessWarningWorkflow = elevatedProcessWarningWorkflow
            ?? throw new ArgumentNullException(nameof(elevatedProcessWarningWorkflow));
        this.initialize = initialize ?? throw new ArgumentNullException(nameof(initialize));
    }

    /// <summary>
    /// Starts constructor-safe shell work exactly once.
    /// </summary>
    internal bool ActivateConstructedShell()
    {
        if (Interlocked.Exchange(ref constructedShellActivated, 1) != 0)
        {
            return false;
        }

        return startupUpdateWorkflow.Start();
    }

    /// <summary>
    /// Starts content-rendered shell work exactly once and preserves the existing
    /// initialization, initial-selection, and context-idle warning ordering.
    /// </summary>
    internal Task ActivateRenderedShell(
        Action applyInitialSelection,
        Action<Action> scheduleContextIdle,
        Func<bool> canPresentElevatedProcessWarning)
    {
        if (applyInitialSelection == null)
        {
            throw new ArgumentNullException(nameof(applyInitialSelection));
        }
        if (scheduleContextIdle == null)
        {
            throw new ArgumentNullException(nameof(scheduleContextIdle));
        }
        if (canPresentElevatedProcessWarning == null)
        {
            throw new ArgumentNullException(nameof(canPresentElevatedProcessWarning));
        }
        if (Interlocked.Exchange(ref renderedShellActivated, 1) != 0)
        {
            return Task.CompletedTask;
        }

        Task<bool> initializationTask = initialize();
        applyInitialSelection();
        scheduleContextIdle(() => elevatedProcessWarningWorkflow.Start(canPresentElevatedProcessWarning));
        return initializationTask;
    }
}
