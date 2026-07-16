using System;
using System.IO;
using System.Linq;
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
