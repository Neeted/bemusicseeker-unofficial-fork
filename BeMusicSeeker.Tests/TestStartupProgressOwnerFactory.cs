using System;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class TestStartupProgressOwnerFactory
{
    internal static StartupProgressWorkflowOwner Create(Func<bool> backgroundTaskEnrollmentReady = null)
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
            backgroundTaskEnrollmentReady);
    }
}
