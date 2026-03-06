using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryInitializationServiceTests
{
    [TestMethod]
    public void RunInitialize_InvokesAllPhasesAndWaitsForContinuations()
    {
        BmsLibraryInitializationService service = new BmsLibraryInitializationService();
        int phase1Count = 0;
        int phase2Count = 0;
        int phase3Count = 0;
        int continuationCount = 0;

        InitializationExecutionResult result = service.RunInitialize(
            new List<Action>
            {
                delegate
                {
                    Interlocked.Increment(ref continuationCount);
                }
            },
            new SemaphoreSlim(2, 2),
            delegate
            {
                Interlocked.Increment(ref phase1Count);
            },
            delegate
            {
                Interlocked.Increment(ref phase2Count);
            },
            delegate
            {
                Interlocked.Increment(ref phase3Count);
            });

        Assert.AreEqual(1, phase1Count);
        Assert.AreEqual(1, phase2Count);
        Assert.AreEqual(1, phase3Count);
        Assert.AreEqual(1, continuationCount);
        Assert.IsTrue(result.Phase1MinLoadMs >= 0);
        Assert.IsTrue(result.Phase2ScanMaintMs >= 0);
        Assert.IsTrue(result.Phase3InstallMaintenanceMs >= 0);
        Assert.IsTrue(result.WaitContinuationMs >= 0);
        Assert.IsTrue(result.TotalMs >= 0);
    }
}
