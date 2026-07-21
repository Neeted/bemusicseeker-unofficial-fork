using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PackageRecordWorkflowOwnerTests
{
    [TestMethod]
    public async Task RemoveAllAsync_WithoutAttachedLibraryIsNoOp()
    {
        var events = new List<string>();
        var store = new RecordingStore(events);
        var owner = CreateOwner(() => null!, events, store);

        await owner.RemoveAllAsync(DeleteInstallPackageRecordsKind.Pending);

        Assert.AreEqual(0, store.RemoveAllCount);
        CollectionAssert.AreEqual(Array.Empty<string>(), events);
    }

    [DataTestMethod]
    [DataRow((int)DeleteInstallPackageRecordsKind.Pending)]
    [DataRow((int)DeleteInstallPackageRecordsKind.Installed)]
    public async Task RemoveAllAsync_PreservesMutationBoundaryOrder(int kindValue)
    {
        var kind = (DeleteInstallPackageRecordsKind)kindValue;
        var library = CreateLibrary();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var owner = CreateOwner(() => library, events, store);

        await owner.RemoveAllAsync(kind);

        Assert.AreEqual(kind, store.LastKind);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "suppression-start", "store-remove-all", "suppression-end", "activity-end" },
            events);
    }

    [TestMethod]
    public async Task RemovePackagesAsync_MaterializesNonNullPackagesBeforeBackgroundMutation()
    {
        var library = CreateLibrary();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var owner = CreateOwner(() => library, events, store);
        var first = new ChartPackage();
        var second = new ChartPackage();

        await owner.RemovePackagesAsync(
            DeleteInstallPackageRecordsKind.Installed,
            new[] { first, null, second });

        Assert.AreEqual(DeleteInstallPackageRecordsKind.Installed, store.LastKind);
        CollectionAssert.AreEqual(new[] { first, second }, (System.Collections.ICollection)store.LastPackages);
    }

    [TestMethod]
    public async Task RemoveSelectionAsync_ResolvesAndRemovesPackagesInsideOneMutation()
    {
        var library = CreateLibrary();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var expectedPackage = new ChartPackage();
        store.ResolvedPackages = [expectedPackage];
        var owner = CreateOwner(() => library, events, store);
        ChartOperationTarget target = CreateTarget();

        await owner.RemoveSelectionAsync(
            DeleteInstallPackageRecordsRequest.CreatePending([target]));

        Assert.AreEqual(DeleteInstallPackageRecordsKind.Pending, store.LastKind);
        CollectionAssert.AreEqual(new[] { target }, (System.Collections.ICollection)store.LastTargets);
        CollectionAssert.AreEqual(new[] { expectedPackage }, (System.Collections.ICollection)store.LastPackages);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "suppression-start", "store-resolve", "store-remove-packages", "suppression-end", "activity-end" },
            events);
    }

    [TestMethod]
    public async Task RemovePackagesAsync_PropagatesFailureAndClosesPresentationBoundary()
    {
        var events = new List<string>();
        var store = new RecordingStore(events) { Failure = new InvalidOperationException("remove failed") };
        var owner = CreateOwner(CreateLibrary, events, store);

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => owner.RemovePackagesAsync(DeleteInstallPackageRecordsKind.Pending, [new ChartPackage()]));

        Assert.AreEqual("remove failed", exception.Message);
        CollectionAssert.AreEqual(
            new[] { "activity-start", "suppression-start", "store-remove-packages", "suppression-end", "activity-end" },
            events);
    }

    [TestMethod]
    public async Task RemoveAllAsync_WaitsForSharedChartFileGate()
    {
        var synchronizer = new ChartFileOperationSynchronizer();
        var events = new List<string>();
        var store = new RecordingStore(events);
        var owner = new PackageRecordWorkflowOwner(
            CreateLibrary,
            synchronizer,
            new RecordingPresentation(events),
            store);
        using var gateHeld = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        Task gateHolder = Task.Run(() =>
        {
            using (synchronizer.Enter())
            {
                gateHeld.Set();
                Assert.IsTrue(releaseGate.Wait(TimeSpan.FromSeconds(5)));
            }
        });
        Assert.IsTrue(gateHeld.Wait(TimeSpan.FromSeconds(5)));

        Task removal = owner.RemoveAllAsync(DeleteInstallPackageRecordsKind.Installed);
        Assert.IsTrue(SpinWait.SpinUntil(
            () => removal.Status == TaskStatus.Running || removal.IsCompleted,
            TimeSpan.FromSeconds(5)));
        Assert.IsFalse(removal.IsCompleted);
        Assert.AreEqual(0, store.RemoveAllCount);

        releaseGate.Set();
        await removal;
        await gateHolder;
        Assert.AreEqual(1, store.RemoveAllCount);
    }

    private static PackageRecordWorkflowOwner CreateOwner(
        Func<BMSLibrary> libraryProvider,
        List<string> events,
        IPackageRecordStore store)
    {
        return new PackageRecordWorkflowOwner(
            libraryProvider,
            new ChartFileOperationSynchronizer(),
            new RecordingPresentation(events),
            store);
    }

    private static BMSLibrary CreateLibrary()
    {
        return (BMSLibrary)FormatterServices.GetUninitializedObject(typeof(BMSLibrary));
    }

    private static ChartOperationTarget CreateTarget()
    {
        var chart = (ChartFile)FormatterServices.GetUninitializedObject(typeof(ChartFile));
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.PendingPackage,
            isOwned: false,
            isPending: true,
            isPlaylistMissing: false,
            ChartOperationCapabilities.UpdateInstallDestination);
    }

    private sealed class RecordingPresentation : IPackageRecordMutationPresentation
    {
        private readonly List<string> events;

        internal RecordingPresentation(List<string> events)
        {
            this.events = events;
        }

        public void BeginActivity() => events.Add("activity-start");

        public void BeginRefreshSuppression() => events.Add("suppression-start");

        public void EndRefreshSuppression() => events.Add("suppression-end");

        public void EndActivity() => events.Add("activity-end");
    }

    private sealed class RecordingStore : IPackageRecordStore
    {
        private readonly List<string> events;

        internal RecordingStore(List<string> events)
        {
            this.events = events;
        }

        internal int RemoveAllCount { get; private set; }

        internal DeleteInstallPackageRecordsKind LastKind { get; private set; }

        internal IReadOnlyList<ChartPackage> LastPackages { get; private set; } = [];

        internal IReadOnlyList<ChartOperationTarget> LastTargets { get; private set; } = [];

        internal IReadOnlyList<ChartPackage> ResolvedPackages { get; set; } = [];

        internal Exception? Failure { get; set; }

        public void RemoveAll(BMSLibrary library, DeleteInstallPackageRecordsKind kind)
        {
            events.Add("store-remove-all");
            RemoveAllCount++;
            LastKind = kind;
            ThrowIfConfigured();
        }

        public void RemovePackages(
            BMSLibrary library,
            DeleteInstallPackageRecordsKind kind,
            IReadOnlyList<ChartPackage> packages)
        {
            events.Add("store-remove-packages");
            LastKind = kind;
            LastPackages = packages;
            ThrowIfConfigured();
        }

        public IReadOnlyList<ChartPackage> ResolvePackages(
            BMSLibrary library,
            DeleteInstallPackageRecordsKind kind,
            IReadOnlyList<ChartOperationTarget> targets)
        {
            events.Add("store-resolve");
            LastKind = kind;
            LastTargets = targets;
            return ResolvedPackages;
        }

        private void ThrowIfConfigured()
        {
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }
}
