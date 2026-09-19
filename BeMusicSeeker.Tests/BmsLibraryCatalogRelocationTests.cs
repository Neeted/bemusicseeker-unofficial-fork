using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryStateApplierTestSupport;
using PackageStateMutationApplier = BeMusicSeeker.Models.BmsLibraryInternal.PackageLifecycleOwner.PackageStateMutationApplier;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryCatalogRelocationTests
{
    [TestMethod]
    public void ApplyCatalogRelocation_UpdatesStorageRowsAndInstalledPackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplier_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "OldFolder");
            string newDirectoryPath = Path.Combine(tempRootPath, "NewFolder");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldChartPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newChartPath = Path.Combine(newDirectoryPath, "chart.bms");
            string oldBmsonPath = Path.Combine(oldDirectoryPath, "chart.bmson");
            string newBmsonPath = Path.Combine(newDirectoryPath, "chart.bmson");
            File.WriteAllText(oldChartPath, "#PLAYER 1");
            File.WriteAllText(newChartPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(newDirectoryPath, "readme.txt"), "text group");
            File.WriteAllText(oldBmsonPath, "{}");
            File.WriteAllText(newBmsonPath, "{}");
            try
            {
                var movedFile = new TestableBmsFile
                {
                    path = newChartPath
                };
                movedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                var installLinkedFile = new TestableBmsFile
                {
                    path = Path.Combine(tempRootPath, "pending_chart.bms")
                };
                ChartPackage installedPackage = ChartPackageTestExtensions.CreatePackage([movedFile]);
                installedPackage.path = oldDirectoryPath;
                installedPackage.delete_parent = false;
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDB.folder>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                    var oldRow = new TestableBmsFile
                    {
                        path = oldChartPath
                    };
                    oldRow.SetHash(movedFile.hash);
                    songDb.InsertOrReplace(oldRow, typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = oldDirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        title = "OldFolder",
                        parent = "stale-parent",
                        type = 1
                    }, typeof(LR2SongDB.folder));
                    songDb.InsertOrReplace(new LR2SongDBExtended.bmson_song
                    {
                        path = oldBmsonPath,
                        folder = oldDirectoryPath
                    }, typeof(LR2SongDBExtended.bmson_song));
                }

                List<BMSFile> libraryFiles = [movedFile];
                List<LR2SongDBExtended.bmson_song> bmsonSongs =
                [
                    new LR2SongDBExtended.bmson_song
                    {
                        path = oldBmsonPath,
                        folder = oldDirectoryPath
                    }
                ];
                ObservableCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
                ObservableCollection<ChartPackage> installedPackages = CreatePackageCollection([installedPackage]);
                var callbacks = new TrackingCallbacks();
                PackageStateMutationApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
                var folderPathChanges = new List<LibraryFolderPathChange>();
                folderPathChanges.Add(new LibraryFolderPathChange
                {
                    OldFolderPath = oldDirectoryPath,
                    NewFolderPath = newDirectoryPath
                });
                var pathChanges = new List<LibraryChartPathChange>();
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                });
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSongs[0]),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });
                var installDestinationChanges = new List<LibraryInstallDestinationChange>();
                installDestinationChanges.Add(new LibraryInstallDestinationChange
                {
                    Chart = ChartFileProjection.WithPackageState(
                        ChartFileProjection.FromBmsFile(installLinkedFile, includeWarningSnapshot: false),
                        oldDirectoryPath,
                        "Old destination title",
                        "Old destination artist",
                        [Path.Combine(tempRootPath, "Candidate")],
                        []),
                    NewInstallDestination = newDirectoryPath
                });
                var packagePathChanges = new List<LibraryInstalledPackagePathChange>();
                packagePathChanges.Add(new LibraryInstalledPackagePathChange
                {
                    Package = installedPackage,
                    NewPath = newDirectoryPath
                });

                LibraryCatalogMutationFacts catalogFacts = new([], pathChanges, folderPathChanges);
                LibraryPackageReferenceFacts packageFacts = new(installDestinationChanges, packagePathChanges);
                CatalogMutationReceipt relocationReceipt = ApplyCatalogRelocation(songDbPath, catalogFacts, callbacks);
                Assert.IsTrue(relocationReceipt.Applied);
                Assert.AreEqual(2, relocationReceipt.PathFacts.Count);
                ApplyCommittedMutation(applier, catalogFacts, packageFacts);

                Assert.AreEqual(newChartPath, movedFile.path);
                Assert.AreEqual(1, movedFile.txt);
                ChartFile appliedInstallDestinationChart = packageFacts.CreateAppliedInstallDestinationChartSnapshots().Single();
                Assert.AreSame(installLinkedFile, appliedInstallDestinationChart.GetBmsStorageOwner());
                Assert.AreEqual(newDirectoryPath, appliedInstallDestinationChart.InstallDestination);
                Assert.AreEqual("Old destination title", appliedInstallDestinationChart.InstallDestinationTitle);
                Assert.AreEqual("Old destination artist", appliedInstallDestinationChart.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { Path.Combine(tempRootPath, "Candidate") }, appliedInstallDestinationChart.InstallDestinationSuggestions.ToArray());
                Assert.AreEqual(newDirectoryPath, installedPackage.path);
                Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
                Assert.AreEqual(newBmsonPath, bmsonSongs[0].path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                verifySongDb.CreateTable<LR2SongDB.song>();
                verifySongDb.CreateTable<LR2SongDB.folder>();
                verifySongDb.CreateTable<LR2SongDBExtended.bmson_song>();
                Assert.IsTrue(verifySongDb.Table<BMSFile>().Any(file => file.path == newChartPath));
                Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", newChartPath));
                Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(file => file.path == oldChartPath));
                LR2SongDB.folder movedFolder = verifySongDb.Table<LR2SongDB.folder>().Single(folder => folder.path == newDirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(newDirectoryPath)), movedFolder.parent);
                Assert.IsTrue(verifySongDb.Table<LR2SongDBExtended.bmson_song>().Any(song => song.path == newBmsonPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void ApplyCatalogRelocation_BatchPathReplacePreservesUserColumnsMaintenanceDigestAndFolderMetadata()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplierBatch_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "OldFolder");
            string newDirectoryPath = Path.Combine(tempRootPath, "NewFolder");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldFirstPath = Path.Combine(oldDirectoryPath, "first.bms");
            string newFirstPath = Path.Combine(newDirectoryPath, "first.bms");
            string oldSecondPath = Path.Combine(oldDirectoryPath, "second.bms");
            string newSecondPath = Path.Combine(newDirectoryPath, "second.bms");
            string oldBmsonPath = Path.Combine(oldDirectoryPath, "chart.bmson");
            string newBmsonPath = Path.Combine(newDirectoryPath, "chart.bmson");
            File.WriteAllText(newFirstPath, "#PLAYER 1");
            File.WriteAllText(newSecondPath, "#PLAYER 1");
            File.WriteAllText(newBmsonPath, "{}");
            File.WriteAllText(Path.Combine(newDirectoryPath, "readme.txt"), "text group");
            try
            {
                var firstFile = new TestableBmsFile
                {
                    path = oldFirstPath
                };
                firstFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                firstFile.SetSha256("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                firstFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(firstFile)
                {
                    wav_files_existing = 7,
                    wav_files_defined = 9,
                    lr2_warning_flags = 11
                }, suppressPropertyChanged: true, origin: MaintenanceInfoOrigin.DbHydrated);
                var secondFile = new TestableBmsFile
                {
                    path = oldSecondPath
                };
                secondFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                secondFile.SetSha256("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                secondFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(secondFile), suppressPropertyChanged: true, origin: MaintenanceInfoOrigin.Placeholder);
                var bmsonSong = new LR2SongDBExtended.bmson_song
                {
                    path = oldBmsonPath,
                    folder = oldDirectoryPath,
                    title = "Bmson",
                    md5 = "cccccccccccccccccccccccccccccccc",
                    sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                    MaintenanceInfo = new BMSFileMaintenanceInfo
                    {
                        path = oldBmsonPath,
                        hash = "cccccccccccccccccccccccccccccccc",
                        wav_files_existing = 3
                    }
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDB.folder>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                    songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
                    songDb.InsertOrReplace(firstFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(secondFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    songDb.Execute("UPDATE song SET favorite = 1, adddate = 123, tag = 'favorite-tag' WHERE path = ?;", oldFirstPath);
                    songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = oldFirstPath, hash = firstFile.hash, wav_files_existing = 1 }, typeof(LR2SongDBExtended.maintenance));
                    songDb.InsertOrReplace(BMSFileMaintenanceInfo.CreateForBmson(oldBmsonPath, bmsonSong.md5), typeof(LR2SongDBExtended.maintenance));
                    songDb.InsertOrReplace(bmsonSong, typeof(LR2SongDBExtended.bmson_song));
                    songDb.InsertOrReplace(new LR2SongDB.folder
                    {
                        title = "OldFolder",
                        subtitle = "keep-subtitle",
                        category = "keep-category",
                        info_a = "keep-info-a",
                        info_b = "keep-info-b",
                        command = "keep-command",
                        path = oldDirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        type = 7,
                        banner = "keep-banner",
                        parent = "stale-parent",
                        date = 100,
                        max = 200,
                        adddate = 300
                    }, typeof(LR2SongDB.folder));
                    songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                    {
                        md5 = firstFile.hash,
                        sha256 = firstFile.sha256
                    }, typeof(LR2SongDBExtended.chart_digest_map));
                }

                var callbacks = new TrackingCallbacks();
                var folderPathChanges = new List<LibraryFolderPathChange>();
                folderPathChanges.Add(new LibraryFolderPathChange
                {
                    OldFolderPath = oldDirectoryPath,
                    NewFolderPath = newDirectoryPath
                });
                var pathChanges = new List<LibraryChartPathChange>();
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(firstFile),
                    OldPath = oldFirstPath,
                    NewPath = newFirstPath
                });
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(secondFile),
                    OldPath = oldSecondPath,
                    NewPath = newSecondPath
                });
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSong),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });

                CatalogMutationReceipt result = ApplyCatalogRelocation(
                    songDbPath,
                    new LibraryCatalogMutationFacts([], pathChanges, folderPathChanges),
                    callbacks);

                Assert.IsTrue(result.BmsPathDbMs >= 0);
                Assert.IsTrue(result.BmsonPathDbMs >= 0);
                Assert.AreEqual(3, result.PathFacts.Count);
                Assert.AreEqual(newFirstPath, firstFile.path);
                Assert.AreEqual(newSecondPath, secondFile.path);
                Assert.AreEqual(newBmsonPath, bmsonSong.path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                verifySongDb.CreateTable<LR2SongDB.song>();
                verifySongDb.CreateTable<LR2SongDB.folder>();
                verifySongDb.CreateTable<LR2SongDBExtended.maintenance>();
                verifySongDb.CreateTable<LR2SongDBExtended.bmson_song>();
                verifySongDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
                Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(file => file.path == oldFirstPath || file.path == oldSecondPath));
                Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", newFirstPath));
                Assert.AreEqual(123, verifySongDb.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?;", newFirstPath));
                Assert.AreEqual("favorite-tag", verifySongDb.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?;", newFirstPath));
                Assert.AreEqual(7, verifySongDb.ExecuteScalar<int>("SELECT wav_files_existing FROM maintenance WHERE path = ?;", newFirstPath));
                Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", newSecondPath));
                Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", firstFile.hash, firstFile.sha256));
                Assert.AreEqual(newDirectoryPath, verifySongDb.ExecuteScalar<string>("SELECT folder FROM bmson_song WHERE path = ?;", newBmsonPath));
                Assert.AreEqual(3, verifySongDb.ExecuteScalar<int>("SELECT wav_files_existing FROM maintenance WHERE path = ?;", newBmsonPath));
                LR2SongDB.folder movedFolder = verifySongDb.Table<LR2SongDB.folder>().Single(folder => folder.path == newDirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                Assert.AreEqual("NewFolder", movedFolder.title);
                Assert.AreEqual("keep-subtitle", movedFolder.subtitle);
                Assert.AreEqual("keep-category", movedFolder.category);
                Assert.AreEqual("keep-info-a", movedFolder.info_a);
                Assert.AreEqual("keep-info-b", movedFolder.info_b);
                Assert.AreEqual("keep-command", movedFolder.command);
                Assert.AreEqual(7, movedFolder.type);
                Assert.AreEqual("keep-banner", movedFolder.banner);
                Assert.AreEqual(100, movedFolder.date);
                Assert.AreEqual(200, movedFolder.max);
                Assert.AreEqual(300, movedFolder.adddate);
                Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(newDirectoryPath)), movedFolder.parent);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void ApplyCatalogRelocation_WhenBatchDbFails_DoesNotMutateLivePath()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplierRollback_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "OldFolder");
            string newDirectoryPath = Path.Combine(tempRootPath, "NewFolder");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldChartPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newChartPath = Path.Combine(newDirectoryPath, "chart.bms");
            File.WriteAllText(newChartPath, "#PLAYER 1");
            try
            {
                var movedFile = new TestableBmsFile
                {
                    path = oldChartPath
                };
                movedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.InsertOrReplace(movedFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                    string escapedNewChartPath = newChartPath.Replace("'", "''");
                    songDb.Execute("CREATE TRIGGER fail_song_insert BEFORE INSERT ON song WHEN NEW.path = '" + escapedNewChartPath + "' BEGIN SELECT RAISE(ABORT, 'forced failure'); END;");
                }

                var callbacks = new TrackingCallbacks();
                LibraryCatalogMutationFacts catalogFacts = new(
                    [],
                    [new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                    }],
                    []);

                Assert.ThrowsException<SQLite.SQLiteException>(() => ApplyCatalogRelocation(songDbPath, catalogFacts, callbacks));

                Assert.AreEqual(oldChartPath, movedFile.path);
                Assert.AreEqual(1, callbacks.SongDbWriteFailureCount);
                Assert.AreEqual("lr2_song_db_library_mutation_path_replace_failed", callbacks.LastSongDbWriteFailureStage);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                verifySongDb.CreateTable<LR2SongDB.song>();
                Assert.AreEqual(1, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", oldChartPath));
                Assert.AreEqual(0, verifySongDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE path = ?;", newChartPath));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void ApplyCatalogRelocation_PathReplaceUsesSharedLr2CompatibilityNormalizer()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplier_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "OldFolder");
            string newDirectoryPath = Path.Combine(tempRootPath, "New😀Folder");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldChartPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newChartPath = Path.Combine(newDirectoryPath, "chart.bms");
            File.WriteAllText(oldChartPath, "#PLAYER 1");
            File.WriteAllText(newChartPath, "#PLAYER 1");
            try
            {
                var movedFile = new TestableBmsFile
                {
                    path = newChartPath
                };
                movedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    var oldRow = new TestableBmsFile
                    {
                        path = oldChartPath,
                        folder = "stale-folder",
                        parent = "stale-parent"
                    };
                    oldRow.SetHash(movedFile.hash);
                    songDb.InsertOrReplace(oldRow, typeof(LR2SongDB.song));
                }

                var callbacks = new TrackingCallbacks();
                LibraryCatalogMutationFacts catalogFacts = new(
                    [],
                    [new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                    }],
                    []);

                ApplyCatalogRelocation(songDbPath, catalogFacts, callbacks);

                Assert.IsTrue(string.IsNullOrWhiteSpace(movedFile.folder));
                Assert.IsTrue(string.IsNullOrWhiteSpace(movedFile.parent));
                Assert.IsTrue(movedFile.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                verifySongDb.CreateTable<LR2SongDB.song>();
                Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(file => file.path == oldChartPath));
                Assert.AreEqual(1L, verifySongDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", newChartPath));
                Assert.IsTrue(string.IsNullOrWhiteSpace(verifySongDb.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", newChartPath)));
                Assert.IsTrue(string.IsNullOrWhiteSpace(verifySongDb.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", newChartPath)));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void ApplyCatalogRelocation_ReevaluatesLr2CompatibilityFactsWithoutParentTraversal()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplier_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldChartPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newChartPath = Path.Combine(newDirectoryPath, "chart.bms");
            File.WriteAllText(newChartPath, "#PLAYER 1");
            try
            {
                var movedFile = new TestableBmsFile { path = newChartPath };
                movedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                movedFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(movedFile)
                {
                    hash = movedFile.hash,
                    path = oldChartPath,
                    lr2_warning_flags = (int)Lr2CompatibilityWarningFlags.None,
                    lr2_resource_max_relative_cp932_bytes = 240,
                    lr2_resource_has_parent_traversal = false
                }, suppressPropertyChanged: true, origin: MaintenanceInfoOrigin.DbHydrated);
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    BMSFile oldSongRow = movedFile.CreateSongRowPersistenceCopy();
                    oldSongRow.path = oldChartPath;
                    songDb.InsertOrReplace(oldSongRow, typeof(LR2SongDB.song));
                }

                var callbacks = new TrackingCallbacks();
                LibraryCatalogMutationFacts catalogFacts = new(
                    [],
                    [new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                    }],
                    []);

                ApplyCatalogRelocation(songDbPath, catalogFacts, callbacks);

                int flags = movedFile.maintenanceInfo.lr2_warning_flags.GetValueOrDefault();
                Assert.IsTrue((flags & (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0);
                Assert.IsFalse(movedFile.maintenanceInfo.lr2_resource_has_parent_traversal.GetValueOrDefault());
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                int persistedFlags = verifySongDb.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", newChartPath);
                Assert.IsTrue((persistedFlags & (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void ApplyCatalogRelocation_ReparsesLr2CompatibilityFactsWithParentTraversal()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplier_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldChartPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newChartPath = Path.Combine(newDirectoryPath, "chart.bms");
            string parentResourcePath = @"..\Shared\" + new string('a', 240) + ".wav";
            File.WriteAllText(newChartPath, "#PLAYER 1\r\n#WAV01 " + parentResourcePath + "\r\n");
            try
            {
                var movedFile = new TestableBmsFile { path = newChartPath };
                movedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                movedFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(movedFile)
                {
                    hash = movedFile.hash,
                    path = oldChartPath,
                    lr2_warning_flags = (int)Lr2CompatibilityWarningFlags.None,
                    lr2_resource_max_relative_cp932_bytes = 1,
                    lr2_resource_has_parent_traversal = true
                }, suppressPropertyChanged: true, origin: MaintenanceInfoOrigin.DbHydrated);
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    BMSFile oldSongRow = movedFile.CreateSongRowPersistenceCopy();
                    oldSongRow.path = oldChartPath;
                    songDb.InsertOrReplace(oldSongRow, typeof(LR2SongDB.song));
                }

                var callbacks = new TrackingCallbacks();
                LibraryCatalogMutationFacts catalogFacts = new(
                    [],
                    [new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                    }],
                    []);

                ApplyCatalogRelocation(songDbPath, catalogFacts, callbacks);

                int flags = movedFile.maintenanceInfo.lr2_warning_flags.GetValueOrDefault();
                Assert.IsTrue((flags & (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0);
                Assert.IsTrue(movedFile.maintenanceInfo.lr2_resource_has_parent_traversal.GetValueOrDefault());
                Assert.IsTrue(movedFile.maintenanceInfo.lr2_resource_max_relative_cp932_bytes > 1);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                int persistedFlags = verifySongDb.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", newChartPath);
                Assert.IsTrue((persistedFlags & (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }
}
