using System;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class TestStartupProgressOwnerFactory
{
    /// <summary>
    /// Creates a startup-progress owner with synchronous test boundaries.
    /// </summary>
    /// <param name="backgroundTasksIdle">Reports whether the test scheduler has no required work.</param>
    /// <param name="requiredInitializationSchedulingComplete">Reports whether required task enrollment is closed.</param>
    /// <param name="completionHideDelay">Controls when a completed operation may be hidden.</param>
    /// <returns>A startup-progress owner configured for isolated tests.</returns>
    internal static StartupProgressWorkflowOwner Create(
        Func<Task>? completionHideDelay = null,
        Func<bool>? backgroundTasksIdle = null,
        Func<bool>? requiredInitializationSchedulingComplete = null)
    {
        backgroundTasksIdle ??= () => false;
        requiredInitializationSchedulingComplete ??= () => true;
        return new StartupProgressWorkflowOwner(
            () => new StartupProgressVersionSnapshot(),
            (_, _) => { },
            action => action(),
            _ => { },
            _ => { },
            backgroundTasksIdle,
            (generation, revision) => backgroundTasksIdle(),
            requiredInitializationSchedulingComplete,
            new object(),
            completionHideDelay);
    }
}
