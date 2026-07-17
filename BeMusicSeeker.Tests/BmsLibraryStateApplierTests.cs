using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryStateApplierTests
{
    [TestMethod]
    public void ApplyPendingPackageMutationDelta_UpdatesPendingCollectionAndDeletesInstallRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedPackage = new ChartPackage
            {
                path = "C:\\Pending\\Removed",
                delete_parent = false
            };
            var remainingPackage = new ChartPackage
            {
                path = "C:\\Pending\\Remaining",
                delete_parent = false
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(removedPackage, typeof(LR2SongDBExtended.install));
            }

            List<BMSFile> libraryFiles = [];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([removedPackage, remainingPackage]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.ApplyPendingPackageMutationDelta(new PendingPackageMutationDelta
            {
                HasChanges = true,
                RemainingPackages = [remainingPackage],
                InstallPathsToDelete = [removedPackage.path]
            });

            Assert.AreEqual(1, pendingPackages.Count);
            Assert.AreSame(remainingPackage, pendingPackages.Single());
            Assert.AreEqual(1, callbacks.PendingPackagesSetCount);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDBExtended.install>();
            Assert.AreEqual(0, verifySongDb.Table<ChartPackage>().Count());
        });
    }

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
                var installedPackage = ChartPackageTestExtensions.CreatePackage([movedFile]);
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
                DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
                DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([installedPackage]);
                var callbacks = new TrackingCallbacks();
                BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
                var delta = new LibraryMutationDelta
                {
                    RaiseInstalledPackagesChanged = true
                };
                delta.FolderPathChanges.Add(new LibraryFolderPathChange
                {
                    OldFolderPath = oldDirectoryPath,
                    NewFolderPath = newDirectoryPath
                });
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                });
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSongs[0]),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });
                delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
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
                delta.UpdatedInstalledPackagePaths.Add(new LibraryInstalledPackagePathChange
                {
                    Package = installedPackage,
                    NewPath = newDirectoryPath
                });

                CatalogMutationReceipt relocationReceipt = ApplyCatalogRelocation(songDbPath, delta, callbacks);
                Assert.IsTrue(relocationReceipt.Applied);
                Assert.AreEqual(2, relocationReceipt.PathFacts.Count);
                ApplyCommittedMutation(applier, delta);

                Assert.AreEqual(newChartPath, movedFile.path);
                Assert.AreEqual(1, movedFile.txt);
                ChartFile appliedInstallDestinationChart = delta.CreateAppliedInstallDestinationChartSnapshots().Single();
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
                var delta = new LibraryMutationDelta();
                delta.FolderPathChanges.Add(new LibraryFolderPathChange
                {
                    OldFolderPath = oldDirectoryPath,
                    NewFolderPath = newDirectoryPath
                });
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(firstFile),
                    OldPath = oldFirstPath,
                    NewPath = newFirstPath
                });
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(secondFile),
                    OldPath = oldSecondPath,
                    NewPath = newSecondPath
                });
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSong),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });

                CatalogMutationReceipt result = ApplyCatalogRelocation(songDbPath, delta, callbacks);

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
                var delta = new LibraryMutationDelta();
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                });

                Assert.ThrowsException<SQLite.SQLiteException>(() => ApplyCatalogRelocation(songDbPath, delta, callbacks));

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
                var delta = new LibraryMutationDelta();
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                });

                ApplyCatalogRelocation(songDbPath, delta, callbacks);

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
                var delta = new LibraryMutationDelta();
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                });

                ApplyCatalogRelocation(songDbPath, delta, callbacks);

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
                var delta = new LibraryMutationDelta();
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(movedFile),
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                });

                ApplyCatalogRelocation(songDbPath, delta, callbacks);

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

    [TestMethod]
    public void ApplyLibraryMutationDelta_FullClearBuildsChartSnapshotWithoutMutatingBmsOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous");
            List<BMSFile> libraryFiles = [file];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
            var delta = new LibraryMutationDelta();
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: true),
                    @"C:\Deleted",
                    "Deleted title",
                    "Deleted artist",
                    [@"C:\Deleted", @"C:\Other"],
                    file.Warnings.ToStructuredList()),
                NewInstallDestination = null,
                ClearInstallDestinationState = true
            });

            ApplyCommittedMutation(applier, delta);

            Assert.IsTrue(file.Warnings.ToStructuredList().Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            ChartFile appliedChart = delta.CreateAppliedInstallDestinationChartSnapshots().Single();
            Assert.AreSame(file, appliedChart.GetBmsStorageOwner());
            Assert.AreEqual(string.Empty, appliedChart.InstallDestination);
            Assert.AreEqual(string.Empty, appliedChart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, appliedChart.InstallDestinationArtist);
            Assert.AreEqual(0, appliedChart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(appliedChart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_PathOnlyNullBuildsChartSnapshotWithoutMutatingBmsOwner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var file = new TestableBmsFile
            {
                path = @"C:\Library\chart.bms"
            };
            file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "ambiguous");
            List<BMSFile> libraryFiles = [file];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
            var delta = new LibraryMutationDelta();
            delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ChartFileProjection.WithPackageState(
                    ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: true),
                    @"C:\Installed",
                    "Candidate title",
                    "Candidate artist",
                    [@"C:\Installed", @"C:\Other"],
                    file.Warnings.ToStructuredList()),
                NewInstallDestination = null,
                ClearInstallDestinationState = false
            });

            ApplyCommittedMutation(applier, delta);

            Assert.IsTrue(file.Warnings.ToStructuredList().Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            ChartFile appliedChart = delta.CreateAppliedInstallDestinationChartSnapshots().Single();
            Assert.AreSame(file, appliedChart.GetBmsStorageOwner());
            Assert.AreEqual(string.Empty, appliedChart.InstallDestination);
            Assert.AreEqual("Candidate title", appliedChart.InstallDestinationTitle);
            Assert.AreEqual("Candidate artist", appliedChart.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { @"C:\Installed", @"C:\Other" }, appliedChart.InstallDestinationSuggestions.ToArray());
        });
    }

    [TestMethod]
    public void LibraryMutationDelta_CreateAppliedSnapshotsDoesNotRequireStateApplierWriteback()
    {
        var file = new TestableBmsFile
        {
            path = @"C:\Library\chart.bms"
        };
        file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var delta = new LibraryMutationDelta();
        delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
        {
            Chart = ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false),
                @"C:\Old",
                string.Empty,
                string.Empty,
                []),
            NewInstallDestination = @"C:\New"
        });

        ChartFile appliedChart = delta.CreateAppliedInstallDestinationChartSnapshots().Single();

        Assert.AreEqual(@"C:\New", appliedChart.InstallDestination);
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterPrunesInstalledPackagesWithoutStorageCollectionWriteback()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedFile = new TestableBmsFile
            {
                path = "C:\\Library\\remove.bms"
            };
            removedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var keptFile = new TestableBmsFile
            {
                path = "C:\\Library\\keep.bms"
            };
            keptFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var removedPackage = ChartPackageTestExtensions.CreatePackage([removedFile]);
            removedPackage.path = "C:\\Installed\\RemovePkg";
            removedPackage.delete_parent = false;
            var keptPackage = ChartPackageTestExtensions.CreatePackage([keptFile]);
            keptPackage.path = "C:\\Installed\\KeepPkg";
            keptPackage.delete_parent = false;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(keptFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = removedFile.path }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = keptFile.path }, typeof(LR2SongDBExtended.maintenance));
            }

            List<BMSFile> libraryFiles = [removedFile, keptFile];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([removedPackage, keptPackage]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterDelta([ChartFileProjection.FromBmsStorageOwnerIdentity(removedFile)]));

            Assert.AreEqual(2, libraryFiles.Count);
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(keptPackage, installedPackages.Single());
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_PathCleanupPrunesInstalledPackagesByPath()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var canonicalFile = new TestableBmsFile
            {
                path = "C:\\Library\\remove.bms"
            };
            canonicalFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var keptFile = new TestableBmsFile
            {
                path = "C:\\Library\\keep.bms"
            };
            keptFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var removedPackage = ChartPackageTestExtensions.CreatePackage([canonicalFile]);
            removedPackage.path = "C:\\Installed\\RemovePkg";
            removedPackage.delete_parent = false;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(canonicalFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(keptFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = canonicalFile.path }, typeof(LR2SongDBExtended.maintenance));
            }

            List<BMSFile> libraryFiles = [canonicalFile, keptFile];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([removedPackage]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, canonicalFile.path));
            ApplyCommittedMutation(applier, delta);

            Assert.AreEqual(2, libraryFiles.Count);
            Assert.AreEqual(0, installedPackages.Count);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_PathCleanupDoesNotPruneRelocatedDestination()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var relocatedFile = new TestableBmsFile
            {
                path = "C:\\Library\\new.bms"
            };
            relocatedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var relocatedPackage = ChartPackageTestExtensions.CreatePackage([relocatedFile]);
            relocatedPackage.path = "C:\\Installed\\RelocatedPkg";
            relocatedPackage.delete_parent = false;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(relocatedFile, typeof(LR2SongDB.song));
            }

            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([relocatedPackage]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(
                songDbPath,
                callbacks,
                () => pendingPackages,
                packages => pendingPackages = packages,
                () => installedPackages,
                packages => installedPackages = packages);
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(
                ChartFileKind.Bms,
                relocatedFile.path));

            ApplyCommittedMutation(
                applier,
                delta,
                [new CatalogRelocationPathFact(
                    ChartFileKind.Bms,
                    "C:\\Library\\old.bms",
                    relocatedFile.path)]);

            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(relocatedPackage, installedPackages.Single());
            Assert.AreEqual(0, callbacks.InstalledPackagesChangedCount);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterDoesNotMaterializeUnmatchedAdapterlessBmsonInstalledPackageEntry()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedFile = new TestableBmsFile
            {
                path = "C:\\Library\\remove.bms"
            };
            removedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            PackageChartEntry unmatchedBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\keep.bmson",
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }));
            var mixedPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(removedFile)), unmatchedBmsonEntry]);
            mixedPackage.path = "C:\\Installed\\MixedPkg";
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
            }

            List<BMSFile> libraryFiles = [removedFile];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [];
            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([mixedPackage]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterDelta([ChartFileProjection.FromBmsStorageOwnerIdentity(removedFile)]));

            Assert.AreEqual(1, libraryFiles.Count);
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(mixedPackage, installedPackages.Single());
            Assert.AreEqual(1, mixedPackage.ChartEntries.Count);
            Assert.AreEqual(unmatchedBmsonEntry.Chart.Path, mixedPackage.ChartEntries.Single().Chart.Path);
            Assert.IsNull(unmatchedBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterLeavesBmsonStorageToCatalogOwner()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\remove.bmson",
                folder = "C:\\Library"
            };
            var keptSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\keep.bmson",
                folder = "C:\\Library"
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(removedSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(keptSong, typeof(LR2SongDBExtended.bmson_song));
            }

            List<BMSFile> libraryFiles = [];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [removedSong, keptSong];
            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterDelta([ChartFileProjection.FromBmsonStorageOwnerIdentity(removedSong)]));

            Assert.AreEqual(2, bmsonSongs.Count);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterRemovesBmsonRowsFromInstalledPackages()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\remove.bmson",
                folder = "C:\\Library",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            };
            var keptSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\keep.bmson",
                folder = "C:\\Library",
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            };
            var removedPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(removedSong))]);
            removedPackage.path = "C:\\Installed\\RemovePkg";
            removedPackage.delete_parent = false;
            var keptPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(keptSong))]);
            keptPackage.path = "C:\\Installed\\KeepPkg";
            keptPackage.delete_parent = false;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(removedSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(keptSong, typeof(LR2SongDBExtended.bmson_song));
            }

            List<BMSFile> libraryFiles = [];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [removedSong, keptSong];
            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([removedPackage, keptPackage]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterDelta([ChartFileProjection.FromBmsonStorageOwnerIdentity(removedSong)]));

            Assert.AreEqual(2, bmsonSongs.Count);
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(keptPackage, installedPackages.Single());
            Assert.AreEqual(1, callbacks.InstalledPackagesSetCount);
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterRemovesAdapterlessBmsonInstalledPackageEntryWithoutMaterializing()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\remove.bmson",
                folder = "C:\\Library",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            };
            var keptSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\keep.bmson",
                folder = "C:\\Library",
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            };
            PackageChartEntry removedEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(removedSong));
            PackageChartEntry keptEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(keptSong));
            ChartPackage package = ChartPackage.FromChartEntries([removedEntry, keptEntry]);
            package.path = "C:\\Installed\\MixedPkg";
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(removedSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(keptSong, typeof(LR2SongDBExtended.bmson_song));
            }

            List<BMSFile> libraryFiles = [];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [removedSong, keptSong];
            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([package]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            ApplyCommittedMutation(
                applier,
                CreateUnregisterDelta([ChartFileProjection.FromBmsonStorageOwnerIdentity(removedSong)]));

            Assert.AreEqual(2, bmsonSongs.Count);
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(package, installedPackages.Single());
            Assert.AreEqual(1, package.ChartEntries.Count);
            Assert.AreEqual(keptSong.path, package.ChartEntries.Single().Chart.Path);
            Assert.IsNull(removedEntry.GetBmsOwnerForTest());
            Assert.IsNull(keptEntry.GetBmsOwnerForTest());
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterDoesNotWriteBmsonRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var removedSong = new LR2SongDBExtended.bmson_song
            {
                path = "C:\\Library\\remove.bmson",
                folder = "C:\\Library"
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                songDb.InsertOrReplace(removedSong, typeof(LR2SongDBExtended.bmson_song));
            }

            List<BMSFile> libraryFiles = [];
            List<LR2SongDBExtended.bmson_song> bmsonSongs = [removedSong];
            DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
            DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([]);
            var callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removedSong));

            ApplyCommittedMutation(applier, delta);

            Assert.AreEqual(1, bmsonSongs.Count);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_MoveAndRemoveSameOwnerPrunesRelocatedBmsAndBmsonPackages()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplierMoveRemove_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRootPath);
            string oldBmsPath = Path.Combine(tempRootPath, "old.bms");
            string newBmsPath = Path.Combine(tempRootPath, "new.bms");
            string oldBmsonPath = Path.Combine(tempRootPath, "old.bmson");
            string newBmsonPath = Path.Combine(tempRootPath, "new.bmson");
            try
            {
                File.WriteAllText(oldBmsPath, "#PLAYER 1");
                File.WriteAllText(newBmsPath, "#PLAYER 1");
                File.WriteAllText(oldBmsonPath, "{}");
                File.WriteAllText(newBmsonPath, "{}");
                var bmsFile = new TestableBmsFile { path = oldBmsPath };
                bmsFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                var bmsonSong = new LR2SongDBExtended.bmson_song
                {
                    path = oldBmsonPath,
                    folder = tempRootPath,
                    md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                };
                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.bmson_song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.InsertOrReplace(bmsFile, typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(bmsonSong, typeof(LR2SongDBExtended.bmson_song));
                }

                ChartPackage bmsPackage = ChartPackageTestExtensions.CreatePackage(bmsFile);
                ChartPackage bmsonPackage = ChartPackage.FromChartEntries(
                [
                    PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong))
                ]);
                DispatcherCollection<ChartPackage> pendingPackages = CreatePackageCollection([]);
                DispatcherCollection<ChartPackage> installedPackages = CreatePackageCollection([bmsPackage, bmsonPackage]);
                var callbacks = new TrackingCallbacks();
                BmsLibraryStateApplier applier = CreateStateApplier(
                    songDbPath,
                    callbacks,
                    () => pendingPackages,
                    packages => pendingPackages = packages,
                    () => installedPackages,
                    packages => installedPackages = packages);

                var delta = new LibraryMutationDelta();
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(bmsFile),
                    OldPath = oldBmsPath,
                    NewPath = newBmsPath
                });
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSong),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });
                delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsFile));
                delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsonSong));

                var catalogOwner = new CatalogMutationOwner(
                    new CatalogStorageRowsOwner(),
                    new CatalogOwnedCollectionOwner(),
                    new BmsLibraryDbGateway(songDbPath));
                CatalogMutationReceipt receipt = catalogOwner.ApplyCatalogMutation(
                    delta,
                    delta.ChartRemoveRequests);

                Assert.IsTrue(receipt.Applied);
                Assert.AreEqual(2, receipt.RemovedCharts.Count);
                Assert.AreEqual(2, receipt.MovedCharts.Count);
                Assert.AreEqual(
                    bmsonSong.sha256,
                    receipt.RemovedCharts.Single(fact => fact.Kind == ChartFileKind.Bmson).Sha256);

                applier.ApplyLibraryMutationDelta(
                    delta,
                    receipt.RemovedCharts,
                    receipt.PathFacts);

                Assert.AreEqual(0, installedPackages.Count);
                Assert.AreEqual(2, callbacks.InstalledPackagesChangedCount);
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

    private static BmsLibraryStateApplier CreateStateApplier(
        string songDbPath,
        TrackingCallbacks callbacks,
        Func<DispatcherCollection<ChartPackage>> getPendingPackages,
        Action<DispatcherCollection<ChartPackage>> setPendingPackages,
        Func<DispatcherCollection<ChartPackage>> getInstalledPackages,
        Action<DispatcherCollection<ChartPackage>> setInstalledPackages)
    {
        return new BmsLibraryStateApplier(
            new BmsLibraryDbGateway(songDbPath),
            getPendingPackages,
            delegate (DispatcherCollection<ChartPackage> packages)
            {
                callbacks.PendingPackagesSetCount++;
                setPendingPackages(packages);
            },
            getInstalledPackages,
            delegate (DispatcherCollection<ChartPackage> packages)
            {
                callbacks.InstalledPackagesSetCount++;
                setInstalledPackages(packages);
            },
            () => callbacks.InstalledPackagesChangedCount++);
    }

    private static CatalogMutationReceipt ApplyCatalogRelocation(
        string songDbPath,
        LibraryMutationDelta delta,
        TrackingCallbacks callbacks)
    {
        var owner = new CatalogMutationOwner(
            new CatalogStorageRowsOwner(),
            new CatalogOwnedCollectionOwner(),
            new BmsLibraryDbGateway(songDbPath),
            delegate (string stage, Exception ex)
            {
                callbacks.SongDbWriteFailureCount++;
                callbacks.LastSongDbWriteFailureStage = stage;
                callbacks.LastSongDbWriteFailure = ex;
            });
        return owner.ApplyCatalogMutation(delta, []);
    }

    private static DispatcherCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>([.. (packages ?? [])]), Dispatcher.CurrentDispatcher);
    }

    private static LibraryMutationDelta CreateUnregisterDelta(IEnumerable<ChartFile> charts)
    {
        var delta = new LibraryMutationDelta();
        delta.ChartRemoveRequests.AddRange((charts ?? [])
            .Select(OwnedChartRemoveRequest.FromOwnerReferenceChart)
            .Where(request => request != null));
        return delta;
    }

    private static void ApplyCommittedMutation(
        BmsLibraryStateApplier applier,
        LibraryMutationDelta delta,
        IEnumerable<CatalogRelocationPathFact> protectedPathFacts = null!)
    {
        applier.ApplyLibraryMutationDelta(
            delta,
            CatalogChartMutationFact.CreateRemovalFacts(delta?.ChartRemoveRequests),
            protectedPathFacts ?? []);
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed class TrackingCallbacks
    {
        public int PendingPackagesSetCount { get; set; }

        public int InstalledPackagesSetCount { get; set; }

        public int InstalledPackagesChangedCount { get; set; }

        public int SongDbWriteFailureCount { get; set; }

        public string LastSongDbWriteFailureStage { get; set; } = string.Empty;

        public Exception LastSongDbWriteFailure { get; set; } = null!;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }
    }
}
