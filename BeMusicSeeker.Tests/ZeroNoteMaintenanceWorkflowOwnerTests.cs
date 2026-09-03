using System;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ZeroNoteMaintenanceWorkflowOwnerTests
{
    [TestMethod]
    public async Task RecheckAsync_WithoutAttachedLibraryIsNoOp()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        int recheckCount = 0;
        var owner = new ZeroNoteMaintenanceWorkflowOwner(
            () => null!,
            synchronizer,
            _ => recheckCount++);

        bool rechecked = await owner.RecheckAsync();

        Assert.IsFalse(rechecked);
        Assert.AreEqual(0, recheckCount);
    }

    [TestMethod]
    public async Task RecheckAsync_FailsFastWhileSharedChartFileGateIsBusyThenRunsAfterRelease()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        var library = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        int recheckCount = 0;
        var owner = new ZeroNoteMaintenanceWorkflowOwner(
            () => library,
            synchronizer,
            actualLibrary =>
            {
                Assert.AreSame(library, actualLibrary);
                Interlocked.Increment(ref recheckCount);
            });
        Assert.IsTrue(synchronizer.TryEnter(out IDisposable incumbent));
        try
        {
            Assert.IsFalse(owner.Recheck());
            Assert.AreEqual(0, Volatile.Read(ref recheckCount));
        }
        finally
        {
            incumbent.Dispose();
        }

        Assert.IsTrue(owner.Recheck());
        Assert.AreEqual(1, Volatile.Read(ref recheckCount));
    }

    [TestMethod]
    public async Task RecheckAsync_PropagatesLibraryFailure()
    {
        var library = (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
        var owner = new ZeroNoteMaintenanceWorkflowOwner(
            () => library,
            new ChartFileOperationSynchronizer(),
            _ => throw new InvalidOperationException("recheck failed"));

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.RecheckAsync());

        Assert.AreEqual("recheck failed", exception.Message);
    }
}
