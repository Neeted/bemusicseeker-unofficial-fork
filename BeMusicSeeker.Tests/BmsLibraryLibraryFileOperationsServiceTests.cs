using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryLibraryFileOperationsServiceTests
{
    [TestMethod]
    public void ProcessInvalidExtensionRename_DeletesSourceWhenDestinationHasSameHash()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "chart.bmx");
            File.WriteAllText(sourcePath, "same");
            File.WriteAllText(destinationPath, "same");
            TestableBmsFile file = new TestableBmsFile
            {
                path = sourcePath
            };

            RenameInvalidExtensionOutcome result = service.ProcessInvalidExtensionRename(file, destinationPath, fileMutationService, null);

            Assert.AreEqual(RenameInvalidExtensionAction.DeletedAsDuplicate, result.Action);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(destinationPath));
        });
    }

    [TestMethod]
    public void ProcessInvalidExtensionRename_DeletesSourceWhenSuffixedCandidateHasSameHash()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "chart.bmx");
            string suffixedDestinationPath = Path.Combine(tempDirectoryPath, "chart(1).bmx");
            File.WriteAllText(sourcePath, "same");
            File.WriteAllText(destinationPath, "different");
            File.WriteAllText(suffixedDestinationPath, "same");
            TestableBmsFile file = new TestableBmsFile
            {
                path = sourcePath
            };

            RenameInvalidExtensionOutcome result = service.ProcessInvalidExtensionRename(file, destinationPath, fileMutationService, null);

            Assert.AreEqual(RenameInvalidExtensionAction.DeletedAsDuplicate, result.Action);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(destinationPath));
            Assert.IsTrue(File.Exists(suffixedDestinationPath));
            Assert.IsFalse(File.Exists(Path.Combine(tempDirectoryPath, "chart(2).bmx")));
        });
    }

    [TestMethod]
    public void ProcessInvalidExtensionRename_RenamesToFirstAvailableSuffixAfterDifferentCandidates()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "chart.bmx");
            string suffixedDestinationPath = Path.Combine(tempDirectoryPath, "chart(1).bmx");
            string finalDestinationPath = Path.Combine(tempDirectoryPath, "chart(2).bmx");
            File.WriteAllText(sourcePath, "source");
            File.WriteAllText(destinationPath, "different-a");
            File.WriteAllText(suffixedDestinationPath, "different-b");
            TestableBmsFile file = new TestableBmsFile
            {
                path = sourcePath
            };

            RenameInvalidExtensionOutcome result = service.ProcessInvalidExtensionRename(file, destinationPath, fileMutationService, null);

            Assert.AreEqual(RenameInvalidExtensionAction.Renamed, result.Action);
            Assert.AreEqual(finalDestinationPath, result.FinalPath);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsTrue(File.Exists(destinationPath));
            Assert.IsTrue(File.Exists(suffixedDestinationPath));
            Assert.IsTrue(File.Exists(finalDestinationPath));
        });
    }

    [TestMethod]
    public void ProcessInvalidExtensionRename_DeletesSourceWhenDirectoryThenSuffixedFileHasSameHash()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "chart.bmx");
            string suffixedDestinationPath = Path.Combine(tempDirectoryPath, "chart(1).bmx");
            File.WriteAllText(sourcePath, "same");
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(suffixedDestinationPath, "same");
            TestableBmsFile file = new TestableBmsFile
            {
                path = sourcePath
            };

            RenameInvalidExtensionOutcome result = service.ProcessInvalidExtensionRename(file, destinationPath, fileMutationService, null);

            Assert.AreEqual(RenameInvalidExtensionAction.DeletedAsDuplicate, result.Action);
            Assert.IsFalse(File.Exists(sourcePath));
            Assert.IsTrue(Directory.Exists(destinationPath));
            Assert.IsTrue(File.Exists(suffixedDestinationPath));
        });
    }

    [TestMethod]
    public void GetPendingPackagesFullyCoveredBySelection_ReturnsOnlyFullySelectedPackages()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile selectedA = CreateFile("C:\\Pending\\Pkg1\\a.bms");
        TestableBmsFile selectedB = CreateFile("C:\\Pending\\Pkg1\\b.bms");
        TestableBmsFile partial = CreateFile("C:\\Pending\\Pkg2\\a.bms");
        TestableBmsFile partialUnselected = CreateFile("C:\\Pending\\Pkg2\\b.bms");
        BMSPackage pkg1 = new BMSPackage(new BMSFile[] { selectedA, selectedB })
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        BMSPackage pkg2 = new BMSPackage(new BMSFile[] { partial, partialUnselected })
        {
            path = "C:\\Pending\\Pkg2",
            delete_parent = false
        };

        List<BMSPackage> result = service.GetPendingPackagesFullyCoveredBySelection(
            new[] { pkg1, pkg2 },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selectedA.path, selectedB.path, partial.path },
            new HashSet<BMSFile> { selectedA, selectedB, partial });

        CollectionAssert.AreEqual(new[] { pkg1 }, result);
    }

    [TestMethod]
    public void MoveFolderAndUpdateReferences_RewritesDirectoryIndexAndBuildFolderMoveDeltaTracksReferences()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string sourceRoot = Path.Combine(tempDirectoryPath, "Src");
            string nestedDirectoryPath = Path.Combine(sourceRoot, "Nested");
            Directory.CreateDirectory(nestedDirectoryPath);
            File.WriteAllText(Path.Combine(nestedDirectoryPath, "chart.bms"), "#PLAYER 1");
            string destinationRoot = Path.Combine(tempDirectoryPath, "Dst");
            BMSDirectoryFileNameHash folderHash = new BMSDirectoryFileNameHash();
            folderHash.AddDir(sourceRoot, update: true);
            folderHash.AddDir(nestedDirectoryPath, update: true);
            uint sourceHash = BMSDirectoryFileNameHash.GetLookupHash("root.wav");
            uint nestedHash = BMSDirectoryFileNameHash.GetLookupHash("chart.bms");
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceRoot, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), new[] { sourceHash }, Array.Empty<uint>(), Array.Empty<uint>());
            lookupCache.AddDir(nestedDirectoryPath, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>(), new[] { nestedHash }, Array.Empty<uint>(), Array.Empty<uint>());
            lookupCache.EnsureAudioRelativeDirectoriesByHashes(new[] { sourceHash, nestedHash });

            TestableBmsFile libraryFile = CreateFile(Path.Combine(sourceRoot, "Nested", "chart.bms"));
            libraryFile.instl_dst = sourceRoot;
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            pendingFile.instl_dst = nestedDirectoryPath;
            BMSPackage pendingPackage = new BMSPackage(new BMSFile[] { pendingFile })
            {
                path = Path.Combine(tempDirectoryPath, "Pending"),
                delete_parent = false
            };
            BMSPackage installedPackage = new BMSPackage(new BMSFile[] { libraryFile })
            {
                path = nestedDirectoryPath,
                delete_parent = false
            };

            service.MoveFolderAndUpdateReferences(
                sourceRoot,
                destinationRoot,
                folderHash,
                lookupCache,
                fileMutationService,
                null);
            LibraryMutationDelta delta = service.BuildFolderMoveDelta(
                sourceRoot,
                destinationRoot,
                new[] { libraryFile },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                new[] { pendingPackage },
                new[] { installedPackage },
                unregister: false,
                raiseBmsFilesChanged: false);

            Assert.IsFalse(Directory.Exists(sourceRoot));
            Assert.IsTrue(Directory.Exists(destinationRoot));
            CollectionAssert.Contains(folderHash.Keys, destinationRoot);
            CollectionAssert.Contains(folderHash.Keys, Path.Combine(destinationRoot, "Nested"));
            CollectionAssert.AreEquivalent(new[] { destinationRoot }, lookupCache.GetDirectoriesByAudioRelativeHash(sourceHash).ToArray());
            CollectionAssert.AreEquivalent(new[] { Path.Combine(destinationRoot, "Nested") }, lookupCache.GetDirectoriesByAudioRelativeHash(nestedHash).ToArray());
            Assert.AreEqual(2, delta.UpdatedInstallDestinations.Count);
            Assert.AreEqual(1, delta.UpdatedInstalledPackagePaths.Count);
            CollectionAssert.AreEquivalent(
                new[] { Path.Combine(destinationRoot, "Nested"), destinationRoot },
                delta.UpdatedInstallDestinations.Select((LibraryInstallDestinationChange change) => change.NewInstallDestination).ToArray());
            Assert.AreEqual(Path.Combine(destinationRoot, "Nested"), delta.UpdatedInstalledPackagePaths[0].NewPath);
        });
    }

    [TestMethod]
    public void DeleteLibraryFiles_RemovesFolderAndClearsInstallDestinations()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string chartPath = Path.Combine(folderPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            TestableBmsFile libraryFile = CreateFile(chartPath);
            libraryFile.instl_dst = folderPath;
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            pendingFile.instl_dst = folderPath;
            BMSPackage pendingPackage = new BMSPackage(new BMSFile[] { pendingFile })
            {
                path = Path.Combine(tempDirectoryPath, "Pending"),
                delete_parent = false
            };
            BMSDirectoryFileNameHash folderHash = new BMSDirectoryFileNameHash();
            folderHash.AddDir(folderPath, update: true);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();

            LibraryRemovalResult result = service.DeleteLibraryFiles(
                new[] { libraryFile },
                new[] { libraryFile },
                new[] { pendingPackage },
                folderHash,
                lookupCache,
                false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.RemovedFiles.Count);
            Assert.AreSame(libraryFile, result.RemovedFiles[0]);
            Assert.AreEqual(0, result.Failures.Count);
            Assert.IsNull(pendingFile.instl_dst);
            Assert.IsNull(libraryFile.instl_dst);
            Assert.IsFalse(Directory.Exists(folderPath));
            CollectionAssert.DoesNotContain(folderHash.Keys, folderPath);
        });
    }

    [TestMethod]
    public void DeleteLibraryCharts_BmsonOnlyFolderDeletesFolderAndReturnsBmsonChart()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string folderPath = Path.Combine(tempDirectoryPath, "BmsonSong");
            Directory.CreateDirectory(folderPath);
            string chartPath = Path.Combine(folderPath, "chart.bmson");
            File.WriteAllText(chartPath, "{}");
            LR2SongDBExtended.bmson_song song = new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = folderPath
            };
            BMSDirectoryFileNameHash folderHash = new BMSDirectoryFileNameHash();
            folderHash.AddDir(folderPath, update: true);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();

            LibraryRemovalResult result = service.DeleteLibraryCharts(
                new[] { LibraryChartRef.FromBmsonSong(song) },
                new[] { LibraryChartRef.FromBmsonSong(song) },
                Array.Empty<BMSPackage>(),
                folderHash,
                lookupCache,
                false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.RemovedCharts.Count);
            Assert.AreEqual(LibraryChartKind.Bmson, result.RemovedCharts[0].Kind);
            Assert.AreSame(song, result.RemovedCharts[0].BmsonSong);
            Assert.AreEqual(1, result.RemovedFiles.Count);
            Assert.IsTrue(PendingChartEntry.IsBmsonChartFile(result.RemovedFiles[0]));
            Assert.AreEqual(0, result.Failures.Count);
            Assert.IsFalse(Directory.Exists(folderPath));
            CollectionAssert.DoesNotContain(folderHash.Keys, folderPath);
        });
    }

    [TestMethod]
    public void BuildFolderMoveDelta_CanSuppressMainViewRefreshForRename()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile file1 = CreateFile("C:\\Lib\\Src\\A\\a.bms");
        TestableBmsFile file2 = CreateFile("C:\\Lib\\Src\\B\\b.bms");

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            new[] { file1, file2 },
            Array.Empty<LR2SongDBExtended.bmson_song>(),
            Array.Empty<BMSPackage>(),
            Array.Empty<BMSPackage>(),
            unregister: false,
            raiseBmsFilesChanged: false);

        Assert.AreEqual(2, delta.FolderPathChanges.Count);
        Assert.AreEqual(2, delta.FilePathChanges.Count);
        Assert.AreEqual(0, delta.FilesToUnregister.Count);
        CollectionAssert.AreEquivalent(
            new[] { "C:\\Lib\\Dst\\A\\a.bms", "C:\\Lib\\Dst\\B\\b.bms" },
            delta.FilePathChanges.Select((LibraryFilePathChange change) => change.NewPath).ToArray());
        Assert.IsFalse(delta.RaiseBmsFilesChanged);
        Assert.IsTrue(delta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(delta.InvalidateParentFolderCache);
    }

    [TestMethod]
    public void BuildFolderMoveDelta_RaisesMainViewRefreshForRootMove()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile file = CreateFile("C:\\Lib\\Src\\A\\a.bms");

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            new[] { file },
            Array.Empty<LR2SongDBExtended.bmson_song>(),
            Array.Empty<BMSPackage>(),
            Array.Empty<BMSPackage>(),
            unregister: false,
            raiseBmsFilesChanged: true);

        Assert.IsTrue(delta.RaiseBmsFilesChanged);
        Assert.AreEqual("C:\\Lib\\Dst\\A\\a.bms", delta.FilePathChanges.Single().NewPath);
    }

    [TestMethod]
    public void BuildAutoRenamePlans_SkipsNestedFoldersAndAddsCollisionSuffix()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            string rootPath = Path.Combine(tempDirectoryPath, "Songs");
            string sourcePath = Path.Combine(rootPath, "OldFolder");
            string nestedPath = Path.Combine(sourcePath, "Nested");
            Directory.CreateDirectory(nestedPath);
            File.WriteAllText(Path.Combine(sourcePath, "chart.bms"), "#PLAYER 1");
            string collisionPath = Path.Combine(rootPath, "Renamed");
            Directory.CreateDirectory(collisionPath);
            TestableBmsFile file = CreateFile(Path.Combine(sourcePath, "chart.bms"));
            TestableBmsFile nestedFile = CreateFile(Path.Combine(nestedPath, "nested.bms"));

            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlans(
                new[] { file, nestedFile },
                new[] { file, nestedFile },
                Array.Empty<string>(),
                renameRootFolder: true,
                (_, parentDir, _) => Path.Combine(parentDir, "Renamed"));

            Assert.AreEqual(1, plans.Count((FolderAutoRenamePlan plan) => !string.IsNullOrWhiteSpace(plan.DestinationDirectory)));
            Assert.AreEqual(Path.Combine(rootPath, "Renamed (2)"), plans.Single((FolderAutoRenamePlan plan) => !string.IsNullOrWhiteSpace(plan.DestinationDirectory)).DestinationDirectory);
        });
    }

    [TestMethod]
    public void BuildAutoRenamePlans_UsesBmsonRowsAsMetadataSource()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            string rootPath = Path.Combine(tempDirectoryPath, "Songs");
            string sourcePath = Path.Combine(rootPath, "OldFolder");
            Directory.CreateDirectory(sourcePath);
            string chartPath = Path.Combine(sourcePath, "chart.bmson");
            File.WriteAllText(chartPath, "{}");
            PendingChartEntry bmsonRow = PendingChartEntry.CreateFromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = sourcePath,
                title = "BmsonTitle",
                artist = "BmsonArtist"
            });

            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlans(
                new[] { bmsonRow },
                new[] { bmsonRow },
                Array.Empty<string>(),
                renameRootFolder: true,
                (children, parentDir, _) => Path.Combine(parentDir, children.First().Title + "_" + children.First().Artist));

            Assert.AreEqual(1, plans.Count);
            Assert.AreEqual(Path.Combine(rootPath, "BmsonTitle_BmsonArtist"), plans[0].DestinationDirectory);
        });
    }

    [TestMethod]
    public void BuildFolderMoveDelta_TracksBmsonSongPathChanges()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        LR2SongDBExtended.bmson_song bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            folder = "C:\\Lib\\Src\\Pkg"
        };

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            Array.Empty<BMSFile>(),
            new[] { bmsonSong },
            Array.Empty<BMSPackage>(),
            Array.Empty<BMSPackage>(),
            unregister: false,
            raiseBmsFilesChanged: false);

        Assert.AreEqual(1, delta.BmsonSongPathChanges.Count);
        Assert.AreEqual("C:\\Lib\\Src\\Pkg\\chart.bmson", delta.BmsonSongPathChanges[0].OldPath);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bmson", delta.BmsonSongPathChanges[0].NewPath);
        Assert.IsTrue(delta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(delta.InvalidateParentFolderCache);
        Assert.IsFalse(delta.RaiseBmsFilesChanged);
    }

    [TestMethod]
    public void BuildFolderMoveDelta_BmsonRootMove_RaisesMainViewRefresh()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        LR2SongDBExtended.bmson_song bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            folder = "C:\\Lib\\Src\\Pkg"
        };

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            Array.Empty<BMSFile>(),
            new[] { bmsonSong },
            Array.Empty<BMSPackage>(),
            Array.Empty<BMSPackage>(),
            unregister: false,
            raiseBmsFilesChanged: true);

        Assert.IsTrue(delta.RaiseBmsFilesChanged);
        Assert.AreEqual(1, delta.BmsonSongPathChanges.Count);
    }

    [TestMethod]
    public void BuildFolderMoveDelta_UnregisterTracksBmsonSongsSeparately()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        LR2SongDBExtended.bmson_song bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            folder = "C:\\Lib\\Src\\Pkg"
        };
        TestableBmsFile bmsFile = CreateFile("C:\\Lib\\Src\\Pkg\\chart.bms");

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            new[] { bmsFile },
            new[] { bmsonSong },
            Array.Empty<BMSPackage>(),
            Array.Empty<BMSPackage>(),
            unregister: true);

        CollectionAssert.AreEqual(new BMSFile[] { bmsFile }, delta.FilesToUnregister);
        CollectionAssert.AreEqual(new[] { bmsonSong }, delta.BmsonSongsToUnregister);
        Assert.AreEqual(0, delta.FilePathChanges.Count);
        Assert.AreEqual(0, delta.BmsonSongPathChanges.Count);
        Assert.IsTrue(delta.InvalidateBMSHashIndex);
        Assert.IsTrue(delta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(delta.InvalidateParentFolderCache);
        Assert.IsTrue(delta.ClearDuplicatedCache);
    }

    [TestMethod]
    public void BuildFileMoveDelta_BmsonUsesBmsonSongDelta()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        LR2SongDBExtended.bmson_song bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\Src\\chart.bmson",
            folder = "C:\\Lib\\Src"
        };
        PendingChartEntry bmsonRow = PendingChartEntry.CreateFromBmsonSong(bmsonSong);

        LibraryMutationDelta moveDelta = service.BuildFileMoveDelta(bmsonRow, "C:\\Lib\\Dst\\chart.bmson", unregister: false);
        LibraryMutationDelta unregisterDelta = service.BuildFileMoveDelta(bmsonRow, "C:\\Lib\\Dst\\chart.bmson", unregister: true);

        Assert.AreEqual(0, moveDelta.FilePathChanges.Count);
        Assert.AreEqual(1, moveDelta.BmsonSongPathChanges.Count);
        Assert.AreSame(bmsonSong, moveDelta.BmsonSongPathChanges[0].Song);
        Assert.AreEqual("C:\\Lib\\Dst\\chart.bmson", moveDelta.BmsonSongPathChanges[0].NewPath);
        Assert.IsTrue(moveDelta.RaiseBmsFilesChanged);
        Assert.IsTrue(moveDelta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(moveDelta.InvalidateParentFolderCache);
        Assert.IsTrue(moveDelta.ClearDuplicatedCache);

        Assert.AreEqual(0, unregisterDelta.FilesToUnregister.Count);
        CollectionAssert.AreEqual(new[] { bmsonSong }, unregisterDelta.BmsonSongsToUnregister);
        Assert.IsTrue(unregisterDelta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(unregisterDelta.InvalidateParentFolderCache);
        Assert.IsTrue(unregisterDelta.ClearDuplicatedCache);
    }

    [TestMethod]
    public void BuildChartFileMoveDelta_UsesChartKindWithoutPendingAdapter()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile bmsFile = CreateFile("C:\\Lib\\Src\\chart.bms");
        LR2SongDBExtended.bmson_song bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\Src\\chart.bmson",
            folder = "C:\\Lib\\Src"
        };

        LibraryMutationDelta bmsDelta = service.BuildChartFileMoveDelta(LibraryChartRef.FromBmsFile(bmsFile), "C:\\Lib\\Dst\\chart.bms", unregister: false);
        LibraryMutationDelta bmsonDelta = service.BuildChartFileMoveDelta(LibraryChartRef.FromBmsonSong(bmsonSong), "C:\\Lib\\Dst\\chart.bmson", unregister: false);
        LibraryMutationDelta unregisterBmsonDelta = service.BuildChartFileMoveDelta(LibraryChartRef.FromBmsonSong(bmsonSong), "C:\\Lib\\Dst\\chart.bmson", unregister: true);

        Assert.AreEqual(1, bmsDelta.FilePathChanges.Count);
        Assert.AreSame(bmsFile, bmsDelta.FilePathChanges[0].File);
        Assert.AreEqual(0, bmsDelta.BmsonSongPathChanges.Count);
        Assert.AreEqual(0, bmsonDelta.FilePathChanges.Count);
        Assert.AreEqual(1, bmsonDelta.BmsonSongPathChanges.Count);
        Assert.AreSame(bmsonSong, bmsonDelta.BmsonSongPathChanges[0].Song);
        CollectionAssert.AreEqual(new[] { bmsonSong }, unregisterBmsonDelta.BmsonSongsToUnregister);
    }

    [TestMethod]
    public void FixInstallationDirectory_ReturnsMutationDeltaAndDuplicateRemovalCandidates()
    {
        BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile movedFile = CreateFile("C:\\Broken\\move.bms");
        movedFile.instl_dst = "C:\\Installed\\Move";
        TestableBmsFile duplicateFile = CreateFile("C:\\Broken\\dup.bms");
        duplicateFile.instl_dst = "C:\\Installed\\Dup";

        LibraryFixInstallationResult result = service.FixInstallationDirectory(
            new[] { movedFile, duplicateFile },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            delegate (BMSPackage package, string destinationDirectory)
            {
                if (package.BMSFiles[0] == duplicateFile)
                {
                    package.BMSFiles.Clear();
                    return true;
                }
                movedFile.path = Path.Combine(destinationDirectory, "move.bms");
                return true;
            },
            file => file == duplicateFile);

        Assert.AreEqual(2, result.RequestedCount);
        Assert.AreEqual(1, result.MovedCount);
        Assert.AreEqual(1, result.DuplicateSkippedCount);
        Assert.AreEqual(1, result.MutationDelta.FilePathChanges.Count);
        Assert.AreEqual(Path.Combine("C:\\Installed\\Move", "move.bms"), result.MutationDelta.FilePathChanges[0].NewPath);
        CollectionAssert.AreEqual(new[] { duplicateFile }, result.FilesToRemove);
        CollectionAssert.AreEqual(new[] { movedFile }, result.MaintenanceTargets);
    }

    [TestMethod]
    public void RenameLibraryFileExtensions_ReturnsRenamedAndDuplicateDeletedFiles()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryLibraryFileOperationsService service = new BmsLibraryLibraryFileOperationsService();
            TestFileMutationService fileMutationService = new TestFileMutationService();
            string renameSourcePath = Path.Combine(tempDirectoryPath, "rename_me.bms");
            string duplicateSourcePath = Path.Combine(tempDirectoryPath, "duplicate.bms");
            string duplicateDestinationPath = Path.Combine(tempDirectoryPath, "duplicate.bme");
            File.WriteAllText(renameSourcePath, "rename");
            File.WriteAllText(duplicateSourcePath, "same");
            File.WriteAllText(duplicateDestinationPath, "same");
            TestableBmsFile renameFile = CreateFile(renameSourcePath);
            TestableBmsFile duplicateFile = CreateFile(duplicateSourcePath);

            LibraryMutationDelta delta = service.RenameLibraryFileExtensions(
                new[] { renameFile, duplicateFile },
                ".bme",
                unregister: false,
                (file, requestedPath) => service.ProcessInvalidExtensionRename(file, requestedPath, fileMutationService, null));

            Assert.AreEqual(1, delta.RenamedCount);
            Assert.AreEqual(1, delta.DuplicateDeletedCount);
            Assert.AreEqual(0, delta.SkippedCount);
            Assert.AreEqual(1, delta.FilePathChanges.Count);
            Assert.AreEqual(1, delta.FilesToUnregister.Count);
            Assert.AreEqual(Path.Combine(tempDirectoryPath, "rename_me.bme"), delta.FilePathChanges[0].NewPath);
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectoryPath, "rename_me.bme")));
            Assert.IsFalse(File.Exists(renameSourcePath));
            Assert.IsFalse(File.Exists(duplicateSourcePath));
            Assert.IsTrue(File.Exists(duplicateDestinationPath));
        });
    }

    private static TestableBmsFile CreateFile(string path)
    {
        return new TestableBmsFile
        {
            path = path
        };
    }

    private static void WithTemporaryDirectory(Action<string> testAction)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FileOperationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        try
        {
            testAction(tempDirectoryPath);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }
    private sealed class TestableBmsFile : BMSFile
    {
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }
            if (overwrite && File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }
            File.Move(sourcePath, destinationPath);
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            if (overwrite && Directory.Exists(destinationPath))
            {
                Directory.Delete(destinationPath, recursive: true);
            }
            string destinationParentPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentPath))
            {
                Directory.CreateDirectory(destinationParentPath);
            }
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }

        private static void CopyDirectory(string sourcePath, string destinationPath)
        {
            Directory.CreateDirectory(destinationPath);
            foreach (string directoryPath in Directory.GetDirectories(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directoryPath.Replace(sourcePath, destinationPath));
            }
            foreach (string filePath in Directory.GetFiles(sourcePath, "*", System.IO.SearchOption.AllDirectories))
            {
                string destinationFilePath = filePath.Replace(sourcePath, destinationPath);
                string destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }
                File.Copy(filePath, destinationFilePath, overwrite: true);
            }
        }
    }
}
