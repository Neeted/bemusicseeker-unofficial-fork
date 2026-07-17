using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CatalogMutationOwnerTests
{
    [TestMethod]
    public void Apply_EmitsCanonicalReceiptForBmsAndBmsonReplacement()
    {
        var oldBms = CreateBms("old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var oldBmson = CreateBmson("old.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var nextBms = CreateBms("next.bms", "cccccccccccccccccccccccccccccccc");
        var nextBmson = CreateBmson("next.bmson", "dddddddddddddddddddddddddddddddd");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([oldBms], [oldBmson]);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([oldBms], [oldBmson]),
            initialRows.BmsRowsVersion,
            initialRows.BmsonRowsVersion);
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogFileScanStorageReplacementRequest request = owner.CreateFileScanStorageReplacementRequest(
            hasDbDiff: true,
            nextBmsRows: [nextBms],
            nextBmsonRows: [nextBmson],
            deletedBmsPaths: [oldBms.path],
            deletedBmsonPaths: [oldBmson.path],
            addedBmsFiles: [nextBms],
            addedBmsonSongs: [nextBmson]);

        CatalogFileScanStorageReplacementReceipt receipt = owner.ApplyFileScanStorageReplacement(request);

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.FileScanStorageReplacement, receipt.Kind);
        Assert.IsTrue(receipt.OwnedCollectionApplied);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.PreviousBmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.PreviousBmsonRowsVersion);
        Assert.AreEqual(initialRows.BmsRowsVersion + 1, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion + 1, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.AreEqual(2, receipt.AddedCharts.Count);
        Assert.AreEqual(2, receipt.RemovedCharts.Count);
        Assert.AreEqual(nextBms.path, receipt.AddedCharts[0].Path);
        Assert.AreEqual(nextBmson.path, receipt.AddedCharts[1].Path);
        Assert.AreEqual(oldBms.path, receipt.RemovedCharts[0].Path);
        Assert.AreEqual(oldBmson.path, receipt.RemovedCharts[1].Path);
        Assert.AreEqual(ownedCollectionOwner.CollectionVersion, receipt.OwnedCollectionVersion);
    }

    [TestMethod]
    public void Apply_WithoutDbDiffDoesNotChangeRowsOrEmitMutationFacts()
    {
        var bms = CreateBms("current.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([bms], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogFileScanStorageReplacementRequest request = owner.CreateFileScanStorageReplacementRequest(
            hasDbDiff: false,
            nextBmsRows: [bms],
            nextBmsonRows: [],
            deletedBmsPaths: [],
            deletedBmsonPaths: [],
            addedBmsFiles: [],
            addedBmsonSongs: []);

        CatalogFileScanStorageReplacementReceipt receipt = owner.ApplyFileScanStorageReplacement(request);

        Assert.IsFalse(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.NoOp, receipt.Kind);
        Assert.IsFalse(receipt.OwnedCollectionApplied);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.AreEqual(0, receipt.AddedCharts.Count);
        Assert.AreEqual(0, receipt.RemovedCharts.Count);
        Assert.AreSame(bms, storageRowsOwner.BmsRows[0]);
    }

    [TestMethod]
    public void ApplyStorageRowsReplacement_EmitsChangedKindsAndVersions()
    {
        var oldBms = CreateBms("old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var oldBmson = CreateBmson("old.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var newBms = CreateBms("new.bms", "cccccccccccccccccccccccccccccccc");
        var newBmson = CreateBmson("new.bmson", "dddddddddddddddddddddddddddddddd");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([oldBms], [oldBmson]);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([oldBms], [oldBmson]),
            initialRows.BmsRowsVersion,
            initialRows.BmsonRowsVersion));
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogStorageRowsReplacementRequest request = owner.CreateStorageRowsReplacementRequest(
            [newBms],
            [newBmson],
            replaceBmsRows: true,
            replaceBmsonRows: true);
        CatalogStorageRowsReplacementReceipt receipt = owner.ApplyStorageRowsReplacement(request);

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.StorageRowsReplacement, receipt.Kind);
        Assert.IsTrue(receipt.BmsRowsChanged);
        Assert.IsTrue(receipt.BmsonRowsChanged);
        Assert.IsTrue(receipt.OwnedCollectionInvalidated);
        Assert.IsFalse(ownedCollectionOwner.IsInitialized);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.PreviousBmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.PreviousBmsonRowsVersion);
        Assert.AreEqual(initialRows.BmsRowsVersion + 1, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion + 1, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.AreSame(newBms, storageRowsOwner.BmsRows.Single());
        Assert.AreSame(newBmson, storageRowsOwner.BmsonRows.Single());
    }

    [TestMethod]
    public void ApplyStorageRowsReplacement_LeavesUnselectedRowsUntouched()
    {
        var oldBms = CreateBms("old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var oldBmson = CreateBmson("old.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var newBms = CreateBms("new.bms", "cccccccccccccccccccccccccccccccc");
        var newBmson = CreateBmson("new.bmson", "dddddddddddddddddddddddddddddddd");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([oldBms], [oldBmson]);
        var owner = new CatalogMutationOwner(storageRowsOwner, new CatalogOwnedCollectionOwner());

        CatalogStorageRowsReplacementReceipt receipt = owner.ApplyStorageRowsReplacement(
            owner.CreateStorageRowsReplacementRequest(
                [newBms],
                [newBmson],
                replaceBmsRows: true,
                replaceBmsonRows: false));

        Assert.IsTrue(receipt.Applied);
        Assert.IsTrue(receipt.BmsRowsChanged);
        Assert.IsFalse(receipt.BmsonRowsChanged);
        Assert.IsTrue(receipt.OwnedCollectionInvalidated);
        Assert.AreEqual(initialRows.BmsRowsVersion + 1, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.AreSame(newBms, storageRowsOwner.BmsRows.Single());
        Assert.AreSame(oldBmson, storageRowsOwner.BmsonRows.Single());
    }

    [TestMethod]
    public void ApplyStorageRowsRemoval_RemovesOwnerAndPathRowsWithPerKindVersions()
    {
        var removedBms = CreateBms("removed.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var keptBms = CreateBms("kept.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var removedBmson = CreateBmson("removed.bmson", "cccccccccccccccccccccccccccccccc");
        var keptBmson = CreateBmson("kept.bmson", "dddddddddddddddddddddddddddddddd");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot(
            [removedBms, keptBms],
            [removedBmson, keptBmson]);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([removedBms, keptBms], [removedBmson, keptBmson]),
            initialRows.BmsRowsVersion,
            initialRows.BmsonRowsVersion));
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogStorageRowsRemovalRequest request = owner.CreateStorageRowsRemovalRequest(
        [
            OwnedChartRemoveRequest.FromOwnerReference(removedBms),
            OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bmson, removedBmson.path)
        ]);
        CatalogStorageRowsRemovalReceipt receipt = owner.ApplyStorageRowsRemoval(request);

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.StorageRowsRemoval, receipt.Kind);
        Assert.IsTrue(receipt.BmsRowsChanged);
        Assert.IsTrue(receipt.BmsonRowsChanged);
        Assert.IsTrue(ownedCollectionOwner.IsInitialized);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.PreviousBmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.PreviousBmsonRowsVersion);
        Assert.AreEqual(initialRows.BmsRowsVersion + 1, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion + 1, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.AreSame(keptBms, storageRowsOwner.BmsRows.Single());
        Assert.AreSame(keptBmson, storageRowsOwner.BmsonRows.Single());
    }

    [TestMethod]
    public void ApplyStorageRowsRemoval_EmptyRequestIsNoOp()
    {
        var bms = CreateBms("kept.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var bmson = CreateBmson("kept.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([bms], [bmson]);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([bms], [bmson]),
            initialRows.BmsRowsVersion,
            initialRows.BmsonRowsVersion));
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogStorageRowsRemovalReceipt receipt = owner.ApplyStorageRowsRemoval(
            owner.CreateStorageRowsRemovalRequest([]));

        Assert.IsFalse(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.NoOp, receipt.Kind);
        Assert.IsFalse(receipt.BmsRowsChanged);
        Assert.IsFalse(receipt.BmsonRowsChanged);
        Assert.IsTrue(ownedCollectionOwner.IsInitialized);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.AreSame(bms, storageRowsOwner.BmsRows.Single());
        Assert.AreSame(bmson, storageRowsOwner.BmsonRows.Single());
    }

    [TestMethod]
    public void ApplyOwnedCollectionMutation_EmitsReceiptAndAppliesRemovePathAndAddFacts()
    {
        string oldPath = Path.Combine("C:\\Library", "old", "moved.bms");
        string newPath = Path.Combine("C:\\Library", "new", "moved.bms");
        var movedBms = CreateBms("moved.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        movedBms.path = oldPath;
        var removedBms = CreateBms("removed.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var keptBmson = CreateBmson("kept.bmson", "cccccccccccccccccccccccccccccccc");
        var addedBms = CreateBms("added.bms", "dddddddddddddddddddddddddddddddd");
        var addedBmson = CreateBmson("added.bmson", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot(
            [movedBms, removedBms],
            [keptBmson]);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([movedBms, removedBms], [keptBmson]),
            initialRows.BmsRowsVersion,
            initialRows.BmsonRowsVersion));
        int expectedCollectionVersion = ownedCollectionOwner.IncrementVersion();
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        movedBms.path = newPath;
        CatalogOwnedCollectionMutationRequest request = owner.CreateOwnedCollectionMutationRequest(
            [OwnedChartRemoveRequest.FromOwnerReference(removedBms)],
            [new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(movedBms),
                OldPath = oldPath,
                NewPath = newPath
            }],
            [addedBms],
            [addedBmson],
            new StorageRowsVersionSnapshot(
                initialRows.BmsRowsVersion,
                initialRows.BmsonRowsVersion,
                initialRows.BmsRowsVersion + 1,
                initialRows.BmsonRowsVersion + 1));
        CatalogStorageRowsSnapshot appliedRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot(
            [movedBms, addedBms],
            [keptBmson, addedBmson]);
        CatalogOwnedCollectionMutationReceipt receipt = owner.ApplyOwnedCollectionMutation(request);

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.OwnedCollectionMutation, receipt.Kind);
        Assert.IsTrue(receipt.OwnedCollectionApplied);
        Assert.AreEqual(expectedCollectionVersion, receipt.OwnedCollectionVersion);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.PreviousBmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.PreviousBmsonRowsVersion);
        Assert.AreEqual(appliedRows.BmsRowsVersion, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(appliedRows.BmsonRowsVersion, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.IsTrue(ownedCollectionOwner.IsCurrent(
            receipt.StorageRowsVersion.BmsRowsVersion,
            receipt.StorageRowsVersion.BmsonRowsVersion));
        OwnedChartStorageOwnerView view = ownedCollectionOwner.Collection.CreateStorageOwnerView();
        Assert.IsFalse(view.ContainsOwnerPath(removedBms.path));
        Assert.IsTrue(view.ContainsOwnerPath(newPath));
        Assert.IsTrue(view.ContainsOwnerPath(addedBms.path));
        Assert.IsTrue(view.ContainsOwnerPath(addedBmson.path));
    }

    [TestMethod]
    public void ApplyOwnedCollectionMutation_StaleRequestResetsCollectionWithoutThrowing()
    {
        var bms = CreateBms("current.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var addedBms = CreateBms("added.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([bms], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([bms], []),
            initialRows.BmsRowsVersion,
            initialRows.BmsonRowsVersion));
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogOwnedCollectionMutationReceipt receipt = owner.ApplyOwnedCollectionMutation(
            owner.CreateOwnedCollectionMutationRequest(
                [],
                [],
                [addedBms],
                [],
                new StorageRowsVersionSnapshot(
                    initialRows.BmsRowsVersion + 1,
                    initialRows.BmsonRowsVersion,
                    initialRows.BmsRowsVersion + 2,
                    initialRows.BmsonRowsVersion + 1)));

        Assert.IsFalse(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.NoOp, receipt.Kind);
        Assert.IsFalse(receipt.OwnedCollectionApplied);
        Assert.IsFalse(ownedCollectionOwner.IsInitialized);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.PreviousBmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.PreviousBmsonRowsVersion);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.BmsRowsVersion);
    }

    [TestMethod]
    public void ApplyOwnedCollectionMutation_EmptyRequestIsNoOp()
    {
        var bms = CreateBms("current.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([bms], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([bms], []),
            initialRows.BmsRowsVersion,
            initialRows.BmsonRowsVersion));
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogOwnedCollectionMutationReceipt receipt = owner.ApplyOwnedCollectionMutation(
            owner.CreateOwnedCollectionMutationRequest(
                [],
                [],
                [],
                [],
                new StorageRowsVersionSnapshot(initialRows.BmsRowsVersion, initialRows.BmsonRowsVersion)));

        Assert.IsFalse(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.NoOp, receipt.Kind);
        Assert.IsFalse(receipt.OwnedCollectionApplied);
        Assert.IsTrue(ownedCollectionOwner.IsInitialized);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.BmsonRowsVersion);
    }

    [TestMethod]
    public void ApplyInstalledTargetUpsert_EmitsReceiptAndReplacesSamePathRows()
    {
        var oldBms = CreateBms("same.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var oldBmson = CreateBmson("same.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var newBms = CreateBms("same.bms", "cccccccccccccccccccccccccccccccc");
        var newBmson = CreateBmson("same.bmson", "dddddddddddddddddddddddddddddddd");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([oldBms], [oldBmson]);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([oldBms], [oldBmson]),
            initialRows.BmsRowsVersion,
            initialRows.BmsonRowsVersion));
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogInstalledTargetUpsertRequest request = owner.CreateInstalledTargetUpsertRequest([newBms], [newBmson]);
        CatalogInstalledTargetUpsertReceipt receipt = owner.ApplyInstalledTargetUpsert(request);

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.InstalledTargetUpsert, receipt.Kind);
        Assert.IsTrue(receipt.OwnedCollectionApplied);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.PreviousBmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.PreviousBmsonRowsVersion);
        Assert.AreEqual(initialRows.BmsRowsVersion + 1, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion + 1, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.AreEqual(2, receipt.AddedCharts.Count);
        CollectionAssert.AreEquivalent(
            new[] { newBms.path, newBmson.path },
            receipt.AddedCharts.Select(fact => fact.Path).ToArray());
        Assert.AreEqual(ownedCollectionOwner.CollectionVersion, receipt.OwnedCollectionVersion);
        Assert.AreSame(newBms, storageRowsOwner.BmsRows.Single());
        Assert.AreSame(newBmson, storageRowsOwner.BmsonRows.Single());
        OwnedChartStorageOwnerView view = ownedCollectionOwner.Collection.CreateStorageOwnerView();
        Assert.IsTrue(view.ContainsOwnerPath(newBms.path));
        Assert.IsTrue(view.ContainsOwnerPath(newBmson.path));
    }

    [TestMethod]
    public void ApplyInstalledTargetUpsert_RejectsStaleRequestBeforeChangingRows()
    {
        var original = CreateBms("original.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var replacement = CreateBms("replacement.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        storageRowsOwner.ReplaceRowsAndCaptureSnapshot([original], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogInstalledTargetUpsertRequest request = owner.CreateInstalledTargetUpsertRequest([replacement], []);
        var concurrent = CreateBms("concurrent.bms", "cccccccccccccccccccccccccccccccc");
        storageRowsOwner.ReplaceRowsAndCaptureSnapshot([concurrent], []);

        Assert.ThrowsException<InvalidOperationException>(() => owner.ApplyInstalledTargetUpsert(request));
        Assert.AreSame(concurrent, storageRowsOwner.BmsRows.Single());
    }

    [TestMethod]
    public void ApplyInstalledTargetUpsert_EmptyRequestIsNoOp()
    {
        var storageRowsOwner = new CatalogStorageRowsOwner();
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogInstalledTargetUpsertRequest request = owner.CreateInstalledTargetUpsertRequest([], []);
        CatalogInstalledTargetUpsertReceipt receipt = owner.ApplyInstalledTargetUpsert(request);

        Assert.IsFalse(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.NoOp, receipt.Kind);
        Assert.AreEqual(0, receipt.AddedCharts.Count);
        Assert.AreEqual(0, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(0, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.AreEqual(0, storageRowsOwner.BmsRows.Count);
        Assert.AreEqual(0, storageRowsOwner.BmsonRows.Count);
    }

    [TestMethod]
    public void ApplyDigestMutation_EmitsImmutableReceiptAndUpdatesOwnedDigestRows()
    {
        var bms = CreateBms("digest.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        bms.SetSha256(new string('b', 64));
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([bms], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([bms], []),
            initialRows.BmsRowsVersion,
            initialRows.BmsonRowsVersion));
        ownedCollectionOwner.Collection.CreateDuplicateChartRowSnapshot();
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);
        string newMd5 = "cccccccccccccccccccccccccccccccc";
        string newSha256 = new string('d', 64);
        var change = new LibraryChartDigestChange(
            LibraryChartKind.Bms,
            bms.path,
            bms.hash,
            bms.sha256,
            newMd5,
            newSha256);

        CatalogDigestMutationRequest request = owner.CreateDigestMutationRequest([change]);
        CatalogDigestMutationReceipt receipt = owner.ApplyDigestMutation(request);

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.DigestMutation, receipt.Kind);
        Assert.IsTrue(receipt.OwnedCollectionApplied);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.AreEqual(1, receipt.DigestChanges.Count);
        Assert.AreSame(change, receipt.DigestChanges[0]);
        Assert.AreEqual(newMd5, ownedCollectionOwner.Collection.CreateDuplicateChartRowSnapshot().Rows.Single().LookupHash);
        Assert.AreEqual(ownedCollectionOwner.CollectionVersion, receipt.OwnedCollectionVersion);
    }

    [TestMethod]
    public void ApplyDigestMutation_EmptyRequestIsNoOpAndSnapshotsInput()
    {
        var storageRowsOwner = new CatalogStorageRowsOwner();
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);
        var ignored = new LibraryChartDigestChange(
            LibraryChartKind.Bms,
            Path.Combine("C:\\Library", "unchanged.bms"),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            null,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            null);

        CatalogDigestMutationRequest request = owner.CreateDigestMutationRequest([ignored]);
        CatalogDigestMutationReceipt receipt = owner.ApplyDigestMutation(request);

        Assert.IsFalse(receipt.Applied);
        Assert.AreEqual(CatalogMutationApplyKind.NoOp, receipt.Kind);
        Assert.IsFalse(receipt.OwnedCollectionApplied);
        Assert.AreEqual(0, receipt.DigestChanges.Count);
        Assert.AreEqual(0, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(0, receipt.StorageRowsVersion.BmsonRowsVersion);
    }

    [TestMethod]
    public void ApplyDigestMutation_ShaOnlyChangeKeepsDuplicateLookupHash()
    {
        var bms = CreateBms("sha-only.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        bms.SetSha256(new string('b', 64));
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([bms], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
            OwnedChartCollectionState.FromStorageRows([bms], []),
            initialRows.BmsRowsVersion,
            initialRows.BmsonRowsVersion));
        ownedCollectionOwner.Collection.CreateDuplicateChartRowSnapshot();
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner);
        var change = new LibraryChartDigestChange(
            LibraryChartKind.Bms,
            bms.path,
            bms.hash,
            bms.sha256,
            bms.hash,
            new string('c', 64));

        CatalogDigestMutationReceipt receipt = owner.ApplyDigestMutation(
            owner.CreateDigestMutationRequest([change]));

        Assert.IsTrue(receipt.Applied);
        Assert.AreEqual(1, receipt.DigestChanges.Count);
        Assert.AreEqual(bms.hash, ownedCollectionOwner.Collection.CreateDuplicateChartRowSnapshot().Rows.Single().LookupHash);
    }

    [TestMethod]
    public void MaintenanceWriteRequest_SnapshotsPersistenceInputs()
    {
        TestableBmsFile song = CreateBms("snapshot.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        song.SetMaintenanceInfo(new BMSFileMaintenanceInfo(song)
        {
            encoding = "shift_jis",
            is_encoding_fixed = true
        }, suppressPropertyChanged: true, MaintenanceInfoOrigin.DbHydrated);
        var bmson = CreateBmson("snapshot.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        bmson.MaintenanceInfo = new BMSFileMaintenanceInfo
        {
            path = bmson.path,
            hash = bmson.md5,
            encoding = "utf-8"
        };

        CatalogMaintenanceWriteRequest request = new(
            [song.TryGetMaintenanceInfoWithoutCreating()],
            [song],
            [bmson],
            [" C:\\Library\\stale.maintenance ", "c:\\library\\STALE.MAINTENANCE"]);

        song.path = "C:\\Library\\changed.bms";
        song.SetTitle("changed");
        bmson.title = "changed";

        Assert.AreEqual(1, request.MaintenanceInfos.Count);
        Assert.AreEqual("C:\\Library\\snapshot.bms", request.MaintenanceInfos[0].path);
        Assert.AreEqual(1, request.Songs.Count);
        Assert.AreEqual("C:\\Library\\snapshot.bms", request.Songs[0].path);
        Assert.AreEqual(1, request.BmsonSongs.Count);
        Assert.AreEqual("snapshot.bmson", request.BmsonSongs[0].title);
        Assert.AreEqual(1, request.StaleMaintenancePaths.Count);
        Assert.AreEqual("C:\\Library\\stale.maintenance", request.StaleMaintenancePaths[0]);
    }

    [TestMethod]
    public void ChartInfoWriteRequest_SnapshotsRowsAndDeleteKeys()
    {
        var chartInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = new string('a', 64),
            md5 = new string('b', 32),
            charthash = new string('c', 64),
            notes = 123,
            parser_version = 7,
            updated_at = DateTime.UtcNow
        };
        var failure = new LR2SongDBExtended.chart_info_parse_failure
        {
            md5 = chartInfo.md5,
            sha256 = chartInfo.sha256,
            path = "C:\\Library\\snapshot.bms",
            failure_kind = "parse_failed",
            parser_version = 7,
            message = "original"
        };
        CatalogChartInfoWriteRequest request = new(
            [new ChartDigestBackfillEntry(chartInfo.md5, chartInfo.sha256)],
            [chartInfo],
            [failure],
            ["  " + chartInfo.md5, chartInfo.md5.ToUpperInvariant()]);

        chartInfo.notes = 999;
        failure.message = "changed";

        Assert.AreEqual(1, request.DigestEntries.Count);
        Assert.AreEqual(123, request.ChartInfoRows[0].notes);
        Assert.AreEqual("original", request.ParseFailureRows[0].message);
        Assert.AreEqual(1, request.ParseFailureDeleteMd5s.Count);
    }

    [TestMethod]
    public void ChartInfoOwner_UpsertUsesSha256ThenDeterministicMd5Candidate()
    {
        var owner = new CatalogChartInfoOwner(
            _ => { },
            () => false,
            (_, _) => false,
            () => null,
            _ => { });
        var first = new LR2SongDBExtended.chart_info
        {
            sha256 = new string('b', 64),
            md5 = new string('a', 32)
        };
        var second = new LR2SongDBExtended.chart_info
        {
            sha256 = new string('a', 64),
            md5 = first.md5
        };

        owner.ReplaceIndex([first], hydrated: true);
        owner.UpsertIndex([second], "test");

        Assert.AreSame(second, owner.ResolveChartInfo(null, first.md5, null, null));
        Assert.AreSame(first, owner.ResolveChartInfo(first.sha256, null, null, null));
    }

    [TestMethod]
    public void ChartInfoOwner_QueueHydration_WhenSchedulerRejects_CompletesCurrentRequest()
    {
        var notifications = new System.Collections.Generic.List<string>();
        Func<string, string, string, Func<Task>, bool> rejectScheduler = (_, _, _, _) => false;
        var owner = new CatalogChartInfoOwner(
            notifications.Add,
            () => false,
            (_, _) => false,
            () => rejectScheduler,
            _ => { });

        owner.QueueHydration("test_scheduler_rejected", queueBackfillAfterHydration: true, () => Task.CompletedTask);

        Assert.AreEqual(1, owner.ChartInfoHydrationRequestedVersion);
        Assert.AreEqual(1, owner.ChartInfoHydrationCompletedVersion);
        Assert.IsFalse(owner.ChartInfoHydrationRunning);
        CollectionAssert.Contains(notifications, nameof(BMSLibrary.ChartInfoHydrationRunning));
    }

    [TestMethod]
    public void ChartInfoOwner_ResolverSnapshot_ExcludesStaleParserRows()
    {
        var owner = new CatalogChartInfoOwner(
            _ => { },
            () => false,
            (_, _) => false,
            () => null,
            _ => { });
        string sha256 = new string('a', 64);
        string md5 = new string('b', 32);
        owner.ReplaceIndex(
        [
            new LR2SongDBExtended.chart_info
            {
                sha256 = sha256,
                md5 = md5,
                parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1
            }
        ],
        hydrated: true);
        var row = new TestableBmsFile();
        row.SetSha256(sha256);
        row.SetHash(md5);

        Func<BMSFile, LR2SongDBExtended.chart_info> resolver = owner.CreateLr2ResolverSnapshot();

        Assert.IsNull(resolver(row));
    }

    private static TestableBmsFile CreateBms(string fileName, string hash)
    {
        var file = new TestableBmsFile
        {
            path = Path.Combine("C:\\Library", fileName)
        };
        file.SetHash(hash);
        file.SetTitle(fileName);
        file.SetArtist("artist");
        return file;
    }

    private static LR2SongDBExtended.bmson_song CreateBmson(string fileName, string hash)
    {
        return new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Library", fileName),
            md5 = hash,
            title = fileName,
            artist = "artist"
        };
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void SetHash(string value) => hash = value;

        internal void SetSha256(string value) => sha256 = value;

        internal void SetTitle(string value) => title = value;

        internal void SetArtist(string value) => artist = value;
    }
}
