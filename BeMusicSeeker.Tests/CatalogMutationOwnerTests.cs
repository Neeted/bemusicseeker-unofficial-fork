using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Tests.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CatalogMutationOwnerTests
{
    [TestMethod]
    public void CreateRelocationRequest_SnapshotsPreparedPathFacts()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogRelocationRequest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string oldPath = Path.Combine(tempRootPath, "old.bms");
        string newPath = Path.Combine(tempRootPath, "new.bms");
        string laterPath = Path.Combine(tempRootPath, "later.bms");
        File.WriteAllText(newPath, "#PLAYER 1");
        File.WriteAllText(laterPath, "#PLAYER 1");
        try
        {
            var movedBms = CreateBms(Path.GetFileName(oldPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            movedBms.path = oldPath;
            var delta = new LibraryMutationDelta();
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(movedBms),
                OldPath = oldPath,
                NewPath = newPath
            });

            var owner = new CatalogMutationOwner(new CatalogStorageRowsOwner(), new CatalogOwnedCollectionOwner(), null);
            CatalogRelocationRequest request = owner.CreateRelocationRequest(delta);

            delta.ChartPathChanges[0].NewPath = laterPath;
            movedBms.path = laterPath;

            Assert.AreEqual(1, request.BmsPathReplacements.Count);
            Assert.AreEqual(oldPath, request.BmsPathReplacements[0].OldPath);
            Assert.AreEqual(newPath, request.BmsPathReplacements[0].Song.path);
            Assert.AreSame(movedBms, request.BmsPathReplacements[0].LiveOwner);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyRelocation_CombinedBmsBmsonAndFolderEmitsDurableReceipt()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogRelocation_" + Guid.NewGuid().ToString("N"));
        string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
        string newDirectoryPath = Path.Combine(tempRootPath, "New");
        Directory.CreateDirectory(oldDirectoryPath);
        Directory.CreateDirectory(newDirectoryPath);
        string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
        string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
        string oldBmsonPath = Path.Combine(oldDirectoryPath, "chart.bmson");
        string newBmsonPath = Path.Combine(newDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(newBmsPath, "#PLAYER 1");
        File.WriteAllText(newBmsonPath, "{}");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var bms = CreateBms(Path.GetFileName(oldBmsPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            bms.path = oldBmsPath;
            var bmson = CreateBmson(Path.GetFileName(oldBmsonPath), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            bmson.path = oldBmsonPath;
            bmson.folder = oldDirectoryPath;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(bms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = oldDirectoryPath + Path.DirectorySeparatorChar,
                    title = "Old",
                    parent = "stale-parent"
                }, typeof(LR2SongDB.folder));
                songDb.InsertOrReplace(bmson, typeof(LR2SongDBExtended.bmson_song));
            }

            var storageRowsOwner = new CatalogStorageRowsOwner();
            storageRowsOwner.ReplaceRowsAndCaptureSnapshot([bms], [bmson]);
            CatalogStorageRowsSnapshot capturedRows = storageRowsOwner.CaptureSnapshot();
            var owner = new CatalogMutationOwner(
                storageRowsOwner,
                new CatalogOwnedCollectionOwner(),
                new BmsLibraryDbGateway(songDbPath));
            var delta = new LibraryMutationDelta();
            delta.FolderPathChanges.Add(new LibraryFolderPathChange
            {
                OldFolderPath = oldDirectoryPath,
                NewFolderPath = newDirectoryPath
            });
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(bms),
                OldPath = oldBmsPath,
                NewPath = newBmsPath
            });
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsonStorageOwnerIdentity(bmson),
                OldPath = oldBmsonPath,
                NewPath = newBmsonPath
            });

            CatalogMutationReceipt receipt = owner.ApplyCatalogMutation(delta, []);

            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(2, receipt.PathFacts.Count);
            Assert.AreEqual(1, receipt.StorageRowsVersion.PreviousBmsRowsVersion);
            Assert.AreEqual(2, receipt.StorageRowsVersion.BmsRowsVersion);
            Assert.AreEqual(1, receipt.StorageRowsVersion.PreviousBmsonRowsVersion);
            Assert.AreEqual(2, receipt.StorageRowsVersion.BmsonRowsVersion);
            Assert.AreEqual(newBmsPath, bms.path);
            Assert.AreEqual(newBmsonPath, bmson.path);
            CollectionAssert.AreEqual(new[] { bms }, storageRowsOwner.BmsRows.ToArray());
            CollectionAssert.AreEqual(new[] { bmson }, storageRowsOwner.BmsonRows.ToArray());
            CollectionAssert.AreEqual(new[] { bms }, capturedRows.BmsRows.ToArray());
            CollectionAssert.AreEqual(new[] { bmson }, capturedRows.BmsonRows.ToArray());
            Assert.AreEqual(newBmsPath, capturedRows.BmsRows[0].path);
            Assert.AreEqual(newBmsonPath, capturedRows.BmsonRows[0].path);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDB.song>();
            verifySongDb.CreateTable<LR2SongDB.folder>();
            verifySongDb.CreateTable<LR2SongDBExtended.bmson_song>();
            Assert.IsTrue(verifySongDb.Table<BMSFile>().Any(row => row.path == newBmsPath));
            Assert.IsTrue(verifySongDb.Table<LR2SongDBExtended.bmson_song>().Any(row => row.path == newBmsonPath));
            Assert.IsTrue(verifySongDb.Table<LR2SongDB.folder>().Any(row => row.path == newDirectoryPath + Path.DirectorySeparatorChar));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyCatalogMutation_CombinesRelocationRemovalAndOwnedProjectionAfterOneCommit()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutation_" + Guid.NewGuid().ToString("N"));
        string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
        string newDirectoryPath = Path.Combine(tempRootPath, "New");
        Directory.CreateDirectory(oldDirectoryPath);
        Directory.CreateDirectory(newDirectoryPath);
        string movedOldPath = Path.Combine(oldDirectoryPath, "moved.bms");
        string movedNewPath = Path.Combine(newDirectoryPath, "moved.bms");
        string removedBmsPath = Path.Combine(oldDirectoryPath, "removed.bms");
        string removedBmsonPath = Path.Combine(oldDirectoryPath, "removed.bmson");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(movedNewPath, "#PLAYER 1");
        File.WriteAllText(removedBmsPath, "#PLAYER 1");
        File.WriteAllText(removedBmsonPath, "{}");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var movedBms = CreateBms(Path.GetFileName(movedOldPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            movedBms.path = movedOldPath;
            var removedBms = CreateBms(Path.GetFileName(removedBmsPath), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            removedBms.path = removedBmsPath;
            var removedBmson = CreateBmson(Path.GetFileName(removedBmsonPath), "cccccccccccccccccccccccccccccccc");
            removedBmson.path = removedBmsonPath;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(movedBms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                songDb.InsertOrReplace(removedBms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = removedBmsPath }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(removedBmson, typeof(LR2SongDBExtended.bmson_song));
                BmsLibraryDbGateway.EnsureAppOwnedSchema(songDb);
                songDb.InsertOrReplace(
                    new LR2SongDBExtended.chart_digest_map
                    {
                        md5 = removedBms.hash,
                        sha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
                    },
                    typeof(LR2SongDBExtended.chart_digest_map));
            }

            var storageRowsOwner = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot(
                [movedBms, removedBms],
                [removedBmson]);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows([movedBms, removedBms], [removedBmson]),
                initialRows.BmsRowsVersion,
                initialRows.BmsonRowsVersion));
            var owner = new CatalogMutationOwner(
                storageRowsOwner,
                ownedCollectionOwner,
                new BmsLibraryDbGateway(songDbPath));
            var delta = new LibraryMutationDelta();
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(movedBms),
                OldPath = movedOldPath,
                NewPath = movedNewPath
            });
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedBms));
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedBmson));

            CatalogMutationReceipt receipt = owner.ApplyCatalogMutation(delta, delta.ChartRemoveRequests);

            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(CatalogMutationApplyKind.GenericMutation, receipt.Kind);
            Assert.AreEqual(0, receipt.AddedCharts.Count);
            CollectionAssert.AreEquivalent(
                new[] { removedBmsPath, removedBmsonPath },
                receipt.RemovedCharts.Select(fact => fact.Path).ToArray());
            Assert.AreEqual(1, receipt.MovedCharts.Count);
            Assert.AreEqual(movedOldPath, receipt.MovedCharts[0].OldPath);
            Assert.AreEqual(movedNewPath, receipt.MovedCharts[0].NewPath);
            Assert.AreEqual(movedBms.hash, receipt.MovedCharts[0].Md5);
            Assert.IsTrue(receipt.OwnedCollectionApplied);
            Assert.AreEqual(ownedCollectionOwner.CollectionVersion, receipt.OwnedCollectionVersion);
            Assert.AreEqual(initialRows.BmsRowsVersion + 1, receipt.StorageRowsVersion.BmsRowsVersion);
            Assert.AreEqual(initialRows.BmsonRowsVersion + 1, receipt.StorageRowsVersion.BmsonRowsVersion);
            Assert.AreEqual(movedNewPath, movedBms.path);
            Assert.AreEqual(1, storageRowsOwner.BmsRows.Count);
            Assert.AreSame(movedBms, storageRowsOwner.BmsRows.Single());
            Assert.AreEqual(0, storageRowsOwner.BmsonRows.Count);
            Assert.AreEqual(1, ownedCollectionOwner.Collection.CreateSnapshot().Count);
            CollectionAssert.Contains(ownedCollectionOwner.Collection.CreatePathSnapshot(), movedNewPath);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.IsTrue(verifySongDb.Table<BMSFile>().Any(row => row.path == movedNewPath));
            Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(row => row.path == removedBmsPath));
            Assert.IsFalse(verifySongDb.Table<LR2SongDBExtended.bmson_song>().Any(row => row.path == removedBmsonPath));
            Assert.IsFalse(verifySongDb.Table<BMSFileMaintenanceInfo>().Any(row => row.path == removedBmsPath));
            Assert.IsFalse(verifySongDb.Table<LR2SongDBExtended.chart_digest_map>().Any(row => row.md5 == removedBms.hash));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyCatalogMutation_WhenRemovalFailsRollsBackRelocationAndLiveVersions()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutationRollback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string oldPath = Path.Combine(tempRootPath, "moved.bms");
        string newPath = Path.Combine(tempRootPath, "moved-new.bms");
        string removedPath = Path.Combine(tempRootPath, "removed.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(newPath, "#PLAYER 1");
        File.WriteAllText(removedPath, "#PLAYER 1");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var movedBms = CreateBms(Path.GetFileName(oldPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            movedBms.path = oldPath;
            var removedBms = CreateBms(Path.GetFileName(removedPath), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            removedBms.path = removedPath;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(movedBms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                songDb.InsertOrReplace(removedBms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                string escapedRemovedPath = removedPath.Replace("'", "''");
                songDb.Execute("CREATE TRIGGER fail_catalog_remove BEFORE DELETE ON song WHEN OLD.path = '" + escapedRemovedPath + "' BEGIN SELECT RAISE(ABORT, 'forced removal failure'); END;");
            }

            var storageRowsOwner = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([movedBms, removedBms], []);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows([movedBms, removedBms], []),
                initialRows.BmsRowsVersion,
                initialRows.BmsonRowsVersion));
            var owner = new CatalogMutationOwner(
                storageRowsOwner,
                ownedCollectionOwner,
                new BmsLibraryDbGateway(songDbPath));
            var delta = new LibraryMutationDelta();
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(movedBms),
                OldPath = oldPath,
                NewPath = newPath
            });
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedBms));

            Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyCatalogMutation(delta, delta.ChartRemoveRequests));

            Assert.AreEqual(oldPath, movedBms.path);
            Assert.AreEqual(initialRows.BmsRowsVersion, storageRowsOwner.BmsRowsVersion);
            Assert.AreEqual(2, storageRowsOwner.BmsRows.Count);
            Assert.AreEqual(2, ownedCollectionOwner.Collection.CreateSnapshot().Count);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.IsTrue(verifySongDb.Table<BMSFile>().Any(row => row.path == oldPath));
            Assert.IsTrue(verifySongDb.Table<BMSFile>().Any(row => row.path == removedPath));
            Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(row => row.path == newPath));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyCatalogMutation_SamePathDifferentOwnersKeepsRelocatedOwnerRow()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutationSamePath_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string removedPath = Path.Combine(tempRootPath, "existing.bms");
        string movedOldPath = Path.Combine(tempRootPath, "moved-old.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(removedPath, "#PLAYER 1");
        File.WriteAllText(movedOldPath, "#PLAYER 1");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var removedBms = CreateBms(Path.GetFileName(removedPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            removedBms.path = removedPath;
            var movedBms = CreateBms(Path.GetFileName(movedOldPath), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            movedBms.path = movedOldPath;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(removedBms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                songDb.InsertOrReplace(movedBms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                songDb.InsertOrReplace(
                    new BMSFileMaintenanceInfo
                    {
                        path = removedPath,
                        hash = removedBms.hash
                    },
                    typeof(LR2SongDBExtended.maintenance));
            }

            var storageRowsOwner = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([removedBms, movedBms], []);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows([removedBms, movedBms], []),
                initialRows.BmsRowsVersion,
                initialRows.BmsonRowsVersion));
            var owner = new CatalogMutationOwner(
                storageRowsOwner,
                ownedCollectionOwner,
                new BmsLibraryDbGateway(songDbPath));
            var delta = new LibraryMutationDelta();
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(movedBms),
                OldPath = movedOldPath,
                NewPath = removedPath
            });
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedBms));

            owner.ApplyCatalogMutation(delta, delta.ChartRemoveRequests);

            Assert.AreEqual(removedPath, movedBms.path);
            Assert.AreEqual(1, storageRowsOwner.BmsRows.Count);
            Assert.AreSame(movedBms, storageRowsOwner.BmsRows.Single());
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            BMSFile persisted = verifySongDb.Table<BMSFile>().Single(row => row.path == removedPath);
            Assert.AreEqual(movedBms.hash, persisted.hash);
            Assert.IsFalse(verifySongDb.Table<BMSFileMaintenanceInfo>().Any(row => row.path == removedPath));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyCatalogMutation_CaseVariantPathCollisionRemovesOldExactRow()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutationCaseCollision_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string removedPath = Path.Combine(tempRootPath, "existing.bms");
        string movedOldPath = Path.Combine(tempRootPath, "moved-old.bms");
        string movedNewPath = Path.Combine(tempRootPath, "EXISTING.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(removedPath, "#PLAYER 1");
        File.WriteAllText(movedOldPath, "#PLAYER 1");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var removedBms = CreateBms(Path.GetFileName(removedPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            removedBms.path = removedPath;
            var movedBms = CreateBms(Path.GetFileName(movedOldPath), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            movedBms.path = movedOldPath;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(removedBms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                songDb.InsertOrReplace(movedBms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            }

            var storageRowsOwner = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([removedBms, movedBms], []);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows([removedBms, movedBms], []),
                initialRows.BmsRowsVersion,
                initialRows.BmsonRowsVersion));
            var owner = new CatalogMutationOwner(
                storageRowsOwner,
                ownedCollectionOwner,
                new BmsLibraryDbGateway(songDbPath));
            var delta = new LibraryMutationDelta();
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(movedBms),
                OldPath = movedOldPath,
                NewPath = movedNewPath
            });
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedBms));

            owner.ApplyCatalogMutation(delta, delta.ChartRemoveRequests);

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(row => row.path == removedPath));
            Assert.AreEqual(movedBms.hash, verifySongDb.Table<BMSFile>().Single(row => row.path == movedNewPath).hash);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyCatalogMutation_PathCleanupDoesNotRemoveRelocatedDestination()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutationProtectedDestination_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string oldPath = Path.Combine(tempRootPath, "old.bms");
        string newPath = Path.Combine(tempRootPath, "new.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllText(newPath, "#PLAYER 1");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var movedBms = CreateBms(Path.GetFileName(oldPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            movedBms.path = oldPath;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(movedBms.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            }

            var storageRowsOwner = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([movedBms], []);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows([movedBms], []),
                initialRows.BmsRowsVersion,
                initialRows.BmsonRowsVersion));
            var owner = new CatalogMutationOwner(
                storageRowsOwner,
                ownedCollectionOwner,
                new BmsLibraryDbGateway(songDbPath));
            var delta = new LibraryMutationDelta();
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(movedBms),
                OldPath = oldPath,
                NewPath = newPath
            });
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, newPath));

            CatalogMutationReceipt receipt = owner.ApplyCatalogMutation(delta, delta.ChartRemoveRequests);

            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(1, receipt.PathFacts.Count);
            Assert.AreEqual(newPath, receipt.PathFacts.Single().NewPath);
            Assert.AreEqual(newPath, movedBms.path);
            Assert.AreSame(movedBms, storageRowsOwner.BmsRows.Single());
            Assert.AreEqual(newPath, ownedCollectionOwner.Collection.CreatePathSnapshot().Single());
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1, verifySongDb.Table<BMSFile>().Count());
            Assert.AreEqual(newPath, verifySongDb.Table<BMSFile>().Single().path);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// PathCleanup の対象集合だけを実接続へ渡し、背景行を managed へ返さずに
    /// digest の所有判定と BMS／BMSON の分離を維持します。
    /// </summary>
    [TestMethod]
    public void ApplyCatalogMutation_PathCleanupUsesBoundedExactSetAndPreservesDigestOwnership()
    {
        const int removalCount = 4;
        const int smallBackgroundCount = 16;
        const int largeBackgroundCount = 128;
        // temp/schemaの固定scanの揺れだけを許容し、背景差112件のscan増加は通さない。
        const int allowedFixedScanGrowth = 32;
        BoundedCleanupRun smallRun = ExecuteBoundedCleanupCase(smallBackgroundCount);
        BoundedCleanupRun largeRun = ExecuteBoundedCleanupCase(largeBackgroundCount);

        Assert.AreEqual(removalCount, smallRun.RemovalCount);
        Assert.AreEqual(removalCount, largeRun.RemovalCount);
        Assert.AreEqual(
            smallRun.ReturnedCatalogRows,
            largeRun.ReturnedCatalogRows,
            "背景行数を増やしても対象SQLの返却行数が変わりました。");
        Assert.IsTrue(smallRun.ProfiledStatementCount > 0, "16行背景のPROFILE callbackを観測できませんでした。");
        Assert.IsTrue(largeRun.ProfiledStatementCount > 0, "128行背景のPROFILE callbackを観測できませんでした。");
        Assert.IsTrue(
            largeRun.FullScanSteps <= smallRun.FullScanSteps + allowedFixedScanGrowth,
            "背景行の増加に伴って全SQLのFULLSCAN_STEPが増えました。16="
            + smallRun.FullScanSteps + ", 128=" + largeRun.FullScanSteps
            + ", statements=" + largeRun.ProfiledStatementCount);
        // VM_STEPはindex range traversalも含むため、FULLSCAN_STEPやROW callbackが不変でも
        // 背景表を広く読む集合SQLの増加を検出できる。対象件数とtemp表の大きさは両ケースで固定する。
        Assert.AreEqual(
            smallRun.VmSteps,
            largeRun.VmSteps,
            "背景行の増加に伴って全SQLのVM_STEPが増えました。16=" + smallRun.VmSteps
            + ", 128=" + largeRun.VmSteps + Environment.NewLine
            + FormatStatementMetrics(largeRun.Statements));
        Assert.IsTrue(smallRun.QueryPlans.Count > 0, "16行背景の補助query planを取得できませんでした。");
        Assert.IsTrue(largeRun.QueryPlans.Count > 0, "128行背景の補助query planを取得できませんでした。");
    }

    private static BoundedCleanupRun ExecuteBoundedCleanupCase(int backgroundCount)
    {
        const string sharedMd5 = "11111111111111111111111111111111";
        const string orphanMd5 = "22222222222222222222222222222222";
        const string bmsonMd5 = "33333333333333333333333333333333";
        string tempRootPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_CatalogMutationBoundedCleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string targetBmsSharedPath = Path.Combine(tempRootPath, "target-shared.bms");
        string targetBmsOrphanPath = Path.Combine(tempRootPath, "target-orphan.bms");
        string targetBmsonPath = Path.Combine(tempRootPath, "target.bmson");
        string maintenanceOnlyPath = Path.Combine(tempRootPath, "maintenance-only.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            TestableBmsFile targetBmsShared = CreateBms("target-shared.bms", sharedMd5);
            targetBmsShared.path = targetBmsSharedPath;
            TestableBmsFile targetBmsOrphan = CreateBms("target-orphan.bms", orphanMd5);
            targetBmsOrphan.path = targetBmsOrphanPath;
            LR2SongDBExtended.bmson_song targetBmson = CreateBmson("target.bmson", bmsonMd5);
            targetBmson.path = targetBmsonPath;
            TestableBmsFile survivingShared = CreateBms("surviving-shared.bms", sharedMd5);
            survivingShared.path = Path.Combine(tempRootPath, "surviving-shared.bms");
            TestableBmsFile[] backgroundBmsRows = Enumerable.Range(0, backgroundCount - 1)
                .Select(index =>
                {
                    TestableBmsFile row = CreateBms(
                        "background-" + index.ToString("D3") + ".bms",
                        (index + 100).ToString("x32"));
                    row.path = Path.Combine(tempRootPath, row.title);
                    return row;
                })
                .Append(survivingShared)
                .ToArray();
            LR2SongDBExtended.bmson_song[] backgroundBmsonRows = Enumerable.Range(0, backgroundCount)
                .Select(index =>
                {
                    string fileName = "background-" + index.ToString("D3") + ".bmson";
                    LR2SongDBExtended.bmson_song row = CreateBmson(
                        fileName,
                        (index + 500).ToString("x32"));
                    row.path = Path.Combine(tempRootPath, fileName);
                    return row;
                })
                .ToArray();
            TestableBmsFile[] allBmsRows = [targetBmsShared, targetBmsOrphan, .. backgroundBmsRows];
            LR2SongDBExtended.bmson_song[] allBmsonRows = [targetBmson, .. backgroundBmsonRows];

            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(setup);
                BmsLibraryDbGateway.EnsureMaintenanceSchema(setup);
                BmsLibraryDbGateway.EnsureSongLookupIndexes(setup);
                foreach (TestableBmsFile row in allBmsRows)
                {
                    setup.InsertOrReplace(row.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                }
                foreach (LR2SongDBExtended.bmson_song row in allBmsonRows)
                {
                    setup.InsertOrReplace(row, typeof(LR2SongDBExtended.bmson_song));
                }
                foreach (string path in new[] { targetBmsSharedPath, targetBmsOrphanPath, targetBmsonPath, maintenanceOnlyPath }
                    .Concat(backgroundBmsRows.Select(row => row.path))
                    .Concat(backgroundBmsonRows.Select(row => row.path)))
                {
                    setup.InsertOrReplace(
                        new LR2SongDBExtended.maintenance { path = path },
                        typeof(LR2SongDBExtended.maintenance));
                }
                foreach ((string md5, string sha256) in new[]
                {
                    (sharedMd5, new string('a', 64)),
                    (orphanMd5, new string('b', 64)),
                    (bmsonMd5, new string('c', 64))
                })
                {
                    setup.InsertOrReplace(
                        new LR2SongDBExtended.chart_digest_map { md5 = md5, sha256 = sha256 },
                        typeof(LR2SongDBExtended.chart_digest_map));
                }
            }

            var storageRowsOwner = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot(
                allBmsRows.Cast<BMSFile>().ToList(),
                allBmsonRows.ToList());
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows(allBmsRows, allBmsonRows),
                initialRows.BmsRowsVersion,
                initialRows.BmsonRowsVersion));
            var removeRequests = new[]
            {
                OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, targetBmsSharedPath),
                OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, targetBmsOrphanPath),
                OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, maintenanceOnlyPath),
                OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bmson, targetBmsonPath)
            };
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.AddRange(removeRequests);

            using var observation = new SqliteStatementObservation();
            var owner = new CatalogMutationOwner(
                storageRowsOwner,
                ownedCollectionOwner,
                new BmsLibraryDbGateway(songDbPath, songDbFactory: observation.OpenSongDb));

            CatalogMutationReceipt receipt = owner.ApplyCatalogMutation(delta, removeRequests);
            observation.ThrowIfCallbackFailed();

            Assert.IsTrue(receipt.Applied);
            CollectionAssert.AreEquivalent(
                removeRequests.Select(request => request.Path).ToArray(),
                receipt.RemovedCharts.Select(fact => fact.Path).ToArray());
            string[] expectedBmsPaths = backgroundBmsRows.Select(row => row.path).ToArray();
            string[] expectedBmsonPaths = backgroundBmsonRows.Select(row => row.path).ToArray();
            string[] expectedOwnedPaths = expectedBmsPaths.Concat(expectedBmsonPaths).ToArray();
            CollectionAssert.AreEquivalent(
                expectedBmsPaths,
                storageRowsOwner.BmsRows.Select(row => row.path).ToArray());
            CollectionAssert.AreEquivalent(
                expectedBmsonPaths,
                storageRowsOwner.BmsonRows.Select(row => row.path).ToArray());
            CollectionAssert.AreEquivalent(
                expectedOwnedPaths,
                ownedCollectionOwner.Collection.CreatePathSnapshot());
            using (var verifySongDb = new LR2SongDBExtended(songDbPath))
            {
                List<LR2SongDB.song> remainingBmsRows = [.. verifySongDb.Table<LR2SongDB.song>()];
                List<LR2SongDBExtended.bmson_song> remainingBmsonRows = [.. verifySongDb.Table<LR2SongDBExtended.bmson_song>()];
                List<LR2SongDBExtended.maintenance> remainingMaintenanceRows = [.. verifySongDb.Table<LR2SongDBExtended.maintenance>()];
                List<LR2SongDBExtended.chart_digest_map> remainingDigests = [.. verifySongDb.Table<LR2SongDBExtended.chart_digest_map>()];
                Assert.AreEqual(backgroundCount, remainingBmsRows.Count);
                Assert.AreEqual(backgroundCount, remainingBmsonRows.Count);
                Assert.AreEqual(backgroundCount + backgroundCount, remainingMaintenanceRows.Count);
                CollectionAssert.AreEquivalent(
                    expectedBmsPaths,
                    remainingBmsRows.Select(row => row.path).ToArray());
                CollectionAssert.AreEquivalent(
                    expectedBmsonPaths,
                    remainingBmsonRows.Select(row => row.path).ToArray());
                CollectionAssert.DoesNotContain(
                    remainingMaintenanceRows.Select(row => row.path).ToArray(),
                    targetBmsSharedPath);
                CollectionAssert.DoesNotContain(
                    remainingMaintenanceRows.Select(row => row.path).ToArray(),
                    targetBmsOrphanPath);
                CollectionAssert.DoesNotContain(
                    remainingMaintenanceRows.Select(row => row.path).ToArray(),
                    targetBmsonPath);
                CollectionAssert.DoesNotContain(
                    remainingMaintenanceRows.Select(row => row.path).ToArray(),
                    maintenanceOnlyPath);
                Assert.IsTrue(remainingDigests.Any(row => row.md5 == sharedMd5));
                Assert.IsFalse(remainingDigests.Any(row => row.md5 == orphanMd5));
                Assert.IsTrue(remainingDigests.Any(row => row.md5 == bmsonMd5));
            }

            IReadOnlyList<SqliteStatementObservation.SqliteObservedStatement> statements = observation.Statements;
            int returnedCatalogRows = observation.CountReturnedRows(IsCatalogResultStatement);
            Assert.IsTrue(
                returnedCatalogRows <= removeRequests.Length,
                "対象集合を超えるcatalog返却行: " + returnedCatalogRows + Environment.NewLine
                + string.Join(
                    Environment.NewLine,
                    statements.Select(statement => statement.RowCount + " rows, fullscan="
                        + statement.FullScanSteps + ", vm=" + statement.VmSteps + " " + statement.Sql)));
            Assert.IsFalse(statements.Any(IsPerHashOrphanQuery));
            IReadOnlyList<string> queryPlans = observation.ExplainCatalogQueryPlans(songDbPath);
            Assert.IsTrue(queryPlans.Count > 0);
            return new BoundedCleanupRun(
                removeRequests.Length,
                returnedCatalogRows,
                observation.CountFullScanSteps(),
                observation.CountVmSteps(),
                statements.Count(statement => statement.ProfileCount > 0),
                queryPlans,
                statements);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed record BoundedCleanupRun(
        int RemovalCount,
        int ReturnedCatalogRows,
        int FullScanSteps,
        int VmSteps,
        int ProfiledStatementCount,
        IReadOnlyList<string> QueryPlans,
        IReadOnlyList<SqliteStatementObservation.SqliteObservedStatement> Statements);

    private static string FormatStatementMetrics(
        IEnumerable<SqliteStatementObservation.SqliteObservedStatement> statements)
    {
        return string.Join(
            Environment.NewLine,
            statements
                .Where(statement => statement.ProfileCount > 0)
                .Select(statement => "fullscan=" + statement.FullScanSteps + ", vm="
                    + statement.VmSteps + ", sql=" + statement.Sql));
    }

    [TestMethod]
    public void ApplyCatalogMutation_RemovalDeletesMaintenanceWhenSongRowIsMissing()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogMutationMaintenanceDrift_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string removedPath = Path.Combine(tempRootPath, "missing-song.bms");
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            var removedBms = CreateBms(Path.GetFileName(removedPath), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            removedBms.path = removedPath;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(
                    new BMSFileMaintenanceInfo
                    {
                        path = removedPath,
                        hash = removedBms.hash
                    },
                    typeof(LR2SongDBExtended.maintenance));
            }

            var storageRowsOwner = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([removedBms], []);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows([removedBms], []),
                initialRows.BmsRowsVersion,
                initialRows.BmsonRowsVersion));
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedBms));

            new CatalogMutationOwner(
                storageRowsOwner,
                ownedCollectionOwner,
                new BmsLibraryDbGateway(songDbPath))
                .ApplyCatalogMutation(delta, delta.ChartRemoveRequests);

            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.IsFalse(verifySongDb.Table<BMSFileMaintenanceInfo>().Any(row => row.path == removedPath));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

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
        int initialOwnedCollectionVersion = ownedCollectionOwner.CollectionVersion;
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, null);

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
        Assert.AreEqual(initialOwnedCollectionVersion + 1, receipt.OwnedCollectionVersion);
        Assert.AreEqual(ownedCollectionOwner.CollectionVersion, receipt.OwnedCollectionVersion);
    }

    [TestMethod]
    public void Apply_WithoutDbDiffDoesNotChangeRowsOrEmitMutationFacts()
    {
        var bms = CreateBms("current.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([bms], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        int initialOwnedCollectionVersion = ownedCollectionOwner.CollectionVersion;
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, null);

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
        Assert.AreEqual(initialOwnedCollectionVersion, receipt.OwnedCollectionVersion);
        Assert.AreEqual(initialOwnedCollectionVersion, ownedCollectionOwner.CollectionVersion);
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
        int initialOwnedCollectionVersion = ownedCollectionOwner.CollectionVersion;
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, null);

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
        Assert.AreEqual(initialOwnedCollectionVersion + 1, receipt.OwnedCollectionVersion);
        Assert.AreEqual(receipt.OwnedCollectionVersion, ownedCollectionOwner.CollectionVersion);
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
        var owner = new CatalogMutationOwner(storageRowsOwner, new CatalogOwnedCollectionOwner(), null);

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
    public void ApplyStorageRowsReplacement_ExplicitSameInputStillPublishesVersion()
    {
        var bms = CreateBms("same-input.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([bms], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, null);

        CatalogStorageRowsReplacementRequest request = owner.CreateStorageRowsReplacementRequest(
            storageRowsOwner.BmsRows,
            [],
            replaceBmsRows: true,
            replaceBmsonRows: false);
        CatalogStorageRowsReplacementReceipt receipt = owner.ApplyStorageRowsReplacement(request);

        Assert.IsTrue(receipt.Applied);
        Assert.IsTrue(receipt.BmsRowsChanged);
        Assert.IsFalse(receipt.BmsonRowsChanged);
        Assert.AreEqual(initialRows.BmsRowsVersion + 1, receipt.StorageRowsVersion.BmsRowsVersion);
        Assert.AreSame(bms, storageRowsOwner.BmsRows.Single());
    }

    [TestMethod]
    public void StorageRowsOwner_Background16And128WithFixedDeltaKeepsWarmCaptureAndGetterBounded()
    {
        StorageWorkScenario small = RunStorageWorkScenario(16);
        StorageWorkScenario large = RunStorageWorkScenario(128);

        Assert.IsTrue(small.ColdEnumerationCount > 0);
        Assert.IsTrue(small.ColdVisitedEntryCount > 0);
        Assert.IsTrue(small.ColdMaterializationCount > 0);
        Assert.IsTrue(small.ColdAccessCount > 0);
        Assert.IsTrue(large.ColdEnumerationCount > 0);
        Assert.IsTrue(large.ColdVisitedEntryCount > 0);
        Assert.IsTrue(large.ColdMaterializationCount > 0);
        Assert.IsTrue(large.ColdAccessCount > 0);

        Assert.AreEqual(0, small.WarmEnumerationCount);
        Assert.AreEqual(0, small.WarmVisitedEntryCount);
        Assert.AreEqual(0, small.WarmMaterializationCount);
        Assert.AreEqual(0, large.WarmEnumerationCount);
        Assert.AreEqual(0, large.WarmVisitedEntryCount);
        Assert.AreEqual(0, large.WarmMaterializationCount);
        Assert.AreEqual(small.WarmEnumerationCount, large.WarmEnumerationCount);
        Assert.AreEqual(small.WarmVisitedEntryCount, large.WarmVisitedEntryCount);
        Assert.AreEqual(small.WarmMaterializationCount, large.WarmMaterializationCount);
        Assert.IsTrue(small.WarmAccessCount > 0);
        Assert.IsTrue(large.WarmAccessCount > 0);
        Assert.IsTrue(small.WarmAccessCount <= CalculateWarmAccessUpperBound(16));
        Assert.IsTrue(large.WarmAccessCount <= CalculateWarmAccessUpperBound(128));
    }

    [TestMethod]
    public void StorageRowsOwner_RawBmsonUpsertNormalizesOnlyOnBmsonChangeAndKeepsTieSlot()
    {
        var z = CreateBmson("z.bmson", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var upper = CreateBmson("A.bmson", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var lower = CreateBmson("a.bmson", "cccccccccccccccccccccccccccccccc");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        storageRowsOwner.ReplaceBmsonRows([z, upper, lower]);
        var addedBms = CreateBms("added.bms", "dddddddddddddddddddddddddddddddd");

        storageRowsOwner.ApplyInstalledTargets(ChartStorageTargetSet.FromRows([addedBms], []));

        CollectionAssert.AreEqual(
            new[] { z, upper, lower },
            storageRowsOwner.BmsonRows.ToArray());

        var replacement = CreateBmson("A.bmson", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        storageRowsOwner.ApplyInstalledTargets(ChartStorageTargetSet.FromRows([], [replacement]));

        CollectionAssert.AreEqual(
            new[] { replacement, lower, z },
            storageRowsOwner.BmsonRows.ToArray());
    }

    [TestMethod]
    public void ApplyInstalledTargetUpsert_EmitsReceiptAndReplacesSamePathRows()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogInstalledTarget_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
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
            var owner = new CatalogMutationOwner(
                storageRowsOwner,
                ownedCollectionOwner,
                new BmsLibraryDbGateway(songDbPath));

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
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(newBms.path, verifySongDb.Table<LR2SongDB.song>().Single(row => row.path == newBms.path).path);
            Assert.AreEqual(newBmson.path, verifySongDb.Table<LR2SongDBExtended.bmson_song>().Single(row => row.path == newBmson.path).path);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// generic／installの両upsertで別exact keyを保持し、複数BMSONの入力を畳みません。
    /// </summary>
    [DataTestMethod]
    [DataRow("CHART", false, false)]
    [DataRow("other", false, false)]
    [DataRow(".\\chart", false, false)]
    [DataRow("CHART", true, false)]
    [DataRow("other", true, false)]
    [DataRow("CHART", false, true)]
    [DataRow("CHART", true, true)]
    public void ApplyInstalledTargetUpsert_PreservesEveryExactKey(string siblingName, bool generic, bool replaceBoth)
    {
        BmsLibraryStateApplierTestSupport.WithTemporarySongDb(songDbPath =>
        {
            var oldBms = CreateBms("chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var keptBms = CreateBms(siblingName + ".bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            keptBms.favorite = 7;
            keptBms.tag = "preserved";
            var oldBmson = CreateBmson("chart.bmson", "cccccccccccccccccccccccccccccccc");
            var keptBmson = CreateBmson(siblingName + ".bmson", "dddddddddddddddddddddddddddddddd");
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDBExtended.bmson_song>();
                db.InsertOrReplace(oldBms, typeof(LR2SongDB.song));
                db.InsertOrReplace(keptBms, typeof(LR2SongDB.song));
                db.InsertOrReplace(oldBmson, typeof(LR2SongDBExtended.bmson_song));
                db.InsertOrReplace(keptBmson, typeof(LR2SongDBExtended.bmson_song));
            }
            var storage = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initial = storage.ReplaceRowsAndCaptureSnapshot([oldBms, keptBms], [oldBmson, keptBmson]);
            var owned = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(owned.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows([oldBms, keptBms], [oldBmson, keptBmson]),
                initial.BmsRowsVersion, initial.BmsonRowsVersion));
            var owner = new CatalogMutationOwner(storage, owned, new BmsLibraryDbGateway(songDbPath));
            var addedBms = CreateBms("chart.bms", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            var addedBmson = CreateBmson("chart.bmson", "ffffffffffffffffffffffffffffffff");
            BMSFile[] bmsInput = replaceBoth ? [addedBms, keptBms] : [addedBms];
            LR2SongDBExtended.bmson_song[] bmsonInput = replaceBoth ? [addedBmson, keptBmson] : [addedBmson];

            if (generic)
            {
                Assert.IsTrue(owner.ApplyCatalogMutation(new LibraryMutationDelta(), [], bmsInput, bmsonInput).Applied);
            }
            else
            {
                Assert.IsTrue(owner.ApplyInstalledTargetUpsert(bmsInput, bmsonInput).Applied);
            }

            CollectionAssert.AreEquivalent(new[] { addedBms, keptBms }, storage.BmsRows.ToArray());
            CollectionAssert.AreEquivalent(new[] { addedBmson, keptBmson }, storage.BmsonRows.ToArray());
            CollectionAssert.AreEquivalent(new[] { addedBms.path, keptBms.path, addedBmson.path, keptBmson.path }, owned.Collection.CreatePathSnapshot());
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(2, readback.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(2, readback.Table<LR2SongDBExtended.bmson_song>().Count());
            Assert.AreEqual(addedBms.hash, readback.Find<LR2SongDB.song>(addedBms.path).hash);
            Assert.AreEqual(keptBms.hash, readback.Find<LR2SongDB.song>(keptBms.path).hash);
            Assert.AreEqual(7, readback.Find<LR2SongDB.song>(keptBms.path).favorite);
            Assert.AreEqual("preserved", readback.Find<LR2SongDB.song>(keptBms.path).tag);
            Assert.AreEqual(addedBmson.md5, readback.Find<LR2SongDBExtended.bmson_song>(addedBmson.path).md5);
            Assert.AreEqual(keptBmson.md5, readback.Find<LR2SongDBExtended.bmson_song>(keptBmson.path).md5);
        });
    }

    [TestMethod]
    public void ApplyInstalledTargetUpsert_RejectsStaleRequestBeforeChangingRows()
    {
        var original = CreateBms("original.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var replacement = CreateBms("replacement.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        storageRowsOwner.ReplaceRowsAndCaptureSnapshot([original], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, null);

        CatalogInstalledTargetUpsertRequest request = owner.CreateInstalledTargetUpsertRequest([replacement], []);
        var concurrent = CreateBms("concurrent.bms", "cccccccccccccccccccccccccccccccc");
        storageRowsOwner.ReplaceRowsAndCaptureSnapshot([concurrent], []);

        Assert.ThrowsException<InvalidOperationException>(() => owner.ApplyInstalledTargetUpsert(request));
        Assert.AreSame(concurrent, storageRowsOwner.BmsRows.Single());
    }

    [TestMethod]
    public void ApplyInstalledTargetUpsert_DatabaseFailureLeavesLiveRowsUnchanged()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogInstalledTargetFailure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        try
        {
            var original = CreateBms("original.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var replacement = CreateBms("replacement.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var storageRowsOwner = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([original], []);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows([original], []),
                initialRows.BmsRowsVersion,
                initialRows.BmsonRowsVersion));
            var owner = new CatalogMutationOwner(
                storageRowsOwner,
                ownedCollectionOwner,
                new BmsLibraryDbGateway(tempRootPath));

            Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyInstalledTargetUpsert([replacement], []));

            Assert.AreEqual(initialRows.BmsRowsVersion, storageRowsOwner.BmsRowsVersion);
            Assert.AreSame(original, storageRowsOwner.BmsRows.Single());
            Assert.IsTrue(ownedCollectionOwner.Collection.CreateStorageOwnerView().ContainsOwnerPath(original.path));
            Assert.IsFalse(ownedCollectionOwner.Collection.CreateStorageOwnerView().ContainsOwnerPath(replacement.path));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyInstalledTargetUpsert_EmptyRequestIsNoOp()
    {
        var storageRowsOwner = new CatalogStorageRowsOwner();
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, null);

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
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, null);
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
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, null);
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
        var owner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, null);
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
    public void ApplyCatalogSongCommands_PersistModeAndPlaylistLevelRows()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogSongCommands_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            string songPath = Path.Combine("C:\\Library", "command.bms");
            string[] rawValues = new string[29];
            rawValues[0] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            rawValues[1] = "command.bms";
            rawValues[7] = songPath;
            rawValues[14] = "4";
            rawValues[18] = "7";
            BMSFile song = BMSFile.FromSongTableRawValues(rawValues);
            var owner = new CatalogMutationOwner(
                new CatalogStorageRowsOwner(),
                new CatalogOwnedCollectionOwner(),
                new BmsLibraryDbGateway(songDbPath));

            owner.ApplyModeChangeSongRows([song]);
            owner.ApplyPlaylistLevelRows([song]);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(7, verify.ExecuteScalar<int>("SELECT mode FROM song WHERE path = ?;", songPath));
            Assert.AreEqual(4, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", songPath));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyPlaylistLevelRows_RethrowsAndPublishesFailureFact()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_CatalogSongCommandFailure_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            string songPath = Path.Combine("C:\\Library", "command-failure.bms");
            string[] rawValues = new string[29];
            rawValues[0] = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            rawValues[1] = "command-failure.bms";
            rawValues[7] = songPath;
            rawValues[14] = "4";
            rawValues[18] = "7";
            BMSFile song = BMSFile.FromSongTableRawValues(rawValues);
            var failureFacts = new System.Collections.Generic.List<CatalogWriteFailureFact>();
            var owner = new CatalogMutationOwner(
                new CatalogStorageRowsOwner(),
                new CatalogOwnedCollectionOwner(),
                new BmsLibraryDbGateway(songDbPath));
            owner.CatalogWriteFailurePublished += (sender, fact) => failureFacts.Add(fact);
            owner.ApplyModeChangeSongRows([song]);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.Execute("CREATE TRIGGER catalog_song_command_failure BEFORE UPDATE OF level ON song BEGIN SELECT RAISE(ABORT, 'forced level failure'); END;");
            }

            Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyPlaylistLevelRows([song]));
            Assert.AreEqual(1, failureFacts.Count);
            Assert.AreEqual("lr2_song_db_playlist_level_update_failed", failureFacts[0].Stage);
            Assert.IsFalse(string.IsNullOrWhiteSpace(failureFacts[0].DisplayedMessage));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
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
    public void ChartInfoStorageWriteRequest_SnapshotsStorageRows()
    {
        TestableBmsFile bms = CreateBms("storage-snapshot.bms", new string('a', 32));
        bms.level = 4;
        LR2SongDBExtended.bmson_song bmson = CreateBmson("storage-snapshot.bmson", new string('b', 32));
        bmson.title = "original";
        var chartInfo = new LR2SongDBExtended.chart_info
        {
            md5 = bms.hash,
            level = 12,
            difficulty = 4,
            maxbpm = 180.9,
            notes = 1234
        };
        Lr2ChartInfoSongProjection projection =
            Lr2ChartInfoSongProjection.Create(bms.path, bms.hash, chartInfo);
        var request = new CatalogChartInfoStorageWriteRequest(
            [bms],
            [bmson],
            new CatalogChartInfoWriteRequest(),
            [projection]);

        bms.path = "C:\\Library\\changed.bms";
        bms.level = 99;
        bmson.path = "C:\\Library\\changed.bmson";
        bmson.title = "changed";
        chartInfo.level = 99;
        chartInfo.difficulty = -1;

        Assert.AreEqual("C:\\Library\\storage-snapshot.bms", request.BmsRows.Single().path);
        Assert.AreEqual(4, request.BmsRows.Single().level);
        Assert.AreEqual("C:\\Library\\storage-snapshot.bmson", request.BmsonRows.Single().path);
        Assert.AreEqual("original", request.BmsonRows.Single().title);
        Assert.AreEqual(12, request.ChartInfoSongProjections.Single().Level);
        Assert.AreEqual(4, request.ChartInfoSongProjections.Single().Difficulty);
    }

    [TestMethod]
    public void ApplyChartInfoStorageWrite_WhenChartInfoInsertFailsRollsBackGeneratedSongRowsAndFacts()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoStorageRollback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            TestableBmsFile stored = CreateBms("atomic.bms", new string('c', 32));
            stored.path = Path.Combine(tempRootPath, "atomic.bms");
            stored.level = 2;
            stored.favorite = 7;
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
                setup.InsertOrReplace(stored.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                setup.Execute("CREATE TRIGGER fail_chart_info_storage BEFORE INSERT ON chart_info BEGIN SELECT RAISE(ABORT, 'forced chart-info failure'); END;");
            }
            TestableBmsFile generated = CreateBms("atomic.bms", stored.hash);
            generated.path = stored.path;
            generated.level = 12;
            var chartInfo = new LR2SongDBExtended.chart_info
            {
                sha256 = new string('d', 64),
                md5 = stored.hash,
                level = 12,
                parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                updated_at = DateTime.UtcNow
            };
            var owner = new CatalogMutationOwner(
                new CatalogStorageRowsOwner(),
                new CatalogOwnedCollectionOwner(),
                new BmsLibraryDbGateway(songDbPath));
            var request = new CatalogChartInfoStorageWriteRequest(
                [generated],
                [],
                new CatalogChartInfoWriteRequest(
                    [new ChartDigestBackfillEntry(stored.hash, chartInfo.sha256)],
                    [chartInfo]));

            Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyChartInfoStorageWrite(request));

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", stored.path).Single();
            Assert.AreEqual(2, song.level);
            Assert.AreEqual(7, song.favorite);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyChartInfoStorageWrite_WhenChartInfoInsertFailsRollsBackNarrowSongProjection()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoProjectionRollback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            TestableBmsFile stored = CreateBms("projection-atomic.bms", new string('c', 32));
            stored.path = Path.Combine(tempRootPath, "projection-atomic.bms");
            stored.level = 2;
            stored.mode = 11;
            stored.favorite = 7;
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
                setup.InsertOrReplace(stored.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                setup.Execute("CREATE TRIGGER fail_chart_info_projection BEFORE INSERT ON chart_info BEGIN SELECT RAISE(ABORT, 'forced chart-info failure'); END;");
            }
            var chartInfo = new LR2SongDBExtended.chart_info
            {
                sha256 = new string('d', 64),
                md5 = stored.hash,
                level = 12,
                difficulty = 4,
                mode = 14,
                parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                updated_at = DateTime.UtcNow
            };
            var owner = new CatalogMutationOwner(
                new CatalogStorageRowsOwner(),
                new CatalogOwnedCollectionOwner(),
                new BmsLibraryDbGateway(songDbPath));
            var request = new CatalogChartInfoStorageWriteRequest(
                [],
                [],
                new CatalogChartInfoWriteRequest(
                    [new ChartDigestBackfillEntry(stored.hash, chartInfo.sha256)],
                    [chartInfo]),
                [Lr2ChartInfoSongProjection.Create(stored.path, stored.hash, chartInfo)]);

            Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyChartInfoStorageWrite(request));

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", stored.path).Single();
            Assert.AreEqual(2, song.level);
            Assert.AreEqual(11, song.mode);
            Assert.AreEqual(7, song.favorite);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_digest_map';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
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

    private static StorageWorkScenario RunStorageWorkScenario(int backgroundCount)
    {
        var workObserver = new RecordingCatalogStorageSequenceWorkObserver();
        var storageRowsOwner = new CatalogStorageRowsOwner(workObserver);
        var bmsRows = new List<BMSFile>(backgroundCount);
        var bmsonRows = new List<LR2SongDBExtended.bmson_song>(backgroundCount);
        for (int i = 0; i < backgroundCount; i++)
        {
            bmsRows.Add(CreateBms($"background-{i}.bms", i.ToString("x32")));
            bmsonRows.Add(CreateBmson($"background-{i}.bmson", i.ToString("x32")));
        }

        storageRowsOwner.ReplaceRowsAndCaptureSnapshot(bmsRows, bmsonRows);
        var materializedBmsRows = new BMSFile[storageRowsOwner.BmsRows.Count];
        ((ICollection<BMSFile>)storageRowsOwner.BmsRows).CopyTo(materializedBmsRows, 0);
        var materializedBmsonRows = new LR2SongDBExtended.bmson_song[storageRowsOwner.BmsonRows.Count];
        ((ICollection<LR2SongDBExtended.bmson_song>)storageRowsOwner.BmsonRows)
            .CopyTo(materializedBmsonRows, 0);
        _ = storageRowsOwner.BmsRows[0];
        _ = storageRowsOwner.BmsonRows[0];
        StorageWorkCounts coldMaterialization = workObserver.Capture();

        storageRowsOwner.ApplyInstalledTargets(ChartStorageTargetSet.FromRows(
            [],
            [CreateBmson($"cold-{backgroundCount}.bmson", $"{backgroundCount:x32}")]));
        StorageWorkCounts coldNormalization = workObserver.Capture();
        workObserver.Reset();

        CatalogStorageRowsSnapshot firstDeltaSnapshot = null!;
        BMSFile firstDeltaBms = null!;
        LR2SongDBExtended.bmson_song firstDeltaBmson = null!;
        for (int command = 0; command < 2; command++)
        {
            string suffix = $"{backgroundCount}";
            BMSFile deltaBms = CreateBms(
                $"delta-{suffix}.bms",
                $"{(backgroundCount + command + 1):x32}");
            LR2SongDBExtended.bmson_song deltaBmson = CreateBmson(
                $"delta-{suffix}.bmson",
                $"{(backgroundCount + command + 1):x32}");
            storageRowsOwner.ApplyInstalledTargets(ChartStorageTargetSet.FromRows(
                [deltaBms],
                [deltaBmson]));

            CatalogStorageRowsSnapshot captured = storageRowsOwner.CaptureSnapshot();
            _ = captured.BmsRows.Count;
            _ = captured.BmsonRows.Count;
            IReadOnlyList<BMSFile> bmsView = storageRowsOwner.GetBmsRowsReadOnly();
            IReadOnlyList<LR2SongDBExtended.bmson_song> bmsonView = storageRowsOwner.GetBmsonRowsReadOnly();
            _ = bmsView.Count;
            _ = bmsonView.Count;
            Assert.AreEqual(backgroundCount + 1, captured.BmsRows.Count);
            Assert.AreEqual(backgroundCount + 2, captured.BmsonRows.Count);
            Assert.AreEqual(backgroundCount + 1, bmsView.Count);
            Assert.AreEqual(backgroundCount + 2, bmsonView.Count);

            int bmsDeltaIndex = backgroundCount;
            int bmsonDeltaIndex = backgroundCount + 1;
            if (command == 0)
            {
                firstDeltaSnapshot = captured;
                firstDeltaBms = deltaBms;
                firstDeltaBmson = deltaBmson;
                Assert.AreSame(deltaBms, captured.BmsRows[bmsDeltaIndex]);
                Assert.AreSame(deltaBmson, captured.BmsonRows[bmsonDeltaIndex]);
                Assert.AreSame(deltaBms, bmsView[bmsDeltaIndex]);
                Assert.AreSame(deltaBmson, bmsonView[bmsonDeltaIndex]);
            }
            else
            {
                Assert.IsNotNull(firstDeltaSnapshot);
                Assert.AreSame(deltaBms, captured.BmsRows[bmsDeltaIndex]);
                Assert.AreSame(deltaBmson, captured.BmsonRows[bmsonDeltaIndex]);
                Assert.AreSame(firstDeltaBms, firstDeltaSnapshot.BmsRows[bmsDeltaIndex]);
                Assert.AreSame(firstDeltaBmson, firstDeltaSnapshot.BmsonRows[bmsonDeltaIndex]);
            }
        }

        StorageWorkCounts warm = workObserver.Capture();
        Assert.AreEqual(backgroundCount + 1, storageRowsOwner.BmsRows.Count);
        Assert.AreEqual(backgroundCount + 2, storageRowsOwner.BmsonRows.Count);
        Assert.IsTrue(coldNormalization.EnumerationCount > coldMaterialization.EnumerationCount);
        Assert.IsTrue(coldNormalization.MaterializationCount > coldMaterialization.MaterializationCount);
        return new StorageWorkScenario(
            coldMaterialization.EnumerationCount + coldNormalization.EnumerationCount,
            coldMaterialization.VisitedEntryCount + coldNormalization.VisitedEntryCount,
            coldMaterialization.MaterializationCount + coldNormalization.MaterializationCount,
            coldMaterialization.AccessCount + coldNormalization.AccessCount,
            warm.EnumerationCount,
            warm.VisitedEntryCount,
            warm.MaterializationCount,
            warm.AccessCount);
    }

    private static int CalculateWarmAccessUpperBound(int backgroundCount)
    {
        int target = backgroundCount + 3;
        int powerOfTwo = 1;
        int ceilingLog2 = 0;
        while (powerOfTwo < target)
        {
            powerOfTwo <<= 1;
            ceilingLog2++;
        }
        return 8 + (4 * (ceilingLog2 + 1));
    }

    private static bool IsCatalogResultStatement(string sql)
    {
        return sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && (ContainsCatalogTable(sql, "song")
                || ContainsCatalogTable(sql, "bmson_song")
                || ContainsCatalogTable(sql, "maintenance"));
    }

    private static bool IsPerHashOrphanQuery(SqliteStatementObservation.SqliteObservedStatement statement)
    {
        return statement.Sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            && statement.Sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase)
            && ContainsCatalogTable(statement.Sql, "song")
            && statement.Sql.Contains("hash", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCatalogTable(string sql, string tableName)
    {
        return sql.Contains("FROM " + tableName, StringComparison.OrdinalIgnoreCase)
            || sql.Contains("FROM \"" + tableName + "\"", StringComparison.OrdinalIgnoreCase);
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

    private sealed class RecordingCatalogStorageSequenceWorkObserver : ICatalogStorageSequenceWorkObserver
    {
        internal int AccessCount { get; private set; }

        internal int EnumerationCount { get; private set; }

        internal int VisitedEntryCount { get; private set; }

        internal int MaterializationCount { get; private set; }

        public void ObserveAccess() => AccessCount++;

        public void ObserveEnumeration() => EnumerationCount++;

        public void ObserveEntryVisit() => VisitedEntryCount++;

        public void ObserveMaterialization(int count) => MaterializationCount += count;

        internal StorageWorkCounts Capture()
        {
            return new StorageWorkCounts(
                EnumerationCount,
                VisitedEntryCount,
                MaterializationCount,
                AccessCount);
        }

        internal void Reset()
        {
            AccessCount = 0;
            EnumerationCount = 0;
            VisitedEntryCount = 0;
            MaterializationCount = 0;
        }
    }

    private readonly record struct StorageWorkCounts(
        int EnumerationCount,
        int VisitedEntryCount,
        int MaterializationCount,
        int AccessCount);

    private readonly record struct StorageWorkScenario(
        int ColdEnumerationCount,
        int ColdVisitedEntryCount,
        int ColdMaterializationCount,
        int ColdAccessCount,
        int WarmEnumerationCount,
        int WarmVisitedEntryCount,
        int WarmMaterializationCount,
        int WarmAccessCount);
}
