using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CatalogFileScanStorageReplacementOwnerTests
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
        var owner = new CatalogFileScanStorageReplacementOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogFileScanStorageReplacementRequest request = owner.CreateRequest(
            hasDbDiff: true,
            nextBmsRows: [nextBms],
            nextBmsonRows: [nextBmson],
            deletedBmsPaths: [oldBms.path],
            deletedBmsonPaths: [oldBmson.path],
            addedBmsFiles: [nextBms],
            addedBmsonSongs: [nextBmson])
            .WithAddedTargets(ChartStorageTargetSet.FromRows([nextBms], [nextBmson]));

        CatalogFileScanStorageReplacementReceipt receipt = owner.Apply(request);

        Assert.IsTrue(receipt.Applied);
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
        var owner = new CatalogFileScanStorageReplacementOwner(storageRowsOwner, ownedCollectionOwner);

        CatalogFileScanStorageReplacementRequest request = owner.CreateRequest(
            hasDbDiff: false,
            nextBmsRows: [bms],
            nextBmsonRows: [],
            deletedBmsPaths: [],
            deletedBmsonPaths: [],
            addedBmsFiles: [],
            addedBmsonSongs: []);

        CatalogFileScanStorageReplacementReceipt receipt = owner.Apply(request);

        Assert.IsFalse(receipt.Applied);
        Assert.IsFalse(receipt.OwnedCollectionApplied);
        Assert.AreEqual(initialRows.BmsRowsVersion, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreEqual(initialRows.BmsonRowsVersion, receipt.StorageRowsVersion.BmsonRowsVersion);
        Assert.AreEqual(0, receipt.AddedCharts.Count);
        Assert.AreEqual(0, receipt.RemovedCharts.Count);
        Assert.AreSame(bms, storageRowsOwner.BmsRows[0]);
    }

    private static BMSFile CreateBms(string fileName, string hash)
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

        internal void SetTitle(string value) => title = value;

        internal void SetArtist(string value) => artist = value;
    }
}
