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
            Assert.AreEqual(0, receipt.StorageRowsVersion.PreviousBmsRowsVersion);
            Assert.AreEqual(1, receipt.StorageRowsVersion.BmsRowsVersion);
            Assert.AreEqual(0, receipt.StorageRowsVersion.PreviousBmsonRowsVersion);
            Assert.AreEqual(1, receipt.StorageRowsVersion.BmsonRowsVersion);
            Assert.AreEqual(newBmsPath, bms.path);
            Assert.AreEqual(newBmsonPath, bmson.path);
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
        Assert.AreEqual(ownedCollectionOwner.CollectionVersion, receipt.OwnedCollectionVersion);
    }

    [TestMethod]
    public void Apply_WithoutDbDiffDoesNotChangeRowsOrEmitMutationFacts()
    {
        var bms = CreateBms("current.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var storageRowsOwner = new CatalogStorageRowsOwner();
        CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([bms], []);
        var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
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
