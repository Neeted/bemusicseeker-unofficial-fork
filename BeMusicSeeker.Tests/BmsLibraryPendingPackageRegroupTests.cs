using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPendingPackageRegroupTests
{
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_DoesNotRegroupSplitPackagesWhenPendingDestinationsMatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageA");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageA");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BMSFiles = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(firstPackage);

            AssertPendingPackagePaths(library, firstPackage.path, secondPackage.path);
            CollectionAssert.AreEquivalent(new[] { firstPackage.path, secondPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectoryByFile_DoesNotRegroupSplitPackagesWhenPendingDestinationsMatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageB");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageB");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BMSFiles = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(firstPackage.GetBmsOwnersForTest().Single())), asParallel: false, fixMode: false);

            AssertPendingPackagePaths(library, firstPackage.path, secondPackage.path);
            CollectionAssert.AreEquivalent(new[] { firstPackage.path, secondPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectoryByFiles_MatchesAdapterlessBmsonPackageByChartPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonAdapterless");
            string chartPath = CreateBmsonFile(sourceDirectoryPath, "chart.bmson", "Adapterless", "Test");
            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(chartPath);
            ChartPackage pendingPackage = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong))
            ]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            pendingPackage.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;
            PackageChartEntry selectedChart = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));

            library.BMSFiles = [];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            Assert.IsNull(pendingPackage.ChartEntries.Single().GetBmsOwnerForTest());

            library.SearchEstimatedInstallationDirectory([selectedChart], asParallel: false, fixMode: false);

            PackageChartEntry entry = pendingPackage.ChartEntries.Single();
            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            Assert.IsNull(entry.GetBmsOwnerForTest());
            Assert.AreEqual(chartPath, entry.Chart.Path);
        });
    }

    [TestMethod]
    public void PendingInstallEstimate_InstalledCollectionChangesDuringAttempt_RebuildsPartitionAndReleasesSearchingState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        List<int> attempts = [];
        bool installedCollectionMutated = false;
        BMSLibrary? observedLibrary = null;
        string? observedInstalledPath = null;
        var observer = new RecordingInstallEstimationExecutionObserver(
            attemptObserver: observation =>
            {
                attempts.Add(observation.Attempt);
                if (observation.Attempt == 0 && !installedCollectionMutated)
                {
                    BMSLibrary currentLibrary = observedLibrary!;
                    string currentInstalledPath = observedInstalledPath!;
                    currentLibrary.BMSFiles = [BMSFile.CreateBMSFileFromFile(currentInstalledPath)];
                    installedCollectionMutated = true;
                }
            });
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            observedLibrary = library;
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "InstalledCollectionStale");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "InstalledCollectionStale");
            string pendingInstalledPath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "installed.bms",
                "#PLAYER 1\r\n#TITLE Already Installed\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "missing.bms",
                "#PLAYER 1\r\n#TITLE Missing\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedPath = CreateBmsFileWithContents(
                candidateDirectoryPath,
                "installed.bms",
                "#PLAYER 1\r\n#TITLE Already Installed\r\n#ARTIST Test\r\n");
            observedInstalledPath = installedPath;
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "audio");
            BMSFile pendingInstalled = BMSFile.CreateBMSFileFromFile(pendingInstalledPath);
            BMSFile pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalled, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            ChartPackage emptyPackage = ChartPackage.FromChartEntries([]);
            emptyPackage.path = Path.Combine(sourceDirectoryPath, "Empty");
            emptyPackage.delete_parent = false;
            library.BMSFiles = [];
            SeedPendingPackages(library, songDbPath, pendingPackage, emptyPackage);
            SetLibraryResourceIndex(
                library,
                BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            // The public package-batch overload is the deterministic entry point for the retry pipeline;
            // keep the second package empty so it only enables that batch shape.
            library.SearchEstimatedInstallationDirectory([pendingPackage, emptyPackage]);

            Assert.IsTrue(installedCollectionMutated);
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 1 }, attempts);
            PackageChartEntry installedEntry = GetEntryByFileName(pendingPackage, "installed.bms");
            PackageChartEntry missingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsFalse(missingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(string.IsNullOrWhiteSpace(installedEntry.Chart.InstallDestination));
            Assert.AreEqual(candidateDirectoryPath, missingEntry.Chart.InstallDestination);
            Assert.IsFalse(installedEntry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
            Assert.IsFalse(missingEntry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
            Assert.IsFalse(library.GetPendingEstimateQueueStatusSnapshot().IsActive);
            Assert.IsFalse(library.GetInstallEstimationProgressSnapshot().IsActive);
        }, observer);
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MultiPackageBatchReportsExecutionPolicyAndProgress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        const int packageCount = 2;
        int expectedWorkItemDegree = BMSLibrary.ResolveInstallEstimationDefaultDegree();
        int expectedMaxActive = Math.Min(packageCount, expectedWorkItemDegree);
        var observer = new RecordingInstallEstimationExecutionObserver(expectedMaxActive);

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "ObserverFirst");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "ObserverSecond");
            string firstPendingPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "Observer First");
            string secondPendingPath = CreateBmsFile(secondSourceDirectoryPath, "second.bms", "Observer Second");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(firstPendingPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(secondPendingPath);

            // Both packages contain one supported missing chart, so the public
            // route must prepare and dispatch two evaluation requests.
            library.BMSFiles = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory([firstPackage, secondPackage]);
        }, observer);

        Assert.AreEqual(packageCount, observer.WorkItems.Count);
        Assert.AreEqual(expectedMaxActive, observer.MaxActive);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, packageCount).ToArray(),
            observer.WorkItems.Select(item => item.OrderIndex).OrderBy(order => order).ToArray());
        Assert.IsTrue(observer.WorkItems.All(item => item.Source == PendingInstallEstimateBatchSource.ManualReestimate));
        Assert.IsTrue(observer.WorkItems.All(item => item.WorkItemDegree == expectedWorkItemDegree));
        Assert.IsTrue(observer.WorkItems.All(item => item.CandidateEvaluationDegree == 1));
        CollectionAssert.AreEquivalent(
            new[] { "first.bms", "second.bms" },
            observer.WorkItems.Select(item => item.DisplayName).ToArray());

        List<InstallEstimationProgressObservation> activeProgress =
            [.. observer.Progress.Where(progress => progress.IsActive)];
        Assert.IsTrue(activeProgress.Count > 0);
        Assert.IsTrue(activeProgress.All(progress =>
            progress.Source == InstallEstimationProgressSource.ManualReestimate
            && progress.TotalWorkCount == packageCount
            && progress.CompletedWorkCount >= 0
            && progress.CompletedWorkCount <= packageCount));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, packageCount + 1).ToArray(),
            activeProgress.Select(progress => progress.CompletedWorkCount).Distinct().ToArray());
        InstallEstimationProgressObservation inactiveProgress = observer.Progress.Last();
        Assert.IsFalse(inactiveProgress.IsActive);
        Assert.AreEqual(InstallEstimationProgressSource.None, inactiveProgress.Source);
        Assert.AreEqual(0, inactiveProgress.TotalWorkCount);
        Assert.AreEqual(0, inactiveProgress.CompletedWorkCount);

        Assert.AreEqual(packageCount, observer.Attempts.Count);
        Assert.IsTrue(observer.Attempts.All(attempt =>
            attempt.Source == PendingInstallEstimateBatchSource.ManualReestimate
            && attempt.Attempt == 0
            && !string.IsNullOrWhiteSpace(attempt.DisplayName)
            && attempt.CurrentnessStamp.ResourceIndexGeneration >= 0));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, packageCount).ToArray(),
            observer.Attempts.Select(attempt => attempt.OrderIndex).OrderBy(order => order).ToArray());
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_ObserverFailureIsPropagated()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var observer = new ThrowingInstallEstimationExecutionObserver();
        Assert.ThrowsException<InvalidOperationException>(() =>
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                ChartPackage firstPackage = CreatePendingSingleFilePackage(
                    CreateBmsFile(Path.Combine(tempRootPath, "Pending", "FailureFirst"), "first.bms", "Failure First"));
                ChartPackage secondPackage = CreatePendingSingleFilePackage(
                    CreateBmsFile(Path.Combine(tempRootPath, "Pending", "FailureSecond"), "second.bms", "Failure Second"));
                library.BMSFiles = [];
                SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

                library.SearchEstimatedInstallationDirectory([firstPackage, secondPackage]);
            }, observer));
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_SingleFileRelativeResourceSelectsExternalCandidateWithoutSourceScanWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "RelativeSingleFile");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "RelativeCandidate");
            string sourceSoundDirectoryPath = Path.Combine(sourceDirectoryPath, "sound");
            string candidateSoundDirectoryPath = Path.Combine(candidateDirectoryPath, "sound");
            string unrelatedNestedDirectoryPath = Path.Combine(sourceDirectoryPath, "Unrelated", "Nested");
            Directory.CreateDirectory(sourceSoundDirectoryPath);
            Directory.CreateDirectory(candidateSoundDirectoryPath);
            Directory.CreateDirectory(unrelatedNestedDirectoryPath);
            string pendingPath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "pending.bms",
                "#PLAYER 1\r\n#TITLE Relative\r\n#ARTIST Test\r\n#WAVAA sound/00.wav\r\n#00111:AA\r\n");
            string candidatePath = CreateBmsFileWithContents(
                candidateDirectoryPath,
                "candidate.bms",
                "#PLAYER 1\r\n#TITLE Relative\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(sourceSoundDirectoryPath, "00.wav"), "source");
            File.WriteAllText(Path.Combine(candidateSoundDirectoryPath, "00.wav"), "candidate");
            File.WriteAllText(Path.Combine(unrelatedNestedDirectoryPath, "noise.bin"), "noise");
            ChartPackage pendingPackage = CreatePendingSingleFilePackage(pendingPath);
            library.BMSFiles = [BMSFile.CreateBMSFileFromFile(candidatePath)];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(sourceDirectoryPath, ["pending.bms", Path.Combine("sound", "00.wav")]);
            cache.AddDir(candidateDirectoryPath, ["candidate.bms", Path.Combine("sound", "00.wav")]);
            SetLibraryResourceIndex(library, cache);
            SetLibraryResourceIndex(library, cache);

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry entry = GetOnlyEntry(pendingPackage);
            Assert.AreEqual(candidateDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreNotEqual(sourceDirectoryPath, entry.Chart.InstallDestination);
            Assert.IsTrue(entry.ResourceSnapshot.AudioReferences.Single().IsPathAware);
            Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SourceSurfaceScanLimitExceeded));
        });
    }

    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_RegroupsSplitPackagesWhenEligibleDirectoryIsSupplied()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageC");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageC");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BMSFiles = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_UsesAdapterlessBmsonEntriesForEligibility()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageBmsonRegroup");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Regroup Bmson", "Bmson Artist");
            LR2SongDBExtended.bmson_song pendingSong = BmsonSongParser.Parse(pendingBmsonPath);
            ChartPackage adapterlessPackage = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(
                    pendingSong,
                    includeWarningSnapshot: false))
            ]);
            adapterlessPackage.path = pendingBmsonPath;
            adapterlessPackage.delete_parent = true;
            ChartPackage bmsPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "chart.bms", "Regroup Bms"), destinationDirectoryPath);
            string installedBmsonPath = CreateBmsonFile(destinationDirectoryPath, "installed.bmson", "Regroup Bmson", "Bmson Artist");
            LR2SongDBExtended.bmson_song installedSong = BmsonSongParser.Parse(installedBmsonPath);
            Assert.IsNull(adapterlessPackage.ChartEntries.Single().GetBmsOwnerForTest());

            library.BMSFiles = [];
            library.BmsonSongs = [installedSong];
            SeedPendingPackages(library, songDbPath, adapterlessPackage, bmsPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, expectedFileCount: 2);
            PackageChartEntry regroupedBmson = regroupedPackage.ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bmson);
            PackageChartEntry regroupedBms = regroupedPackage.ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bms);
            Assert.AreEqual(pendingBmsonPath, regroupedBmson.Chart.Path);
            Assert.IsNull(regroupedBmson.GetBmsOwnerForTest());
            Assert.IsTrue(string.IsNullOrWhiteSpace(regroupedBmson.Chart.InstallDestination));
            Assert.AreEqual(destinationDirectoryPath, regroupedBms.Chart.InstallDestination);
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DeduplicatesAdapterlessBmsonByChartTarget()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonDuplicateRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageBmsonDuplicateRegroup");
            string pendingBmsonPath = CreateHealthyBmsonFile(sourceDirectoryPath, "pending.bmson", "Duplicate Bmson", "Bmson Artist");
            LR2SongDBExtended.bmson_song pendingSong = BmsonSongParser.Parse(pendingBmsonPath);
            PackageChartEntry firstEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingSong));
            string staleSnapshotPath = Path.Combine(sourceDirectoryPath, "pending-stale-snapshot.bmson");
            PackageChartEntry secondEntry = PackageChartEntry.FromChart(WithChartPath(ChartFileProjection.FromBmsonSong(pendingSong), staleSnapshotPath));
            ChartPackage firstPackage = ChartPackage.FromChartEntries([firstEntry]);
            firstPackage.path = pendingBmsonPath;
            firstPackage.delete_parent = true;
            ChartPackage secondPackage = ChartPackage.FromChartEntries([secondEntry]);
            secondPackage.path = staleSnapshotPath;
            secondPackage.delete_parent = true;
            string installedBmsonPath = CreateHealthyBmsonFile(destinationDirectoryPath, "installed.bmson", "Duplicate Bmson", "Bmson Artist");
            LR2SongDBExtended.bmson_song installedSong = BmsonSongParser.Parse(installedBmsonPath);
            Assert.IsNull(firstEntry.GetBmsOwnerForTest());
            Assert.IsNull(secondEntry.GetBmsOwnerForTest());

            library.BMSFiles = [];
            library.BmsonSongs = [installedSong];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = library.ChartPackagesPending.Single();
            Assert.AreEqual(sourceDirectoryPath, regroupedPackage.path);
            Assert.AreEqual(1, regroupedPackage.ChartEntries.Count);
            Assert.IsNull(regroupedPackage.ChartEntries.Single().GetBmsOwnerForTest());
            Assert.AreEqual(pendingBmsonPath, regroupedPackage.ChartEntries.Single().Chart.Path);
            Assert.IsTrue(string.IsNullOrWhiteSpace(regroupedPackage.ChartEntries.Single().Chart.InstallDestination));
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DoesNotApplyInstallDestinationToInstalledOnlyEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "InstalledOnlyRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "InstalledOnlyRegroup");
            string firstContents = "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n";
            string secondContents = "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n";
            string pendingAPath = CreateBmsFileWithContents(sourceDirectoryPath, "a.bms", firstContents);
            string pendingBPath = CreateBmsFileWithContents(sourceDirectoryPath, "b.bms", secondContents);
            string installedAPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed-a.bms", firstContents);
            string installedBPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed-b.bms", secondContents);
            ChartPackage firstPackage = CreatePendingSingleFilePackage(pendingAPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(pendingBPath);

            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(installedAPath),
                BMSFile.CreateBMSFileFromFile(installedBPath)
            ];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, expectedFileCount: 2);
            Assert.IsTrue(regroupedPackage.ChartEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
            Assert.IsTrue(regroupedPackage.ChartEntries.All(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled)));
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_AppliesInstallDestinationOnlyToMissingEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedRegroup");
            string installedContents = "#PLAYER 1\r\n#TITLE Installed\r\n#ARTIST Test\r\n";
            string pendingInstalledPath = CreateBmsFileWithContents(sourceDirectoryPath, "installed.bms", installedContents);
            string pendingMissingPath = CreateBmsFile(sourceDirectoryPath, "missing.bms", "Missing");
            string installedPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", installedContents);
            ChartPackage installedPackage = CreatePendingSingleFilePackage(pendingInstalledPath);
            ChartPackage missingPackage = CreatePendingSingleFilePackage(pendingMissingPath, destinationDirectoryPath);

            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(installedPath)
            ];
            SeedPendingPackages(library, songDbPath, installedPackage, missingPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, expectedFileCount: 2);
            PackageChartEntry installedEntry = GetEntryByFileName(regroupedPackage, "installed.bms");
            PackageChartEntry missingEntry = GetEntryByFileName(regroupedPackage, "missing.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(installedEntry.Chart.InstallDestination));
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.AreEqual(destinationDirectoryPath, missingEntry.Chart.InstallDestination);
            Assert.IsFalse(missingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_ReinitializesWarningsWhenEligibleDirectoryIsSupplied()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageD");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageD");
            ChartPackage strictWarningPackage = CreatePendingSingleFilePackage(
                CreateBmsFileWithContents(
                    sourceDirectoryPath,
                    "strict.bms",
                    "#PLAYER 1\r\n#TITLE Strict\r\n#ARTIST Test\r\n#WAVAA sound_missing.wav\r\n#00111:AA\r\n"),
                destinationDirectoryPath);
            ChartPackage normalPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "normal.bms", "Normal"), destinationDirectoryPath);

            library.BMSFiles = [];
            ApplySingleFileWarnings(strictWarningPackage, normalPackage);
            SeedPendingPackages(library, songDbPath, strictWarningPackage, normalPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            PackageChartEntry strictWarningEntry = GetEntryByFileName(regroupedPackage, "strict.bms");
            PackageChartEntry normalEntry = GetEntryByFileName(regroupedPackage, "normal.bms");
            Assert.IsTrue(strictWarningEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(strictWarningEntry), "WAV");
            Assert.IsFalse(strictWarningEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SingleBmsFile));
            Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(normalEntry));
        });
    }

    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_SynchronizesRepresentativeMetadataWithoutClearingSuggestions()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageRegroupMetadata");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageRegroupMetadata");
            string installedFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            PackageChartEntry firstPendingEntry = GetOnlyEntry(firstPackage);
            firstPendingEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(destinationDirectoryPath, string.Empty, string.Empty, [destinationDirectoryPath]));
            firstPendingEntry.SetWarning(ChartWarningKind.InstallEstimationLowConfidence, BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationLowConfidence);

            library.BMSFiles = [BMSFile.CreateBMSFileFromFile(installedFilePath)];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            foreach (PackageChartEntry regroupedEntry in regroupedPackage.ChartEntries)
            {
                Assert.AreEqual("Installed Title", regroupedEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Installed Artist", regroupedEntry.Chart.InstallDestinationArtist);
            }
            Assert.AreEqual(1, firstPendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.AreEqual(destinationDirectoryPath, firstPendingEntry.Chart.InstallDestinationSuggestions.Single());
        });
    }

    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DurableFailureLeavesSourceEntryStateUnchanged()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageRegroupDbFailure");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageRegroupDbFailure");
            string installedFilePath = CreateBmsFileWithContents(
                destinationDirectoryPath,
                "representative.bms",
                "#PLAYER 1\r\n#TITLE Resolved Title\r\n#ARTIST Resolved Artist\r\n");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            PackageChartEntry firstPendingEntry = GetOnlyEntry(firstPackage);
            firstPendingEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(
                destinationDirectoryPath,
                "Original Title",
                "Original Artist",
                [Path.Combine(tempRootPath, "Suggested")]));
            firstPendingEntry.SetWarning(
                ChartWarningKind.InstallEstimationLowConfidence,
                BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationLowConfidence);
            PackageChartInstallDestinationState expectedDestinationState = firstPendingEntry.CaptureInstallDestinationState();
            string[] expectedWarnings = [.. firstPendingEntry.Chart.Warnings.Select(warning => warning.Kind + "|" + warning.Message)];

            library.BMSFiles = [BMSFile.CreateBMSFileFromFile(installedFilePath)];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            // ReplaceInstallRows is intentionally the first durable step after
            // the detached regroup graph has been prepared.  A directory at the
            // database path forces that step to fail without changing the live
            // source entries.
            File.Delete(songDbPath);
            Directory.CreateDirectory(songDbPath);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(
                () => InvokeRegroupForSourceDirectories(library, sourceDirectoryPath));
            Assert.IsInstanceOfType<SQLite.SQLiteException>(exception.InnerException);

            PackageChartInstallDestinationState actualDestinationState = firstPendingEntry.CaptureInstallDestinationState();
            Assert.AreEqual(expectedDestinationState.Destination, actualDestinationState.Destination);
            Assert.AreEqual(expectedDestinationState.Title, actualDestinationState.Title);
            Assert.AreEqual(expectedDestinationState.Artist, actualDestinationState.Artist);
            CollectionAssert.AreEqual(expectedDestinationState.Suggestions.ToArray(), actualDestinationState.Suggestions.ToArray());
            CollectionAssert.AreEqual(
                expectedWarnings,
                firstPendingEntry.Chart.Warnings.Select(warning => warning.Kind + "|" + warning.Message).ToArray());
            CollectionAssert.AreEqual(new[] { firstPackage, secondPackage }, library.ChartPackagesPending.ToArray());
        });
    }

    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DoesNotRegroupWhenDirectoryPackageAlreadyExists()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageE");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageE");
            CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A");
            CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B");
            var directoryPackage = new ChartPackage
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };
            ChartPackage splitPackage = CreatePendingSingleFilePackage(Path.Combine(sourceDirectoryPath, "a.bms"), destinationDirectoryPath);

            library.BMSFiles = [];
            SeedPendingPackages(library, songDbPath, directoryPackage, splitPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertPendingPackagePaths(library, sourceDirectoryPath, splitPackage.path);
            CollectionAssert.AreEquivalent(new[] { sourceDirectoryPath, splitPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_SkipsDeferredEstimatePackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageDeferred");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageDeferred");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            firstPackage.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;

            library.BMSFiles = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertPendingPackagePaths(library, firstPackage.path, secondPackage.path);
            CollectionAssert.AreEquivalent(new[] { firstPackage.path, secondPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_LowConfidenceLeavesInstallDestinationEmptyAndShowsRepresentativeMetadata()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidence");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual("Candidate A", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist A", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), BeMusicSeeker.Properties.Resources.Warning_InstallEstimationAmbiguousPrefix);
            StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationAmbiguous);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateADirectoryPath);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateADirectoryPath);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateBDirectoryPath);
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_AdapterlessBmsonLowConfidenceStaysOnChartEntry()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonLowConfidence");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(pendingBmsonPath)));
            ChartPackage pendingPackage = ChartPackage.FromChartEntries([entry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.IsNull(entry.GetBmsOwnerForTest());
            Assert.IsTrue(string.IsNullOrWhiteSpace(entry.Chart.InstallDestination));
            Assert.AreEqual("Candidate A", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist A", entry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, entry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));

        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_BatchAdapterlessBmsonLowConfidenceStaysOnChartEntry()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonBatchLowConfidence");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonBatchOther");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string firstPendingBmsonPath = CreateBmsonFile(firstSourceDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            string secondPendingBmsonPath = CreateBmsonFile(secondSourceDirectoryPath, "other.bmson", "Other Bmson", "Other Artist");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            PackageChartEntry firstEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(firstPendingBmsonPath)));
            ChartPackage firstPackage = ChartPackage.FromChartEntries([firstEntry]);
            firstPackage.path = firstSourceDirectoryPath;
            firstPackage.delete_parent = false;
            PackageChartEntry secondEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(secondPendingBmsonPath)));
            ChartPackage secondPackage = ChartPackage.FromChartEntries([secondEntry]);
            secondPackage.path = secondSourceDirectoryPath;
            secondPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))
            ];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(firstSourceDirectoryPath, secondSourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(firstSourceDirectoryPath, secondSourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory([firstPackage, secondPackage]);

            Assert.IsNull(firstEntry.GetBmsOwnerForTest());
            Assert.IsTrue(string.IsNullOrWhiteSpace(firstEntry.Chart.InstallDestination));
            Assert.AreEqual("Candidate A", firstEntry.Chart.InstallDestinationTitle);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, firstEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(firstEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageUnresolvedInstalledDestination_DefersWithoutFallbackEstimation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedUnresolved");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Missing\r\n#ARTIST Test\r\n#WAVAA missing.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");

            var pendingInstalledA = BMSFile.CreateBMSFileFromFile(pendingInstalledAPath);
            var pendingInstalledB = BMSFile.CreateBMSFileFromFile(pendingInstalledBPath);
            var pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(installedAPath),
                BMSFile.CreateBMSFileFromFile(installedBPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.InstalledDestinationResolveFailed, pendingPackage.DeferredEstimateReason);
            Assert.IsTrue(GetEntryByFileName(pendingPackage, "installedA.bms").Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(GetEntryByFileName(pendingPackage, "installedB.bms").Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestinationTitle));
            Assert.AreEqual(0, pendingMissingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
            Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationResolveFailed);
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_UnsupportedParentResourcePath_WarnsAndSkipsEstimation()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageUnsupportedResourcePath");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "Candidate");
            string pendingFilePath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "unsupported.bms",
                "#PLAYER 1\r\n"
                + "#TITLE Unsupported Resource Path\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA ..\\Base\\sound.wav\r\n"
                + "#00111:AA\r\n");
            string candidateFilePath = CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", "#PLAYER 1\r\n#TITLE Current Directory Resource Path\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "audio");
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([BMSFile.CreateBMSFileFromFile(pendingFilePath)]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;

            library.BMSFiles = [BMSFile.CreateBMSFileFromFile(candidateFilePath)];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.UnsupportedResourcePath, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingEntry = GetEntryByFileName(pendingPackage, "unsupported.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.UnsupportedResourcePath));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), BeMusicSeeker.Properties.Resources.Warning_UnsupportedResourcePath);
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_CurrentDirectoryResourcePath_NormalizesAndEstimates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageCurrentDirectoryResourcePath");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "Candidate");
            string pendingFilePath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "current-dir.bms",
                "#PLAYER 1\r\n"
                + "#TITLE Current Directory Resource Path\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA .\\sound.wav\r\n"
                + "#00111:AA\r\n");
            string candidateFilePath = CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", "#PLAYER 1\r\n#TITLE Current Directory Resource Path\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "audio");
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([BMSFile.CreateBMSFileFromFile(pendingFilePath)]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;

            library.BMSFiles = [BMSFile.CreateBMSFileFromFile(candidateFilePath)];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingEntry = GetEntryByFileName(pendingPackage, "current-dir.bms");
            Assert.AreEqual(candidateDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.UnsupportedResourcePath));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageMultipleCandidates_UsesFinalEvaluationWinner()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedWinner");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

            var pendingInstalledA = BMSFile.CreateBMSFileFromFile(pendingInstalledAPath);
            var pendingInstalledB = BMSFile.CreateBMSFileFromFile(pendingInstalledBPath);
            var pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(installedAPath),
                BMSFile.CreateBMSFileFromFile(installedBPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.AreEqual(installedBDirectoryPath, pendingMissingEntry.Chart.InstallDestination);
            Assert.AreEqual(0, pendingMissingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageEquivalentCandidates_UsesDirectoryUniquePrimaryHashCount()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedCountTieBreak");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            string extraABmsonPath = CreateBmsonFile(installedADirectoryPath, "extraA.bmson", "Extra A", "Test");
            File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

            var pendingInstalledA = BMSFile.CreateBMSFileFromFile(pendingInstalledAPath);
            var pendingInstalledB = BMSFile.CreateBMSFileFromFile(pendingInstalledBPath);
            var pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(installedAPath),
                BMSFile.CreateBMSFileFromFile(installedBPath)
            ];
            library.BmsonSongs =
            [
                BmsonSongParser.Parse(extraABmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.AreEqual(installedADirectoryPath, pendingMissingEntry.Chart.InstallDestination);
            Assert.AreEqual(0, pendingMissingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageMultipleViableCandidates_ShowsInstalledDestinationAmbiguousWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAmbiguous");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Missing\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

            var pendingInstalledA = BMSFile.CreateBMSFileFromFile(pendingInstalledAPath);
            var pendingInstalledB = BMSFile.CreateBMSFileFromFile(pendingInstalledBPath);
            var pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(installedAPath),
                BMSFile.CreateBMSFileFromFile(installedBPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
            CollectionAssert.AreEquivalent(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
            Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstalledDestinationAmbiguous);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationAmbiguous.Split('\n')[0]);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedADirectoryPath);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedBDirectoryPath);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void SearchEstimatedInstallationDirectory_MixedPackageStrongMetadataWithSetting_AutoAppliesFirstCandidateAndKeepsAutoAppliedWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAutoApplyStrong");
                string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string installedAContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE A\r\n";
                string installedBContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE B\r\n";
                string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", installedAContents);
                string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", installedBContents);
                string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", installedAContents);
                string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", installedBContents);
                File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

                var pendingInstalledA = BMSFile.CreateBMSFileFromFile(pendingInstalledAPath);
                var pendingInstalledB = BMSFile.CreateBMSFileFromFile(pendingInstalledBPath);
                var pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
                var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
                pendingPackage.path = sourceDirectoryPath;
                pendingPackage.delete_parent = false;
                library.BMSFiles =
                [
                    BMSFile.CreateBMSFileFromFile(installedAPath),
                    BMSFile.CreateBMSFileFromFile(installedBPath)
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
                Assert.AreEqual(installedADirectoryPath, pendingMissingEntry.Chart.InstallDestination);
                Assert.AreEqual("Target Song", pendingMissingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", pendingMissingEntry.Chart.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
                Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
                StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstalledDestinationAutoAppliedAmbiguous);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationAutoAppliedAmbiguous.Split('\n')[0]);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedADirectoryPath);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedBDirectoryPath);
            });
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void SearchEstimatedInstallationDirectory_MixedPackageStrongMetadataWithSettingOff_LeavesInstallDestinationEmpty()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithAutoApplyAmbiguousInstallDestination(false, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAutoApplyStrongOff");
                string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string installedAContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE A\r\n";
                string installedBContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE B\r\n";
                string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", installedAContents);
                string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", installedBContents);
                string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", installedAContents);
                string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", installedBContents);
                File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

                var pendingInstalledA = BMSFile.CreateBMSFileFromFile(pendingInstalledAPath);
                var pendingInstalledB = BMSFile.CreateBMSFileFromFile(pendingInstalledBPath);
                var pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
                var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
                pendingPackage.path = sourceDirectoryPath;
                pendingPackage.delete_parent = false;
                library.BMSFiles =
                [
                    BMSFile.CreateBMSFileFromFile(installedAPath),
                    BMSFile.CreateBMSFileFromFile(installedBPath)
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
                Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
                Assert.AreEqual("Target Song", pendingMissingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", pendingMissingEntry.Chart.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
                Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            });
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void SearchEstimatedInstallationDirectory_MixedPackageWeakMetadataWithSetting_LeavesInstallDestinationEmpty()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAutoApplyWeak");
                string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
                string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
                string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Missing\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
                string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
                File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

                var pendingInstalledA = BMSFile.CreateBMSFileFromFile(pendingInstalledAPath);
                var pendingInstalledB = BMSFile.CreateBMSFileFromFile(pendingInstalledBPath);
                var pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
                var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
                pendingPackage.path = sourceDirectoryPath;
                pendingPackage.delete_parent = false;
                library.BMSFiles =
                [
                    BMSFile.CreateBMSFileFromFile(installedAPath),
                    BMSFile.CreateBMSFileFromFile(installedBPath)
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
                Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
                CollectionAssert.AreEquivalent(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
                Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            });
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MetadataTieBreakSelectsMatchingCandidate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMetadataTieBreak");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA1.bms", "#PLAYER 1\r\n#TITLE Another Song\r\n#ARTIST Someone\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA2.bms", "#PLAYER 1\r\n#TITLE Another Song [7K]\r\n#ARTIST Someone\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB1.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB2.bms", "#PLAYER 1\r\n#TITLE Target Song (Hyper)\r\n#ARTIST Artist / obj: diff\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA1.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA2.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB1.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB2.bms"))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.AreEqual(candidateBDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Target Song", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void SearchEstimatedInstallationDirectory_AmbiguousStrongMetadataWithSetting_AppliesDestinationAndKeepsWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageAmbiguousStrongMetadata");
                string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n");
                CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n");
                File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

                var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
                var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
                pendingPackage.path = pendingFilePath;
                pendingPackage.delete_parent = true;
                library.BMSFiles =
                [
                    BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                    BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
                Assert.AreEqual(candidateADirectoryPath, pendingEntry.Chart.InstallDestination);
                Assert.AreEqual("Target Song", pendingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", pendingEntry.Chart.InstallDestinationArtist);
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
                CollectionAssert.AreEqual(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateADirectoryPath);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateBDirectoryPath);
            });
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MetadataMismatchLeavesInstallDestinationEmptyAndShowsSingleSuggestion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMetadataMismatch");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "OnlyCandidate");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Base Artist obj: Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", "#PLAYER 1\r\n#TITLE Completely Different\r\n#ARTIST Another Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "dst");

            var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateDirectoryPath, "candidate.bms"))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual("Completely Different", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Another Artist", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { candidateDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), BeMusicSeeker.Properties.Resources.Warning_InstallEstimationMetadataMismatchPrefix);
            StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationMetadataMismatch);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateDirectoryPath);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateDirectoryPath);
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_HighConfidenceWithoutCandidate_DoesNotSetLowConfidenceState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageNoCandidate");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");

            var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BMSFiles = [];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationArtist));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(pendingEntry));
        });
    }

    [TestMethod]
    public void SearchMergeDestinationForPendingPackage_ResolvedPathUpdatesRepresentativeMetadata()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMergeResolved");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageMergeResolved");
            string pendingFileAPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingA.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            string pendingFileBPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingB.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            string installedFileAPath = CreateBmsFileWithContents(destinationDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            string installedFileBPath = CreateBmsFileWithContents(destinationDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");

            var pendingFileA = BMSFile.CreateBMSFileFromFile(pendingFileAPath);
            var pendingFileB = BMSFile.CreateBMSFileFromFile(pendingFileBPath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFileA, pendingFileB]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(installedFileAPath),
                BMSFile.CreateBMSFileFromFile(installedFileBPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
                Assert.AreEqual("Installed Title", pendingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Installed Artist", pendingEntry.Chart.InstallDestinationArtist);
            }
        });
    }

    [TestMethod]
    public void SearchMergeDestinationForPendingPackage_ResolvesBmsonFromInstalledBmsonHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "BmsonMergeResolved");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonMergeResolved");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Installed Bmson", "Bmson Artist");
            string installedBmsonPath = CreateBmsonFile(destinationDirectoryPath, "installed.bmson", "Installed Bmson", "Bmson Artist");
            PackageChartEntry pendingBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles = [];
            library.BmsonSongs =
            [
                BmsonSongParser.Parse(installedBmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            PackageChartEntry entry = pendingPackage.ChartEntries.Single();
            Assert.AreEqual(destinationDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreEqual("Installed Bmson", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Bmson Artist", entry.Chart.InstallDestinationArtist);
            Assert.IsNull(pendingBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void SearchMergeDestinationForPendingPackage_ResolvedAdapterlessBmsonStaysAdapterless()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "BmsonMergeAdapterless");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonMergeAdapterless");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Installed Bmson", "Bmson Artist");
            string installedBmsonPath = CreateBmsonFile(destinationDirectoryPath, "installed.bmson", "Installed Bmson", "Bmson Artist");
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(pendingBmsonPath)));
            ChartPackage pendingPackage = ChartPackage.FromChartEntries([entry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles = [];
            library.BmsonSongs = [BmsonSongParser.Parse(installedBmsonPath)];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            Assert.IsNull(entry.GetBmsOwnerForTest());
            Assert.AreEqual(destinationDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreEqual("Installed Bmson", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Bmson Artist", entry.Chart.InstallDestinationArtist);
        });
    }

    [TestMethod]
    public void SearchCorrectInstallationDirectoryCharts_BmsonOwnedChartIgnoresCurrentInstalledIndexSelfMatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonWrong");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonCorrect");
            string sourceBmsonPath = CreateBmsonFile(sourceDirectoryPath, "source.bmson", "Repair Bmson", "Bmson Artist");
            string destinationBmsPath = CreateBmsFileWithContents(destinationDirectoryPath, "destination.bms", "#PLAYER 1\r\n#TITLE Repair Bmson\r\n#ARTIST Bmson Artist\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(Path.Combine(destinationDirectoryPath, "sound.wav"), "sound");
            LR2SongDBExtended.bmson_song sourceSong = BmsonSongParser.Parse(sourceBmsonPath);
            library.BMSFiles = [BMSFile.CreateBMSFileFromFile(destinationBmsPath)];
            library.BmsonSongs = [sourceSong];
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, destinationDirectoryPath));

            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(sourceSong));
            library.SearchCorrectInstallationDirectoryCharts([entry]);

            Assert.AreEqual(destinationDirectoryPath, entry.Chart.InstallDestination);
            Assert.IsNull(entry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void SearchMergeDestinationForPendingPackage_ResolvesMixedBmsAndBmsonPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedMergeResolved");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedMergeResolved");
            string pendingBmsPath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Mixed BMS\r\n#ARTIST Mixed Artist\r\n");
            string installedBmsPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Mixed BMS\r\n#ARTIST Mixed Artist\r\n");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Mixed Bmson", "Mixed Artist");
            string installedBmsonPath = CreateBmsonFile(destinationDirectoryPath, "installed.bmson", "Mixed Bmson", "Mixed Artist");
            var pendingBms = BMSFile.CreateBMSFileFromFile(pendingBmsPath);
            PackageChartEntry pendingBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingBms)), pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(installedBmsPath)
            ];
            library.BmsonSongs =
            [
                BmsonSongParser.Parse(installedBmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            }
            Assert.IsNull(pendingBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void SearchMergeDestinationForPendingPackage_MixedSplitInstalledDirectoriesUsesMostMatchingDirectory()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedMergeSplit");
            string primaryDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedMergePrimary");
            string secondaryDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedMergeSecondary");
            string pendingBmsAPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingA.bms", "#PLAYER 1\r\n#TITLE Majority BMS A\r\n#ARTIST Mixed Artist\r\n");
            string pendingBmsBPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingB.bms", "#PLAYER 1\r\n#TITLE Majority BMS B\r\n#ARTIST Mixed Artist\r\n");
            string installedBmsAPath = CreateBmsFileWithContents(primaryDestinationDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Majority BMS A\r\n#ARTIST Mixed Artist\r\n");
            string installedBmsBPath = CreateBmsFileWithContents(primaryDestinationDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Majority BMS B\r\n#ARTIST Mixed Artist\r\n");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Minority Bmson", "Mixed Artist");
            string installedBmsonPath = CreateBmsonFile(secondaryDestinationDirectoryPath, "installed.bmson", "Minority Bmson", "Mixed Artist");
            var pendingBmsA = BMSFile.CreateBMSFileFromFile(pendingBmsAPath);
            var pendingBmsB = BMSFile.CreateBMSFileFromFile(pendingBmsBPath);
            PackageChartEntry pendingBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingBmsA)), PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingBmsB)), pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(installedBmsAPath),
                BMSFile.CreateBMSFileFromFile(installedBmsBPath)
            ];
            library.BmsonSongs =
            [
                BmsonSongParser.Parse(installedBmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(primaryDestinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            }
            Assert.IsNull(pendingBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void SearchMergeDestinationForPendingPackage_MixedSplitTieFallsBackToResourceCandidate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedMergeTie");
            string resourceDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "ResourceWinner");
            string hashOnlyDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "HashOnlyTie");
            string pendingBmsPath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Tie BMS\r\n#ARTIST Mixed Artist\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedBmsPath = CreateBmsFileWithContents(resourceDestinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Tie BMS\r\n#ARTIST Mixed Artist\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Tie Bmson", "Mixed Artist");
            string installedBmsonPath = CreateBmsonFile(hashOnlyDestinationDirectoryPath, "installed.bmson", "Tie Bmson", "Mixed Artist");
            File.WriteAllText(Path.Combine(resourceDestinationDirectoryPath, "sound.wav"), "resource");
            var pendingBms = BMSFile.CreateBMSFileFromFile(pendingBmsPath);
            PackageChartEntry pendingBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingBms)), pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(installedBmsPath)
            ];
            library.BmsonSongs =
            [
                BmsonSongParser.Parse(installedBmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, resourceDestinationDirectoryPath, hashOnlyDestinationDirectoryPath));

            library.SearchMergeDestinationForPendingPackage(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(resourceDestinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            }
            Assert.IsNull(pendingBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void SearchMergeDestinationForPendingCharts_UsesExternalCandidateOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMergeByFile");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "MergeCandidate");
            string pendingFilePath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "pending.bms",
                "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist obj: Diff\r\n#WAVAA 00.wav\r\n#WAVAB 01.wav\r\n#00111:AAAB\r\n");
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "00.wav"), "src");
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "01.wav"), "src");
            CreateBmsFileWithContents(candidateDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "01.wav"), "dst");

            var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BMSFiles = [BMSFile.CreateBMSFileFromFile(Path.Combine(candidateDirectoryPath, "installed.bms"))];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            PackageChartEntry pendingEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingFile));
            library.SearchMergeDestinationForPendingCharts([pendingEntry]);

            Assert.AreEqual(candidateDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Installed Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
        });
    }

    [TestMethod]
    public void SetPendingInstallDestination_UpdatesRepresentativeMetadata()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string pendingDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageManual");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "Manual");
            string pendingFilePath = CreateBmsFileWithContents(pendingDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n");
            string installedFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");

            var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BMSFiles = [BMSFile.CreateBMSFileFromFile(installedFilePath)];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            bool succeeded = library.SetPendingInstallDestination(pendingEntry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Installed Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
        });
    }

    [TestMethod]
    public void SetPendingInstallDestination_UpdatesAdapterlessBmsonEntryWithoutMaterializingAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string pendingDirectoryPath = Path.Combine(tempRootPath, "Pending", "BmsonManual");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonManual");
            string pendingBmsonPath = CreateBmsonFile(pendingDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            string nestedDestinationDirectoryPath = Path.Combine(destinationDirectoryPath, "Nested");
            string installedBmsonPath = CreateBmsonFile(nestedDestinationDirectoryPath, "installed.bmson", "Installed Bmson", "Installed Artist");
            LR2SongDBExtended.bmson_song pendingSong = BmsonSongParser.Parse(pendingBmsonPath);
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingSong));
            ChartPackage pendingPackage = ChartPackage.FromChartEntries([entry]);
            pendingPackage.path = pendingDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles = [];
            library.BmsonSongs = [BmsonSongParser.Parse(installedBmsonPath)];
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destinationDirectoryPath, nestedDestinationDirectoryPath));
            SeedPendingPackages(library, songDbPath, pendingPackage);
            bool succeeded = library.SetPendingInstallDestination(entry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.IsNull(entry.GetBmsOwnerForTest());
            Assert.AreEqual(destinationDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreEqual("Installed Bmson", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", entry.Chart.InstallDestinationArtist);
        });
    }

    [TestMethod]
    public void SetPendingInstallDestination_AllowsStandaloneLibraryFileForFullScan()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Library", "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Library", "Destination");
            string sourceFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "source.bms", "#PLAYER 1\r\n#TITLE Source\r\n#ARTIST Test\r\n");
            string destinationFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "destination.bms", "#PLAYER 1\r\n#TITLE Destination Title\r\n#ARTIST Destination Artist\r\n");

            var sourceFile = BMSFile.CreateBMSFileFromFile(sourceFilePath);
            library.BMSFiles =
            [
                sourceFile,
                BMSFile.CreateBMSFileFromFile(destinationFilePath)
            ];

            PackageChartEntry sourceEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(sourceFile));
            bool succeeded = library.SetPendingInstallDestination(sourceEntry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(destinationDirectoryPath, sourceEntry.Chart.InstallDestination);
            Assert.AreEqual("Destination Title", sourceEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Destination Artist", sourceEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, sourceEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(sourceEntry));
        });
    }

    [TestMethod]
    public void SetPendingInstallDestination_AllowsStandaloneLibraryBmsonForFullScanWithoutAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Library", "BmsonSource");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Library", "BmsonDestination");
            string sourceBmsonPath = CreateBmsonFile(sourceDirectoryPath, "source.bmson", "Source Bmson", "Source Artist");
            string destinationBmsonPath = CreateBmsonFile(destinationDirectoryPath, "destination.bmson", "Destination Bmson", "Destination Artist");
            LR2SongDBExtended.bmson_song sourceSong = BmsonSongParser.Parse(sourceBmsonPath);
            LR2SongDBExtended.bmson_song destinationSong = BmsonSongParser.Parse(destinationBmsonPath);
            PackageChartEntry sourceEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(sourceSong));
            library.BMSFiles = [];
            library.BmsonSongs = [sourceSong, destinationSong];

            bool succeeded = library.SetPendingInstallDestination(sourceEntry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.IsNull(sourceEntry.GetBmsOwnerForTest());
            Assert.AreEqual(destinationDirectoryPath, sourceEntry.Chart.InstallDestination);
            Assert.AreEqual("Destination Bmson", sourceEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Destination Artist", sourceEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, sourceEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(sourceEntry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
        });
    }

    [TestMethod]
    public void SetPendingInstallDestination_RejectsStandaloneFileThatIsNotInLibrary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "External", "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Library", "Destination");
            string sourceFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "source.bms", "#PLAYER 1\r\n#TITLE Source\r\n#ARTIST Test\r\n");
            string destinationFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "destination.bms", "#PLAYER 1\r\n#TITLE Destination Title\r\n#ARTIST Destination Artist\r\n");

            var sourceFile = BMSFile.CreateBMSFileFromFile(sourceFilePath);
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(destinationFilePath)
            ];

            bool succeeded = library.SetPendingInstallDestination(PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(sourceFile)), destinationDirectoryPath);

            Assert.IsFalse(succeeded);
            Assert.AreEqual(string.Empty, ChartFileProjection.FromBmsFile(sourceFile).InstallDestination);
        });
    }

    [TestMethod]
    public void SetPendingInstallDestination_FromLowConfidenceCandidates_PreservesWarningAndSuggestions()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidenceManual");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            bool succeeded = library.SetPendingInstallDestination(pendingEntry, candidateBDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(candidateBDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Candidate B", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist B", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), BeMusicSeeker.Properties.Resources.Warning_InstallEstimationAmbiguousPrefix);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateADirectoryPath);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateBDirectoryPath);
        });
    }

    [TestMethod]
    public void SetPendingInstallDestination_FromAdapterlessBmsonLowConfidenceCandidate_PreservesWarningAndSuggestions()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonLowConfidenceManual");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            LR2SongDBExtended.bmson_song pendingSong = BmsonSongParser.Parse(pendingBmsonPath);
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingSong));
            ChartPackage pendingPackage = ChartPackage.FromChartEntries([entry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            bool succeeded = library.SetPendingInstallDestination(entry, candidateBDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.IsNull(entry.GetBmsOwnerForTest());
            Assert.AreEqual(candidateBDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreEqual("Candidate B", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist B", entry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, entry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
        });
    }

    [TestMethod]
    public void SetPendingInstallDestination_WithManualDirectory_ClearsLowConfidenceState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidenceManualFree");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string manualDirectoryPath = Path.Combine(tempRootPath, "Installed", "Manual");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            string manualInstalledFilePath = CreateBmsFileWithContents(manualDirectoryPath, "manual.bms", "#PLAYER 1\r\n#TITLE Manual Title\r\n#ARTIST Manual Artist\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms")),
                BMSFile.CreateBMSFileFromFile(manualInstalledFilePath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath, manualDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            bool succeeded = library.SetPendingInstallDestination(pendingEntry, manualDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(manualDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Manual Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Manual Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(pendingEntry));
        });
    }

    [TestMethod]
    public void RemoveInstallDestination_ClearsAllAmbiguousWarningLinesAndRepresentativeMetadata()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidenceClear");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);
            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));

            library.RemoveInstallDestination([pendingEntry]);

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationArtist));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(pendingEntry));
        });
    }

    [TestMethod]
    public void RemoveInstallDestination_ClearsMetadataMismatchWarningLinesAndRepresentativeMetadata()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMetadataMismatchClear");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "OnlyCandidate");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Base Artist obj: Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", "#PLAYER 1\r\n#TITLE Completely Different\r\n#ARTIST Another Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "dst");

            var pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BMSFiles =
            [
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateDirectoryPath, "candidate.bms"))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);
            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));

            library.RemoveInstallDestination([pendingEntry]);

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationArtist));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(pendingEntry));
        });
    }

    private static void InvokeRegroupForSourceDirectories(BMSLibrary library, params string[] sourceDirectoryPaths)
    {
        MethodInfo? regroupMethod = typeof(BMSLibrary).GetMethod("TryRegroupPendingPackagesForSourceDirectoriesUnsafe", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(regroupMethod);
        regroupMethod!.Invoke(library, [sourceDirectoryPaths]);
    }

    private static ChartPackage AssertRegroupedPendingPackage(BMSLibrary library, string expectedPackagePath, string expectedDestinationDirectory, int expectedFileCount)
    {
        ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, expectedPackagePath, expectedFileCount);
        Assert.IsTrue(regroupedPackage.ChartEntries.All(entry => string.Equals(entry.Chart.InstallDestination, expectedDestinationDirectory, StringComparison.OrdinalIgnoreCase)));
        return regroupedPackage;
    }

    private static ChartPackage AssertRegroupedPendingPackage(BMSLibrary library, string expectedPackagePath, int expectedFileCount)
    {
        Assert.AreEqual(1, library.ChartPackagesPending.Count);
        ChartPackage regroupedPackage = library.ChartPackagesPending.Single();
        Assert.AreEqual(expectedPackagePath, regroupedPackage.path);
        Assert.IsFalse(regroupedPackage.delete_parent);
        Assert.AreEqual(expectedFileCount, regroupedPackage.ChartEntries.Count);
        return regroupedPackage;
    }

    private static void AssertPendingPackagePaths(BMSLibrary library, params string[] expectedPaths)
    {
        Assert.AreEqual(expectedPaths.Length, library.ChartPackagesPending.Count);
        CollectionAssert.AreEquivalent(
            expectedPaths,
            library.ChartPackagesPending.Select(package => package.path).ToArray());
    }

    private static void SeedPendingPackages(BMSLibrary library, string songDbPath, params ChartPackage[] packages)
    {
        library.ChartPackagesPending = CreatePackageCollection(packages);
        using var songDb = new LR2SongDBExtended(songDbPath);
        songDb.CreateTable<LR2SongDBExtended.install>();
        foreach (ChartPackage package in packages)
        {
            songDb.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
        }
    }

    private static DirectoryResourceLookupCache BuildDirectoryLookupCache(params string[] directories)
    {
        var cache = new DirectoryResourceLookupCache();
        foreach (string directoryPath in directories.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
        {
            cache.AddDir(directoryPath, Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName));
        }
        return cache;
    }

    private static void SetLibraryResourceIndex(BMSLibrary library, DirectoryResourceLookupCache cache)
    {
        var index = new LibraryResourceIndex();
        typeof(LibraryResourceIndex)
            .GetProperty(nameof(LibraryResourceIndex.DirectoryLookupCache), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(index, cache);
        FieldInfo ownerField = typeof(BMSLibrary).GetField(
            "libraryResourceIndexOwner",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var owner = (LibraryResourceIndexOwner)ownerField.GetValue(library)!;
        owner.Replace(index);
    }

    private static string[] LoadInstallPaths(string songDbPath)
    {
        using var songDb = new LR2SongDBExtended(songDbPath);
        songDb.CreateTable<LR2SongDBExtended.install>();
        return [.. songDb.Query<InstallRowRecord>("SELECT path FROM install")
            .Select(row => row.path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static ChartPackage CreatePendingSingleFilePackage(string filePath, string installDestination = "")
    {
        var file = BMSFile.CreateBMSFileFromFile(filePath);
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));
        if (!string.IsNullOrWhiteSpace(installDestination))
        {
            entry.SetInstallDestinationPathOnly(installDestination);
        }
        ChartPackage package = ChartPackage.FromChartEntries([entry]);
        package.path = filePath;
        package.delete_parent = true;
        return package;
    }

    private static PackageChartEntry GetOnlyEntry(ChartPackage package)
    {
        Assert.IsNotNull(package);
        return package.ChartEntries.Single();
    }

    private static PackageChartEntry GetEntryByFileName(ChartPackage package, string fileName)
    {
        Assert.IsNotNull(package);
        return package.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateBmsFile(string directoryPath, string fileName, string titleSuffix)
    {
        return CreateBmsFileWithContents(directoryPath, fileName, "#PLAYER 1\r\n#TITLE " + titleSuffix + "\r\n#ARTIST Test\r\n");
    }

    private static string CreateBmsFileWithContents(string directoryPath, string fileName, string contents)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, contents);
        return filePath;
    }

    private static string CreateBmsonFile(string directoryPath, string fileName, string title, string artist)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"" + title + "\",\"artist\":\"" + artist + "\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]"
            + "}");
        return filePath;
    }

    private static string CreateHealthyBmsonFile(string directoryPath, string fileName, string title, string artist)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"" + title + "\",\"artist\":\"" + artist + "\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[]"
            + "}");
        return filePath;
    }

    private static ChartFile WithChartPath(ChartFile source, string path)
    {
        return new ChartFile(
            source.Kind,
            path,
            source.Md5,
            source.Sha256,
            source.Title,
            source.RawTitle,
            source.Artist,
            source.Genre,
            source.Folder,
            source.Tag,
            source.LevelText,
            source.Level,
            source.Mode,
            source.ChartInfo,
            source.GetBmsStorageOwner(),
            source.GetBmsonStorageOwner(),
            source.Subtitle,
            source.AudioResourcePaths,
            source.VisualResourcePaths,
            source.Stagefile,
            source.Backbmp,
            source.Banner,
            source.InstallDestination,
            source.InstallDestinationTitle,
            source.InstallDestinationArtist,
            source.InstallDestinationSuggestions,
            source.Warnings,
            source.WAVHealth,
            source.BGAHealth,
            source.MovieHealth,
            source.StagefileHealth,
            source.BannerHealth,
            source.BackbmpHealth,
            source.EncodingName);
    }

    private static void ApplySingleFileWarnings(params ChartPackage[] packages)
    {
        foreach (BMSFile file in (packages ?? []).Where(package => package != null).SelectMany(package => package.GetBmsOwnersForTest()).Where(file => file != null))
        {
            file.SetWarning(ChartWarningKind.SingleBmsFile, BeMusicSeeker.Properties.Resources.Warning_SingleBmsFile);
        }
    }

    private static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    private static void WithTemporaryLibrary(
        Action<string, string, BMSLibrary> testAction,
        IInstallEstimationExecutionObserver? installEstimationExecutionObserver = null)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PendingRegroupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            TestBmsLibrary library = installEstimationExecutionObserver == null
                ? new TestBmsLibrary(songDbPath, null!, null, null!, new RecordingDialogService())
                : new TestBmsLibrary(songDbPath, null!, null, null!, new RecordingDialogService(), installEstimationExecutionObserver);
            testAction(tempRootPath, songDbPath, library);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static void WithAutoApplyAmbiguousInstallDestination(bool enabled, Action action)
    {
        bool original = BeMusicSeeker.Properties.Settings.Default.AutoApplyAmbiguousInstallDestination;
        try
        {
            BeMusicSeeker.Properties.Settings.Default.AutoApplyAmbiguousInstallDestination = enabled;
            action();
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.AutoApplyAmbiguousInstallDestination = original;
        }
    }

    private sealed class RecordingInstallEstimationExecutionObserver : IInstallEstimationExecutionObserver
    {
        private readonly object gate = new();
        private readonly Barrier? workItemBarrier;
        private readonly Action<InstallEstimationAttemptEvaluatedObservation>? attemptObserver;
        private int activeWorkItems;
        private int maxActive;

        internal RecordingInstallEstimationExecutionObserver(
            int expectedMaxActive = 0,
            Action<InstallEstimationAttemptEvaluatedObservation>? attemptObserver = null)
        {
            this.attemptObserver = attemptObserver;
            if (expectedMaxActive > 1)
            {
                workItemBarrier = new Barrier(expectedMaxActive);
            }
        }

        internal List<InstallEstimationWorkItemObservation> WorkItems { get; } = [];

        internal List<InstallEstimationProgressObservation> Progress { get; } = [];

        internal List<InstallEstimationAttemptEvaluatedObservation> Attempts { get; } = [];

        internal int MaxActive => Volatile.Read(ref maxActive);

        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            lock (gate)
            {
                WorkItems.Add(observation);
            }
            int currentActive = Interlocked.Increment(ref activeWorkItems);
            UpdateMaxActive(currentActive);
            workItemBarrier?.SignalAndWait();
            return new WorkItemScope(this);
        }

        public void ObserveProgress(InstallEstimationProgressObservation observation)
        {
            lock (gate)
            {
                Progress.Add(observation);
            }
        }

        public void ObserveAttemptEvaluated(InstallEstimationAttemptEvaluatedObservation observation)
        {
            lock (gate)
            {
                Attempts.Add(observation);
            }
            attemptObserver?.Invoke(observation);
        }

        private void UpdateMaxActive(int currentActive)
        {
            while (true)
            {
                int previousMax = Volatile.Read(ref maxActive);
                if (currentActive <= previousMax
                    || Interlocked.CompareExchange(ref maxActive, currentActive, previousMax) == previousMax)
                {
                    return;
                }
            }
        }

        private void EndWorkItem()
        {
            Interlocked.Decrement(ref activeWorkItems);
        }

        private sealed class WorkItemScope(RecordingInstallEstimationExecutionObserver owner) : IDisposable
        {
            private RecordingInstallEstimationExecutionObserver? owner = owner;

            public void Dispose()
            {
                Interlocked.Exchange(ref owner, null)?.EndWorkItem();
            }
        }
    }

    private sealed class ThrowingInstallEstimationExecutionObserver : IInstallEstimationExecutionObserver
    {
        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            throw new InvalidOperationException("install-estimation observer failed");
        }

        public void ObserveProgress(InstallEstimationProgressObservation observation)
        {
        }

        public void ObserveAttemptEvaluated(InstallEstimationAttemptEvaluatedObservation observation)
        {
        }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon, UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            return UiDialogDefaultResult.OK;
        }
    }

    private sealed class InstallRowRecord
    {
        public string path { get; set; } = string.Empty;
    }
}
