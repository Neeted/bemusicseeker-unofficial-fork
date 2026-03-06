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
            BMSPackage removedPackage = new BMSPackage
            {
                path = "C:\\Pending\\Removed",
                delete_parent = false
            };
            BMSPackage remainingPackage = new BMSPackage
            {
                path = "C:\\Pending\\Remaining",
                delete_parent = false
            };
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(removedPackage, typeof(LR2SongDBExtended.install));
            }

            List<BMSFile> libraryFiles = new List<BMSFile>();
            DispatcherCollection<BMSPackage> pendingPackages = CreatePackageCollection(new[] { removedPackage, remainingPackage });
            DispatcherCollection<BMSPackage> installedPackages = CreatePackageCollection(Array.Empty<BMSPackage>());
            TrackingCallbacks callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.ApplyPendingPackageMutationDelta(new PendingPackageMutationDelta
            {
                HasChanges = true,
                RemainingPackages = new List<BMSPackage> { remainingPackage },
                InstallPathsToDelete = new List<string> { removedPackage.path }
            });

            Assert.AreEqual(1, pendingPackages.Count);
            Assert.AreSame(remainingPackage, pendingPackages.Single());
            Assert.AreEqual(1, callbacks.PendingPackagesSetCount);
            using LR2SongDBExtended verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDBExtended.install>();
            Assert.AreEqual(0, verifySongDb.Table<BMSPackage>().Count());
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
            File.WriteAllText(oldChartPath, "#PLAYER 1");
            File.WriteAllText(newChartPath, "#PLAYER 1");
            try
            {
                TestableBmsFile movedFile = new TestableBmsFile
                {
                    path = newChartPath
                };
                movedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                TestableBmsFile installLinkedFile = new TestableBmsFile
                {
                    path = Path.Combine(tempRootPath, "pending_chart.bms"),
                    instl_dst = oldDirectoryPath
                };
                BMSPackage installedPackage = new BMSPackage(new BMSFile[] { movedFile })
                {
                    path = oldDirectoryPath,
                    delete_parent = false
                };
                using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDB.folder>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    TestableBmsFile oldRow = new TestableBmsFile
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
                }

                List<BMSFile> libraryFiles = new List<BMSFile> { movedFile };
                DispatcherCollection<BMSPackage> pendingPackages = CreatePackageCollection(Array.Empty<BMSPackage>());
                DispatcherCollection<BMSPackage> installedPackages = CreatePackageCollection(new[] { installedPackage });
                TrackingCallbacks callbacks = new TrackingCallbacks();
                BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);
                LibraryMutationDelta delta = new LibraryMutationDelta
                {
                    RaiseBmsFilesChanged = true,
                    RaiseInstalledPackagesChanged = true,
                    InvalidateBMSHashIndex = true,
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
                Assert.AreEqual(1, callbacks.BmsHashInvalidationCount);
                Assert.IsTrue(callbacks.InstalledDirectoryInvalidationCount >= 1);
                Assert.IsTrue(callbacks.ParentFolderInvalidationCount >= 1);
                Assert.AreEqual(1, callbacks.ClearDuplicatedCount);
                Assert.AreEqual(1, callbacks.BmsFilesChangedCount);
                Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
                using LR2SongDBExtended verifySongDb = new LR2SongDBExtended(songDbPath);
                verifySongDb.CreateTable<LR2SongDB.song>();
                verifySongDb.CreateTable<LR2SongDB.folder>();
                Assert.IsTrue(verifySongDb.Table<BMSFile>().Any((BMSFile file) => file.path == newChartPath));
                Assert.IsFalse(verifySongDb.Table<BMSFile>().Any((BMSFile file) => file.path == oldChartPath));
                Assert.IsTrue(verifySongDb.Table<LR2SongDB.folder>().Any((LR2SongDB.folder folder) => folder.path == newDirectoryPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar));
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
            TestableBmsFile removedFile = new TestableBmsFile
            {
                path = "C:\\Library\\remove.bms"
            };
            removedFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            TestableBmsFile keptFile = new TestableBmsFile
            {
                path = "C:\\Library\\keep.bms"
            };
            keptFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            BMSPackage removedPackage = new BMSPackage(new BMSFile[] { removedFile })
            {
                path = "C:\\Installed\\RemovePkg",
                delete_parent = false
            };
            BMSPackage keptPackage = new BMSPackage(new BMSFile[] { keptFile })
            {
                path = "C:\\Installed\\KeepPkg",
                delete_parent = false
            };
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(keptFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = removedFile.path }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = keptFile.path }, typeof(LR2SongDBExtended.maintenance));
            }

            List<BMSFile> libraryFiles = new List<BMSFile> { removedFile, keptFile };
            DispatcherCollection<BMSPackage> pendingPackages = CreatePackageCollection(Array.Empty<BMSPackage>());
            DispatcherCollection<BMSPackage> installedPackages = CreatePackageCollection(new[] { removedPackage, keptPackage });
            TrackingCallbacks callbacks = new TrackingCallbacks();
            BmsLibraryStateApplier applier = CreateStateApplier(songDbPath, callbacks, () => libraryFiles, files => libraryFiles = files, () => pendingPackages, packages => pendingPackages = packages, () => installedPackages, packages => installedPackages = packages);

            applier.UnregisterBmsFiles(new[] { removedFile });

            Assert.AreEqual(1, libraryFiles.Count);
            Assert.AreSame(keptFile, libraryFiles.Single());
            Assert.AreEqual(1, installedPackages.Count);
            Assert.AreSame(keptPackage, installedPackages.Single());
            Assert.AreEqual(1, callbacks.BmsFilesSetCount);
            Assert.AreEqual(1, callbacks.InstalledPackagesChangedCount);
            using LR2SongDBExtended verifySongDb = new LR2SongDBExtended(songDbPath);
            verifySongDb.CreateTable<LR2SongDB.song>();
            verifySongDb.CreateTable<LR2SongDBExtended.maintenance>();
            Assert.IsFalse(verifySongDb.Table<BMSFile>().Any((BMSFile file) => file.path == removedFile.path));
            Assert.IsTrue(verifySongDb.Table<BMSFile>().Any((BMSFile file) => file.path == keptFile.path));
            Assert.IsFalse(verifySongDb.Table<BMSFileMaintenanceInfo>().Any((BMSFileMaintenanceInfo info) => info.path == removedFile.path));
        });
    }

    private static BmsLibraryStateApplier CreateStateApplier(
        string songDbPath,
        TrackingCallbacks callbacks,
        Func<List<BMSFile>> getBmsFiles,
        Action<List<BMSFile>> setBmsFiles,
        Func<DispatcherCollection<BMSPackage>> getPendingPackages,
        Action<DispatcherCollection<BMSPackage>> setPendingPackages,
        Func<DispatcherCollection<BMSPackage>> getInstalledPackages,
        Action<DispatcherCollection<BMSPackage>> setInstalledPackages)
    {
        return new BmsLibraryStateApplier(
            new BmsLibraryDbGateway(songDbPath),
            getBmsFiles,
            delegate (List<BMSFile> files)
            {
                callbacks.BmsFilesSetCount++;
                setBmsFiles(files);
            },
            getPendingPackages,
            delegate (DispatcherCollection<BMSPackage> packages)
            {
                callbacks.PendingPackagesSetCount++;
                setPendingPackages(packages);
            },
            getInstalledPackages,
            delegate (DispatcherCollection<BMSPackage> packages)
            {
                callbacks.InstalledPackagesSetCount++;
                setInstalledPackages(packages);
            },
            () => callbacks.BmsHashInvalidationCount++,
            () => callbacks.InstalledDirectoryInvalidationCount++,
            () => callbacks.ParentFolderInvalidationCount++,
            () => callbacks.ClearDuplicatedCount++,
            () => callbacks.BmsFilesChangedCount++,
            () => callbacks.InstalledPackagesChangedCount++);
    }

    private static DispatcherCollection<BMSPackage> CreatePackageCollection(IEnumerable<BMSPackage> packages)
    {
        return new DispatcherCollection<BMSPackage>(new ObservableCollection<BMSPackage>((packages ?? Enumerable.Empty<BMSPackage>()).ToList()), Dispatcher.CurrentDispatcher);
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StateApplierTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, Array.Empty<byte>());
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

        public int BmsHashInvalidationCount { get; set; }

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
