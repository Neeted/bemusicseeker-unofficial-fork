using System;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class TestStartupProgressOwnerFactory
{
    /// <summary>
    /// Creates a startup-progress owner with synchronous test boundaries.
    /// </summary>
    /// <param name="backgroundTaskEnrollmentReady">Reports whether required background-task enrollment is complete.</param>
    /// <param name="completionHideDelay">Controls when a completed operation may be hidden.</param>
    /// <returns>A startup-progress owner configured for isolated tests.</returns>
    internal static StartupProgressWorkflowOwner Create(
        Func<bool> backgroundTaskEnrollmentReady = null,
        Func<Task> completionHideDelay = null)
    {
        return new StartupProgressWorkflowOwner(
            () => new StartupProgressVersionSnapshot(),
            (_, _) => { },
            action => action(),
            _ => { },
            _ => { },
            () => false,
            (_, _) => false,
            new object(),
            backgroundTaskEnrollmentReady,
            completionHideDelay);
    }
}
