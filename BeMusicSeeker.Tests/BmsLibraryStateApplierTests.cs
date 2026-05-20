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
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => bmsonSongs, songs => bmsonSongs = songs, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

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
    public void ApplyLibraryMutationDelta_UpdatesPathsAndRaisesInvalidations()
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
                    path = Path.Combine(tempRootPath, "pending_chart.bms"),
                    instl_dst = oldDirectoryPath
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
                        parent = "e2977170",
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
                BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => bmsonSongs, songs => bmsonSongs = songs, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
                var delta = new LibraryMutationDelta
                {
                    RaiseBmsFilesChanged = true,
                    RaiseInstalledPackagesChanged = true,
                    InvalidateInstalledDirectoryIndex = true,
                    InvalidateParentFolderCache = true,
                    ClearDuplicatedCache = true
                };
                delta.FolderPathChanges.Add(new LibraryFolderPathChange
                {
                    OldFolderPath = oldDirectoryPath,
                    NewFolderPath = newDirectoryPath
                });
                delta.FilePathChanges.Add(new LibraryFilePathChange
                {
                    File = movedFile,
                    OldPath = oldChartPath,
                    NewPath = newChartPath
                });
                delta.BmsonSongPathChanges.Add(new LibraryBmsonSongPathChange
                {
                    Song = bmsonSongs[0],
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });
                delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    File = installLinkedFile,
                    NewInstallDestination = newDirectoryPath
                });
                delta.UpdatedInstalledPackagePaths.Add(new LibraryInstalledPackagePathChange
                {
                    Package = installedPackage,
                    NewPath = newDirectoryPath
                });

                applier.ApplyLibraryMutationDelta(delta);

                Assert.AreEqual(newChartPath, movedFile.path);
                Assert.AreEqual(newDirectoryPath, installLinkedFile.instl_dst);
                Assert.AreEqual(newDirectoryPath, installedPackage.path);
                Assert.IsTrue(callbacks.InstalledDirectoryInvalidationCount >= 1);
                Assert.IsTrue(callbacks.ParentFolderInvalidationCount >= 1);
                Assert.AreEqual(1, callbacks.ClearDuplicatedCount);
                Assert.AreEqual(1, callbacks.BmsFilesChangedCount);
                Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
                Assert.AreEqual(0, callbacks.BmsonSongsSetCount);
                Assert.AreEqual(newBmsonPath, bmsonSongs[0].path);
                using var verifySongDb = new LR2SongDBExtended(songDbPath);
                verifySongDb.CreateTable<LR2SongDB.song>();
                verifySongDb.CreateTable<LR2SongDB.folder>();
                verifySongDb.CreateTable<LR2SongDBExtended.bmson_song>();
                Assert.IsTrue(verifySongDb.Table<BMSFile>().Any(file => file.path == newChartPath));
                Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(file => file.path == oldChartPath));
                Assert.IsTrue(verifySongDb.Table<LR2SongDB.folder>().Any(folder => folder.path == newDirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar));
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
    public void UnregisterBmsFiles_RemovesSongsAndPrunesInstalledPackages()
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
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => bmsonSongs, songs => bmsonSongs = songs, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.UnregisterBmsFiles([removedFile]);

            Assert.AreEqual(1, libraryFiles.Count);
            Assert.AreSame(keptFile, libraryFiles.Single());
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(keptPackage, installedPackages.Single());
            Assert.AreEqual(1, callbacks.BmsFilesSetCount);
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDB.song>();
            verifySongDb.CreateTable<LR2SongDBExtended.maintenance>();
            Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(file => file.path == removedFile.path));
            Assert.IsTrue(verifySongDb.Table<BMSFile>().Any(file => file.path == keptFile.path));
            Assert.IsFalse(verifySongDb.Table<BMSFileMaintenanceInfo>().Any(info => info.path == removedFile.path));
        });
    }

    [TestMethod]
    public void UnregisterBmsFiles_RemovesLibraryRowsByPathWhenReferenceDiffers()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var canonicalFile = new TestableBmsFile
            {
                path = "C:\\Library\\remove.bms"
            };
            canonicalFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var removedFileReference = new TestableBmsFile
            {
                path = canonicalFile.path
            };
            removedFileReference.SetHash(canonicalFile.hash);
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
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => bmsonSongs, songs => bmsonSongs = songs, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.UnregisterBmsFiles([removedFileReference]);

            Assert.AreEqual(1, libraryFiles.Count);
            Assert.AreSame(keptFile, libraryFiles.Single());
            Assert.AreEqual(0, installedPackages.Count);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDB.song>();
            verifySongDb.CreateTable<LR2SongDBExtended.maintenance>();
            Assert.IsFalse(verifySongDb.Table<BMSFile>().Any(file => file.path == canonicalFile.path));
            Assert.IsFalse(verifySongDb.Table<BMSFileMaintenanceInfo>().Any(info => info.path == canonicalFile.path));
        });
    }

    [TestMethod]
    public void UnregisterBmsFiles_DoesNotMaterializeUnmatchedAdapterlessBmsonInstalledPackageEntry()
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
            var mixedPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChartAdapter(removedFile), unmatchedBmsonEntry]);
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
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => bmsonSongs, songs => bmsonSongs = songs, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.UnregisterBmsFiles([removedFile]);

            Assert.AreEqual(0, libraryFiles.Count);
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(mixedPackage, installedPackages.Single());
            Assert.AreEqual(1, mixedPackage.ChartEntries.Count);
            Assert.AreEqual(unmatchedBmsonEntry.Chart.Path, mixedPackage.ChartEntries.Single().Chart.Path);
            Assert.IsNull(unmatchedBmsonEntry.CompatibilityAdapter);
        });
    }

    [TestMethod]
    public void UnregisterBmsonSongs_RemovesSongsFromCollectionAndDatabase()
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
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => bmsonSongs, songs => bmsonSongs = songs, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.UnregisterBmsonSongs([removedSong]);

            Assert.AreEqual(1, bmsonSongs.Count);
            Assert.AreSame(keptSong, bmsonSongs.Single());
            Assert.AreEqual(1, callbacks.BmsonSongsSetCount);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDBExtended.bmson_song>();
            Assert.IsFalse(verifySongDb.Table<LR2SongDBExtended.bmson_song>().Any(song => song.path == removedSong.path));
            Assert.IsTrue(verifySongDb.Table<LR2SongDBExtended.bmson_song>().Any(song => song.path == keptSong.path));
            Assert.AreEqual(1, callbacks.InstalledDirectoryInvalidationCount);
            Assert.AreEqual(1, callbacks.ParentFolderInvalidationCount);
            Assert.AreEqual(1, callbacks.ClearDuplicatedCount);
        });
    }

    [TestMethod]
    public void UnregisterBmsonSongs_RemovesRowsFromInstalledPackages()
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
            var removedEntry = PendingChartEntry.CreateFromBmsonSong(removedSong);
            var keptEntry = PendingChartEntry.CreateFromBmsonSong(keptSong);
            var removedPackage = ChartPackageTestExtensions.CreatePackage([removedEntry]);
            removedPackage.path = "C:\\Installed\\RemovePkg";
            removedPackage.delete_parent = false;
            var keptPackage = ChartPackageTestExtensions.CreatePackage([keptEntry]);
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
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => bmsonSongs, songs => bmsonSongs = songs, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.UnregisterBmsonSongs([removedSong]);

            Assert.AreEqual(1, bmsonSongs.Count);
            Assert.AreSame(keptSong, bmsonSongs.Single());
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(keptPackage, installedPackages.Single());
            Assert.AreEqual(1, callbacks.BmsonSongsSetCount);
            Assert.AreEqual(1, callbacks.InstalledPackagesSetCount);
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
        });
    }

    [TestMethod]
    public void UnregisterBmsonSongs_RemovesAdapterlessInstalledPackageEntryWithoutMaterializing()
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
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => bmsonSongs, songs => bmsonSongs = songs, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.UnregisterBmsonSongs([removedSong]);

            Assert.AreEqual(1, bmsonSongs.Count);
            Assert.AreSame(keptSong, bmsonSongs.Single());
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(package, installedPackages.Single());
            Assert.AreEqual(1, package.ChartEntries.Count);
            Assert.AreEqual(keptSong.path, package.ChartEntries.Single().Chart.Path);
            Assert.IsNull(removedEntry.CompatibilityAdapter);
            Assert.IsNull(keptEntry.CompatibilityAdapter);
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregistersBmsonSongs()
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
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => bmsonSongs, songs => bmsonSongs = songs, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
            var delta = new LibraryMutationDelta
            {
                InvalidateInstalledDirectoryIndex = true,
                InvalidateParentFolderCache = true,
                ClearDuplicatedCache = true
            };
            delta.BmsonSongsToUnregister.Add(removedSong);

            applier.ApplyLibraryMutationDelta(delta);

            Assert.AreEqual(0, bmsonSongs.Count);
            Assert.AreEqual(1, callbacks.BmsonSongsSetCount);
            Assert.IsTrue(callbacks.InstalledDirectoryInvalidationCount >= 1);
            Assert.IsTrue(callbacks.ParentFolderInvalidationCount >= 1);
            Assert.IsTrue(callbacks.ClearDuplicatedCount >= 1);
            using var verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDBExtended.bmson_song>();
            Assert.IsFalse(verifySongDb.Table<LR2SongDBExtended.bmson_song>().Any(song => song.path == removedSong.path));
        });
    }

    private static BmsLibraryStateApplier CreateStateApplier(
        string songDbPath,
        TrackingCallbacks callbacks,
        Func<List<BMSFile>> getBmsFiles,
        Action<List<BMSFile>> setBmsFiles,
        Func<List<LR2SongDBExtended.bmson_song>> getBmsonSongs,
        Action<List<LR2SongDBExtended.bmson_song>> setBmsonSongs,
        Func<DispatcherCollection<ChartPackage>> getPendingPackages,
        Action<DispatcherCollection<ChartPackage>> setPendingPackages,
        Func<DispatcherCollection<ChartPackage>> getInstalledPackages,
        Action<DispatcherCollection<ChartPackage>> setInstalledPackages)
    {
        return new BmsLibraryStateApplier(
            new BmsLibraryDbGateway(songDbPath),
            getBmsFiles,
            delegate (List<BMSFile> files)
            {
                callbacks.BmsFilesSetCount++;
                setBmsFiles(files);
            },
            getBmsonSongs,
            delegate (List<LR2SongDBExtended.bmson_song> songs)
            {
                callbacks.BmsonSongsSetCount++;
                setBmsonSongs(songs);
            },
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
            () => callbacks.InstalledDirectoryInvalidationCount++,
            () => callbacks.ParentFolderInvalidationCount++,
            () => callbacks.ClearDuplicatedCount++,
            () => callbacks.BmsFilesChangedCount++,
            () => callbacks.InstalledPackagesChangedCount++);
    }

    private static DispatcherCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>([.. (packages ?? [])]), Dispatcher.CurrentDispatcher);
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
        public int BmsFilesSetCount { get; set; }

        public int PendingPackagesSetCount { get; set; }

        public int InstalledPackagesSetCount { get; set; }

        public int BmsonSongsSetCount { get; set; }

        public int InstalledDirectoryInvalidationCount { get; set; }

        public int ParentFolderInvalidationCount { get; set; }

        public int ClearDuplicatedCount { get; set; }

        public int BmsFilesChangedCount { get; set; }

        public int InstalledPackagesChangedCount { get; set; }
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }
    }
}
