using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

internal static class TestStartupProgressOwnerFactory
{
    internal static StartupProgressWorkflowOwner Create()
    {
        return new StartupProgressWorkflowOwner(
            () => new StartupProgressVersionSnapshot(),
            (_, _) => { },
            () => { },
            action => action(),
            _ => { },
            _ => { },
            () => false,
            (_, _) => false,
            new object());
    }
}
