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
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "chart.bmx");
            File.WriteAllText(sourcePath, "same");
            File.WriteAllText(destinationPath, "same");
            var file = new TestableBmsFile
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
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "chart.bmx");
            string suffixedDestinationPath = Path.Combine(tempDirectoryPath, "chart(1).bmx");
            File.WriteAllText(sourcePath, "same");
            File.WriteAllText(destinationPath, "different");
            File.WriteAllText(suffixedDestinationPath, "same");
            var file = new TestableBmsFile
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
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "chart.bmx");
            string suffixedDestinationPath = Path.Combine(tempDirectoryPath, "chart(1).bmx");
            string finalDestinationPath = Path.Combine(tempDirectoryPath, "chart(2).bmx");
            File.WriteAllText(sourcePath, "source");
            File.WriteAllText(destinationPath, "different-a");
            File.WriteAllText(suffixedDestinationPath, "different-b");
            var file = new TestableBmsFile
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
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string sourcePath = Path.Combine(tempDirectoryPath, "chart.bms");
            string destinationPath = Path.Combine(tempDirectoryPath, "chart.bmx");
            string suffixedDestinationPath = Path.Combine(tempDirectoryPath, "chart(1).bmx");
            File.WriteAllText(sourcePath, "same");
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(suffixedDestinationPath, "same");
            var file = new TestableBmsFile
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
        var service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile selectedA = CreateFile("C:\\Pending\\Pkg1\\a.bms");
        TestableBmsFile selectedB = CreateFile("C:\\Pending\\Pkg1\\b.bms");
        TestableBmsFile partial = CreateFile("C:\\Pending\\Pkg2\\a.bms");
        TestableBmsFile partialUnselected = CreateFile("C:\\Pending\\Pkg2\\b.bms");
        var pkg1 = new ChartPackage([selectedA, selectedB])
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        var pkg2 = new ChartPackage([partial, partialUnselected])
        {
            path = "C:\\Pending\\Pkg2",
            delete_parent = false
        };

        List<ChartPackage> result = service.GetPendingPackagesFullyCoveredBySelection(
            [pkg1, pkg2],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selectedA.path, selectedB.path, partial.path },
            [selectedA, selectedB, partial]);

        CollectionAssert.AreEqual(new[] { pkg1 }, result);
    }

    [TestMethod]
    public void MoveFolderAndUpdateReferences_RewritesDirectoryIndexAndBuildFolderMoveDeltaTracksReferences()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string sourceRoot = Path.Combine(tempDirectoryPath, "Src");
            string nestedDirectoryPath = Path.Combine(sourceRoot, "Nested");
            Directory.CreateDirectory(nestedDirectoryPath);
            File.WriteAllText(Path.Combine(nestedDirectoryPath, "chart.bms"), "#PLAYER 1");
            string destinationRoot = Path.Combine(tempDirectoryPath, "Dst");
            uint sourceHash = ChartResourceKeyHash.GetLookupHash("root.wav");
            uint nestedHash = ChartResourceKeyHash.GetLookupHash("chart.bms");
            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceRoot, [sourceHash], [], []);
            lookupCache.AddDir(nestedDirectoryPath, [nestedHash], [], []);
            lookupCache.EnsureAudioRelativeDirectoriesByHashes([sourceHash, nestedHash]);

            TestableBmsFile libraryFile = CreateFile(Path.Combine(sourceRoot, "Nested", "chart.bms"));
            libraryFile.instl_dst = sourceRoot;
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            pendingFile.instl_dst = nestedDirectoryPath;
            var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                folder = Path.Combine(tempDirectoryPath, "Pending"),
                title = "Bmson"
            }));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromCompatibilityAdapter(pendingFile), adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            var installedPackage = new ChartPackage([libraryFile])
            {
                path = nestedDirectoryPath,
                delete_parent = false
            };
            Assert.IsNull(adapterlessBmsonEntry.CompatibilityAdapter);

            service.MoveFolderAndUpdateReferences(
                sourceRoot,
                destinationRoot,
                lookupCache,
                fileMutationService,
                null);
            LibraryMutationDelta delta = service.BuildFolderMoveDelta(
                sourceRoot,
                destinationRoot,
                [libraryFile],
                [],
                [pendingPackage],
                [installedPackage],
                unregister: false,
                raiseBmsFilesChanged: false);

            Assert.IsFalse(Directory.Exists(sourceRoot));
            Assert.IsTrue(Directory.Exists(destinationRoot));
            CollectionAssert.AreEquivalent(new[] { destinationRoot }, lookupCache.GetDirectoriesByAudioRelativeHash(sourceHash).ToArray());
            CollectionAssert.AreEquivalent(new[] { Path.Combine(destinationRoot, "Nested") }, lookupCache.GetDirectoriesByAudioRelativeHash(nestedHash).ToArray());
            Assert.IsNotNull(lookupCache.GetEntryOrNull(destinationRoot));
            Assert.IsNotNull(lookupCache.GetEntryOrNull(Path.Combine(destinationRoot, "Nested")));
            Assert.IsNull(lookupCache.GetEntryOrNull(sourceRoot));
            Assert.IsNull(lookupCache.GetEntryOrNull(nestedDirectoryPath));
            Assert.AreEqual(2, delta.UpdatedInstallDestinations.Count);
            Assert.AreEqual(1, delta.UpdatedInstalledPackagePaths.Count);
            CollectionAssert.AreEquivalent(
                new[] { Path.Combine(destinationRoot, "Nested"), destinationRoot },
                delta.UpdatedInstallDestinations.Select(change => change.NewInstallDestination).ToArray());
            Assert.AreEqual(Path.Combine(destinationRoot, "Nested"), delta.UpdatedInstalledPackagePaths[0].NewPath);
            Assert.IsNull(adapterlessBmsonEntry.CompatibilityAdapter);
        });
    }

    [TestMethod]
    public void DeleteLibraryCharts_RemovesFolderAndClearsInstallDestinations()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string chartPath = Path.Combine(folderPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            TestableBmsFile libraryFile = CreateFile(chartPath);
            libraryFile.instl_dst = folderPath;
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            pendingFile.instl_dst = folderPath;
            var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                folder = Path.Combine(tempDirectoryPath, "Pending"),
                title = "Bmson"
            }));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromCompatibilityAdapter(pendingFile), adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(folderPath, []);
            Assert.IsNull(adapterlessBmsonEntry.CompatibilityAdapter);

            LibraryRemovalResult result = service.DeleteLibraryCharts(
                [LibraryChartRef.FromCompatibilityBmsFile(libraryFile)],
                [LibraryChartRef.FromCompatibilityBmsFile(libraryFile)],
                [pendingPackage],
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
            Assert.IsNull(adapterlessBmsonEntry.CompatibilityAdapter);
            Assert.IsFalse(Directory.Exists(folderPath));
            Assert.IsNull(lookupCache.GetEntryOrNull(folderPath));
        });
    }

    [TestMethod]
    public void DeleteLibraryCharts_BmsonOnlyFolderDeletesFolderAndReturnsBmsonChart()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string folderPath = Path.Combine(tempDirectoryPath, "BmsonSong");
            Directory.CreateDirectory(folderPath);
            string chartPath = Path.Combine(folderPath, "chart.bmson");
            File.WriteAllText(chartPath, "{}");
            var song = new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = folderPath
            };
            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(folderPath, []);

            LibraryRemovalResult result = service.DeleteLibraryCharts(
                [LibraryChartRef.FromBmsonSong(song)],
                [LibraryChartRef.FromBmsonSong(song)],
                [],
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
            Assert.IsNull(lookupCache.GetEntryOrNull(folderPath));
        });
    }

    [TestMethod]
    public void DeleteLibraryCharts_PathOnlySelectionUsesCanonicalLibraryRef()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string chartPath = Path.Combine(folderPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            TestableBmsFile canonicalFile = CreateFile(chartPath);
            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(folderPath, []);

            LibraryRemovalResult result = service.DeleteLibraryCharts(
                [LibraryChartRef.FromPath(LibraryChartKind.Bms, chartPath, canonicalFile.hash, canonicalFile.sha256)],
                [LibraryChartRef.FromCompatibilityBmsFile(canonicalFile)],
                [],
                lookupCache,
                true,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.RemovedCharts.Count);
            Assert.AreSame(canonicalFile, result.RemovedCharts[0].CompatibilityBmsFile);
            Assert.AreEqual(1, result.RemovedFiles.Count);
            Assert.AreSame(canonicalFile, result.RemovedFiles[0]);
            Assert.AreEqual(0, result.Failures.Count);
            Assert.AreEqual(folderPath, fileMutationService.LastDeletedDirectoryPath);
            Assert.AreEqual(RecycleOption.SendToRecycleBin, fileMutationService.LastDirectoryRecycleOption);
            Assert.IsFalse(Directory.Exists(folderPath));
            Assert.IsNull(lookupCache.GetEntryOrNull(folderPath));
        });
    }

    [TestMethod]
    public void DeleteLibraryCharts_NonCatalogPathOnlySelectionReturnsResolveFailure()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string catalogChartPath = Path.Combine(folderPath, "catalog.bms");
            string staleChartPath = Path.Combine(folderPath, "stale.bms");
            File.WriteAllText(catalogChartPath, "#PLAYER 1");
            File.WriteAllText(staleChartPath, "#PLAYER 1");
            TestableBmsFile catalogFile = CreateFile(catalogChartPath);

            LibraryRemovalResult result = service.DeleteLibraryCharts(
                [LibraryChartRef.FromPath(LibraryChartKind.Bms, staleChartPath, null, null)],
                [LibraryChartRef.FromCompatibilityBmsFile(catalogFile)],
                [],
                new DirectoryResourceLookupCache(),
                true,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(0, result.RemovedCharts.Count);
            Assert.AreEqual(0, result.RemovedFiles.Count);
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreEqual("resolve_failed", result.Failures[0].Reason);
            Assert.IsNull(fileMutationService.LastDeletedFilePath);
            Assert.IsNull(fileMutationService.LastDeletedDirectoryPath);
            Assert.IsTrue(File.Exists(staleChartPath));
        });
    }

    [TestMethod]
    public void DeleteLibraryCharts_DifferentBmsInstanceUsesCanonicalLibraryRef()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string chartPath = Path.Combine(folderPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            TestableBmsFile canonicalFile = CreateFile(chartPath);
            TestableBmsFile nonCanonicalFile = CreateFile(Path.Combine(folderPath, ".", "chart.bms"));

            LibraryRemovalResult result = service.DeleteLibraryCharts(
                [LibraryChartRef.FromCompatibilityBmsFile(nonCanonicalFile)],
                [LibraryChartRef.FromCompatibilityBmsFile(canonicalFile)],
                [],
                new DirectoryResourceLookupCache(),
                false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.RemovedCharts.Count);
            Assert.AreSame(canonicalFile, result.RemovedCharts[0].CompatibilityBmsFile);
            Assert.AreEqual(1, result.RemovedFiles.Count);
            Assert.AreSame(canonicalFile, result.RemovedFiles[0]);
            Assert.AreEqual(0, result.Failures.Count);
        });
    }

    [TestMethod]
    public void DeleteLibraryCharts_SameHashDifferentPathDoesNotResolve()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string catalogChartPath = Path.Combine(folderPath, "catalog.bms");
            string selectedChartPath = Path.Combine(folderPath, "selected.bms");
            File.WriteAllText(catalogChartPath, "#PLAYER 1");
            File.WriteAllText(selectedChartPath, "#PLAYER 1");
            TestableBmsFile catalogFile = CreateFile(catalogChartPath);

            LibraryRemovalResult result = service.DeleteLibraryCharts(
                [LibraryChartRef.FromPath(LibraryChartKind.Bms, selectedChartPath, catalogFile.hash, catalogFile.sha256)],
                [LibraryChartRef.FromCompatibilityBmsFile(catalogFile)],
                [],
                new DirectoryResourceLookupCache(),
                false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(0, result.RemovedCharts.Count);
            Assert.AreEqual(0, result.RemovedFiles.Count);
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreEqual("resolve_failed", result.Failures[0].Reason);
            Assert.IsTrue(File.Exists(catalogChartPath));
            Assert.IsTrue(File.Exists(selectedChartPath));
            Assert.IsNull(fileMutationService.LastDeletedFilePath);
            Assert.IsNull(fileMutationService.LastDeletedDirectoryPath);
        });
    }

    [TestMethod]
    public void DeleteLibraryCharts_LastChartInFolderInvokesWholeFolderConfirmation()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string chartPath = Path.Combine(folderPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            TestableBmsFile libraryFile = CreateFile(chartPath);
            int confirmCount = 0;
            string confirmedPath = string.Empty;

            LibraryRemovalResult result = service.DeleteLibraryCharts(
                [LibraryChartRef.FromCompatibilityBmsFile(libraryFile)],
                [LibraryChartRef.FromCompatibilityBmsFile(libraryFile)],
                [],
                new DirectoryResourceLookupCache(),
                false,
                path =>
                {
                    confirmCount++;
                    confirmedPath = path;
                    return true;
                },
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, confirmCount);
            Assert.AreEqual(folderPath, confirmedPath);
            Assert.AreEqual(1, result.FolderDeleteCount);
            Assert.AreEqual(1, result.RemovedCharts.Count);
            Assert.AreSame(libraryFile, result.RemovedCharts[0].CompatibilityBmsFile);
        });
    }

    [TestMethod]
    public void GetWholeFolderDeleteCandidatePaths_PathOnlyExactCatalogPathReturnsFolder()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string chartPath = Path.Combine(folderPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            TestableBmsFile libraryFile = CreateFile(chartPath);

            List<string> paths = service.GetWholeFolderDeleteCandidatePaths(
                [LibraryChartRef.FromPath(LibraryChartKind.Bms, chartPath, libraryFile.hash, libraryFile.sha256)],
                [LibraryChartRef.FromCompatibilityBmsFile(libraryFile)]);

            Assert.AreEqual(1, paths.Count);
            Assert.AreEqual(folderPath, paths[0]);
        });
    }

    [TestMethod]
    public void GetWholeFolderDeleteCandidatePaths_SameHashDifferentPathReturnsEmpty()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string catalogChartPath = Path.Combine(folderPath, "catalog.bms");
            string selectedChartPath = Path.Combine(folderPath, "selected.bms");
            File.WriteAllText(catalogChartPath, "#PLAYER 1");
            File.WriteAllText(selectedChartPath, "#PLAYER 1");
            TestableBmsFile catalogFile = CreateFile(catalogChartPath);

            List<string> paths = service.GetWholeFolderDeleteCandidatePaths(
                [LibraryChartRef.FromPath(LibraryChartKind.Bms, selectedChartPath, catalogFile.hash, catalogFile.sha256)],
                [LibraryChartRef.FromCompatibilityBmsFile(catalogFile)]);

            Assert.AreEqual(0, paths.Count);
        });
    }

    [TestMethod]
    public void GetWholeFolderDeleteCandidatePaths_PartialFolderSelectionReturnsEmpty()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string selectedChartPath = Path.Combine(folderPath, "selected.bms");
            string remainingChartPath = Path.Combine(folderPath, "remaining.bms");
            File.WriteAllText(selectedChartPath, "#PLAYER 1");
            File.WriteAllText(remainingChartPath, "#PLAYER 1");
            TestableBmsFile selectedFile = CreateFile(selectedChartPath);
            TestableBmsFile remainingFile = CreateFile(remainingChartPath);

            List<string> paths = service.GetWholeFolderDeleteCandidatePaths(
                [LibraryChartRef.FromCompatibilityBmsFile(selectedFile)],
                [LibraryChartRef.FromCompatibilityBmsFile(selectedFile), LibraryChartRef.FromCompatibilityBmsFile(remainingFile)]);

            Assert.AreEqual(0, paths.Count);
        });
    }

    [TestMethod]
    public void BuildFolderMoveDelta_CanSuppressMainViewRefreshForRename()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile file1 = CreateFile("C:\\Lib\\Src\\A\\a.bms");
        TestableBmsFile file2 = CreateFile("C:\\Lib\\Src\\B\\b.bms");

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            [file1, file2],
            [],
            [],
            [],
            unregister: false,
            raiseBmsFilesChanged: false);

        Assert.AreEqual(2, delta.FolderPathChanges.Count);
        Assert.AreEqual(2, delta.FilePathChanges.Count);
        Assert.AreEqual(0, delta.FilesToUnregister.Count);
        CollectionAssert.AreEquivalent(
            new[] { "C:\\Lib\\Dst\\A\\a.bms", "C:\\Lib\\Dst\\B\\b.bms" },
            delta.FilePathChanges.Select(change => change.NewPath).ToArray());
        Assert.IsFalse(delta.RaiseBmsFilesChanged);
        Assert.IsTrue(delta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(delta.InvalidateParentFolderCache);
    }

    [TestMethod]
    public void BuildFolderMoveDelta_RaisesMainViewRefreshForRootMove()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile file = CreateFile("C:\\Lib\\Src\\A\\a.bms");

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            [file],
            [],
            [],
            [],
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
            var service = new BmsLibraryLibraryFileOperationsService();
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
                [file, nestedFile],
                [file, nestedFile],
                [],
                renameRootFolder: true,
                (_, parentDir, _) => Path.Combine(parentDir, "Renamed"));

            Assert.AreEqual(1, plans.Count(plan => !string.IsNullOrWhiteSpace(plan.DestinationDirectory)));
            Assert.AreEqual(Path.Combine(rootPath, "Renamed (2)"), plans.Single(plan => !string.IsNullOrWhiteSpace(plan.DestinationDirectory)).DestinationDirectory);
        });
    }

    [TestMethod]
    public void BuildAutoRenamePlans_UsesBmsonRowsAsMetadataSource()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string rootPath = Path.Combine(tempDirectoryPath, "Songs");
            string sourcePath = Path.Combine(rootPath, "OldFolder");
            Directory.CreateDirectory(sourcePath);
            string chartPath = Path.Combine(sourcePath, "chart.bmson");
            File.WriteAllText(chartPath, "{}");
            var bmsonRow = PendingChartEntry.CreateFromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = sourcePath,
                title = "BmsonTitle",
                artist = "BmsonArtist"
            });

            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlans(
                [bmsonRow],
                [bmsonRow],
                [],
                renameRootFolder: true,
                (children, parentDir, _) => Path.Combine(parentDir, children.First().Title + "_" + children.First().Artist));

            Assert.AreEqual(1, plans.Count);
            Assert.AreEqual(Path.Combine(rootPath, "BmsonTitle_BmsonArtist"), plans[0].DestinationDirectory);
        });
    }

    [TestMethod]
    public void BuildAutoRenamePlans_MixedFolderIncludesBmsonRowsInMetadata()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string rootPath = Path.Combine(tempDirectoryPath, "Songs");
            string sourcePath = Path.Combine(rootPath, "OldFolder");
            Directory.CreateDirectory(sourcePath);
            string bmsPath = Path.Combine(sourcePath, "chart.bms");
            string bmsonPath = Path.Combine(sourcePath, "chart.bmson");
            File.WriteAllText(bmsPath, "#PLAYER 1");
            File.WriteAllText(bmsonPath, "{}");
            TestableBmsFile bmsRow = CreateFile(bmsPath);
            bmsRow.SetTitleForTest("BmsTitle");
            var bmsonRow = PendingChartEntry.CreateFromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = bmsonPath,
                folder = sourcePath,
                title = "BmsonTitle",
                artist = "BmsonArtist"
            });

            List<string> directChildTitles = [];
            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlans(
                [bmsRow],
                [bmsRow, bmsonRow],
                [],
                renameRootFolder: true,
                (children, parentDir, _) =>
                {
                    directChildTitles = [.. children.Select(child => child.Title).OrderBy(title => title, StringComparer.Ordinal)];
                    return Path.Combine(parentDir, string.Join("_", directChildTitles));
                });

            CollectionAssert.AreEqual(new[] { "BmsTitle", "BmsonTitle" }, directChildTitles);
            Assert.AreEqual(1, plans.Count);
            Assert.AreEqual(Path.Combine(rootPath, "BmsTitle_BmsonTitle"), plans[0].DestinationDirectory);
        });
    }

    [TestMethod]
    public void BuildFolderMoveDelta_TracksBmsonSongPathChanges()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            folder = "C:\\Lib\\Src\\Pkg"
        };

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            [],
            [bmsonSong],
            [],
            [],
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
        var service = new BmsLibraryLibraryFileOperationsService();
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            folder = "C:\\Lib\\Src\\Pkg"
        };

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            [],
            [bmsonSong],
            [],
            [],
            unregister: false,
            raiseBmsFilesChanged: true);

        Assert.IsTrue(delta.RaiseBmsFilesChanged);
        Assert.AreEqual(1, delta.BmsonSongPathChanges.Count);
    }

    [TestMethod]
    public void PrepareMergeDirectory_TracksInstallDestinationsWithoutMaterializingUnlinkedBmsonEntries()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string sourceRoot = Path.Combine(tempDirectoryPath, "Src");
            string destinationRoot = Path.Combine(tempDirectoryPath, "Dst");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(destinationRoot);
            string chartPath = Path.Combine(sourceRoot, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            TestableBmsFile libraryFile = CreateFile(chartPath);
            libraryFile.instl_dst = sourceRoot;
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            pendingFile.instl_dst = sourceRoot;
            var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                folder = Path.Combine(tempDirectoryPath, "Pending"),
                title = "Bmson"
            }));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromCompatibilityAdapter(pendingFile), adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            Assert.IsNull(adapterlessBmsonEntry.CompatibilityAdapter);

            LibraryMergeResult result = service.PrepareMergeDirectory(
                sourceRoot,
                destinationRoot,
                [libraryFile],
                [],
                [pendingPackage],
                [],
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.IsTrue(result.Success);
            Assert.AreEqual(2, result.ReferenceMutationDelta.UpdatedInstallDestinations.Count);
            CollectionAssert.AreEquivalent(
                new[] { destinationRoot, destinationRoot },
                result.ReferenceMutationDelta.UpdatedInstallDestinations.Select(change => change.NewInstallDestination).ToArray());
            Assert.IsNull(adapterlessBmsonEntry.CompatibilityAdapter);
        });
    }

    [TestMethod]
    public void BuildFolderMoveDelta_MixedBmsAndBmsonTracksBothStorageModels()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile bmsFile = CreateFile("C:\\Lib\\Src\\Pkg\\chart.bms");
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            folder = "C:\\Lib\\Src\\Pkg"
        };

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            [bmsFile],
            [bmsonSong],
            [],
            [],
            unregister: false,
            raiseBmsFilesChanged: true);

        Assert.AreEqual(1, delta.FilePathChanges.Count);
        Assert.AreSame(bmsFile, delta.FilePathChanges[0].File);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bms", delta.FilePathChanges[0].NewPath);
        Assert.AreEqual(1, delta.BmsonSongPathChanges.Count);
        Assert.AreSame(bmsonSong, delta.BmsonSongPathChanges[0].Song);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bmson", delta.BmsonSongPathChanges[0].NewPath);
        Assert.IsTrue(delta.RaiseBmsFilesChanged);
        Assert.IsTrue(delta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(delta.InvalidateParentFolderCache);
        Assert.IsTrue(delta.ClearDuplicatedCache);
    }

    [TestMethod]
    public void BuildFolderMoveDelta_UnregisterTracksBmsonSongsSeparately()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            folder = "C:\\Lib\\Src\\Pkg"
        };
        TestableBmsFile bmsFile = CreateFile("C:\\Lib\\Src\\Pkg\\chart.bms");

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            [bmsFile],
            [bmsonSong],
            [],
            [],
            unregister: true);

        CollectionAssert.AreEqual(new BMSFile[] { bmsFile }, delta.FilesToUnregister);
        CollectionAssert.AreEqual(new[] { bmsonSong }, delta.BmsonSongsToUnregister);
        Assert.AreEqual(0, delta.FilePathChanges.Count);
        Assert.AreEqual(0, delta.BmsonSongPathChanges.Count);
        Assert.IsTrue(delta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(delta.InvalidateParentFolderCache);
        Assert.IsTrue(delta.ClearDuplicatedCache);
    }

    [TestMethod]
    public void FixInstallationDirectory_ReturnsMutationDeltaAndDuplicateRemovalCandidates()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile movedFile = CreateFile("C:\\Broken\\move.bms");
        movedFile.instl_dst = "C:\\Installed\\Move";
        TestableBmsFile duplicateFile = CreateFile("C:\\Broken\\dup.bms");
        duplicateFile.instl_dst = "C:\\Installed\\Dup";

        LibraryFixInstallationResult result = service.FixInstallationDirectory(
            [movedFile, duplicateFile],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            delegate (ChartPackage package, string destinationDirectory)
            {
                if (package.MaterializeChartAdaptersForTest()[0] == duplicateFile)
                {
                    package.ReplaceChartAdapters([]);
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
    public void FixInstallationDirectory_BmsonChartReturnsBmsonSongPathChange()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        var song = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Broken\\move.bmson",
            folder = "C:\\Broken",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256 = new string('b', 64),
            title = "Bmson"
        };
        var movedFile = PendingChartEntry.CreateFromBmsonSong(song);
        movedFile.instl_dst = "C:\\Installed\\Move";

        LibraryFixInstallationResult result = service.FixInstallationDirectory(
            [movedFile],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            delegate (ChartPackage package, string destinationDirectory)
            {
                movedFile.path = Path.Combine(destinationDirectory, "move.bmson");
                return true;
            },
            file => false);

        Assert.AreEqual(1, result.RequestedCount);
        Assert.AreEqual(1, result.MovedCount);
        Assert.AreEqual(0, result.MutationDelta.FilePathChanges.Count);
        Assert.AreEqual(1, result.MutationDelta.BmsonSongPathChanges.Count);
        Assert.AreSame(song, result.MutationDelta.BmsonSongPathChanges[0].Song);
        Assert.AreEqual("C:\\Broken\\move.bmson", result.MutationDelta.BmsonSongPathChanges[0].OldPath);
        Assert.AreEqual(Path.Combine("C:\\Installed\\Move", "move.bmson"), result.MutationDelta.BmsonSongPathChanges[0].NewPath);
        Assert.IsTrue(result.MutationDelta.RaiseBmsFilesChanged);
        Assert.IsTrue(result.MutationDelta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(result.MutationDelta.InvalidateParentFolderCache);
        Assert.IsTrue(result.MutationDelta.ClearDuplicatedCache);
        CollectionAssert.AreEqual(new[] { movedFile }, result.MaintenanceTargets);
    }

    [TestMethod]
    public void RenameLibraryFileExtensions_ReturnsRenamedAndDuplicateDeletedFiles()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new TestFileMutationService();
            string renameSourcePath = Path.Combine(tempDirectoryPath, "rename_me.bms");
            string duplicateSourcePath = Path.Combine(tempDirectoryPath, "duplicate.bms");
            string duplicateDestinationPath = Path.Combine(tempDirectoryPath, "duplicate.bme");
            File.WriteAllText(renameSourcePath, "rename");
            File.WriteAllText(duplicateSourcePath, "same");
            File.WriteAllText(duplicateDestinationPath, "same");
            TestableBmsFile renameFile = CreateFile(renameSourcePath);
            TestableBmsFile duplicateFile = CreateFile(duplicateSourcePath);

            LibraryMutationDelta delta = service.RenameLibraryFileExtensions(
                [renameFile, duplicateFile],
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
        internal void SetTitleForTest(string value)
        {
            Title = value;
        }
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public string LastDeletedFilePath { get; private set; } = null!;

        public string LastDeletedDirectoryPath { get; private set; } = null!;

        public RecycleOption? LastFileRecycleOption { get; private set; }

        public RecycleOption? LastDirectoryRecycleOption { get; private set; }

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
            LastDeletedFilePath = filePath;
            LastFileRecycleOption = recycleOption;
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
            LastDeletedDirectoryPath = directoryPath;
            LastDirectoryRecycleOption = recycleOption;
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
