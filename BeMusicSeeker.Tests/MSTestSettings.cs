using BeMusicSeeker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MSTestSettings
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        RuntimeBootstrap.Initialize();
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        TestUiDispatcherHost.ShutdownApplication();
    }
}
