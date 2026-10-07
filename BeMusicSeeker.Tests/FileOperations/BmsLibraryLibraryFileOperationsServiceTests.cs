using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = sourcePath
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
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = sourcePath
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
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = sourcePath
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
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = sourcePath
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
        ChartFile selectedA = CreateFile("C:\\Pending\\Pkg1\\a.bms");
        ChartFile selectedB = CreateFile("C:\\Pending\\Pkg1\\b.bms");
        ChartFile partial = CreateFile("C:\\Pending\\Pkg2\\a.bms");
        ChartFile partialUnselected = CreateFile("C:\\Pending\\Pkg2\\b.bms");
        ChartPackage pkg1 = ChartPackageTestExtensions.CreatePackage([selectedA, selectedB]);
        pkg1.path = "C:\\Pending\\Pkg1";
        pkg1.delete_parent = false;
        ChartPackage pkg2 = ChartPackageTestExtensions.CreatePackage([partial, partialUnselected]);
        pkg2.path = "C:\\Pending\\Pkg2";
        pkg2.delete_parent = false;

        ChartFile singleA = CreateFile("C:\\Pending\\Singles\\a.bms");
        ChartFile singleB = CreateFile("C:\\Pending\\Singles\\b.bms");
        ChartPackage singlePackageA = ChartPackageTestExtensions.CreatePackage([singleA]);
        singlePackageA.path = singleA.Path;
        ChartPackage singlePackageB = ChartPackageTestExtensions.CreatePackage([singleB]);
        singlePackageB.path = singleB.Path;
        singlePackageB.delete_parent = true;

        List<ChartPackage> result = service.GetPendingPackagesFullyCoveredBySelection(
            [pkg1, pkg2, singlePackageA, singlePackageB],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                selectedA.Path, selectedB.Path, partial.Path, singleA.Path, singleB.Path
            });

        CollectionAssert.AreEqual(new[] { pkg1 }, result);
    }

    [TestMethod]
    public void MoveFolderAndUpdateReferences_RewritesDirectoryIndexAndBuildFolderMoveFactsTracksReferences()
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
            var resourceIndex = new LibraryResourceIndex();
            DirectoryResourceLookupCache lookupCache = resourceIndex.DirectoryLookupCache;
            var resourceIndexOwner = new LibraryResourceIndexOwner(resourceIndex);
            lookupCache.AddDir(sourceRoot, [sourceHash], [], []);
            lookupCache.AddDir(nestedDirectoryPath, [nestedHash], [], []);
            lookupCache.EnsureAudioRelativeDirectoriesByHashes([sourceHash, nestedHash]);

            ChartFile libraryFile = CreateFile(Path.Combine(sourceRoot, "Nested", "chart.bms"));
            ChartFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms")) with { Token = null };
            var adapterlessBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                Folder = Path.Combine(tempDirectoryPath, "Pending"),
                Title = "Bmson",
                RawTitle = "Bmson"
            }));
            var pendingPackage = ChartPackage.FromChartEntries([ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, nestedDirectoryPath), adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            ChartPackage installedPackage = ChartPackageTestExtensions.CreatePackage([libraryFile]);
            installedPackage.path = nestedDirectoryPath;
            installedPackage.delete_parent = false;
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());

            service.MoveFolder(
                sourceRoot,
                destinationRoot,
                fileMutationService,
                null);
            resourceIndexOwner.MoveFolderReferences(sourceRoot, destinationRoot);
            DirectoryResourceLookupCache priorLookupCache = lookupCache;
            lookupCache = resourceIndexOwner.CaptureSnapshot().DirectoryLookupCache;
            LibraryFolderMoveFacts facts = service.BuildFolderMoveFacts(
                sourceRoot,
                destinationRoot,
                [LibraryChartRef.FromChartFile((libraryFile))],
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
            Assert.IsNotNull(priorLookupCache.GetEntryOrNull(sourceRoot));
            Assert.IsNotNull(priorLookupCache.GetEntryOrNull(nestedDirectoryPath));
            Assert.IsNull(priorLookupCache.GetEntryOrNull(destinationRoot));
            Assert.AreEqual(2, facts.PackageReferenceFacts.InstallDestinationChanges.Count);
            Assert.AreEqual(1, facts.PackageReferenceFacts.InstalledPackagePathChanges.Count);
            CollectionAssert.AreEquivalent(
                new[] { Path.Combine(destinationRoot, "Nested"), destinationRoot },
                facts.PackageReferenceFacts.InstallDestinationChanges.Select(change => change.NewInstallDestination).ToArray());
            LibraryInstallDestinationChange libraryDestinationChange = facts.PackageReferenceFacts.InstallDestinationChanges.Single(change => ReferenceEquals(change.Chart.Token, libraryFile.Token));
            ChartFile appliedLibraryChart = libraryDestinationChange.CreateAppliedChartSnapshot(facts.CatalogFacts.ChartPathChanges);
            Assert.AreEqual(Path.Combine(destinationRoot, "Nested", "chart.bms"), appliedLibraryChart.Path);
            Assert.AreEqual(destinationRoot, appliedLibraryChart.InstallDestination);
            Assert.AreEqual(Path.Combine(destinationRoot, "Nested"), facts.PackageReferenceFacts.InstalledPackagePathChanges[0].NewPath);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
        });
    }

    [TestMethod]
    public void MoveFolderAndUpdateReferences_CrossVolumeMoveRewritesDirectoryIndex()
    {
        using var directories = CrossVolumeTestDirectories.CreateOrInconclusive(nameof(MoveFolderAndUpdateReferences_CrossVolumeMoveRewritesDirectoryIndex));
        var service = new BmsLibraryLibraryFileOperationsService();
        string sourceRoot = Path.Combine(directories.SourceBaseDirectory, "Src");
        string nestedDirectoryPath = Path.Combine(sourceRoot, "Nested");
        Directory.CreateDirectory(nestedDirectoryPath);
        File.WriteAllText(Path.Combine(nestedDirectoryPath, "chart.bms"), "#PLAYER 1");
        string destinationRoot = Path.Combine(directories.DestinationBaseDirectory, "Dst");
        uint sourceHash = ChartResourceKeyHash.GetLookupHash("root.wav");
        uint nestedHash = ChartResourceKeyHash.GetLookupHash("chart.bms");
        var resourceIndex = new LibraryResourceIndex();
        DirectoryResourceLookupCache lookupCache = resourceIndex.DirectoryLookupCache;
        var resourceIndexOwner = new LibraryResourceIndexOwner(resourceIndex);
        lookupCache.AddDir(sourceRoot, [sourceHash], [], []);
        lookupCache.AddDir(nestedDirectoryPath, [nestedHash], [], []);
        lookupCache.EnsureAudioRelativeDirectoriesByHashes([sourceHash, nestedHash]);

        service.MoveFolder(
            sourceRoot,
            destinationRoot,
            new ResilientFileMutationService(),
            new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree));
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult =
            resourceIndexOwner.MoveFolderReferences(sourceRoot, destinationRoot).MutationResult;
        DirectoryResourceLookupCache priorLookupCache = lookupCache;
        lookupCache = resourceIndexOwner.CaptureSnapshot().DirectoryLookupCache;

        Assert.IsFalse(LongPathFileSystem.DirectoryExists(sourceRoot));
        Assert.IsTrue(LongPathFileSystem.DirectoryExists(destinationRoot));
        Assert.IsTrue(LongPathFileSystem.FileExists(Path.Combine(destinationRoot, "Nested", "chart.bms")));
        CollectionAssert.AreEquivalent(new[] { destinationRoot }, lookupCache.GetDirectoriesByAudioRelativeHash(sourceHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { Path.Combine(destinationRoot, "Nested") }, lookupCache.GetDirectoriesByAudioRelativeHash(nestedHash).ToArray());
        Assert.IsNull(lookupCache.GetEntryOrNull(sourceRoot));
        Assert.IsNull(lookupCache.GetEntryOrNull(nestedDirectoryPath));
        Assert.IsNotNull(priorLookupCache.GetEntryOrNull(sourceRoot));
        Assert.IsNotNull(priorLookupCache.GetEntryOrNull(nestedDirectoryPath));
        Assert.IsTrue(mutationResult.Changed);
    }

    [TestMethod]
    public void UpdateMovedFolderReferences_RewritesMultipleMovedFoldersInOnePass()
    {
        var resourceIndex = new LibraryResourceIndex();
        DirectoryResourceLookupCache lookupCache = resourceIndex.DirectoryLookupCache;
        var resourceIndexOwner = new LibraryResourceIndexOwner(resourceIndex);
        string firstSource = "C:\\Lib\\First";
        string firstNestedSource = "C:\\Lib\\First\\Nested";
        string secondSource = "C:\\Lib\\Second";
        string firstDestination = "C:\\Lib\\RenamedFirst";
        string firstNestedDestination = "C:\\Lib\\RenamedFirst\\Nested";
        string secondDestination = "C:\\Lib\\RenamedSecond";
        uint firstHash = ChartResourceKeyHash.GetLookupHash("first.wav");
        uint firstNestedHash = ChartResourceKeyHash.GetLookupHash("nested.wav");
        uint secondHash = ChartResourceKeyHash.GetLookupHash("second.wav");
        lookupCache.AddDir(firstSource, [firstHash], [], []);
        lookupCache.AddDir(firstNestedSource, [firstNestedHash], [], []);
        lookupCache.AddDir(secondSource, [secondHash], [], []);
        lookupCache.EnsureAudioRelativeDirectoriesByHashes([firstHash, firstNestedHash, secondHash]);

        LibraryResourceIndexMovedFoldersResult updateResult = resourceIndexOwner.UpdateMovedFolderReferences(
        [
            new LibraryFolderPathChange
            {
                OldFolderPath = firstSource,
                NewFolderPath = firstDestination
            },
            new LibraryFolderPathChange
            {
                OldFolderPath = secondSource,
                NewFolderPath = secondDestination
            }
        ]);
        DirectoryResourceLookupCache priorLookupCache = lookupCache;
        lookupCache = resourceIndexOwner.CaptureSnapshot().DirectoryLookupCache;
        DirectoryResourceLookupCache.ReverseLookupMutationResult result = updateResult.Receipt.MutationResult;

        Assert.IsTrue(result.Changed);
        Assert.AreEqual(2, updateResult.MoveCount);
        Assert.AreEqual(3, updateResult.LookupKeyCount);
        Assert.AreEqual(3, updateResult.MatchedKeyCount);
        Assert.AreEqual(3, result.ReplacedDirectoryCount);
        CollectionAssert.AreEquivalent(new[] { firstDestination }, lookupCache.GetDirectoriesByAudioRelativeHash(firstHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { firstNestedDestination }, lookupCache.GetDirectoriesByAudioRelativeHash(firstNestedHash).ToArray());
        CollectionAssert.AreEquivalent(new[] { secondDestination }, lookupCache.GetDirectoriesByAudioRelativeHash(secondHash).ToArray());
        Assert.IsNull(lookupCache.GetEntryOrNull(firstSource));
        Assert.IsNull(lookupCache.GetEntryOrNull(firstNestedSource));
        Assert.IsNull(lookupCache.GetEntryOrNull(secondSource));
        Assert.IsNotNull(lookupCache.GetEntryOrNull(firstDestination));
        Assert.IsNotNull(lookupCache.GetEntryOrNull(firstNestedDestination));
        Assert.IsNotNull(lookupCache.GetEntryOrNull(secondDestination));
        Assert.IsNotNull(priorLookupCache.GetEntryOrNull(firstSource));
        Assert.IsNotNull(priorLookupCache.GetEntryOrNull(firstNestedSource));
        Assert.IsNotNull(priorLookupCache.GetEntryOrNull(secondSource));
    }

    [TestMethod]
    public void BuildFolderMoveFacts_UsesChartSnapshotInstallDestinationForBmsonLibraryRef()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string sourceRoot = Path.Combine(tempDirectoryPath, "InstallSource");
            string destinationRoot = Path.Combine(tempDirectoryPath, "InstallDestination");
            string libraryDirectory = Path.Combine(tempDirectoryPath, "Library");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(libraryDirectory);
            ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Token = new OwnedChartToken(),
                Path = Path.Combine(libraryDirectory, "chart.bmson"),
                Folder = libraryDirectory,
                Md5 = "abcdefabcdefabcdefabcdefabcdefab",
                Sha256 = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd",
                Title = "Bmson",
                RawTitle = "Bmson"
            };
            ChartFile bmsonChart = ChartFileProjection.WithPackageState(
                (bmsonSong),
                sourceRoot,
                "Install title",
                "Install artist",
                []);

            LibraryFolderMoveFacts facts = service.BuildFolderMoveFacts(
                sourceRoot,
                destinationRoot,
                [],
                CreateInstallDestinationOverlaySnapshot([bmsonChart]),
                [],
                [],
                unregister: false,
                notifyStorageRowPathChanges: false);

            LibraryInstallDestinationChange change = facts.PackageReferenceFacts.InstallDestinationChanges.Single();
            Assert.AreSame(bmsonSong.Token, change.Chart.Token);
            Assert.AreEqual(sourceRoot, change.GetCurrentInstallDestination());
            Assert.AreEqual(destinationRoot, change.NewInstallDestination);
            ChartFile appliedChart = change.CreateAppliedChartSnapshot(facts.CatalogFacts.ChartPathChanges);
            Assert.AreSame(bmsonSong.Token, appliedChart.Token);
            Assert.AreEqual(destinationRoot, appliedChart.InstallDestination);
        });
    }

    [TestMethod]
    public void InstallDestinationOverlaySnapshot_UsesDirectoryBoundary()
    {
        ChartFile sourceFile = CreateFile("C:\\Charts\\source.bms");
        ChartFile childFile = CreateFile("C:\\Charts\\child.bms");
        ChartFile siblingFile = CreateFile("C:\\Charts\\sibling.bms");

        InstallDestinationOverlayChartRefSnapshot snapshot = CreateInstallDestinationOverlaySnapshot([
            CreateLibraryChartRefWithInstallDestination(sourceFile, "C:\\Install\\Source"),
            CreateLibraryChartRefWithInstallDestination(childFile, "C:\\Install\\Source\\Child"),
            CreateLibraryChartRefWithInstallDestination(siblingFile, "C:\\Install\\SourceSibling")
        ]);

        List<LibraryChartRef> refs = snapshot.GetChartRefsUnderInstallDestination("C:\\Install\\Source");

        CollectionAssert.AreEquivalent(
            new[] { sourceFile.Path, childFile.Path },
            refs.Select(chart => chart.Path).ToArray());
    }

    [TestMethod]
    public void BuildFolderMoveFacts_RewritesInstallDestinationWithTrailingSourceSeparator()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        ChartFile libraryFile = CreateFile("C:\\Charts\\library.bms");
        ChartFile pendingFile = CreateFile("C:\\Charts\\pending.bms") with { Token = null };
        PackageChartEntry pendingEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, "C:\\Install\\Source");
        var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);

        LibraryFolderMoveFacts facts = service.BuildFolderMoveFacts(
            "C:\\Install\\Source\\",
            "D:\\Install\\Destination\\",
            [],
            CreateInstallDestinationOverlaySnapshot([CreateLibraryChartRefWithInstallDestination(libraryFile, "C:\\Install\\Source\\Child")]),
            [pendingPackage],
            [],
            unregister: false,
            notifyStorageRowPathChanges: false);

        LibraryInstallDestinationChange pendingChange = facts.PackageReferenceFacts.InstallDestinationChanges.Single(change => ReferenceEquals(change.Entry, pendingEntry));
        LibraryInstallDestinationChange libraryChange = facts.PackageReferenceFacts.InstallDestinationChanges.Single(change => ReferenceEquals(change.Chart.Token, libraryFile.Token));
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
            ChartFile libraryFile = CreateFile(collidingPath);
            ChartFile pendingFile = CreateFile(collidingPath) with { Token = null };
            PackageChartEntry pendingEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, sourceRoot);
            var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);

            LibraryFolderMoveFacts facts = service.BuildFolderMoveFacts(
                sourceRoot,
                destinationRoot,
                [LibraryChartRef.FromChartFile((libraryFile))],
                CreateInstallDestinationOverlaySnapshot([CreateLibraryChartRefWithInstallDestination(libraryFile, sourceRoot)]),
                [pendingPackage],
                [],
                unregister: false,
                notifyStorageRowPathChanges: false);

            LibraryInstallDestinationChange pendingChange = facts.PackageReferenceFacts.InstallDestinationChanges.Single(change => ReferenceEquals(change.Entry, pendingEntry));
            ChartFile pendingSnapshot = pendingChange.CreateAppliedChartSnapshot(facts.CatalogFacts.ChartPathChanges);
            Assert.AreEqual(collidingPath, pendingSnapshot.Path);
            Assert.AreEqual(destinationRoot, pendingSnapshot.InstallDestination);
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
            ChartFile libraryFile = CreateFile(chartPath);

            List<string> paths = service.GetWholeFolderDeleteCandidatePaths(
                [LibraryChartRef.FromPath(ChartFileKind.Bms, chartPath, libraryFile.Md5, libraryFile.Sha256)],
                CreateLibraryChartRefLookup([LibraryChartRef.FromChartFile((libraryFile))]));

            Assert.AreEqual(1, paths.Count);
            Assert.AreEqual(folderPath, paths[0]);
        });
    }

    [TestMethod]
    public void GetWholeFolderDeleteCandidatePaths_CurrentPathlessCanonicalReturnsEmpty()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string folderPath = Path.Combine(tempDirectoryPath, "Song");
            Directory.CreateDirectory(folderPath);
            string stalePath = Path.Combine(folderPath, "stale.bms");
            File.WriteAllText(stalePath, "#PLAYER 1");
            ChartFile libraryFile = CreateFile(stalePath);
            var selectedRef = LibraryChartRef.FromChartFile((libraryFile));
            libraryFile = libraryFile with { Path = null };

            List<string> paths = service.GetWholeFolderDeleteCandidatePaths(
                [selectedRef],
                CreateLibraryChartRefLookup([LibraryChartRef.FromChartFile((libraryFile))]));

            Assert.AreEqual(0, paths.Count);
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
            ChartFile catalogFile = CreateFile(catalogChartPath);

            List<string> paths = service.GetWholeFolderDeleteCandidatePaths(
                [LibraryChartRef.FromPath(ChartFileKind.Bms, selectedChartPath, catalogFile.Md5, catalogFile.Sha256)],
                CreateLibraryChartRefLookup([LibraryChartRef.FromChartFile((catalogFile))]));

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
            ChartFile selectedFile = CreateFile(selectedChartPath);
            ChartFile remainingFile = CreateFile(remainingChartPath);

            List<string> paths = service.GetWholeFolderDeleteCandidatePaths(
                [LibraryChartRef.FromChartFile((selectedFile))],
                CreateLibraryChartRefLookup([LibraryChartRef.FromChartFile((selectedFile)), LibraryChartRef.FromChartFile((remainingFile))]));

            Assert.AreEqual(0, paths.Count);
        });
    }

    [TestMethod]
    public void BuildFolderMoveFacts_CanSuppressStorageRowPathNotificationForRename()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        ChartFile file1 = CreateFile("C:\\Lib\\Src\\A\\a.bms");
        ChartFile file2 = CreateFile("C:\\Lib\\Src\\B\\b.bms");

        LibraryFolderMoveFacts facts = service.BuildFolderMoveFacts(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            CreateLibraryChartRefs([file1, file2]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: false,
            notifyStorageRowPathChanges: false);

        Assert.AreEqual(2, facts.CatalogFacts.FolderPathChanges.Count);
        Assert.AreEqual(2, facts.CatalogFacts.ChartPathChanges.Count);
        Assert.AreEqual(0, facts.CatalogFacts.ChartRemoveRequests.Count);
        CollectionAssert.AreEquivalent(
            new[] { "C:\\Lib\\Dst\\A\\a.bms", "C:\\Lib\\Dst\\B\\b.bms" },
            facts.CatalogFacts.ChartPathChanges.Select(change => change.NewPath).ToArray());
        Assert.AreEqual(LibraryStorageRowPathNotificationPolicy.Suppressed, facts.StorageRowPathNotificationPolicy);
    }

    [TestMethod]
    public void BuildFolderMoveFacts_RequestsStorageRowPathNotificationForRootMove()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        ChartFile file = CreateFile("C:\\Lib\\Src\\A\\a.bms");

        LibraryFolderMoveFacts facts = service.BuildFolderMoveFacts(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            CreateLibraryChartRefs([file]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: false,
            notifyStorageRowPathChanges: true);

        Assert.AreEqual(LibraryStorageRowPathNotificationPolicy.Notify, facts.StorageRowPathNotificationPolicy);
        LibraryChartPathChange pathChange = facts.CatalogFacts.ChartPathChanges.Single();
        Assert.AreEqual("C:\\Lib\\Src\\A\\a.bms", pathChange.OldPath);
        Assert.AreEqual("C:\\Lib\\Dst\\A\\a.bms", pathChange.NewPath);
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
            ChartFile file = CreateFile(Path.Combine(sourcePath, "chart.bms"));
            ChartFile nestedFile = CreateFile(Path.Combine(nestedPath, "nested.bms"));

            ChartFile chart = (file);
            ChartFile nestedChart = (nestedFile);

            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlans(
                [chart, nestedChart],
                [],
                renameRootFolder: true,
                folders =>
                {
                    CollectionAssert.AreEqual(new[] { sourcePath }, folders.ToArray());
                    return [chart, nestedChart];
                },
                (_, parentDir) => Path.Combine(parentDir, "Renamed"));

            Assert.AreEqual(1, plans.Count(plan => !string.IsNullOrWhiteSpace(plan.DestinationDirectory)));
            Assert.AreEqual(Path.Combine(rootPath, "Renamed (2)"), plans.Single(plan => !string.IsNullOrWhiteSpace(plan.DestinationDirectory)).DestinationDirectory);
        });
    }

    [TestMethod]
    public void BuildAutoRenamePlans_ReservesDestinationsAfterFolderNameNormalization()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryLibraryFileOperationsService();
            string rootPath = Path.Combine(tempDirectoryPath, "Songs");
            string firstSourcePath = Path.Combine(rootPath, "FirstSource");
            string secondSourcePath = Path.Combine(rootPath, "SecondSource");
            string existingDestinationPath = Path.Combine(rootPath, "SameName");
            Directory.CreateDirectory(firstSourcePath);
            Directory.CreateDirectory(secondSourcePath);
            Directory.CreateDirectory(existingDestinationPath);
            string firstChartPath = Path.Combine(firstSourcePath, "first.bms");
            string secondChartPath = Path.Combine(secondSourcePath, "second.bms");
            File.WriteAllText(firstChartPath, "#PLAYER 1");
            File.WriteAllText(secondChartPath, "#PLAYER 1");
            ChartFile firstFile = CreateFile(firstChartPath);
            ChartFile secondFile = CreateFile(secondChartPath);
            firstFile = firstFile with { Title = "First", RawTitle = "First" };
            secondFile = secondFile with { Title = "Second", RawTitle = "Second" };
            ChartFile firstChart = (firstFile);
            ChartFile secondChart = (secondFile);

            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlans(
                [firstChart, secondChart],
                [],
                renameRootFolder: true,
                folders =>
                {
                    CollectionAssert.AreEqual(new[] { firstSourcePath, secondSourcePath }, folders.ToArray());
                    return [firstChart, secondChart];
                },
                (children, parentDir) => Path.Combine(parentDir, children.First().Title == "First" ? "Same:Name" : "SameName"),
                name => name.Replace(":", string.Empty));

            CollectionAssert.AreEqual(
                new[]
                {
                    Path.Combine(rootPath, "SameName (2)"),
                    Path.Combine(rootPath, "SameName (3)")
                },
                plans.Select(plan => plan.DestinationDirectory).ToArray());
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
            ChartFile bmsonChart = (ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Token = new OwnedChartToken(),
                Path = chartPath,
                Folder = sourcePath,
                Title = "BmsonTitle",
                RawTitle = "BmsonTitle",
                Artist = "BmsonArtist",
                RawArtist = "BmsonArtist"
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
                (children, parentDir) => Path.Combine(parentDir, children.First().Title + "_" + children.First().Artist));

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
            ChartFile bmsRow = CreateFile(chartPath);
            bmsRow = bmsRow with { Title = "BmsTitle", RawTitle = "BmsTitle" };
            ChartFile bmsChart = (bmsRow);

            List<FolderAutoRenamePlan> plans = service.BuildAutoRenamePlansForSourceFolders(
                [sourcePath],
                [],
                renameRootFolder: true,
                folders =>
                {
                    CollectionAssert.AreEqual(new[] { sourcePath }, folders.ToArray());
                    return [bmsChart];
                },
                (children, parentDir) => Path.Combine(parentDir, children.First().Title));

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
            ChartFile bmsRow = CreateFile(bmsPath);
            bmsRow = bmsRow with { Title = "BmsTitle", RawTitle = "BmsTitle" };
            ChartFile bmsChart = (bmsRow);
            ChartFile bmsonChart = (ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Token = new OwnedChartToken(),
                Path = bmsonPath,
                Folder = sourcePath,
                Title = "BmsonTitle",
                RawTitle = "BmsonTitle",
                Artist = "BmsonArtist",
                RawArtist = "BmsonArtist"
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
                (children, parentDir) =>
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
    public void BuildFolderMoveFacts_TracksBmsonChartPathChanges()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            Folder = "C:\\Lib\\Src\\Pkg"
        };

        LibraryFolderMoveFacts facts = service.BuildFolderMoveFacts(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            CreateLibraryChartRefs([], [bmsonSong]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: false,
            notifyStorageRowPathChanges: false);

        Assert.AreEqual(1, facts.CatalogFacts.ChartPathChanges.Count);
        Assert.AreSame(bmsonSong.Token, facts.CatalogFacts.ChartPathChanges[0].Chart.Token);
        Assert.AreEqual("C:\\Lib\\Src\\Pkg\\chart.bmson", facts.CatalogFacts.ChartPathChanges[0].OldPath);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bmson", facts.CatalogFacts.ChartPathChanges[0].NewPath);
        Assert.AreEqual(LibraryStorageRowPathNotificationPolicy.Suppressed, facts.StorageRowPathNotificationPolicy);
    }

    [TestMethod]
    public void BuildFolderMoveFacts_BmsonRootMove_RequestsStorageRowPathNotification()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            Folder = "C:\\Lib\\Src\\Pkg"
        };

        LibraryFolderMoveFacts facts = service.BuildFolderMoveFacts(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            CreateLibraryChartRefs([], [bmsonSong]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: false,
            notifyStorageRowPathChanges: true);

        Assert.AreEqual(LibraryStorageRowPathNotificationPolicy.Notify, facts.StorageRowPathNotificationPolicy);
        Assert.AreEqual(1, facts.CatalogFacts.ChartPathChanges.Count);
        Assert.AreSame(bmsonSong.Token, facts.CatalogFacts.ChartPathChanges[0].Chart.Token);
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
            ChartFile libraryFile = CreateFile(chartPath);
            ChartFile pendingFile = CreateFile(Path.Combine(tempDirectoryPath, "Pending", "chart.bms")) with { Token = null };
            var adapterlessBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                Folder = Path.Combine(tempDirectoryPath, "Pending"),
                Title = "Bmson",
                RawTitle = "Bmson"
            }));
            var pendingPackage = ChartPackage.FromChartEntries([ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, sourceRoot), adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());

            LibraryMergeResult result = service.PrepareMergeDirectory(
                sourceRoot,
                destinationRoot,
                [LibraryChartRef.FromChartFile((libraryFile))],
                CreateInstallDestinationOverlaySnapshot([CreateLibraryChartRefWithInstallDestination(libraryFile, sourceRoot)]),
                [pendingPackage],
                [],
                _ => new PrimaryHashSetLookup());

            Assert.IsTrue(result.Success);
            Assert.AreEqual(2, result.ReferenceFacts.InstallDestinationChanges.Count);
            CollectionAssert.AreEquivalent(
                new[] { destinationRoot, destinationRoot },
                result.ReferenceFacts.InstallDestinationChanges.Select(change => change.NewInstallDestination).ToArray());
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
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
            ChartFile libraryFile = CreateFile(Path.Combine(sourceRoot, "chart.bms"));
            var adapterlessBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(tempDirectoryPath, "Pending", "chart.bmson"),
                Folder = Path.Combine(tempDirectoryPath, "Pending"),
                Title = "Bmson",
                RawTitle = "Bmson"
            }));
            adapterlessBmsonEntry.SetInstallDestinationPathOnly(sourceRoot);
            var pendingPackage = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            pendingPackage.path = Path.Combine(tempDirectoryPath, "Pending");
            pendingPackage.delete_parent = false;
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());

            LibraryMergeResult result = service.PrepareMergeDirectory(
                sourceRoot,
                destinationRoot,
                CreateLibraryChartRefs([libraryFile]),
                CreateInstallDestinationOverlaySnapshot(),
                [pendingPackage],
                [],
                _ => new PrimaryHashSetLookup());

            Assert.IsTrue(result.Success);
            LibraryInstallDestinationChange change = result.ReferenceFacts.InstallDestinationChanges.Single();
            Assert.AreSame(adapterlessBmsonEntry, change.Entry);
            Assert.AreEqual(destinationRoot, change.NewInstallDestination);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsChartForTest());
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
            ChartFile bmsFile = CreateFile(Path.Combine(sourceRoot, "chart.bms"));
            bmsFile = bmsFile with { Md5 = "cccccccccccccccccccccccccccccccc", Sha256 = null };
            ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Token = new OwnedChartToken(),
                Path = Path.Combine(sourceRoot, "chart.bmson"),
                Folder = sourceRoot,
                Title = "Bmson",
                RawTitle = "Bmson",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Sha256 = new string('b', 64)
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
            CollectionAssert.AreEqual(new[] { bmsFile.Path }, result.SourceCharts.Where(chart => chart.Kind == ChartFileKind.Bms).Select(chart => chart.Path).ToArray());
            CollectionAssert.AreEqual(new[] { bmsonSong.Path }, result.SourceCharts.Where(chart => chart.Kind == ChartFileKind.Bmson).Select(chart => chart.Path).ToArray());
            Assert.AreEqual(2, result.SourceCharts.Count);
            Assert.AreSame(bmsFile.Token, result.SourceCharts.Single(chart => chart.Kind == ChartFileKind.Bms).Token);
            Assert.AreSame(bmsonSong.Token, result.SourceCharts.Single(chart => chart.Kind == ChartFileKind.Bmson).Token);
            CollectionAssert.AreEquivalent(new[] { bmsFile.Md5, bmsonSong.Md5 }, excludedHashes.ToArray());
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
            ChartFile bmsFile = CreateFile(sourcePath);

            LibraryMergeResult result = service.PrepareMergeDirectory(
                sourceRoot,
                destinationRoot,
                CreateLibraryChartRefs([bmsFile]),
                CreateInstallDestinationOverlaySnapshot(),
                [],
                [],
                _ => EmptyPrimaryHashLookup.Instance);

            bmsFile = bmsFile with { Path = movedPath };

            Assert.IsTrue(result.Success);
            Assert.AreEqual(sourcePath, result.SourceCharts.Single().Path);
            Assert.AreEqual(sourcePath, result.SourceCharts.Single().ToChartFileIdentity().Path);
            Assert.AreEqual(movedPath, bmsFile.Path);
            Assert.AreEqual(sourcePath, result.SourceCharts.Single().Path);
            Assert.AreEqual(bmsFile.Md5, result.SourceCharts.Single().Md5);
            Assert.AreEqual(ChartFileKind.Bms, result.SourceCharts.Single().Kind);
        });
    }

    [TestMethod]
    public void BuildFolderMoveFacts_MixedBmsAndBmsonTracksBothStorageModels()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        ChartFile bmsFile = CreateFile("C:\\Lib\\Src\\Pkg\\chart.bms");
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            Folder = "C:\\Lib\\Src\\Pkg"
        };

        LibraryFolderMoveFacts facts = service.BuildFolderMoveFacts(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            CreateLibraryChartRefs([bmsFile], [bmsonSong]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: false,
            notifyStorageRowPathChanges: true);

        Assert.AreEqual(2, facts.CatalogFacts.ChartPathChanges.Count);
        LibraryChartPathChange bmsPathChange = facts.CatalogFacts.ChartPathChanges.Single(change => ReferenceEquals(change.Chart.Token, bmsFile.Token));
        Assert.AreEqual("C:\\Lib\\Src\\Pkg\\chart.bms", bmsPathChange.OldPath);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bms", bmsPathChange.NewPath);
        LibraryChartPathChange bmsonPathChange = facts.CatalogFacts.ChartPathChanges.Single(change => ReferenceEquals(change.Chart.Token, bmsonSong.Token));
        Assert.AreEqual("C:\\Lib\\Src\\Pkg\\chart.bmson", bmsonPathChange.OldPath);
        Assert.AreEqual("C:\\Lib\\Dst\\Pkg\\chart.bmson", bmsonPathChange.NewPath);
        Assert.AreEqual(LibraryStorageRowPathNotificationPolicy.Notify, facts.StorageRowPathNotificationPolicy);
    }

    [TestMethod]
    public void BuildFolderMoveFacts_UnregisterTracksBmsonSongsSeparately()
    {
        var service = new BmsLibraryLibraryFileOperationsService();
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Token = new OwnedChartToken(),
            Path = "C:\\Lib\\Src\\Pkg\\chart.bmson",
            Folder = "C:\\Lib\\Src\\Pkg"
        };
        ChartFile bmsFile = CreateFile("C:\\Lib\\Src\\Pkg\\chart.bms");

        LibraryFolderMoveFacts facts = service.BuildFolderMoveFacts(
            "C:\\Lib\\Src",
            "C:\\Lib\\Dst",
            CreateLibraryChartRefs([bmsFile], [bmsonSong]),
            CreateInstallDestinationOverlaySnapshot(),
            [],
            [],
            unregister: true);

        Assert.AreEqual(2, facts.CatalogFacts.ChartRemoveRequests.Count);
        Assert.AreSame(bmsFile.Token, facts.CatalogFacts.ChartRemoveRequests.Single(request => ReferenceEquals(request.Token, bmsFile.Token)).Token);
        Assert.AreSame(bmsonSong.Token, facts.CatalogFacts.ChartRemoveRequests.Single(request => ReferenceEquals(request.Token, bmsonSong.Token)).Token);
        Assert.AreEqual(0, facts.CatalogFacts.ChartPathChanges.Count);
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
            ChartFile renameFile = CreateFile(renameSourcePath);
            ChartFile duplicateFile = CreateFile(duplicateSourcePath);
            ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Token = new OwnedChartToken(),
                Path = bmsonPath,
                Md5 = new string('b', 32),
                Sha256 = new string('2', 64)
            };
            int callbackCount = 0;

            LibraryFileExtensionRenameResult result = service.RenameLibraryFileExtensions(
                [
                    (renameFile),
                    (duplicateFile),
                    (bmsonSong)
                ],
                ".bme",
                unregister: false,
                (file, requestedPath) =>
                {
                    callbackCount++;
                    return service.ProcessInvalidExtensionRename(file, requestedPath, fileMutationService, null);
                });

            Assert.AreEqual(2, callbackCount);
            Assert.AreEqual(1, result.Report.RenamedCount);
            Assert.AreEqual(1, result.Report.DuplicateDeletedCount);
            Assert.AreEqual(0, result.Report.SkippedCount);
            Assert.AreEqual(1, result.CatalogFacts.ChartPathChanges.Count);
            Assert.AreEqual(1, result.CatalogFacts.ChartRemoveRequests.Count);
            Assert.AreEqual(renameSourcePath, result.CatalogFacts.ChartPathChanges[0].OldPath);
            Assert.AreEqual(Path.Combine(tempDirectoryPath, "rename_me.bme"), result.CatalogFacts.ChartPathChanges[0].NewPath);
            Assert.AreSame(renameFile.Token, result.CatalogFacts.ChartPathChanges[0].Chart.Token);
            Assert.AreSame(duplicateFile.Token, result.CatalogFacts.ChartRemoveRequests[0].Token);
            Assert.IsTrue(File.Exists(Path.Combine(tempDirectoryPath, "rename_me.bme")));
            Assert.IsFalse(File.Exists(renameSourcePath));
            Assert.IsFalse(File.Exists(duplicateSourcePath));
            Assert.IsTrue(File.Exists(duplicateDestinationPath));
            Assert.IsTrue(File.Exists(bmsonPath));
        });
    }

    private static List<LibraryChartRef> CreateLibraryChartRefs(
        IEnumerable<ChartFile> bmsFiles,
        IEnumerable<ChartFile> bmsonSongs = null!)
    {
        return [.. (bmsFiles ?? []).Select(chart => LibraryChartRef.FromChartFile((chart)))
            .Concat((bmsonSongs ?? []).Select(chart => LibraryChartRef.FromChartFile((chart))))
            .Where(chart => chart != null)];
    }

    private static LibraryChartRefIndexSnapshot CreateLibraryChartRefLookup(IEnumerable<LibraryChartRef> charts)
    {
        return LibraryChartRefIndexSnapshot.FromLibraryChartRefs(charts);
    }

    private static InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlaySnapshot(IEnumerable<ChartFile> charts = null!)
    {
        return InstallDestinationOverlayChartRefSnapshot.FromCharts(charts ?? []);
    }

    private static ChartFile CreateLibraryChartRefWithInstallDestination(
        ChartFile file,
        string installDestination,
        string title = "",
        string artist = "",
        IReadOnlyList<string> suggestions = null!)
    {
        return ChartFileProjection.WithPackageState(
            (file),
            installDestination,
            title,
            artist,
            suggestions ?? [],
            file?.Warnings ?? []);
    }

    private static ChartFile CreateFile(string path)
    {
        return ChartTestValues.Empty() with
        {
            Token = new OwnedChartToken(),
            Path = path
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
            string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
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
            string destinationParentPath = Path.GetDirectoryName(destinationPath)!;
            if (!string.IsNullOrWhiteSpace(destinationParentPath))
            {
                Directory.CreateDirectory(destinationParentPath);
            }
            CopyDirectory(sourcePath, destinationPath);
            Directory.Delete(sourcePath, recursive: true);
        }

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            string destinationDirectoryPath = Path.GetDirectoryName(destinationPath)!;
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
            }
            File.Copy(sourcePath, destinationPath, overwrite);
        }

        public void CopyDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            CopyDirectory(sourcePath, destinationPath);
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
                string destinationDirectoryPath = Path.GetDirectoryName(destinationFilePath)!;
                if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
                {
                    Directory.CreateDirectory(destinationDirectoryPath);
                }
                File.Copy(filePath, destinationFilePath, overwrite: true);
            }
        }
    }
}
