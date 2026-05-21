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
        var pkg1 = ChartPackageTestExtensions.CreatePackage([selectedA, selectedB]);
        pkg1.path = "C:\\Pending\\Pkg1";
        pkg1.delete_parent = false;
        var pkg2 = ChartPackageTestExtensions.CreatePackage([partial, partialUnselected]);
        pkg2.path = "C:\\Pending\\Pkg2";
        pkg2.delete_parent = false;

        List<ChartPackage> result = service.GetPendingPackagesFullyCoveredBySelection(
            [pkg1, pkg2],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { selectedA.path, selectedB.path, partial.path });

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
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromBmsFile(pendingFile), adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            var installedPackage = ChartPackageTestExtensions.CreatePackage([libraryFile]);
            installedPackage.path = nestedDirectoryPath;
            installedPackage.delete_parent = false;
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());

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
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
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
            string bmsonChartPath = Path.Combine(folderPath, "chart.bmson");
            File.WriteAllText(bmsonChartPath, "{}");
            TestableBmsFile libraryFile = CreateFile(chartPath);
            libraryFile.instl_dst = folderPath;
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = bmsonChartPath,
                folder = folderPath
            };
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            pendingFile.instl_dst = folderPath;
            var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                folder = Path.Combine(tempDirectoryPath, "Pending"),
                title = "Bmson"
            }));
            adapterlessBmsonEntry.ApplyInstallDestination(folderPath, "Deleted title", "Deleted artist");
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromBmsFile(pendingFile), adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(folderPath, []);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());

            LibraryRemovalResult result = service.DeleteLibraryCharts(
                [LibraryChartRef.FromBmsFile(libraryFile), LibraryChartRef.FromBmsonSong(bmsonSong)],
                [LibraryChartRef.FromBmsFile(libraryFile), LibraryChartRef.FromBmsonSong(bmsonSong)],
                [pendingPackage],
                lookupCache,
                false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.IsTrue(result.RemovedCharts.Any(chart => ReferenceEquals(chart.BmsFile, libraryFile)));
            Assert.IsTrue(result.RemovedCharts.Any(chart => ReferenceEquals(chart.BmsonSong, bmsonSong)));
            Assert.AreEqual(0, result.Failures.Count);
            Assert.IsNull(pendingFile.instl_dst);
            Assert.IsNull(libraryFile.instl_dst);
            Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
            Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestinationTitle);
            CollectionAssert.AreEqual(Array.Empty<string>(), adapterlessBmsonEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
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
                [LibraryChartRef.FromBmsFile(canonicalFile)],
                [],
                lookupCache,
                true,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.RemovedCharts.Count);
            Assert.AreSame(canonicalFile, result.RemovedCharts[0].BmsFile);
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
                [LibraryChartRef.FromBmsFile(catalogFile)],
                [],
                new DirectoryResourceLookupCache(),
                true,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(0, result.RemovedCharts.Count);
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
                [LibraryChartRef.FromBmsFile(nonCanonicalFile)],
                [LibraryChartRef.FromBmsFile(canonicalFile)],
                [],
                new DirectoryResourceLookupCache(),
                false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.RemovedCharts.Count);
            Assert.AreSame(canonicalFile, result.RemovedCharts[0].BmsFile);
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
                [LibraryChartRef.FromBmsFile(catalogFile)],
                [],
                new DirectoryResourceLookupCache(),
                false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(0, result.RemovedCharts.Count);
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
                [LibraryChartRef.FromBmsFile(libraryFile)],
                [LibraryChartRef.FromBmsFile(libraryFile)],
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
            Assert.AreSame(libraryFile, result.RemovedCharts[0].BmsFile);
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
                [LibraryChartRef.FromBmsFile(libraryFile)]);

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
                [LibraryChartRef.FromBmsFile(catalogFile)]);

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
                [LibraryChartRef.FromBmsFile(selectedFile)],
                [LibraryChartRef.FromBmsFile(selectedFile), LibraryChartRef.FromBmsFile(remainingFile)]);

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
        Assert.AreEqual(2, delta.ChartPathChanges.Count);
        Assert.AreEqual(0, delta.ChartsToUnregister.Count);
        CollectionAssert.AreEquivalent(
            new[] { "C:\\Lib\\Dst\\A\\a.bms", "C:\\Lib\\Dst\\B\\b.bms" },
            delta.ChartPathChanges.Select(change => change.NewPath).ToArray());
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
        Assert.AreEqual("C:\\Lib\\Dst\\A\\a.bms", delta.ChartPathChanges.Single().NewPath);
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

            ChartFile chart = ChartFileProjection.FromBmsFile(file);
            ChartFile nestedChart = ChartFileProjection.FromBmsFile(nestedFile);

            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlans(
                [chart, nestedChart],
                [chart, nestedChart],
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
            ChartFile bmsonChart = ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = sourcePath,
                title = "BmsonTitle",
                artist = "BmsonArtist"
            });

            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlans(
                [bmsonChart],
                [bmsonChart],
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
            ChartFile bmsChart = ChartFileProjection.FromBmsFile(bmsRow);
            ChartFile bmsonChart = ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = bmsonPath,
                folder = sourcePath,
                title = "BmsonTitle",
                artist = "BmsonArtist"
            });

            List<string> directChildTitles = [];
            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlans(
                [bmsChart],
                [bmsChart, bmsonChart],
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
    public void BuildFolderMoveDelta_TracksBmsonChartPathChanges()
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

        Assert.AreEqual(1, delta.ChartPathChanges.Count);
        Assert.AreSame(bmsonSong, delta.ChartPathChanges[0].BmsonSong);
        Assert.AreEqual("C:\\Lib\\Src\\Pkg\\chart.bmson", delta.ChartPathChanges[0].OldPath);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bmson", delta.ChartPathChanges[0].NewPath);
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
        Assert.AreEqual(1, delta.ChartPathChanges.Count);
        Assert.AreSame(bmsonSong, delta.ChartPathChanges[0].BmsonSong);
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
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromBmsFile(pendingFile), adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());

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
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void PrepareMergeDirectory_RewritesAdapterlessBmsonInstallDestinationWithoutMaterializingAdapter()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string sourceRoot = Path.Combine(tempDirectoryPath, "Src");
            string destinationRoot = Path.Combine(tempDirectoryPath, "Dst");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(destinationRoot);
            TestableBmsFile libraryFile = CreateFile(Path.Combine(sourceRoot, "chart.bms"));
            var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                folder = Path.Combine(tempDirectoryPath, "Pending"),
                title = "Bmson"
            }));
            adapterlessBmsonEntry.SetInstallDestinationPathOnly(sourceRoot);
            var pendingPackage = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());

            LibraryMergeResult result = service.PrepareMergeDirectory(
                sourceRoot,
                destinationRoot,
                [libraryFile],
                [],
                [pendingPackage],
                [],
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            Assert.IsTrue(result.Success);
            LibraryInstallDestinationChange change = result.ReferenceMutationDelta.UpdatedInstallDestinations.Single();
            Assert.AreSame(adapterlessBmsonEntry, change.Entry);
            Assert.AreEqual(destinationRoot, change.NewInstallDestination);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void PrepareMergeDirectory_MixedBmsAndBmsonKeepsStorageOwnersWithoutBmsonAdapter()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string sourceRoot = Path.Combine(tempDirectoryPath, "Src");
            string destinationRoot = Path.Combine(tempDirectoryPath, "Dst");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(destinationRoot);
            TestableBmsFile bmsFile = CreateFile(Path.Combine(sourceRoot, "chart.bms"));
            bmsFile.ApplySnapshotDigest("cccccccccccccccccccccccccccccccc", null);
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(sourceRoot, "chart.bmson"),
                folder = sourceRoot,
                title = "Bmson",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                sha256 = new string('b', 64)
            };
            HashSet<string> excludedHashes = [];

            LibraryMergeResult result = service.PrepareMergeDirectory(
                sourceRoot,
                destinationRoot,
                [bmsFile],
                [bmsonSong],
                [],
                [],
                charts =>
                {
                    excludedHashes = [.. charts.Select(chart => chart.PrimaryLookupHash).Where(hash => !string.IsNullOrWhiteSpace(hash))];
                    return [];
                });

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { bmsFile }, result.SourceBmsFiles);
            CollectionAssert.AreEqual(new[] { bmsonSong }, result.SourceBmsonSongs);
            Assert.AreEqual(2, result.Repackage.ChartEntries.Count);
            Assert.IsTrue(result.Repackage.ChartEntries.Any(entry => ReferenceEquals(entry.Chart.BmsFile, bmsFile)));
            PackageChartEntry bmsonEntry = result.Repackage.ChartEntries.Single(entry => entry.Chart.Kind == ChartFileKind.Bmson);
            Assert.AreSame(bmsonSong, bmsonEntry.Chart.BmsonSong);
            Assert.IsNull(bmsonEntry.GetBmsOwnerForTest());
            CollectionAssert.AreEquivalent(new[] { bmsFile.hash, bmsonSong.md5 }, excludedHashes.ToArray());
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

        Assert.AreEqual(2, delta.ChartPathChanges.Count);
        LibraryChartPathChange bmsPathChange = delta.ChartPathChanges.Single(change => change.BmsFile == bmsFile);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bms", bmsPathChange.NewPath);
        LibraryChartPathChange bmsonPathChange = delta.ChartPathChanges.Single(change => change.BmsonSong == bmsonSong);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bmson", bmsonPathChange.NewPath);
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

        Assert.AreEqual(2, delta.ChartsToUnregister.Count);
        Assert.AreSame(bmsFile, delta.ChartsToUnregister.Single(chart => chart.BmsFile == bmsFile).BmsFile);
        Assert.AreSame(bmsonSong, delta.ChartsToUnregister.Single(chart => chart.BmsonSong == bmsonSong).BmsonSong);
        Assert.AreEqual(0, delta.ChartPathChanges.Count);
        Assert.IsTrue(delta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(delta.InvalidateParentFolderCache);
        Assert.IsTrue(delta.ClearDuplicatedCache);
    }

    [TestMethod]
    public void FixInstallationDirectory_ChartBmsReturnsMutationDeltaAndDuplicateRemovalCandidates()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile movedFile = CreateFile("C:\\Broken\\move.bms");
        TestableBmsFile duplicateFile = CreateFile("C:\\Broken\\dup.bms");
        ChartFile movedChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(movedFile),
            "C:\\Installed\\Move",
            string.Empty,
            string.Empty,
            []);
        ChartFile duplicateChart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(duplicateFile),
            "C:\\Installed\\Dup",
            string.Empty,
            string.Empty,
            []);

        LibraryFixInstallationResult result = service.FixInstallationDirectory(
            [movedChart, duplicateChart],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            delegate (ChartPackage package, string destinationDirectory)
            {
                if (package.ChartEntries[0].Chart.BmsFile == duplicateFile)
                {
                    package.ReplaceChartEntries([]);
                    return true;
                }
                package.ChartEntries[0].ApplyInstalledPath(Path.Combine(destinationDirectory, "move.bms"));
                return true;
            },
            chart => chart.BmsFile == duplicateFile);

        Assert.AreEqual(2, result.RequestedCount);
        Assert.AreEqual(1, result.MovedCount);
        Assert.AreEqual(1, result.DuplicateSkippedCount);
        Assert.AreEqual(1, result.MutationDelta.ChartPathChanges.Count);
        Assert.AreSame(movedFile, result.MutationDelta.ChartPathChanges[0].BmsFile);
        Assert.AreEqual(Path.Combine("C:\\Installed\\Move", "move.bms"), result.MutationDelta.ChartPathChanges[0].NewPath);
        Assert.AreEqual(1, result.ChartsToRemove.Count);
        Assert.AreSame(duplicateFile, result.ChartsToRemove[0].BmsFile);
        Assert.AreEqual(1, result.MaintenanceCharts.Count);
        Assert.AreSame(movedFile, result.MaintenanceCharts[0].BmsFile);
    }

    [TestMethod]
    public void FixInstallationDirectory_ChartBmsonReturnsBmsonSongPathChangeWithoutCompatibilityAdapter()
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
        ChartFile chart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(song),
            "C:\\Installed\\Move",
            string.Empty,
            string.Empty,
            []);

        LibraryFixInstallationResult result = service.FixInstallationDirectory(
            [chart],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            delegate (ChartPackage package, string destinationDirectory)
            {
                package.ChartEntries[0].ApplyInstalledPath(Path.Combine(destinationDirectory, "move.bmson"));
                return true;
            },
            _ => false);

        Assert.AreEqual(1, result.RequestedCount);
        Assert.AreEqual(1, result.MovedCount);
        Assert.AreEqual(1, result.MutationDelta.ChartPathChanges.Count);
        Assert.AreSame(song, result.MutationDelta.ChartPathChanges[0].BmsonSong);
        Assert.AreEqual("C:\\Broken\\move.bmson", result.MutationDelta.ChartPathChanges[0].OldPath);
        Assert.AreEqual(Path.Combine("C:\\Installed\\Move", "move.bmson"), result.MutationDelta.ChartPathChanges[0].NewPath);
        Assert.AreEqual(1, result.MaintenanceCharts.Count);
        Assert.AreSame(song, result.MaintenanceCharts[0].BmsonSong);
    }

    [TestMethod]
    public void FixInstallationDirectory_ChartBmsonDuplicateReturnsChartRemovalCandidateAfterConfirmation()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        var song = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Broken\\dup.bmson",
            folder = "C:\\Broken",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256 = new string('b', 64),
            title = "Bmson"
        };
        ChartFile chart = ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsonSong(song),
            "C:\\Installed\\Dup",
            string.Empty,
            string.Empty,
            []);
        int confirmCount = 0;

        LibraryFixInstallationResult result = service.FixInstallationDirectory(
            [chart],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            delegate (ChartPackage package, string _)
            {
                package.ReplaceChartEntries([]);
                return true;
            },
            confirmedChart =>
            {
                confirmCount++;
                Assert.AreSame(song, confirmedChart.BmsonSong);
                return true;
            });

        Assert.AreEqual(1, result.RequestedCount);
        Assert.AreEqual(0, result.MovedCount);
        Assert.AreEqual(1, result.DuplicateSkippedCount);
        Assert.AreEqual(1, confirmCount);
        Assert.AreEqual(1, result.ChartsToRemove.Count);
        Assert.AreEqual(LibraryChartKind.Bmson, result.ChartsToRemove[0].Kind);
        Assert.AreSame(song, result.ChartsToRemove[0].BmsonSong);
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
            Assert.AreEqual(1, delta.ChartPathChanges.Count);
            Assert.AreEqual(1, delta.ChartsToUnregister.Count);
            Assert.AreEqual(Path.Combine(tempDirectoryPath, "rename_me.bme"), delta.ChartPathChanges[0].NewPath);
            Assert.AreSame(renameFile, delta.ChartPathChanges[0].BmsFile);
            Assert.AreSame(duplicateFile, delta.ChartsToUnregister[0].BmsFile);
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
