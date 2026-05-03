using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupMemoryPressureServiceTests
{
    [TestMethod]
    public void BuildCheckpointLog_IncludesMemoryCounters()
    {
        StartupMemorySnapshot snapshot = new StartupMemorySnapshot(10L, 20L, 30L, 1, 2, 3);

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

    [TestMethod]
    public void BuildCleanupLog_IncludesBeforeAfterAndCompactionFlag()
    {
        StartupMemoryCleanupResult result = new StartupMemoryCleanupResult(
            new StartupMemorySnapshot(100L, 200L, 300L, 1, 2, 3),
            new StartupMemorySnapshot(10L, 20L, 30L, 4, 5, 6),
            collected: true,
            elapsedMs: 7L);

        string log = StartupMemoryPressureService.BuildCleanupLog("installable_maintenance_deferred", force: true, collected: true, result);

        StringAssert.StartsWith(log, "startup_memory_cleanup");
        StringAssert.Contains(log, "reason=installable_maintenance_deferred");
        StringAssert.Contains(log, "forced=true");
        StringAssert.Contains(log, "collected=true");
        StringAssert.Contains(log, "compactLoh=true");
        StringAssert.Contains(log, "elapsedMs=7");
        StringAssert.Contains(log, "privateBefore=100");
        StringAssert.Contains(log, "privateAfter=10");
        StringAssert.Contains(log, "managedBefore=300");
        StringAssert.Contains(log, "managedAfter=30");
    }

    [TestMethod]
    public void ShouldCleanup_UsesForcePrivateAndManagedThresholds()
    {
        StartupMemorySnapshot low = new StartupMemorySnapshot(10L, 0L, 20L, 0, 0, 0);
        StartupMemorySnapshot highPrivate = new StartupMemorySnapshot(101L, 0L, 20L, 0, 0, 0);
        StartupMemorySnapshot highManaged = new StartupMemorySnapshot(10L, 0L, 51L, 0, 0, 0);

        Assert.IsTrue(StartupMemoryPressureService.ShouldCleanup(force: true, low, privateBytesThreshold: 100L, managedBytesThreshold: 50L));
        Assert.IsFalse(StartupMemoryPressureService.ShouldCleanup(force: false, low, privateBytesThreshold: 100L, managedBytesThreshold: 50L));
        Assert.IsTrue(StartupMemoryPressureService.ShouldCleanup(force: false, highPrivate, privateBytesThreshold: 100L, managedBytesThreshold: 50L));
        Assert.IsTrue(StartupMemoryPressureService.ShouldCleanup(force: false, highManaged, privateBytesThreshold: 100L, managedBytesThreshold: 50L));
    }

    [TestMethod]
    public void CleanupIfNeeded_CanBeTestedWithoutRealGc()
    {
        List<string> logs = new List<string>();
        int collectCount = 0;
        StartupMemorySnapshot before = new StartupMemorySnapshot(200L, 0L, 10L, 0, 0, 0);
        StartupMemorySnapshot after = new StartupMemorySnapshot(50L, 0L, 5L, 1, 1, 1);
        int captureCount = 0;

        StartupMemoryCleanupResult result = StartupMemoryPressureService.CleanupIfNeeded(
            logs.Add,
            "test",
            force: false,
            captureSnapshot: delegate
            {
                captureCount++;
                return captureCount == 1 ? before : after;
            },
            collect: delegate { collectCount++; },
            privateBytesThreshold: 100L,
            managedBytesThreshold: 100L);

        Assert.IsTrue(result.Collected);
        Assert.AreEqual(1, collectCount);
        Assert.AreEqual(1, logs.Count);
        StringAssert.Contains(logs[0], "startup_memory_cleanup");
        StringAssert.Contains(logs[0], "collected=true");
        StringAssert.Contains(logs[0], "privateBefore=200");
        StringAssert.Contains(logs[0], "privateAfter=50");
    }
}
