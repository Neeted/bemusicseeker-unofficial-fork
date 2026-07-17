using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LibraryMutationDeltaApplyCoordinatorTests
{
    [TestMethod]
    public void Apply_OrdersCatalogReceiptResidualAndCompletionWithinResourceHealthWindow()
    {
        var host = new RecordingHost();

        new LibraryMutationDeltaApplyCoordinator(host).Apply(
            new LibraryMutationDelta(),
            performanceLogContext: "test_order");

        Assert.AreEqual(
            "guard,begin,build,suppression.begin,catalog,suppression.end,publish,residual,complete,sync,dispatch,performance",
            string.Join(",", host.Events));
        Assert.AreEqual(1, host.InputVersionAtCatalog % 2);
        Assert.AreEqual(1, host.InputVersionAtResidual % 2);
        Assert.AreEqual(2, host.InputVersionAtComplete);
        Assert.AreEqual(2, host.CompletedTargetInputVersion);
        Assert.AreEqual(17L, host.LastTimings.StatePackageApplyMs);
        Assert.AreEqual(11L, host.LastTimings.StateFolderDbMs);
        Assert.AreEqual(12L, host.LastTimings.StatePathMemoryApplyMs);
        Assert.AreEqual(13L, host.LastTimings.StateBmsPathDbMs);
        Assert.AreEqual(14L, host.LastTimings.StateBmsonPathDbMs);
        Assert.AreEqual(15L, host.LastTimings.StateBmsRemovalDbMs);
        Assert.AreEqual(16L, host.LastTimings.StateBmsonRemovalDbMs);
        Assert.IsTrue(host.LastTimings.StateApplyMs >= 0);
        Assert.IsTrue(host.LastTimings.ElapsedMs >= 0);
        Assert.AreEqual(1, host.ResidualReceiptIds.Count);
        Assert.AreEqual(1, host.ResidualReceiptIds[0]);
    }

    [TestMethod]
    public void Apply_ResetsReceiptStateBeforeEachMutation()
    {
        var host = new RecordingHost
        {
            LeaveCatalogReceiptUnchangedOnSecondApply = true
        };
        var coordinator = new LibraryMutationDeltaApplyCoordinator(host);

        coordinator.Apply(new LibraryMutationDelta());
        coordinator.Apply(new LibraryMutationDelta());

        Assert.AreEqual(2, host.BuildCount);
        Assert.AreEqual(2, host.CatalogApplyCount);
        Assert.AreEqual(2, host.ResidualReceiptIds.Count);
        Assert.AreEqual(1, host.ResidualReceiptIds[0]);
        Assert.IsNull(host.ResidualReceiptIds[1]);
    }

    [TestMethod]
    public void Apply_WhenCatalogFails_RebasesUncommittedInputAndUsesFailureFallback()
    {
        var host = new RecordingHost
        {
            ThrowInCatalog = true
        };

        Assert.ThrowsException<InvalidOperationException>(
            () => new LibraryMutationDeltaApplyCoordinator(host).Apply(new LibraryMutationDelta()));

        Assert.AreEqual(
            "guard,begin,build,suppression.begin,catalog,suppression.end,rebase,fallback",
            string.Join(",", host.Events));
        Assert.IsTrue(host.RebaseRequested);
        Assert.AreEqual(2, host.RebasedTargetInputVersion);
        Assert.IsFalse(host.ResidualWasCalled);
        Assert.IsFalse(host.Completed);
    }

    [TestMethod]
    public void Apply_WhenResidualFails_PreservesCommittedCatalogAndUsesFailureFallback()
    {
        var host = new RecordingHost
        {
            ThrowInResidual = true
        };

        Assert.ThrowsException<InvalidOperationException>(
            () => new LibraryMutationDeltaApplyCoordinator(host).Apply(new LibraryMutationDelta()));

        Assert.AreEqual(
            "guard,begin,build,suppression.begin,catalog,suppression.end,publish,residual,rebase,fallback",
            string.Join(",", host.Events));
        Assert.IsFalse(host.RebaseRequested);
        Assert.AreEqual(2, host.RebasedTargetInputVersion);
        Assert.IsTrue(host.CatalogCommitted);
        Assert.IsFalse(host.Completed);
        Assert.IsFalse(host.Synchronized);
        Assert.IsFalse(host.Dispatched);
    }

    private sealed class RecordingHost : ILibraryMutationDeltaApplyHost
    {
        private readonly ResourceHealthIndexOwner resourceHealthOwner;

        private ResourceHealthIndexOwner.ResourceHealthInputMutation activeResourceHealthMutation = null!;

        private object? catalogReceipt;

        public RecordingHost()
        {
            resourceHealthOwner = null!;
            resourceHealthOwner = new ResourceHealthIndexOwner(
                new BmsLibraryMaintenanceService(),
                _ => { },
                () => new ResourceHealthIndexCurrentVersion(
                    new StorageRowsVersionSnapshot(0, 0),
                    ownedCollectionVersion: 0,
                    resourceHealthOwner.CurrentInputVersion));
        }

        public List<string> Events { get; } = [];

        public List<object?> ResidualReceiptIds { get; } = [];

        public bool LeaveCatalogReceiptUnchangedOnSecondApply { get; set; }

        public bool ThrowInCatalog { get; set; }

        public bool ThrowInResidual { get; set; }

        public int BuildCount { get; private set; }

        public int CatalogApplyCount { get; private set; }

        public bool CatalogCommitted { get; private set; }

        public bool ResidualWasCalled { get; private set; }

        public bool Completed { get; private set; }

        public bool Synchronized { get; private set; }

        public bool Dispatched { get; private set; }

        public bool RebaseRequested { get; private set; }

        public int RebasedTargetInputVersion { get; private set; } = -1;

        public int InputVersionAtCatalog { get; private set; } = -1;

        public int InputVersionAtResidual { get; private set; } = -1;

        public int InputVersionAtComplete { get; private set; } = -1;

        public int CompletedTargetInputVersion { get; private set; } = -1;

        public LibraryMutationDeltaApplyTimings LastTimings { get; private set; } = null!;

        public void ThrowIfLr2SongDbSyncMutationBlocked(string operationName)
        {
            Events.Add("guard");
        }

        public ResourceHealthIndexOwner.ResourceHealthInputMutation BeginResourceHealthInputMutation()
        {
            Events.Add("begin");
            activeResourceHealthMutation = resourceHealthOwner.BeginInputMutation();
            return activeResourceHealthMutation;
        }

        public void BuildMutationResult(LibraryMutationDelta delta, int baseInputVersion, bool baseIndexCurrent)
        {
            Events.Add("build");
            BuildCount++;
            catalogReceipt = null;
        }

        public void PublishOwnedCollectionChangeNotification()
        {
            Events.Add("publish");
        }

        public IDisposable SuppressResourceHealthIndexInvalidationIfNeeded()
        {
            Events.Add("suppression.begin");
            return new RecordingScope(() => Events.Add("suppression.end"));
        }

        public BmsLibraryStateApplyResult ApplyCatalogMutationToState(LibraryMutationDelta delta)
        {
            Events.Add("catalog");
            InputVersionAtCatalog = resourceHealthOwner.CurrentInputVersion;
            CatalogApplyCount++;
            if (ThrowInCatalog)
            {
                throw new InvalidOperationException("catalog failure");
            }
            CatalogCommitted = true;
            if (!LeaveCatalogReceiptUnchangedOnSecondApply || CatalogApplyCount == 1)
            {
                catalogReceipt = CatalogApplyCount;
            }
            return new BmsLibraryStateApplyResult
            {
                FolderDbMs = 11,
                PathMemoryApplyMs = 12,
                BmsPathDbMs = 13,
                BmsonPathDbMs = 14,
                BmsRemovalDbMs = 15,
                BmsonRemovalDbMs = 16
            };
        }

        public BmsLibraryStateApplyResult ApplyConsumerResidualState(LibraryMutationDelta delta)
        {
            Events.Add("residual");
            InputVersionAtResidual = resourceHealthOwner.CurrentInputVersion;
            ResidualWasCalled = true;
            ResidualReceiptIds.Add(catalogReceipt);
            if (ThrowInResidual)
            {
                throw new InvalidOperationException("residual failure");
            }
            return new BmsLibraryStateApplyResult
            {
                PackageApplyMs = 17
            };
        }

        public void CompleteResourceHealthMutation(int targetInputVersion)
        {
            Events.Add("complete");
            Completed = true;
            InputVersionAtComplete = resourceHealthOwner.CurrentInputVersion;
            CompletedTargetInputVersion = targetInputVersion;
        }

        public void RebaseResourceHealthAfterFailure(
            ResourceHealthIndexOwner.ResourceHealthInputMutation resourceHealthMutation)
        {
            Events.Add("rebase");
            RebasedTargetInputVersion = resourceHealthMutation?.TargetInputVersion ?? -1;
            RebaseRequested = !CatalogCommitted;
        }

        public void SyncLr2NormalFoldersForOwnedMutation(string reason)
        {
            Events.Add("sync");
            Synchronized = true;
        }

        public void ApplyFailureFallback()
        {
            Events.Add("fallback");
        }

        public void DispatchOwnedChartCollectionMutation(string reason)
        {
            Events.Add("dispatch");
            Dispatched = true;
        }

        public void LogLibraryMutationDeltaPerformance(
            LibraryMutationDelta delta,
            string performanceLogContext,
            LibraryMutationDeltaApplyTimings timings)
        {
            Events.Add("performance");
            LastTimings = timings;
        }

        private sealed class RecordingScope(Action dispose) : IDisposable
        {
            public void Dispose()
            {
                dispose();
            }
        }
    }
}
