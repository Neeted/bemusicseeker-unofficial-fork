using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MaintenanceHydrationApplyCoordinatorTests
{
    [TestMethod]
    public void Apply_NullResultDoesNotTouchHost()
    {
        var host = new CapturingHost();
        var coordinator = new MaintenanceHydrationApplyCoordinator(host);

        coordinator.Apply(null);

        Assert.AreEqual(0, host.Events.Count);
    }

    [TestMethod]
    public void Apply_AttachesMaintenanceSnapshotsCleansStaleRowsAndDispatches()
    {
        TestableBmsFile file = CreateFile(@"C:\Library\hydrated.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var ownerView = new OwnedChartStorageOwnerView(
            [file],
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                OwnedChartCollectionState.CreateOwnedPathKey(file.path)
            });
        ResourceMaintenanceTargetSet targetSet = ResourceMaintenanceTargetSet.ForFullOwned(
            [ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false)],
            new StorageRowsVersionSnapshot(1, 1),
            ownedCollectionVersion: 2,
            resourceHealthInputVersion: 3);
        var host = new CapturingHost
        {
            OwnerView = ownerView,
            FullOwnedTargetSet = targetSet,
            DeletedStaleMaintenanceRows = 1
        };
        var result = new MaintenanceTableHydrationResult();
        result.MaintenanceMap[file.path] = new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            wav_files_defined = 2,
            wav_files_existing = 1
        };
        result.MaintenanceMap[@"C:\Library\stale.bms"] = new BMSFileMaintenanceInfo
        {
            path = @"C:\Library\stale.bms",
            hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var coordinator = new MaintenanceHydrationApplyCoordinator(host);

        coordinator.Apply(result);

        CollectionAssert.AreEqual(
            new[]
            {
                "enter_storage_lock",
                "create_owner_view",
                "begin_resource_health_input_mutation",
                "dispose_resource_health_input_mutation",
                "create_full_owned_target_set:maintenance_hydration",
                "delete_stale_rows:C:\\Library\\stale.bms",
                "dispose_storage_lock",
                "dispatch"
            },
            host.Events);
        Assert.IsTrue(result.ViewRefreshQueued);
        Assert.AreEqual(1, result.AppliedBmsCount);
        Assert.AreEqual(1, result.ValidSnapshotCount);
        Assert.AreEqual(1, result.OwnerPathCount);
        Assert.AreEqual(1, result.StalePathCount);
        Assert.AreEqual(1, result.CleanupDeletedCount);
        BMSFileMaintenanceInfo attachedInfo = file.TryGetMaintenanceInfoWithoutCreating();
        Assert.IsNotNull(attachedInfo);
        Assert.AreEqual(2, attachedInfo.wav_files_defined);
        Assert.AreSame(result, host.DispatchedResult);
        Assert.AreEqual(targetSet.Count, host.DispatchedTargets.Count);
    }

    [TestMethod]
    public void Apply_CleanupFailureInvalidatesAndRethrows()
    {
        TestableBmsFile file = CreateFile(@"C:\Library\hydrated.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var ownerView = new OwnedChartStorageOwnerView(
            [file],
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                OwnedChartCollectionState.CreateOwnedPathKey(file.path)
            });
        var host = new CapturingHost
        {
            OwnerView = ownerView,
            DeleteStaleMaintenanceRowsException = new InvalidOperationException("cleanup failed")
        };
        var result = new MaintenanceTableHydrationResult();
        result.MaintenanceMap[@"C:\Library\stale.bms"] = new BMSFileMaintenanceInfo
        {
            path = @"C:\Library\stale.bms",
            hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        };
        var coordinator = new MaintenanceHydrationApplyCoordinator(host);

        Assert.ThrowsException<InvalidOperationException>(() => coordinator.Apply(result));

        CollectionAssert.Contains(host.Events, "force_invalidate:maintenance_hydration_cleanup_failed");
        Assert.IsFalse(result.ViewRefreshQueued);
        Assert.IsNull(host.DispatchedResult);
    }

    private static TestableBmsFile CreateFile(string path, string hash)
    {
        var file = new TestableBmsFile();
        file.path = path;
        file.SetHash(hash);
        return file;
    }

    private sealed class CapturingHost : IMaintenanceHydrationApplyHost
    {
        public List<string> Events { get; } = [];

        public OwnedChartStorageOwnerView OwnerView { get; set; } = new([], [], new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public ResourceMaintenanceTargetSet FullOwnedTargetSet { get; set; } = ResourceMaintenanceTargetSet.ForFullOwned(
            [],
            new StorageRowsVersionSnapshot(1, 1),
            ownedCollectionVersion: 1,
            resourceHealthInputVersion: 1);

        public int DeletedStaleMaintenanceRows { get; set; }

        public Exception? DeleteStaleMaintenanceRowsException { get; set; }

        public MaintenanceTableHydrationResult? DispatchedResult { get; private set; }

        public ResourceMaintenanceTargetSet DispatchedTargets { get; private set; }

        public IDisposable EnterOwnedStorageWriteLock()
        {
            Events.Add("enter_storage_lock");
            return new CallbackDisposable(() => Events.Add("dispose_storage_lock"));
        }

        public OwnedChartStorageOwnerView CreateOwnedChartStorageOwnerView()
        {
            Events.Add("create_owner_view");
            return OwnerView;
        }

        public IDisposable BeginResourceHealthInputMutation()
        {
            Events.Add("begin_resource_health_input_mutation");
            return new CallbackDisposable(() => Events.Add("dispose_resource_health_input_mutation"));
        }

        public ResourceMaintenanceTargetSet CreateFullOwnedResourceMaintenanceTargetSet(string reason)
        {
            Events.Add("create_full_owned_target_set:" + reason);
            return FullOwnedTargetSet;
        }

        public int DeleteStaleMaintenanceRows(IEnumerable<string> staleMaintenancePaths)
        {
            Events.Add("delete_stale_rows:" + string.Join("|", staleMaintenancePaths ?? []));
            if (DeleteStaleMaintenanceRowsException != null)
            {
                throw DeleteStaleMaintenanceRowsException;
            }
            return DeletedStaleMaintenanceRows;
        }

        public void ForceInvalidateResourceHealthIndex(string reason)
        {
            Events.Add("force_invalidate:" + reason);
        }

        public void DispatchMaintenanceHydrationResult(
            MaintenanceTableHydrationResult result,
            ResourceMaintenanceTargetSet resourceHealthTargets)
        {
            Events.Add("dispatch");
            DispatchedResult = result;
            DispatchedTargets = resourceHealthTargets;
        }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }
    }
}
