using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupMemoryPressureServiceTests
{
    [TestMethod]
    public void BuildCheckpointLog_IncludesMemoryCounters()
    {
        var snapshot = new StartupMemorySnapshot(10L, 20L, 30L, 1, 2, 3);

        string log = StartupMemoryPressureService.BuildCheckpointLog("file_diff", "after", snapshot);

        StringAssert.StartsWith(log, "startup_memory_checkpoint");
        StringAssert.Contains(log, "phase=file_diff");
        StringAssert.Contains(log, "point=after");
        StringAssert.Contains(log, "privateBytes=10");
        StringAssert.Contains(log, "workingSet=20");
        StringAssert.Contains(log, "managedBytes=30");
        StringAssert.Contains(log, "gen0=1");
        StringAssert.Contains(log, "gen1=2");
        StringAssert.Contains(log, "gen2=3");
    }

}
