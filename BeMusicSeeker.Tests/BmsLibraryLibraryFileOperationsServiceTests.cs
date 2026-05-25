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
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                folder = Path.Combine(tempDirectoryPath, "Pending"),
                title = "Bmson"
            }));
            var pendingPackage = ChartPackage.FromChartEntries([ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, nestedDirectoryPath), adapterlessBmsonEntry]);
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
                [LibraryChartRef.FromBmsFile(libraryFile)],
                CreateInstallDestinationOverlaySnapshot([CreateLibraryChartRefWithInstallDestination(libraryFile, sourceRoot)]),
                [pendingPackage],
                [installedPackage],
                unregister: false,
                notifyStorageRowPathChanges: false);

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
            LibraryInstallDestinationChange libraryDestinationChange = delta.UpdatedInstallDestinations.Single(change => ReferenceEquals(change.GetBmsStorageOwner(), libraryFile));
            ChartFile appliedLibraryChart = libraryDestinationChange.CreateAppliedChartSnapshot(delta.ChartPathChanges);
            Assert.AreEqual(Path.Combine(destinationRoot, "Nested", "chart.bms"), appliedLibraryChart.Path);
            Assert.AreEqual(destinationRoot, appliedLibraryChart.InstallDestination);
            Assert.AreEqual(Path.Combine(destinationRoot, "Nested"), delta.UpdatedInstalledPackagePaths[0].NewPath);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void BuildFolderMoveDelta_UsesChartSnapshotInstallDestinationForBmsonLibraryRef()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string sourceRoot = Path.Combine(tempDirectoryPath, "InstallSource");
            string destinationRoot = Path.Combine(tempDirectoryPath, "InstallDestination");
            string libraryDirectory = Path.Combine(tempDirectoryPath, "Library");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(libraryDirectory);
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(libraryDirectory, "chart.bmson"),
                folder = libraryDirectory,
                md5 = "abcdefabcdefabcdefabcdefabcdefab",
                sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd",
                title = "Bmson"
            };
            ChartFile bmsonChart = ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(bmsonSong),
                sourceRoot,
                "Install title",
                "Install artist",
                []);

            LibraryMutationDelta delta = service.BuildFolderMoveDelta(
                sourceRoot,
                destinationRoot,
                [],
                CreateInstallDestinationOverlaySnapshot([LibraryChartRef.FromChartFile(bmsonChart)]),
                [],
                [],
                unregister: false,
                notifyStorageRowPathChanges: false);

            LibraryInstallDestinationChange change = delta.UpdatedInstallDestinations.Single();
            Assert.IsNull(change.GetBmsStorageOwner());
            Assert.AreSame(bmsonSong, change.Chart.GetBmsonStorageOwner());
            Assert.AreEqual(sourceRoot, change.GetCurrentInstallDestination());
            Assert.AreEqual(destinationRoot, change.NewInstallDestination);
            ChartFile appliedChart = change.CreateAppliedChartSnapshot(delta.ChartPathChanges);
            Assert.AreSame(bmsonSong, appliedChart.GetBmsonStorageOwner());
            Assert.AreEqual(destinationRoot, appliedChart.InstallDestination);
        });
    }

    [TestMethod]
    public void InstallDestinationOverlaySnapshot_UsesDirectoryBoundary()
    {
        var sourceFile = CreateFile("C:\\Charts\\source.bms");
        var childFile = CreateFile("C:\\Charts\\child.bms");
        var siblingFile = CreateFile("C:\\Charts\\sibling.bms");

        InstallDestinationOverlayChartRefSnapshot snapshot = CreateInstallDestinationOverlaySnapshot([
            CreateLibraryChartRefWithInstallDestination(sourceFile, "C:\\Install\\Source"),
            CreateLibraryChartRefWithInstallDestination(childFile, "C:\\Install\\Source\\Child"),
            CreateLibraryChartRefWithInstallDestination(siblingFile, "C:\\Install\\SourceSibling")
        ]);

        List<LibraryChartRef> refs = snapshot.GetChartRefsUnderInstallDestination("C:\\Install\\Source");

        CollectionAssert.AreEquivalent(
            new[] { sourceFile, childFile },
            refs.Select(chart => chart.GetBmsStorageOwner()).ToArray());
    }

    [TestMethod]
    public void BuildFolderMoveDelta_RewritesInstallDestinationWithTrailingSourceSeparator()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile libraryFile = CreateFile("C:\\Charts\\library.bms");
        TestableBmsFile pendingFile = CreateFile("C:\\Charts\\pending.bms");
        var pendingEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, "C:\\Install\\Source");
        var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Install\\Source\\",
            "D:\\Install\\Destination\\",
            [],
            CreateInstallDestinationOverlaySnapshot([CreateLibraryChartRefWithInstallDestination(libraryFile, "C:\\Install\\Source\\Child")]),
            [pendingPackage],
            [],
            unregister: false,
            notifyStorageRowPathChanges: false);

        LibraryInstallDestinationChange pendingChange = delta.UpdatedInstallDestinations.Single(change => ReferenceEquals(change.Entry, pendingEntry));
        LibraryInstallDestinationChange libraryChange = delta.UpdatedInstallDestinations.Single(change => ReferenceEquals(change.GetBmsStorageOwner(), libraryFile));
        Assert.AreEqual("D:\\Install\\Destination", pendingChange.NewInstallDestination);
        Assert.AreEqual("D:\\Install\\Destination\\Child", libraryChange.NewInstallDestination);
    }

    [TestMethod]
    public void FolderMoveAppliedInstallDestinationSnapshotDoesNotRewritePendingEntryByPathCollision()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string sourceRoot = Path.Combine(tempDirectoryPath, "Src");
            string destinationRoot = Path.Combine(tempDirectoryPath, "Dst");
            Directory.CreateDirectory(sourceRoot);
            string collidingPath = Path.Combine(sourceRoot, "chart.bms");
            TestableBmsFile libraryFile = CreateFile(collidingPath);
            TestableBmsFile pendingFile = CreateFile(collidingPath);
            var pendingEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, sourceRoot);
            var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);

            LibraryMutationDelta delta = service.BuildFolderMoveDelta(
                sourceRoot,
                destinationRoot,
                [LibraryChartRef.FromBmsFile(libraryFile)],
                CreateInstallDestinationOverlaySnapshot([CreateLibraryChartRefWithInstallDestination(libraryFile, sourceRoot)]),
                [pendingPackage],
                [],
                unregister: false,
                notifyStorageRowPathChanges: false);

            LibraryInstallDestinationChange pendingChange = delta.UpdatedInstallDestinations.Single(change => ReferenceEquals(change.Entry, pendingEntry));
            ChartFile pendingSnapshot = pendingChange.CreateAppliedChartSnapshot(delta.ChartPathChanges);
            Assert.AreEqual(collidingPath, pendingSnapshot.Path);
            Assert.AreEqual(destinationRoot, pendingSnapshot.InstallDestination);
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
            LibraryChartRef libraryRef = CreateLibraryChartRefWithInstallDestination(libraryFile, folderPath);
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = bmsonChartPath,
                folder = folderPath
            };
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            pendingFile.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "pending bms warning");
            PackageChartEntry pendingBmsEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                pendingFile,
                folderPath,
                "Pending BMS title",
                "Pending BMS artist",
                [Path.Combine(tempDirectoryPath, "Other")]);
            var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                folder = Path.Combine(tempDirectoryPath, "Pending"),
                title = "Bmson"
            }));
            adapterlessBmsonEntry.ApplyInstallDestination(folderPath, "Deleted title", "Deleted artist");
            var pendingPackage = ChartPackage.FromChartEntries([pendingBmsEntry, adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(folderPath, []);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());

            LibraryRemovalResult result = service.DeleteLibraryCharts(
                [libraryRef, LibraryChartRef.FromBmsonSong(bmsonSong)],
                CreateLibraryChartRefLookup([libraryRef, LibraryChartRef.FromBmsonSong(bmsonSong)]),
                CreateInstallDestinationOverlaySnapshot([libraryRef]),
                [pendingPackage],
                lookupCache,
                false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.IsTrue(result.RemovedCharts.Any(chart => ReferenceEquals(chart.GetBmsStorageOwner(), libraryFile)));
            Assert.IsTrue(result.RemovedCharts.Any(chart => ReferenceEquals(chart.GetBmsonStorageOwner(), bmsonSong)));
            Assert.AreEqual(0, result.Failures.Count);
            Assert.AreEqual(string.Empty, pendingBmsEntry.Chart.InstallDestination);
            Assert.AreEqual(string.Empty, pendingBmsEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, pendingBmsEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(Array.Empty<string>(), pendingBmsEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsFalse(pendingBmsEntry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            LibraryInstallDestinationChange libraryClear = result.MutationDelta.UpdatedInstallDestinations.Single();
            Assert.AreSame(libraryFile, libraryClear.GetBmsStorageOwner());
            Assert.IsTrue(libraryClear.ClearInstallDestinationState);
            Assert.IsNull(libraryClear.NewInstallDestination);
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
                CreateLibraryChartRefLookup([LibraryChartRef.FromBmsonSong(song)]),
                CreateInstallDestinationOverlaySnapshot(),
                [],
                lookupCache,
                false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.RemovedCharts.Count);
            Assert.AreEqual(LibraryChartKind.Bmson, result.RemovedCharts[0].Kind);
            Assert.AreSame(song, result.RemovedCharts[0].GetBmsonStorageOwner());
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
                CreateLibraryChartRefLookup([LibraryChartRef.FromBmsFile(canonicalFile)]),
                CreateInstallDestinationOverlaySnapshot(),
                [],
                lookupCache,
                true,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.RemovedCharts.Count);
            Assert.AreSame(canonicalFile, result.RemovedCharts[0].GetBmsStorageOwner());
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
                CreateLibraryChartRefLookup([LibraryChartRef.FromBmsFile(catalogFile)]),
                CreateInstallDestinationOverlaySnapshot(),
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
                CreateLibraryChartRefLookup([LibraryChartRef.FromBmsFile(canonicalFile)]),
                CreateInstallDestinationOverlaySnapshot(),
                [],
                new DirectoryResourceLookupCache(),
                false,
                _ => true,
                fileMutationService,
                null,
                null);

            Assert.AreEqual(1, result.RemovedCharts.Count);
            Assert.AreSame(canonicalFile, result.RemovedCharts[0].GetBmsStorageOwner());
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
                CreateLibraryChartRefLookup([LibraryChartRef.FromBmsFile(catalogFile)]),
                CreateInstallDestinationOverlaySnapshot(),
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
                CreateLibraryChartRefLookup([LibraryChartRef.FromBmsFile(libraryFile)]),
                CreateInstallDestinationOverlaySnapshot(),
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
            Assert.AreSame(libraryFile, result.RemovedCharts[0].GetBmsStorageOwner());
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
                CreateLibraryChartRefLookup([LibraryChartRef.FromBmsFile(libraryFile)]));

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
                CreateLibraryChartRefLookup([LibraryChartRef.FromBmsFile(catalogFile)]));

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
                CreateLibraryChartRefLookup([LibraryChartRef.FromBmsFile(selectedFile), LibraryChartRef.FromBmsFile(remainingFile)]));

            Assert.AreEqual(0, paths.Count);
        });
    }

    [TestMethod]
    public void BuildFolderMoveDelta_CanSuppressStorageRowPathNotificationForRename()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile file1 = CreateFile("C:\\Lib\\Src\\A\\a.bms");
        TestableBmsFile file2 = CreateFile("C:\\Lib\\Src\\B\\b.bms");

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            CreateLibraryChartRefs([file1, file2]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: false,
            notifyStorageRowPathChanges: false);

        Assert.AreEqual(2, delta.FolderPathChanges.Count);
        Assert.AreEqual(2, delta.ChartPathChanges.Count);
        Assert.AreEqual(0, delta.ChartsToUnregister.Count);
        CollectionAssert.AreEquivalent(
            new[] { "C:\\Lib\\Dst\\A\\a.bms", "C:\\Lib\\Dst\\B\\b.bms" },
            delta.ChartPathChanges.Select(change => change.NewPath).ToArray());
        Assert.IsFalse(delta.NotifyStorageRowPathChanges);
        Assert.IsTrue(delta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(delta.InvalidateParentFolderCache);
    }

    [TestMethod]
    public void BuildFolderMoveDelta_RequestsStorageRowPathNotificationForRootMove()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        TestableBmsFile file = CreateFile("C:\\Lib\\Src\\A\\a.bms");

        LibraryMutationDelta delta = service.BuildFolderMoveDelta(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            CreateLibraryChartRefs([file]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: false,
            notifyStorageRowPathChanges: true);

        Assert.IsTrue(delta.NotifyStorageRowPathChanges);
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
                [],
                renameRootFolder: true,
                folders =>
                {
                    CollectionAssert.AreEqual(new[] { sourcePath }, folders.ToArray());
                    return [chart, nestedChart];
                },
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
                [],
                renameRootFolder: true,
                folders =>
                {
                    CollectionAssert.AreEqual(new[] { sourcePath }, folders.ToArray());
                    return [bmsonChart];
                },
                (children, parentDir, _) => Path.Combine(parentDir, children.First().Title + "_" + children.First().Artist));

            Assert.AreEqual(1, plans.Count);
            Assert.AreEqual(Path.Combine(rootPath, "BmsonTitle_BmsonArtist"), plans[0].DestinationDirectory);
        });
    }

    [TestMethod]
    public void BuildAutoRenamePlansForSourceFolders_UsesDirectoryInputWithoutSelectedCharts()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string rootPath = Path.Combine(tempDirectoryPath, "Songs");
            string sourcePath = Path.Combine(rootPath, "OldFolder");
            Directory.CreateDirectory(sourcePath);
            string chartPath = Path.Combine(sourcePath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            TestableBmsFile bmsRow = CreateFile(chartPath);
            bmsRow.SetTitleForTest("BmsTitle");
            ChartFile bmsChart = ChartFileProjection.FromBmsFile(bmsRow);

            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlansForSourceFolders(
                [sourcePath],
                [],
                renameRootFolder: true,
                folders =>
                {
                    CollectionAssert.AreEqual(new[] { sourcePath }, folders.ToArray());
                    return [bmsChart];
                },
                (children, parentDir, _) => Path.Combine(parentDir, children.First().Title));

            Assert.AreEqual(1, plans.Count);
            Assert.AreEqual(Path.Combine(rootPath, "BmsTitle"), plans[0].DestinationDirectory);
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
                [],
                renameRootFolder: true,
                folders =>
                {
                    CollectionAssert.AreEqual(new[] { sourcePath }, folders.ToArray());
                    return [bmsChart, bmsonChart];
                },
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
            CreateLibraryChartRefs([], [bmsonSong]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: false,
            notifyStorageRowPathChanges: false);

        Assert.AreEqual(1, delta.ChartPathChanges.Count);
        Assert.AreSame(bmsonSong, delta.ChartPathChanges[0].GetBmsonStorageOwner());
        Assert.AreEqual("C:\\Lib\\Src\\Pkg\\chart.bmson", delta.ChartPathChanges[0].OldPath);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bmson", delta.ChartPathChanges[0].NewPath);
        Assert.IsTrue(delta.InvalidateInstalledDirectoryIndex);
        Assert.IsTrue(delta.InvalidateParentFolderCache);
        Assert.IsFalse(delta.NotifyStorageRowPathChanges);
    }

    [TestMethod]
    public void BuildFolderMoveDelta_BmsonRootMove_RequestsStorageRowPathNotification()
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
            CreateLibraryChartRefs([], [bmsonSong]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: false,
            notifyStorageRowPathChanges: true);

        Assert.IsTrue(delta.NotifyStorageRowPathChanges);
        Assert.AreEqual(1, delta.ChartPathChanges.Count);
        Assert.AreSame(bmsonSong, delta.ChartPathChanges[0].GetBmsonStorageOwner());
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
            TestableBmsFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms"));
            var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                folder = Path.Combine(tempDirectoryPath, "Pending"),
                title = "Bmson"
            }));
            var pendingPackage = ChartPackage.FromChartEntries([ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, sourceRoot), adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());

            LibraryMergeResult result = service.PrepareMergeDirectory(
                sourceRoot,
                destinationRoot,
                [LibraryChartRef.FromBmsFile(libraryFile)],
                CreateInstallDestinationOverlaySnapshot([CreateLibraryChartRefWithInstallDestination(libraryFile, sourceRoot)]),
                [pendingPackage],
                [],
                _ => new PrimaryHashSetLookup());

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
                CreateLibraryChartRefs([libraryFile]),
                CreateInstallDestinationOverlaySnapshot(),
                [pendingPackage],
                [],
                _ => new PrimaryHashSetLookup());

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
                CreateLibraryChartRefs([bmsFile], [bmsonSong]),
                CreateInstallDestinationOverlaySnapshot(),
                [],
                [],
                charts =>
                {
                    excludedHashes = [.. charts.Select(chart => chart.PrimaryLookupHash).Where(hash => !string.IsNullOrWhiteSpace(hash))];
                    return EmptyPrimaryHashLookup.Instance;
                });

            Assert.IsTrue(result.Success);
            CollectionAssert.AreEqual(new[] { bmsFile }, result.SourceCharts.Select(chart => chart.GetBmsStorageOwner()).Where(file => file != null).ToArray());
            CollectionAssert.AreEqual(new[] { bmsonSong }, result.SourceCharts.Select(chart => chart.GetBmsonStorageOwner()).Where(song => song != null).ToArray());
            Assert.AreEqual(2, result.Repackage.ChartEntries.Count);
            Assert.IsTrue(result.Repackage.ChartEntries.Any(entry => ReferenceEquals(entry.Chart.GetBmsStorageOwner(), bmsFile)));
            PackageChartEntry bmsonEntry = result.Repackage.ChartEntries.Single(entry => entry.Chart.Kind == ChartFileKind.Bmson);
            Assert.AreSame(bmsonSong, bmsonEntry.Chart.GetBmsonStorageOwner());
            Assert.IsNull(bmsonEntry.GetBmsOwnerForTest());
            CollectionAssert.AreEquivalent(new[] { bmsFile.hash, bmsonSong.md5 }, excludedHashes.ToArray());
        });
    }

    [TestMethod]
    public void PrepareMergeDirectory_SourceChartsKeepPreparedPathAfterOwnerPathChanges()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string sourceRoot = Path.Combine(tempDirectoryPath, "Src");
            string destinationRoot = Path.Combine(tempDirectoryPath, "Dst");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(destinationRoot);
            string sourcePath = Path.Combine(sourceRoot, "chart.bms");
            string movedPath = Path.Combine(destinationRoot, "chart.bms");
            TestableBmsFile bmsFile = CreateFile(sourcePath);

            LibraryMergeResult result = service.PrepareMergeDirectory(
                sourceRoot,
                destinationRoot,
                CreateLibraryChartRefs([bmsFile]),
                CreateInstallDestinationOverlaySnapshot(),
                [],
                [],
                _ => EmptyPrimaryHashLookup.Instance);

            bmsFile.path = movedPath;

            Assert.IsTrue(result.Success);
            Assert.AreEqual(sourcePath, result.SourceCharts.Single().Path);
            Assert.AreEqual(sourcePath, result.SourceCharts.Single().ToChartFile().Path);
            Assert.AreSame(bmsFile, result.SourceCharts.Single().GetBmsStorageOwner());
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
            CreateLibraryChartRefs([bmsFile], [bmsonSong]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: false,
            notifyStorageRowPathChanges: true);

        Assert.AreEqual(2, delta.ChartPathChanges.Count);
        LibraryChartPathChange bmsPathChange = delta.ChartPathChanges.Single(change => change.GetBmsStorageOwner() == bmsFile);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bms", bmsPathChange.NewPath);
        LibraryChartPathChange bmsonPathChange = delta.ChartPathChanges.Single(change => change.GetBmsonStorageOwner() == bmsonSong);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bmson", bmsonPathChange.NewPath);
        Assert.IsTrue(delta.NotifyStorageRowPathChanges);
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
            CreateLibraryChartRefs([bmsFile], [bmsonSong]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: true);

        Assert.AreEqual(2, delta.ChartsToUnregister.Count);
        Assert.AreSame(bmsFile, delta.ChartsToUnregister.Single(chart => chart.GetBmsStorageOwner() == bmsFile).GetBmsStorageOwner());
        Assert.AreSame(bmsonSong, delta.ChartsToUnregister.Single(chart => chart.GetBmsonStorageOwner() == bmsonSong).GetBmsonStorageOwner());
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
            delegate (ChartPackage package, string destinationDirectory)
            {
                if (package.ChartEntries[0].Chart.GetBmsStorageOwner() == duplicateFile)
                {
                    package.ReplaceChartEntries([]);
                    return true;
                }
                package.ChartEntries[0].ApplyInstalledPath(Path.Combine(destinationDirectory, "move.bms"));
                return true;
            },
            chart => chart.GetBmsStorageOwner() == duplicateFile);

        Assert.AreEqual(2, result.RequestedCount);
        Assert.AreEqual(1, result.MovedCount);
        Assert.AreEqual(1, result.DuplicateSkippedCount);
        Assert.AreEqual(1, result.MutationDelta.ChartPathChanges.Count);
        Assert.AreSame(movedFile, result.MutationDelta.ChartPathChanges[0].GetBmsStorageOwner());
        Assert.AreEqual(Path.Combine("C:\\Installed\\Move", "move.bms"), result.MutationDelta.ChartPathChanges[0].NewPath);
        Assert.AreEqual(1, result.MutationDelta.UpdatedInstallDestinations.Count);
        Assert.AreSame(movedFile, result.MutationDelta.UpdatedInstallDestinations[0].GetBmsStorageOwner());
        Assert.IsNull(result.MutationDelta.UpdatedInstallDestinations[0].NewInstallDestination);
        Assert.AreEqual(1, result.ChartsToRemove.Count);
        Assert.AreSame(duplicateFile, result.ChartsToRemove[0].GetBmsStorageOwner());
        Assert.AreEqual(1, result.MaintenanceCharts.Count);
        Assert.AreSame(movedFile, result.MaintenanceCharts[0].GetBmsStorageOwner());
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
            delegate (ChartPackage package, string destinationDirectory)
            {
                package.ChartEntries[0].ApplyInstalledPath(Path.Combine(destinationDirectory, "move.bmson"));
                return true;
            },
            _ => false);

        Assert.AreEqual(1, result.RequestedCount);
        Assert.AreEqual(1, result.MovedCount);
        Assert.AreEqual(1, result.MutationDelta.ChartPathChanges.Count);
        Assert.AreSame(song, result.MutationDelta.ChartPathChanges[0].GetBmsonStorageOwner());
        Assert.AreEqual("C:\\Broken\\move.bmson", result.MutationDelta.ChartPathChanges[0].OldPath);
        Assert.AreEqual(Path.Combine("C:\\Installed\\Move", "move.bmson"), result.MutationDelta.ChartPathChanges[0].NewPath);
        Assert.AreEqual(1, result.MaintenanceCharts.Count);
        Assert.AreSame(song, result.MaintenanceCharts[0].GetBmsonStorageOwner());
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
            delegate (ChartPackage package, string _)
            {
                package.ReplaceChartEntries([]);
                return true;
            },
            confirmedChart =>
            {
                confirmCount++;
                Assert.AreSame(song, confirmedChart.GetBmsonStorageOwner());
                return true;
            });

        Assert.AreEqual(1, result.RequestedCount);
        Assert.AreEqual(0, result.MovedCount);
        Assert.AreEqual(1, result.DuplicateSkippedCount);
        Assert.AreEqual(1, confirmCount);
        Assert.AreEqual(1, result.ChartsToRemove.Count);
        Assert.AreEqual(LibraryChartKind.Bmson, result.ChartsToRemove[0].Kind);
        Assert.AreSame(song, result.ChartsToRemove[0].GetBmsonStorageOwner());
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
            string bmsonPath = Path.Combine(tempDirectoryPath, "skip.bmson");
            File.WriteAllText(renameSourcePath, "rename");
            File.WriteAllText(duplicateSourcePath, "same");
            File.WriteAllText(duplicateDestinationPath, "same");
            File.WriteAllText(bmsonPath, "{}");
            TestableBmsFile renameFile = CreateFile(renameSourcePath);
            TestableBmsFile duplicateFile = CreateFile(duplicateSourcePath);
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = bmsonPath,
                md5 = new string('b', 32),
                sha256 = new string('2', 64)
            };
            int callbackCount = 0;

            LibraryMutationDelta delta = service.RenameLibraryFileExtensions(
                [
                    ChartFileProjection.FromBmsFile(renameFile),
                    ChartFileProjection.FromBmsFile(duplicateFile),
                    ChartFileProjection.FromBmsonSong(bmsonSong)
                ],
                ".bme",
                unregister: false,
                (file, requestedPath) =>
                {
                    callbackCount++;
                    return service.ProcessInvalidExtensionRename(file, requestedPath, fileMutationService, null);
                });

            Assert.AreEqual(2, callbackCount);
            Assert.AreEqual(1, delta.RenamedCount);
            Assert.AreEqual(1, delta.DuplicateDeletedCount);
            Assert.AreEqual(0, delta.SkippedCount);
            Assert.AreEqual(1, delta.ChartPathChanges.Count);
            Assert.AreEqual(1, delta.ChartsToUnregister.Count);
            Assert.AreEqual(Path.Combine(tempDirectoryPath, "rename_me.bme"), delta.ChartPathChanges[0].NewPath);
            Assert.AreSame(renameFile, delta.ChartPathChanges[0].GetBmsStorageOwner());
            Assert.AreSame(duplicateFile, delta.ChartsToUnregister[0].GetBmsStorageOwner());
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectoryPath, "rename_me.bme")));
            Assert.IsFalse(File.Exists(renameSourcePath));
            Assert.IsFalse(File.Exists(duplicateSourcePath));
            Assert.IsTrue(File.Exists(duplicateDestinationPath));
            Assert.IsTrue(File.Exists(bmsonPath));
        });
    }

    private static List<LibraryChartRef> CreateLibraryChartRefs(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs = null!)
    {
        return [.. (bmsFiles ?? []).Select(LibraryChartRef.FromBmsFile)
            .Concat((bmsonSongs ?? []).Select(LibraryChartRef.FromBmsonSong))
            .Where(chart => chart != null)];
    }

    private static LibraryChartRefIndexSnapshot CreateLibraryChartRefLookup(IEnumerable<LibraryChartRef> charts)
    {
        return LibraryChartRefIndexSnapshot.FromLibraryChartRefs(charts);
    }

    private static InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlaySnapshot(IEnumerable<LibraryChartRef> charts = null!)
    {
        return InstallDestinationOverlayChartRefSnapshot.FromLibraryChartRefs(charts ?? []);
    }

    private static LibraryChartRef CreateLibraryChartRefWithInstallDestination(
        BMSFile file,
        string installDestination,
        string title = "",
        string artist = "",
        IReadOnlyList<string> suggestions = null!)
    {
        return LibraryChartRef.FromChartFile(ChartFileProjection.WithPackageState(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: true),
            installDestination,
            title,
            artist,
            suggestions ?? [],
            file?.Warnings.ToStructuredList() ?? []));
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
