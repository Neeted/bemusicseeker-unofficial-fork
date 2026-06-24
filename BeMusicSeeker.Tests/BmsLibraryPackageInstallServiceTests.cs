using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Livet;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPackageInstallServiceTests
{
    [TestMethod]
    public void GetPendingPackagesContainingOnlyInstalledCharts_UsesPackageChartEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "package");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, "{\"info\":{\"title\":\"Song\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[]}");
            var package = new ChartPackage
            {
                path = packageDirectoryPath
            };

            List<ChartPackage> result = service.GetPendingPackagesContainingOnlyInstalledCharts(
                [package],
                chart => chart?.Kind == ChartFileKind.Bmson && string.Equals(chart.Path, bmsonPath, StringComparison.OrdinalIgnoreCase));

            Assert.AreEqual(1, result.Count);
            Assert.AreSame(package, result[0]);
        });
    }

    [TestMethod]
    public void GetPendingPackagesContainingOnlyInstalledCharts_MatchesInstalledBmsonByChartEntryHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string packageDirectoryPath = Path.Combine(tempRootPath, "package");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, "{\"info\":{\"title\":\"Song\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[]}");
            LR2SongDBExtended.bmson_song installedBmson = BmsonSongParser.Parse(bmsonPath);
            var pendingPackage = new ChartPackage
            {
                path = packageDirectoryPath
            };
            var library = new BMSLibrary(songDbPath)
            {
                BmsonSongs = [installedBmson],
                ChartPackagesPending = CreatePackageCollection([pendingPackage])
            };

            List<ChartPackage> result = library.GetPendingPackagesContainingOnlyInstalledCharts();

            Assert.AreEqual(1, result.Count);
            Assert.AreSame(pendingPackage, result[0]);
        });
    }

    [TestMethod]
    public void InstallPendingPackagesToEstimatedDestinations_ResourceOnlyBmsonWorksWithoutBmsFiles()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath, string tempRootPath)
        {
            string destinationDirectoryPath = Path.Combine(tempRootPath, "installed");
            string pendingDirectoryPath = Path.Combine(tempRootPath, "pending");
            Directory.CreateDirectory(destinationDirectoryPath);
            Directory.CreateDirectory(pendingDirectoryPath);
            string installedBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            string pendingBmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
            string pendingResourcePath = Path.Combine(pendingDirectoryPath, "sound.wav");
            File.WriteAllText(installedBmsonPath, "{}");
            File.WriteAllText(pendingBmsonPath, "{}");
            File.WriteAllText(pendingResourcePath, "resource");
            var installedBmson = new LR2SongDBExtended.bmson_song
            {
                path = installedBmsonPath,
                folder = destinationDirectoryPath,
                title = "Installed",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                sha256 = new string('b', 64)
            };
            var pendingBmson = new LR2SongDBExtended.bmson_song
            {
                path = pendingBmsonPath,
                folder = pendingDirectoryPath,
                title = "Pending",
                md5 = installedBmson.md5,
                sha256 = installedBmson.sha256
            };
            PackageChartEntry pendingEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingBmson, includeWarningSnapshot: false, includeResourceReferences: false));
            pendingEntry.ApplyInstallDestination(destinationDirectoryPath, "Installed", "Artist");
            ChartPackage pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
            pendingPackage.path = pendingDirectoryPath;
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = null,
                BmsonSongs = [installedBmson],
                ChartPackagesPending = CreatePackageCollection([pendingPackage]),
                ChartPackagesInstalled = CreatePackageCollection([])
            };

            library.InstallPendingPackagesToEstimatedDestinations([pendingPackage]);

            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
            ChartPackage displayPackage = library.ChartPackagesInstalled.Single();
            Assert.AreEqual(destinationDirectoryPath, displayPackage.path);
            ChartFile displayChart = displayPackage.ChartEntries.Single().Chart;
            Assert.AreSame(installedBmson, displayChart.GetBmsonStorageOwner());
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "sound.wav")));
        });
    }

    [TestMethod]
    public void BuildComponentMovePlan_SkipsExcludedPaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "src");
            Directory.CreateDirectory(sourceDirectoryPath);
            string keepFilePath = Path.Combine(sourceDirectoryPath, "keep.txt");
            string skipFilePath = Path.Combine(sourceDirectoryPath, "skip.txt");
            File.WriteAllText(keepFilePath, "keep");
            File.WriteAllText(skipFilePath, "skip");

            ComponentMovePlanBuildResult result = service.BuildComponentMovePlan(
                [sourceDirectoryPath],
                Path.Combine(tempDirectoryPath, "dst"),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { skipFilePath });

            Assert.AreEqual(1, result.PlanItems.Count);
            Assert.AreEqual(1, result.SkippedByExclusion);
            Assert.AreEqual(keepFilePath, result.PlanItems[0].SourcePath);
        });
    }

    [TestMethod]
    public void DecideComponentMove_PrefersOverwriteWhenSourceIsNewer()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string sourceFilePath = Path.Combine(tempDirectoryPath, "source.txt");
            string destinationFilePath = Path.Combine(tempDirectoryPath, "destination.txt");
            File.WriteAllText(sourceFilePath, "source");
            File.WriteAllText(destinationFilePath, "dest");
            File.SetLastWriteTimeUtc(destinationFilePath, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(sourceFilePath, DateTime.UtcNow);

            ComponentMoveDecision decision = service.DecideComponentMove(sourceFilePath, destinationFilePath);

            Assert.AreEqual(ComponentMoveDecision.Overwrite, decision);
        });
    }

    [TestMethod]
    public void BuildPendingPackageMutationDelta_RemovesMatchedChartPathsAndDeletesEmptyPackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile keepFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\keep.bms");
        TestableBmsFile removeFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\remove.bms");
        TestableBmsFile removeWholePackageFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\only.bms");
        var keepPackage = ChartPackageTestExtensions.CreatePackage([keepFile, removeFile]);
        keepPackage.path = "C:\\Pending\\Pkg1";
        keepPackage.delete_parent = false;
        var removePackage = ChartPackageTestExtensions.CreatePackage([removeWholePackageFile]);
        removePackage.path = "C:\\Pending\\Pkg2";
        removePackage.delete_parent = false;

        PendingPackageMutationDelta delta = service.BuildPendingPackageMutationDelta(
            [keepPackage, removePackage],
            chartPathsToRemove: [removeFile.path, removeWholePackageFile.path]);

        Assert.IsTrue(delta.HasChanges);
        Assert.AreEqual(1, delta.RemainingPackages.Count);
        Assert.AreSame(keepPackage, delta.RemainingPackages[0]);
        CollectionAssert.AreEqual(new[] { keepFile }, keepPackage.GetBmsOwnersForTest());
        CollectionAssert.AreEquivalent(new[] { "C:\\Pending\\Pkg2" }, delta.InstallPathsToDelete);
    }

    [TestMethod]
    public void BuildPendingPackageMutationDelta_RemovesAdapterlessBmsonByChartPathWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        var keepEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg\\keep.bmson",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256 = new string('a', 64)
        }));
        var removeEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg\\remove.bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        }));
        ChartPackage package = ChartPackage.FromChartEntries([keepEntry, removeEntry]);
        package.path = "C:\\Pending\\Pkg";

        PendingPackageMutationDelta delta = service.BuildPendingPackageMutationDelta(
            [package],
            chartPathsToRemove: [removeEntry.Chart.Path]);

        Assert.IsTrue(delta.HasChanges);
        Assert.AreEqual(1, delta.RemainingPackages.Count);
        Assert.AreSame(package, delta.RemainingPackages[0]);
        Assert.IsNull(keepEntry.GetBmsOwnerForTest());
        Assert.IsNull(removeEntry.GetBmsOwnerForTest());
        Assert.AreEqual(1, package.ChartEntries.Count);
        Assert.AreEqual(keepEntry.Chart.Path, package.ChartEntries[0].Chart.Path);
        Assert.IsNull(package.ChartEntries[0].GetBmsOwnerForTest());
        Assert.AreEqual(0, delta.InstallPathsToDelete.Count);
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_GroupsNewChartsAndKeepsCleanupOnlyCandidates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Lib\\a.bms");
        TestableBmsFile cleanupInstalledFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Lib\\c.bms");
        TestableBmsFile alreadyInstalledInPackage = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        TestableBmsFile newFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\b.bms");
        TestableBmsFile cleanupOnlyFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\c.bms");
        var mixedPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(alreadyInstalledInPackage, destinationDirectory),
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(newFile, destinationDirectory));
        mixedPackage.path = "C:\\Pending\\Pkg1";
        mixedPackage.delete_parent = false;
        var cleanupOnlyPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(cleanupOnlyFile, destinationDirectory));
        cleanupOnlyPackage.path = "C:\\Pending\\Pkg2";
        cleanupOnlyPackage.delete_parent = false;

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [mixedPackage, cleanupOnlyPackage],
            [mixedPackage, cleanupOnlyPackage],
            CreateInstalledChartLookup([installedFile, cleanupInstalledFile]),
            deletePendingPackageSourceAfterInstall: true,
            countComponentMoveTargets: (pkg, dst, excluded) => pkg.path == cleanupOnlyPackage.path ? 0 : 2);

        Assert.AreEqual(2, plan.SelectedPendingPackages.Count);
        Assert.AreEqual(1, plan.Groups.Count);
        Assert.AreEqual(1, plan.CleanupOnlyCandidates.Count);
        Assert.AreSame(cleanupOnlyPackage, plan.CleanupOnlyCandidates[0]);
        Assert.AreEqual(1, plan.InstallTargetFileCount);
        PendingInstallBatchItem groupedItem = plan.Groups[0].Items.Single();
        Assert.AreSame(mixedPackage, groupedItem.OriginalPackage);
        Assert.AreEqual(destinationDirectory, groupedItem.DestinationDirectory);
        Assert.AreEqual(1, groupedItem.InstallWorkPackage.GetBmsOwnersForTest().Count);
        Assert.AreSame(newFile, groupedItem.InstallWorkPackage.GetBmsOwnersForTest()[0]);
        CollectionAssert.Contains(groupedItem.ExcludedComponentPaths.ToList(), alreadyInstalledInPackage.path);
        PackageChartEntry alreadyInstalledEntry = mixedPackage.ChartEntries.Single(entry => ReferenceEquals(entry.Chart.GetBmsStorageOwner(), alreadyInstalledInPackage));
        Assert.IsTrue(alreadyInstalledEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
        Assert.AreEqual("[1] " + BeMusicSeeker.Properties.Resources.WarningDigest_AlreadyInstalled, ChartWarningTestHelpers.BuildDigestText(alreadyInstalledEntry));
        Assert.IsTrue(plan.FilterMs >= 0);
        Assert.IsTrue(plan.GroupBuildMs >= 0);
        Assert.IsTrue(plan.PlanBuildMs >= 0);
        Assert.AreEqual(2, plan.SelectedPendingCount);
        Assert.AreEqual(1, plan.GroupedPackageCount);
        Assert.AreEqual(1, plan.CleanupOnlyCandidateCount);
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_DoesNotTreatMd5MismatchAsInstalledWhenSha256Matches()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Lib\\a.bms");
        installedFile.SetSha256(new string('b', 64));
        TestableBmsFile pendingFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg1\\a.bms");
        pendingFile.SetSha256(new string('b', 64));
        var pendingPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingFile, destinationDirectory));
        pendingPackage.path = "C:\\Pending\\Pkg1";
        pendingPackage.delete_parent = false;

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [pendingPackage],
            [pendingPackage],
            CreateInstalledChartLookup([installedFile]),
            deletePendingPackageSourceAfterInstall: false,
            countComponentMoveTargets: (_, _, _) => 1);

        Assert.AreEqual(1, plan.Groups.Count);
        Assert.AreEqual(1, plan.Groups[0].Items.Count);
        Assert.AreEqual(1, plan.Groups[0].Items[0].InstallWorkPackage.GetBmsOwnersForTest().Count);
        Assert.AreSame(pendingFile, plan.Groups[0].Items[0].InstallWorkPackage.GetBmsOwnersForTest()[0]);
        Assert.AreEqual(string.Empty, pendingFile.Warnings.BuildDigestText());
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_TreatsInstalledBmsonAsInstalledByPrimaryHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        var installedBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Lib\\chart.bmson",
            folder = "C:\\Lib",
            title = "Installed Bmson",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var pendingBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg1\\chart.bmson",
            folder = "C:\\Pending\\Pkg1",
            title = "Pending Bmson",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        PackageChartEntry pendingEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingBmson));
        pendingEntry.ApplyInstallDestination(destinationDirectory, "Pending Bmson", "Artist");
        ChartPackage pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
        pendingPackage.path = "C:\\Pending\\Pkg1";
        pendingPackage.delete_parent = false;

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [pendingPackage],
            [pendingPackage],
            CreateInstalledChartLookup([], [installedBmson]),
            deletePendingPackageSourceAfterInstall: true,
            countComponentMoveTargets: (_, _, _) => 0);

        Assert.AreEqual(0, plan.Groups.Count);
        Assert.AreEqual(1, plan.CleanupOnlyCandidates.Count);
        Assert.AreSame(pendingPackage, plan.CleanupOnlyCandidates[0]);
        Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
        Assert.IsNull(pendingEntry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_CountsDeferredManualHoldPackagesSeparately()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        var deferredPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
        deferredPackage.path = "C:\\Pending\\Pkg1";
        deferredPackage.delete_parent = false;
        deferredPackage.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [deferredPackage],
            [deferredPackage],
            CreateInstalledChartLookup([]),
            deletePendingPackageSourceAfterInstall: false,
            countComponentMoveTargets: (_, _, _) => 0);

        Assert.AreEqual(1, plan.SelectedPendingPackages.Count);
        Assert.AreEqual(0, plan.Groups.Count);
        Assert.AreEqual(1, plan.DeferredManualHoldCount);
    }

    [TestMethod]
    public void ExecuteEstimatedInstallBatchPlan_ReturnsPendingMutationsAndCleanupSummary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Lib\\a.bms");
        TestableBmsFile cleanupInstalledFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Lib\\c.bms");
        TestableBmsFile alreadyInstalledInPackage = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        TestableBmsFile newFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\b.bms");
        TestableBmsFile cleanupOnlyFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\c.bms");
        var mixedPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(alreadyInstalledInPackage, destinationDirectory),
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(newFile, destinationDirectory));
        mixedPackage.path = "C:\\Pending\\Pkg1";
        mixedPackage.delete_parent = false;
        var cleanupOnlyPackage = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(cleanupOnlyFile, destinationDirectory));
        cleanupOnlyPackage.path = "C:\\Pending\\Pkg2";
        cleanupOnlyPackage.delete_parent = false;
        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [mixedPackage, cleanupOnlyPackage],
            [mixedPackage, cleanupOnlyPackage],
            CreateInstalledChartLookup([installedFile, cleanupInstalledFile]),
            deletePendingPackageSourceAfterInstall: true,
            countComponentMoveTargets: (pkg, dst, excluded) => pkg.path == cleanupOnlyPackage.path ? 0 : 2);

        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            true,
            (installPackages, destinationDirectoryArg, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall) =>
            {
                deferredInstalledPackages.AddRange(installPackages);
                foreach (ChartPackage installPackage in installPackages)
                {
                    deferredMaintenanceCharts.AddRange(installPackage.ChartEntries.Select(entry => entry.Chart));
                }
                return [];
            },
            (originalPackage, destinationDirectoryArg) =>
            {
                ChartPackage package = ChartPackageTestExtensions.CreatePackage(originalPackage.GetBmsOwnersForTest());
                package.path = destinationDirectoryArg;
                package.delete_parent = false;
                return package;
            },
            (cleanupPackage) => cleanupPackage == cleanupOnlyPackage
                ? (true, CleanupSourceKind.MissingSource)
                : (false, CleanupSourceKind.MissingSource));

        Assert.AreEqual(2, result.PendingPackagesToRemove.Count);
        CollectionAssert.AreEquivalent(new[] { mixedPackage.path, cleanupOnlyPackage.path }, result.InstallRowsToDelete.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.AreEqual(1, result.DeferredInstalledPackages.Count);
        Assert.AreEqual(1, result.CleanupOnlySucceeded);
        Assert.AreEqual(0, result.CleanupOnlyFailed);
        Assert.AreEqual(1, result.CleanupOnlyMissingSource);
        Assert.AreEqual(1, result.DeferredMaintenanceCharts.Count);
        Assert.AreSame(newFile, result.DeferredMaintenanceCharts[0].GetBmsStorageOwner());
        Assert.IsTrue(mixedPackage.ChartEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
        Assert.IsTrue(cleanupOnlyPackage.ChartEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
    }

    [TestMethod]
    public void ExecuteEstimatedInstallBatchPlan_CarriesDeferredBmsonMaintenanceCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg1\\chart.bmson",
            folder = "C:\\Pending\\Pkg1",
            title = "Bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        PackageChartEntry bmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
        bmsonEntry.ApplyInstallDestination(destinationDirectory, "Bmson", "Artist");
        ChartPackage bmsonPackage = ChartPackage.FromChartEntries([bmsonEntry]);
        bmsonPackage.path = "C:\\Pending\\Pkg1";

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [bmsonPackage],
            [bmsonPackage],
            CreateInstalledChartLookup([]),
            deletePendingPackageSourceAfterInstall: false,
            countComponentMoveTargets: (_, _, _) => 1);

        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            false,
            (installPackages, destinationDirectoryArg, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall) =>
            {
                deferredInstalledPackages.AddRange(installPackages);
                foreach (PackageChartEntry entry in installPackages.SelectMany(package => package.ChartEntries))
                {
                    deferredMaintenanceCharts.Add(entry?.Chart);
                }
                return [];
            },
            (originalPackage, destinationDirectoryArg) => null,
            (_) => (false, CleanupSourceKind.MissingSource));

        Assert.AreEqual(1, result.DeferredMaintenanceCharts.Count);
        Assert.AreSame(bmsonSong, result.DeferredMaintenanceCharts[0].GetBmsonStorageOwner());
        Assert.IsNull(bmsonEntry.GetBmsOwnerForTest());
        Assert.AreEqual(string.Empty, bmsonEntry.Chart.InstallDestination);
    }

    [TestMethod]
    public void ForceInstallPackages_SkipsWhenConfirmationRejected()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        PackageChartEntry pendingEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingFile));
        pendingEntry.SetInstallDestinationPathOnly("C:\\Installed\\Target");
        var pendingPackage = ChartPackage.FromChartEntries([pendingEntry]);
        pendingPackage.path = "C:\\Pending\\Pkg1";
        pendingPackage.delete_parent = false;

        ForceInstallBatchResult result = service.ForceInstallPackages(
            [pendingPackage],
            [pendingPackage],
            _ => false,
            (_, __) => []);

        Assert.AreEqual(1, result.Requested);
        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, result.Processed);
        Assert.AreEqual(0, result.PendingPackagesToRemove.Count);
        Assert.AreEqual("C:\\Installed\\Target", pendingEntry.Chart.InstallDestination);
    }

    [TestMethod]
    public void ForceInstallPackages_ChecksInstallDestinationWithoutMaterializingAdapterlessBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        PackageChartEntry pendingBmsEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingFile));
        pendingBmsEntry.SetInstallDestinationPathOnly("C:\\Installed\\Target");
        var adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg1\\chart.bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64),
            title = "Bmson"
        }));
        ChartPackage pendingPackage = ChartPackage.FromChartEntries([pendingBmsEntry, adapterlessBmsonEntry]);
        pendingPackage.path = "C:\\Pending\\Pkg1";
        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());

        ForceInstallBatchResult result = service.ForceInstallPackages(
            [pendingPackage],
            [pendingPackage],
            _ => false,
            (_, __) => []);

        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, result.Processed);
        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void ChartPackage_ClearEntryInstallDestinations_DoesNotMaterializeAdapterlessBmsonEntries()
    {
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg\\a.bms");
        PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.WithPackageState(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Pkg\\adapterless.bmson",
            md5 = "cccccccccccccccccccccccccccccccc"
        }), "C:\\Installed\\Target", "Installed", "Artist", []));
        PackageChartEntry bmsEntry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(bmsFile, "C:\\Installed\\Target");
        ChartPackage package = ChartPackage.FromChartEntries(
        [
            bmsEntry,
            adapterlessBmsonEntry
        ]);

        foreach (PackageChartEntry entry in package.ChartEntries)
        {
            entry.ClearInstallDestination();
        }

        Assert.AreEqual(string.Empty, bmsEntry.Chart.InstallDestination);
        Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        Assert.AreEqual(string.Empty, adapterlessBmsonEntry.Chart.InstallDestination);
    }

    [TestMethod]
    public void ApplyPendingResourceHealthProjection_BmsonUsesChartResourceSnapshot()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            var song = new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = tempDirectoryPath,
                title = "BMSON",
                artist = "Artist",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                wav_files = ["missing.wav"]
            };
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(song));

            IReadOnlyList<ChartWarning> warnings = BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(entry);

            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsNull(entry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void ApplyPendingResourceHealthProjection_StoresWarningsAndHealthOnPackageEntry()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            var song = new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = tempDirectoryPath,
                title = "BMSON",
                artist = "Artist",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                wav_files = ["missing.wav"],
                bga_files = ["missing.png"]
            };
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(song));

            IReadOnlyList<ChartWarning> warnings = BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(entry);

            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
            Assert.IsTrue(entry.Chart.WAVHealth.HasValue);
            Assert.IsTrue(entry.Chart.BGAHealth.HasValue);
            Assert.IsNull(entry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void ApplyPendingResourceHealthProjection_BmsUsesChartProjectionWithoutMutatingMaintenance()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                + "#TITLE BMS\r\n"
                + "#WAVAA missing.wav\r\n"
                + "#00111:AA\r\n");
            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));

            IReadOnlyList<ChartWarning> warnings = BmsLibraryPackageInstallService.ApplyPendingResourceHealthProjection(entry);

            Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(file.HasValidMaintenanceInfoSnapshot);
            Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ClassifiesBmsResourcesWithoutMutatingMaintenance()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsMissingResource");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsFilePath = Path.Combine(packageDirectoryPath, "chart.bms");
            File.WriteAllText(
                bmsFilePath,
                "#PLAYER 1\r\n"
                + "#TITLE BMS\r\n"
                + "#WAVAA missing.wav\r\n"
                + "#00111:AA\r\n");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            PackageChartEntry chartEntry = result.PendingPackagesToAdd[0].ChartEntries.Single();
            BMSFile file = chartEntry.Chart.GetBmsStorageOwner();
            Assert.IsNotNull(file);
            Assert.IsFalse(file.HasValidMaintenanceInfoSnapshot);
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ProjectsResourceHealthForAllPackageEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsMixedResource");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(
                Path.Combine(packageDirectoryPath, "missing.bms"),
                "#PLAYER 1\r\n"
                + "#TITLE Missing\r\n"
                + "#WAVAA missing.wav\r\n"
                + "#00111:AA\r\n");
            File.WriteAllText(
                Path.Combine(packageDirectoryPath, "healthy.bms"),
                "#PLAYER 1\r\n"
                + "#TITLE Healthy\r\n"
                + "#WAVAA sound.wav\r\n"
                + "#00111:AA\r\n");
            File.WriteAllBytes(Path.Combine(packageDirectoryPath, "sound.wav"), new byte[] { 1 });
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            ChartPackage pendingPackage = result.PendingPackagesToAdd.Single();
            PackageChartEntry missingEntry = pendingPackage.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals("missing.bms", StringComparison.OrdinalIgnoreCase));
            PackageChartEntry healthyEntry = pendingPackage.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals("healthy.bms", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(missingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(missingEntry.Chart.WAVHealth.HasValue);
            Assert.AreEqual(100, healthyEntry.Chart.WAVHealth);
            Assert.IsFalse(healthyEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ProjectsResourceHealthForAlreadyInstalledEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsInstalledResource");
            Directory.CreateDirectory(packageDirectoryPath);
            string installedPath = Path.Combine(packageDirectoryPath, "installed.bms");
            File.WriteAllText(
                installedPath,
                "#PLAYER 1\r\n"
                + "#TITLE Installed\r\n"
                + "#WAVAA missing.wav\r\n"
                + "#00111:AA\r\n");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                chart => string.Equals(chart?.Path, installedPath, StringComparison.OrdinalIgnoreCase),
                0.6);

            PackageChartEntry installedEntry = result.PendingPackagesToAdd.Single().ChartEntries.Single();
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(installedEntry.Chart.WAVHealth.HasValue);
        });
    }

    [TestMethod]
    public void DeletePendingPackageSources_RemovesPackagesWhoseSourceWasDeleted()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg1");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bms"), "#PLAYER 1");
            var pendingPackage = ChartPackageTestExtensions.CreatePackage(Enumerable.Empty<BMSFile>());
            pendingPackage.path = packageDirectoryPath;
            pendingPackage.delete_parent = false;

            PendingPackageSourceDeletionResult result = service.DeletePendingPackageSources(
                [pendingPackage],
                [pendingPackage],
                sendToRecycleBin: false,
                new TestFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(1, result.Removed);
            Assert.AreEqual(0, result.Failed);
            CollectionAssert.AreEqual(new[] { pendingPackage }, result.PackagesToRemove);
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_PackagesSplitsIndependentChartsIntoSingleFilePackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_a.bms"), "#PLAYER 1\r\n#TITLE A\r\n#WAVAA sound_a.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_b.bms"), "#PLAYER 1\r\n#TITLE B\r\n#WAVAA sound_b.wav\r\n#00111:AA\r\n");

            var service = new BmsLibraryPackageInstallService();
            List<ChartPackage> result = service.SearchChartPackagesRecursivelyWithMetadata(packageDirectoryPath, 0.6).Packages;

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.All(package => File.Exists(package.path)));
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_MarksSplitDirectoryAsRegroupEligible()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_a.bms"), "#PLAYER 1\r\n#TITLE A\r\n#WAVAA sound_a.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_b.bms"), "#PLAYER 1\r\n#TITLE B\r\n#WAVAA sound_b.wav\r\n#00111:AA\r\n");

            var service = new BmsLibraryPackageInstallService();
            ChartPackageDiscoveryResult result = service.SearchChartPackagesRecursivelyWithMetadata(packageDirectoryPath, 0.6);

            Assert.AreEqual(2, result.Packages.Count);
            CollectionAssert.AreEqual(new[] { packageDirectoryPath }, result.RegroupEligibleSourceDirectories);
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_MarksNestedSplitDirectoryAsRegroupEligible()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string rootDirectoryPath = Path.Combine(tempDirectoryPath, "Root");
            string childDirectoryPath = Path.Combine(rootDirectoryPath, "Child");
            Directory.CreateDirectory(childDirectoryPath);
            File.WriteAllText(Path.Combine(childDirectoryPath, "chart_a.bms"), "#PLAYER 1\r\n#TITLE A\r\n#WAVAA sound_a.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(childDirectoryPath, "chart_b.bms"), "#PLAYER 1\r\n#TITLE B\r\n#WAVAA sound_b.wav\r\n#00111:AA\r\n");

            var service = new BmsLibraryPackageInstallService();
            ChartPackageDiscoveryResult result = service.SearchChartPackagesRecursivelyWithMetadata(rootDirectoryPath, 0.6);

            Assert.AreEqual(2, result.Packages.Count);
            CollectionAssert.AreEqual(new[] { childDirectoryPath }, result.RegroupEligibleSourceDirectories);
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_DetectsPureBmsonDirectoryPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bmson"), "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Title\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]}");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "dummy");

            var service = new BmsLibraryPackageInstallService();
            ChartPackageDiscoveryResult result = service.SearchChartPackagesRecursivelyWithMetadata(packageDirectoryPath, 0.6);

            Assert.AreEqual(1, result.Packages.Count);
            Assert.AreEqual(packageDirectoryPath, result.Packages[0].path);
            Assert.AreEqual(1, result.Packages[0].ChartEntries.Count(entry => entry?.Chart?.Kind == ChartFileKind.Bmson));
            Assert.IsTrue(result.Packages[0].ChartEntries.All(entry => entry.GetBmsOwnerForTest() == null));
        });
    }

    [TestMethod]
    public void SearchChartPackagesRecursivelyWithMetadata_DetectsRootAndNestedChartsAsOneDirectoryPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            string nestedDirectoryPath = Path.Combine(packageDirectoryPath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "root.bms"), "#PLAYER 1\r\n#TITLE Root\r\n");
            File.WriteAllText(Path.Combine(nestedDirectoryPath, "another.bms"), "#PLAYER 1\r\n#TITLE Nested\r\n");

            var service = new BmsLibraryPackageInstallService();
            ChartPackageDiscoveryResult result = service.SearchChartPackagesRecursivelyWithMetadata(packageDirectoryPath, 0.6);

            Assert.AreEqual(1, result.Packages.Count);
            Assert.AreEqual(packageDirectoryPath, result.Packages[0].path);
            CollectionAssert.AreEquivalent(
                new[] { "root.bms", "another.bms" },
                result.Packages[0].GetBmsOwnersForTest().Select(file => Path.GetFileName(file.path)).ToArray());
        });
    }

    [TestMethod]
    public void ApplyNestedChartFileWarnings_AddsNestedWarningWithoutMaterializingAdapterlessBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            string nestedDirectoryPath = Path.Combine(packageDirectoryPath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            string rootBmsonPath = Path.Combine(packageDirectoryPath, "root.bmson");
            string nestedBmsonPath = Path.Combine(nestedDirectoryPath, "nested.bmson");
            File.WriteAllText(rootBmsonPath, CreateBmsonJsonWithSound("root.wav"));
            File.WriteAllText(nestedBmsonPath, CreateBmsonJsonWithSound("nested.wav"));
            PackageChartEntry rootEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(rootBmsonPath)));
            PackageChartEntry nestedEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(nestedBmsonPath)));
            ChartPackage package = ChartPackage.FromChartEntries([rootEntry, nestedEntry]);
            package.path = packageDirectoryPath;

            bool applied = BmsLibraryPackageInstallService.ApplyNestedChartFileWarnings(package);

            Assert.IsTrue(applied);
            Assert.IsNull(rootEntry.GetBmsOwnerForTest());
            Assert.IsNull(nestedEntry.GetBmsOwnerForTest());
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            StringAssert.Contains(ChartWarningCollection.BuildTooltipText(nestedEntry.Chart.Warnings), Resources.Warning_NestedChartFileInPackage);
            PackageChartEntry normalizedNestedEntry = PackageChartEntry.FromChart(nestedEntry.Chart);
            Assert.IsNull(normalizedNestedEntry.GetBmsOwnerForTest());
            Assert.IsTrue(normalizedNestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            PackageChartEntry clearedNormalizedNestedEntry = PackageChartEntry.FromChart(nestedEntry.Chart);
            clearedNormalizedNestedEntry.ClearStructuredWarnings();
            Assert.IsFalse(clearedNormalizedNestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_DetectsSingleBmsonFileSelection()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "BmsonSingle");
            Directory.CreateDirectory(sourceDirectoryPath);
            string bmsonFilePath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonFilePath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Title\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},\"lines\":[{\"y\":0}]}");
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "other.txt"), "note");

            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [bmsonFilePath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.AreEqual(bmsonFilePath, result.DiscoveredPackages[0].path);
            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(bmsonFilePath, result.PendingPackagesToAdd[0].path);
            Assert.AreEqual(ChartFileKind.Bmson, result.PendingPackagesToAdd[0].ChartEntries.Single().Chart.Kind);
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_KeepsBmsonPackagePendingWhenResourcesAreMissing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsonMissingResource");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonFilePath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonFilePath, CreateBmsonJsonWithSound("missing.wav"));
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PackageChartEntry chartEntry = result.PendingPackagesToAdd[0].ChartEntries.Single();
            Assert.IsNull(chartEntry.GetBmsOwnerForTest());
            Assert.AreEqual(ChartFileKind.Bmson, chartEntry.Chart.Kind);
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            StringAssert.Contains(chartEntry.Chart.Warnings.Single(warning => warning.Kind == ChartWarningKind.ResourceWavMissing).Message, "WAV");
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ChecksInstalledChartsWithoutMaterializingUnmatchedBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsonInstalled");
            Directory.CreateDirectory(packageDirectoryPath);
            string installedBmsonPath = Path.Combine(packageDirectoryPath, "installed.bmson");
            string unmatchedBmsonPath = Path.Combine(packageDirectoryPath, "unmatched.bmson");
            File.WriteAllText(installedBmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(unmatchedBmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "audio");

            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                chart => string.Equals(chart?.Path, installedBmsonPath, StringComparison.OrdinalIgnoreCase),
                0.6);

            PackageChartEntry installedEntry = result.DiscoveredPackages
                .SelectMany(package => package.ChartEntries)
                .First(entry => string.Equals(entry.Chart.Path, installedBmsonPath, StringComparison.OrdinalIgnoreCase));
            PackageChartEntry unmatchedEntry = result.DiscoveredPackages
                .SelectMany(package => package.ChartEntries)
                .First(entry => string.Equals(entry.Chart.Path, unmatchedBmsonPath, StringComparison.OrdinalIgnoreCase));
            Assert.IsNull(installedEntry.GetBmsOwnerForTest());
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsNull(unmatchedEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void PackageChartEntry_ResourceSnapshotReadsBmsOwnerResourcesFromLightweightProjection()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                + "#TITLE Resource Owner\r\n"
                + "#WAV01 audio.wav\r\n"
                + "#BMP01 movie.mpg\r\n"
                + "#STAGEFILE stage.png\r\n"
                + "#00111:01\r\n"
                + "#00104:01\r\n");
            BMSFile bmsFile = BMSFile.CreateBMSFileFromFile(chartPath);
            ChartFile lightweightChart = ChartFileProjection.FromBmsFile(
                bmsFile,
                includeWarningSnapshot: false,
                includeResourceReferences: false);
            PackageChartEntry entry = PackageChartEntry.FromChart(lightweightChart);

            Assert.AreEqual(1, entry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(1, entry.ResourceSnapshot.MovieReferenceCount);
            Assert.AreEqual(1, entry.ResourceSnapshot.OptionalImageReferenceCount);
        });
    }

    [TestMethod]
    public void PackageChartEntry_ResourceSnapshotReadsBmsonOwnerResourcesFromLightweightProjection()
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"D:\BMS\pkg\chart.bmson",
            md5 = "11111111111111111111111111111111",
            title = "Resource Owner",
            wav_files = ["audio.wav"],
            bga_files = ["movie.mpg"],
            stagefile = "stage.png"
        };
        ChartFile lightweightChart = ChartFileProjection.FromBmsonSong(
            song,
            includeWarningSnapshot: false,
            includeResourceReferences: false);
        PackageChartEntry entry = PackageChartEntry.FromChart(lightweightChart);

        Assert.AreEqual(1, entry.ResourceSnapshot.AudioReferenceCount);
        Assert.AreEqual(1, entry.ResourceSnapshot.MovieReferenceCount);
        Assert.AreEqual(1, entry.ResourceSnapshot.OptionalImageReferenceCount);
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_KeepsChartPackagePendingWhenOnlySameStemChartFileExists()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsMissingSameStem");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsFilePath = Path.Combine(packageDirectoryPath, "chart.bme");
            File.WriteAllText(
                bmsFilePath,
                "#PLAYER 1\r\n"
                + "#TITLE Same Stem Missing\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA chart.wav\r\n"
                + "#BMP01 chart.mpg\r\n"
                + "#00111:AA\r\n"
                + "#00104:01\r\n");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PackageChartEntry chartEntry = result.PendingPackagesToAdd[0].ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bms);
            BMSFile chart = chartEntry.Chart.GetBmsStorageOwner();
            Assert.IsNotNull(chart);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.MovieReferenceCount);
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_DoesNotUseImageAsAudioOrMovieResource()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsImageOnlySameStem");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsFilePath = Path.Combine(packageDirectoryPath, "chart.bme");
            File.WriteAllText(
                bmsFilePath,
                "#PLAYER 1\r\n"
                + "#TITLE Image Only Same Stem\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA chart.wav\r\n"
                + "#BMP01 chart.mpg\r\n"
                + "#00111:AA\r\n"
                + "#00104:01\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.png"), "image");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PackageChartEntry chartEntry = result.PendingPackagesToAdd[0].ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bms);
            BMSFile chart = chartEntry.Chart.GetBmsStorageOwner();
            Assert.IsNotNull(chart);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.MovieReferenceCount);
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_UsesSameStemAudioAndMovieResourcesByCategory()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsCompleteSameStem");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsFilePath = Path.Combine(packageDirectoryPath, "chart.bme");
            File.WriteAllText(
                bmsFilePath,
                "#PLAYER 1\r\n"
                + "#TITLE Complete Same Stem\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA chart.wav\r\n"
                + "#BMP01 chart.mpg\r\n"
                + "#00111:AA\r\n"
                + "#00104:01\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.wav"), "audio");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.mpg"), "movie");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.AutoInstallCandidates.Count);
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);
            PackageChartEntry chartEntry = result.AutoInstallCandidates[0].ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bms);
            BMSFile chart = chartEntry.Chart.GetBmsStorageOwner();
            Assert.IsNotNull(chart);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.MovieReferenceCount);
            Assert.IsFalse(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(chartEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_PrioritizesNestedChartWarningBeforeResourceWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            string nestedDirectoryPath = Path.Combine(packageDirectoryPath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "root.bms"), "#PLAYER 1\r\n#TITLE Root\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "dummy");
            File.WriteAllText(Path.Combine(nestedDirectoryPath, "another.bms"), "#PLAYER 1\r\n#TITLE Nested\r\n#WAVAA missing.wav\r\n#00111:AA\r\n");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PackageChartEntry nestedEntry = result.PendingPackagesToAdd[0].ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals("another.bms", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual("[2] " + BeMusicSeeker.Properties.Resources.WarningDigest_NestedChart + ", " + BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing, ChartWarningTestHelpers.BuildDigestText(nestedEntry));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(nestedEntry), BeMusicSeeker.Properties.Resources.Warning_NestedChartFileInPackage);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(nestedEntry), "WAV");
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ExplicitBmsonFileWithAdjacentResourcesUsesDirectoryPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "BmsonWithResource");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonFilePath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonFilePath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "dummy");
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [bmsonFilePath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.IsTrue(string.Equals(packageDirectoryPath, result.DiscoveredPackages[0].path, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(1, result.AutoInstallCandidates.Count);
            Assert.IsTrue(string.Equals(packageDirectoryPath, result.AutoInstallCandidates[0].path, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);
            PackageChartEntry chartEntry = result.AutoInstallCandidates[0].ChartEntries.Single();
            Assert.IsNull(chartEntry.GetBmsOwnerForTest());
            Assert.AreEqual(ChartFileKind.Bmson, chartEntry.Chart.Kind);
            Assert.AreEqual(1, chartEntry.ResourceSnapshot.AudioReferenceCount);
            Assert.AreEqual(0, chartEntry.Chart.Warnings.Count);
        });
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_ClassifiesDetectedDirectoriesAsInstallable()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string directoryPackagePath = Path.Combine(tempDirectoryPath, "DirPkg");
            Directory.CreateDirectory(directoryPackagePath);
            File.WriteAllText(Path.Combine(directoryPackagePath, "chart_dir.bms"), "#PLAYER 1\r\n#TITLE Dir\r\n#WAVAA sound_dir.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(directoryPackagePath, "sound_dir.wav"), "audio");

            string filePackageDirectoryPath = Path.Combine(tempDirectoryPath, "SinglePkg");
            Directory.CreateDirectory(filePackageDirectoryPath);
            string singleFilePath = Path.Combine(filePackageDirectoryPath, "chart_single.bms");
            File.WriteAllText(singleFilePath, "#PLAYER 1\r\n#TITLE Single\r\n#WAVAA sound_single.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(filePackageDirectoryPath, "sound_single.wav"), "audio");

            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [directoryPackagePath, singleFilePath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(2, result.DiscoveredPackages.Count);
            Assert.AreEqual(2, result.AutoInstallCandidates.Count);
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.RegroupEligibleSourceDirectories.Count);
            Assert.IsTrue(Directory.Exists(result.AutoInstallCandidates[0].path));
            Assert.IsTrue(result.AutoInstallCandidates.Any(package => package.path.Equals(filePackageDirectoryPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(result.DiscoveredPackages.All(package => package.GetBmsOwnersForTest().Count > 0));
            Assert.IsTrue(result.DiscoveryMs >= 0);
            Assert.IsTrue(result.ClassificationMs >= 0);
            Assert.IsTrue(result.TotalMs >= 0);
        });
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_KeepsLaterDuplicateInstallablePackagePendingAfterSuccess()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string firstPackageDirectoryPath = Path.Combine(tempDirectoryPath, "FirstPkg");
            string secondPackageDirectoryPath = Path.Combine(tempDirectoryPath, "SecondPkg");
            Directory.CreateDirectory(firstPackageDirectoryPath);
            Directory.CreateDirectory(secondPackageDirectoryPath);
            string chartContent = "#PLAYER 1\r\n#TITLE Duplicate\r\n#WAVAA sound.wav\r\n#00111:AA\r\n";
            File.WriteAllText(Path.Combine(firstPackageDirectoryPath, "chart.bms"), chartContent);
            File.WriteAllText(Path.Combine(firstPackageDirectoryPath, "sound.wav"), "audio");
            File.WriteAllText(Path.Combine(secondPackageDirectoryPath, "chart.bms"), chartContent);
            File.WriteAllText(Path.Combine(secondPackageDirectoryPath, "sound.wav"), "audio");

            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [firstPackageDirectoryPath, secondPackageDirectoryPath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(2, result.DiscoveredPackages.Count);
            Assert.AreEqual(2, result.AutoInstallCandidates.Count);
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);

            AutoInstallApplyResult applyResult = service.ApplyAutoInstallWorkflow(
                result,
                keepInstallablePackagesPending: false,
                canAutoInstallImmediately: true,
                packages => []);

            Assert.AreEqual(1, applyResult.AutoInstalledPackages.Count);
            Assert.AreEqual(1, applyResult.PendingPackagesToAdd.Count);
            PackageChartEntry pendingEntry = applyResult.PendingPackagesToAdd[0].ChartEntries.Single();
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.AreEqual("[1] " + BeMusicSeeker.Properties.Resources.WarningDigest_AlreadyInstalled, ChartWarningTestHelpers.BuildDigestText(pendingEntry));
        });
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_DoesNotMarkDuplicatePendingWhenPredecessorFails()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile firstFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\First\\chart.bms");
        BMSFile secondFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Second\\chart.bms");
        ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstFile]);
        ChartPackage secondPackage = ChartPackageTestExtensions.CreatePackage([secondFile]);
        var workflow = new AutoInstallWorkflowResult();
        workflow.AutoInstallCandidates.Add(firstPackage);
        workflow.AutoInstallCandidates.Add(secondPackage);
        var service = new BmsLibraryPackageInstallService();

        AutoInstallApplyResult result = service.ApplyAutoInstallWorkflow(
            workflow,
            keepInstallablePackagesPending: false,
            canAutoInstallImmediately: true,
            packages => [firstPackage]);

        CollectionAssert.AreEqual(new[] { firstPackage, secondPackage }, result.PendingPackagesToAdd);
        Assert.AreEqual(0, result.AutoInstalledPackages.Count);
        Assert.IsFalse(secondPackage.ChartEntries.Single().Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_DoesNotWarnPartialDuplicateWhenDuplicatePredecessorFails()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile firstAFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\First\\a.bms");
        BMSFile mixedAFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Mixed\\a.bms");
        BMSFile mixedBFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Mixed\\b.bms");
        BMSFile thirdBFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Third\\b.bms");
        ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstAFile]);
        ChartPackage mixedPackage = ChartPackageTestExtensions.CreatePackage([mixedAFile, mixedBFile]);
        ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage([thirdBFile]);
        var workflow = new AutoInstallWorkflowResult();
        workflow.AutoInstallCandidates.Add(firstPackage);
        workflow.AutoInstallCandidates.Add(mixedPackage);
        workflow.AutoInstallCandidates.Add(thirdPackage);
        var service = new BmsLibraryPackageInstallService();

        AutoInstallApplyResult result = service.ApplyAutoInstallWorkflow(
            workflow,
            keepInstallablePackagesPending: false,
            canAutoInstallImmediately: true,
            packages => [firstPackage]);

        CollectionAssert.AreEqual(new[] { firstPackage, mixedPackage }, result.PendingPackagesToAdd);
        CollectionAssert.AreEqual(new[] { thirdPackage }, result.AutoInstalledPackages);
        Assert.IsFalse(mixedPackage.ChartEntries.Any(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled)));
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_WarnsOnlyDuplicateReasonEntriesForPartialDuplicate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BMSFile firstAFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\First\\a.bms");
        BMSFile mixedAFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Mixed\\a.bms");
        BMSFile mixedBFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Mixed\\b.bms");
        BMSFile thirdBFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Third\\b.bms");
        ChartPackage firstPackage = ChartPackageTestExtensions.CreatePackage([firstAFile]);
        ChartPackage mixedPackage = ChartPackageTestExtensions.CreatePackage([mixedAFile, mixedBFile]);
        ChartPackage thirdPackage = ChartPackageTestExtensions.CreatePackage([thirdBFile]);
        var workflow = new AutoInstallWorkflowResult();
        workflow.AutoInstallCandidates.Add(firstPackage);
        workflow.AutoInstallCandidates.Add(mixedPackage);
        workflow.AutoInstallCandidates.Add(thirdPackage);
        var service = new BmsLibraryPackageInstallService();

        AutoInstallApplyResult result = service.ApplyAutoInstallWorkflow(
            workflow,
            keepInstallablePackagesPending: false,
            canAutoInstallImmediately: true,
            packages => []);

        CollectionAssert.AreEqual(new[] { mixedPackage }, result.PendingPackagesToAdd);
        CollectionAssert.AreEqual(new[] { firstPackage, thirdPackage }, result.AutoInstalledPackages);
        PackageChartEntry mixedAEntry = mixedPackage.ChartEntries.Single(entry => entry.Chart.Path.EndsWith("\\a.bms", StringComparison.OrdinalIgnoreCase));
        PackageChartEntry mixedBEntry = mixedPackage.ChartEntries.Single(entry => entry.Chart.Path.EndsWith("\\b.bms", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(mixedAEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
        Assert.IsFalse(mixedBEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
    }

    [TestMethod]
    [DoNotParallelize]
    public void PrepareAutoInstallWorkflow_DoesNotPrebuildSourceSurfaceForDiscoveredPackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
            {
                string directoryPackagePath = Path.Combine(tempDirectoryPath, "DirPkg");
                Directory.CreateDirectory(directoryPackagePath);
                File.WriteAllText(Path.Combine(directoryPackagePath, "chart_dir.bms"), "#PLAYER 1\r\n#TITLE Dir\r\n#WAVAA sound_dir.wav\r\n#00111:AA\r\n");
                File.WriteAllText(Path.Combine(directoryPackagePath, "sound_dir.wav"), "audio");

                string filePackageDirectoryPath = Path.Combine(tempDirectoryPath, "SinglePkg");
                Directory.CreateDirectory(filePackageDirectoryPath);
                string singleFilePath = Path.Combine(filePackageDirectoryPath, "chart_single.bms");
                File.WriteAllText(singleFilePath, "#PLAYER 1\r\n#TITLE Single\r\n#WAVAA sound_single.wav\r\n#00111:AA\r\n");
                File.WriteAllText(Path.Combine(filePackageDirectoryPath, "sound_single.wav"), "audio");

                var service = new BmsLibraryPackageInstallService();
                AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                    [directoryPackagePath, singleFilePath],
                    [],
                    [],
                    _ => false,
                    0.6);

                Assert.AreEqual(2, result.DiscoveredPackages.Count);
                foreach (ChartPackage package in result.DiscoveredPackages)
                {
                    List<BMSFile> discoveredCharts = [.. package.GetBmsOwnersForTest()];
                    PackageInstallEstimationSnapshot firstSnapshot = BuildPackageSnapshot(package, discoveredCharts);
                    PackageInstallEstimationSnapshot secondSnapshot = BuildPackageSnapshot(package, discoveredCharts);

                    Assert.IsTrue(discoveredCharts.Count > 0);
                    Assert.IsFalse(firstSnapshot.SourceSurfaceCacheHit);
                    Assert.IsTrue(secondSnapshot.SourceSurfaceCacheHit);
                    Assert.AreEqual("bounded_fast_source_surface", firstSnapshot.SourceSurfaceScanBackend);
                }
            });
    }

    [TestMethod]
    public void PackageChartEntry_FromBmsChartProjection_ProjectsBmsMode()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile source = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\chart.bms");
        source.SetMode(7);

        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(source));

        Assert.AreEqual(7, entry.Chart.Mode);
    }

    [TestMethod]
    public void PrepareAutoInstallWorkflow_DoesNotMarkRegroupEligibleSourceDirectoryForPartialFileSelection()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "Partial");
            Directory.CreateDirectory(sourceDirectoryPath);
            string firstFilePath = Path.Combine(sourceDirectoryPath, "chart_a.bms");
            string secondFilePath = Path.Combine(sourceDirectoryPath, "chart_b.bms");
            File.WriteAllText(firstFilePath, "#PLAYER 1\r\n#TITLE A\r\n");
            File.WriteAllText(secondFilePath, "#PLAYER 1\r\n#TITLE B\r\n");
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "chart_c.bms"), "#PLAYER 1\r\n#TITLE C\r\n");

            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [firstFilePath, secondFilePath],
                [],
                [],
                _ => false,
                0.6);

            Assert.AreEqual(2, result.DiscoveredPackages.Count);
            Assert.AreEqual(0, result.RegroupEligibleSourceDirectories.Count);
            Assert.IsTrue(result.DiscoveredPackages.All(package => File.Exists(package.path)));
        });
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_ReturnsPendingAddsRemovesAndEstimateTargets()
    {
        var service = new BmsLibraryPackageInstallService();
        var removePackage = new ChartPackage { path = "C:\\Pending\\Remove" };
        var pendingPackage = new ChartPackage { path = "C:\\Pending\\Keep" };
        var successAutoInstallPackage = new ChartPackage { path = "C:\\Pending\\AutoOk" };
        var failedAutoInstallPackage = new ChartPackage { path = "C:\\Pending\\AutoNg" };
        var workflow = new AutoInstallWorkflowResult();
        workflow.PendingPackagesToRemove.Add(removePackage);
        workflow.PendingPackagesToAdd.Add(pendingPackage);
        workflow.AutoInstallCandidates.Add(successAutoInstallPackage);
        workflow.AutoInstallCandidates.Add(failedAutoInstallPackage);

        AutoInstallApplyResult result = service.ApplyAutoInstallWorkflow(
            workflow,
            keepInstallablePackagesPending: false,
            canAutoInstallImmediately: true,
            packages => [failedAutoInstallPackage]);

        CollectionAssert.AreEqual(new[] { removePackage }, result.PendingPackagesToRemove);
        CollectionAssert.AreEqual(new[] { pendingPackage, failedAutoInstallPackage }, result.PendingPackagesToAdd);
        CollectionAssert.AreEqual(new[] { successAutoInstallPackage }, result.AutoInstalledPackages);
        CollectionAssert.AreEqual(new[] { failedAutoInstallPackage }, result.AutoInstallFailures);
        CollectionAssert.AreEqual(new[] { pendingPackage, failedAutoInstallPackage }, result.EstimateTargets);
        CollectionAssert.AreEquivalent(new[] { removePackage.path }, result.InstallRowsToDelete);
        CollectionAssert.AreEquivalent(new[] { pendingPackage.path, failedAutoInstallPackage.path }, result.InstallRowsToUpsert.Select(pkg => pkg.path).ToArray());
        Assert.IsTrue(result.InstallMs >= 0);
        Assert.IsTrue(result.ApplyMs >= 0);
        Assert.IsTrue(result.TotalMs >= 0);
    }

    [TestMethod]
    public void InstallPackages_ReturnsRegisteredPackagesAndTimingBreakdown()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Installed\\chart.bms");
        string installWarning = string.Format(Resources.Warning_InstallEstimationAmbiguous, "C:\\Installed\\A", "C:\\Installed\\B");
        var package = ChartPackageTestExtensions.CreatePackage([file]);
        PackageChartEntry entry = package.ChartEntries.Single();
        entry.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, installWarning);
        entry.SetWarning(ChartWarningKind.ResourceWavMissing, "pending resource warning");
        entry.RestoreInstallDestinationState(new PackageChartInstallDestinationState("C:\\Installed\\A", "Pending Destination", "Pending Artist", ["C:\\Installed\\A", "C:\\Installed\\B"]));
        package.path = "C:\\Pending\\Pkg1";
        package.delete_parent = false;
        List<BMSFile> songUpserts = [];
        List<BMSFile> maintenanceTargets = [];
        List<BMSFile> scoreTargets = [];
        List<BMSFile> applyTargets = [];
        List<ChartFile> callbackCharts = [];

        PackageInstallExecutionResult result = service.InstallPackages(
            [package],
            "C:\\Installed",
            (_, _, _, _, _) => true,
            result =>
            {
                callbackCharts.AddRange(result.AddedCharts);
                songUpserts.AddRange(GetAddedBmsFiles(result));
            },
            result =>
            {
                callbackCharts.AddRange(result.AddedCharts);
                maintenanceTargets.AddRange(GetAddedBmsFiles(result));
            },
            result =>
            {
                callbackCharts.AddRange(result.AddedCharts);
                scoreTargets.AddRange(GetAddedBmsFiles(result));
            },
            result =>
            {
                callbackCharts.AddRange(result.AddedCharts);
                applyTargets.AddRange(GetAddedBmsFiles(result));
            });

        Assert.AreEqual(1, result.AddedCharts.Count);
        Assert.AreEqual(1, GetAddedBmsFiles(result).Count);
        Assert.AreEqual(1, result.InstalledPackagesToRegister.Count);
        Assert.AreEqual(0, result.FailedPackages.Count);
        CollectionAssert.AreEqual(new[] { file }, songUpserts);
        CollectionAssert.AreEqual(new[] { file }, maintenanceTargets);
        CollectionAssert.AreEqual(new[] { file }, scoreTargets);
        CollectionAssert.AreEqual(new[] { file }, applyTargets);
        Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(entry));
        Assert.AreEqual(string.Empty, entry.Chart.InstallDestination);
        Assert.AreEqual(string.Empty, entry.Chart.InstallDestinationTitle);
        Assert.AreEqual(string.Empty, entry.Chart.InstallDestinationArtist);
        Assert.AreEqual(0, entry.Chart.InstallDestinationSuggestions.Count);
        Assert.AreEqual(string.Empty, result.AddedCharts[0].InstallDestination);
        Assert.AreEqual(string.Empty, result.AddedCharts[0].InstallDestinationTitle);
        Assert.AreEqual(string.Empty, result.AddedCharts[0].InstallDestinationArtist);
        Assert.AreEqual(4, callbackCharts.Count);
        Assert.IsTrue(callbackCharts.All(chart => string.IsNullOrWhiteSpace(chart.InstallDestination)));
        Assert.IsTrue(callbackCharts.All(chart => string.IsNullOrWhiteSpace(chart.InstallDestinationTitle)));
        Assert.IsTrue(callbackCharts.All(chart => string.IsNullOrWhiteSpace(chart.InstallDestinationArtist)));
        Assert.IsTrue(callbackCharts.All(chart => chart.InstallDestinationSuggestions.Count == 0));
        Assert.IsFalse(ChartWarningTestHelpers.BuildTooltipText(entry).Contains(Resources.Warning_InstallEstimationAmbiguousPrefix));
        Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(entry));
        Assert.IsTrue(result.MoveMs >= 0);
        Assert.IsTrue(result.SongDbMs >= 0);
        Assert.IsTrue(result.MaintenanceMs >= 0);
        Assert.IsTrue(result.ScoreMs >= 0);
        Assert.IsTrue(result.ApplyMs >= 0);
        Assert.IsTrue(result.TotalMs >= 0);
    }

    [TestMethod]
    public void InstallPackages_PassesResultToScoreCallback()
    {
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Installed\\chart.bms");
        var package = ChartPackageTestExtensions.CreatePackage([file]);
        package.path = "C:\\Pending\\Pkg1";
        package.delete_parent = false;
        PackageInstallExecutionResult scoreResult = null!;

        PackageInstallExecutionResult result = service.InstallPackages(
            [package],
            "C:\\Installed",
            (_, _, _, _, _) => true,
            result => { },
            result => { },
            result => scoreResult = result,
            result => { });

        Assert.AreEqual(1, result.AddedCharts.Count);
        Assert.AreEqual(1, GetAddedBmsFiles(result).Count);
        Assert.AreSame(result, scoreResult);
    }

    [TestMethod]
    public void InstallPackages_RegistersNestedChartsWhenPackageContainsThem()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile rootFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\root.bms");
        TestableBmsFile nestedFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\sub\\another.bms");
        var package = ChartPackageTestExtensions.CreatePackage([rootFile, nestedFile]);
        package.path = "C:\\Pending\\Pkg1";
        package.delete_parent = false;
        List<BMSFile> songUpserts = [];
        List<BMSFile> maintenanceTargets = [];
        List<BMSFile> applyTargets = [];

        PackageInstallExecutionResult result = service.InstallPackages(
            [package],
            "C:\\Installed",
            (_, _, _, _, _) => true,
            result => songUpserts.AddRange(GetAddedBmsFiles(result)),
            result => maintenanceTargets.AddRange(GetAddedBmsFiles(result)),
            _ => { },
            result => applyTargets.AddRange(GetAddedBmsFiles(result)));

        Assert.AreEqual(2, result.AddedCharts.Count);
        Assert.AreEqual(2, GetAddedBmsFiles(result).Count);
        CollectionAssert.AreEquivalent(new BMSFile[] { rootFile, nestedFile }, songUpserts);
        CollectionAssert.AreEquivalent(new BMSFile[] { rootFile, nestedFile }, maintenanceTargets);
        CollectionAssert.AreEquivalent(new BMSFile[] { rootFile, nestedFile }, applyTargets);
    }

    [TestMethod]
    public void InstallPackages_RegistersOnlyMovedTargetEntriesWithoutMaterializingOutsideBmson()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledPkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Move\r\n");

            TestableBmsFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            PackageChartEntry outsideBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "OtherPkg", "outside.bmson"),
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                sha256 = new string('b', 64)
            }));
            ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(chart)), outsideBmsonEntry]);
            package.path = sourceDirectoryPath;

            var service = new BmsLibraryPackageInstallService();
            PackageInstallExecutionResult result = service.InstallPackages(
                [package],
                destinationDirectoryPath,
                (movePackage, destination, deleteSourceContentsAfterSuccessfulInstall, existingHashes, excludedComponentPaths) =>
                    service.MovePackageFiles(
                        movePackage,
                        destination,
                        new BmsLibraryOptionsSnapshot
                        {
                            EnableSmartComponentOverwrite = false,
                            KeepSmartOverwriteProtectedFilesByRenaming = false
                        },
                        (_, _, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
                        ex => ex.Message,
                        new RealFileMutationService(),
                        null,
                        null,
                        null,
                        _ => { },
                        showMessageBoxOnInstallFail: false,
                        deleteAllContents: deleteSourceContentsAfterSuccessfulInstall,
                        existingHashes: existingHashes,
                        excludedComponentPaths: excludedComponentPaths),
                _ => { },
                _ => { },
                _ => { },
                _ => { });

            Assert.AreEqual(1, result.AddedCharts.Count);
            Assert.AreEqual(1, GetAddedBmsFiles(result).Count);
            Assert.AreSame(chart, GetAddedBmsFiles(result)[0]);
            Assert.AreEqual(destinationDirectoryPath, package.path);
            Assert.AreEqual(Path.Combine(destinationDirectoryPath, "chart.bms"), chart.path);
            Assert.AreEqual(1, package.ChartEntries.Count);
            Assert.AreSame(chart, package.ChartEntries[0].GetBmsOwnerForTest());
            Assert.IsNull(outsideBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void ExecuteInstalledOnlyResourceOverwrite_CategorizesCleanupInstallAndMissingCases()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        var cleanupPackage = ChartPackageTestExtensions.CreatePackage([CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Cleanup\\a.bms")]);
        cleanupPackage.path = "C:\\Pending\\Cleanup";
        cleanupPackage.delete_parent = false;
        var installPackage = ChartPackageTestExtensions.CreatePackage([CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Install\\b.bms")]);
        installPackage.path = "C:\\Pending\\Install";
        installPackage.delete_parent = false;
        var missingDestinationPackage = ChartPackageTestExtensions.CreatePackage([CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Missing\\c.bms")]);
        missingDestinationPackage.path = "C:\\Pending\\Missing";
        missingDestinationPackage.delete_parent = false;

        PendingResourceOverwriteExecutionResult result = service.ExecuteInstalledOnlyResourceOverwrite(
            [cleanupPackage, installPackage, missingDestinationPackage, new ChartPackage { path = "C:\\Pending\\Unknown" }],
            [cleanupPackage, installPackage, missingDestinationPackage],
            true,
            (package) => package.path switch
            {
                "C:\\Pending\\Cleanup" => new InstalledOnlyPackageResolutionResult { DestinationDirectory = "C:\\Installed\\Cleanup", Reason = InstalledDirectoryResolveReason.None },
                "C:\\Pending\\Install" => new InstalledOnlyPackageResolutionResult { DestinationDirectory = "C:\\Installed\\Install", Reason = InstalledDirectoryResolveReason.None },
                _ => new InstalledOnlyPackageResolutionResult { Reason = InstalledDirectoryResolveReason.MissingInstallDestination }
            },
            (_, package) => "log:" + package.path,
            (package, _) => package.path == "C:\\Pending\\Install",
            (_, _) => true,
            (package) => package.path == "C:\\Pending\\Cleanup" ? (true, CleanupSourceKind.MissingSource) : (false, CleanupSourceKind.MissingSource),
            (package) => package == missingDestinationPackage,
            default,
            null,
            _ => { });

        Assert.AreEqual(4, result.Requested);
        Assert.AreEqual(4, result.Processed);
        Assert.AreEqual(1, result.SucceededCleanupOnly);
        Assert.AreEqual(1, result.SucceededInstall);
        Assert.AreEqual(1, result.SkippedMissingInstlDst);
        Assert.AreEqual(1, result.SkippedNotPending);
        Assert.AreEqual(0, result.Failed);
        CollectionAssert.AreEqual(new[] { cleanupPackage }, result.PendingPackagesToRemove);
        CollectionAssert.AreEqual(new[] { cleanupPackage.path }, result.InstallRowsToDelete);
    }

    [TestMethod]
    public void ExecuteInstalledOnlyResourceOverwrite_DoesNotMaterializeAdapterlessBmsonDestinationState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Install\\chart.bmson",
            folder = "C:\\Pending\\Install",
            title = "Adapterless",
            artist = "Artist",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('d', 64)
        };
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
        entry.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "keep warning");
        ChartPackage installPackage = ChartPackage.FromChartEntries([entry]);
        installPackage.path = "C:\\Pending\\Install";

        bool checkedInstallDestination = false;
        PendingResourceOverwriteExecutionResult result = service.ExecuteInstalledOnlyResourceOverwrite(
            [installPackage],
            [installPackage],
            false,
            _ => new InstalledOnlyPackageResolutionResult { DestinationDirectory = "C:\\Installed\\Install", Reason = InstalledDirectoryResolveReason.None },
            (_, package) => "log:" + package.path,
            (_, _) => true,
            delegate
            {
                checkedInstallDestination = string.Equals(entry.Chart.InstallDestination, "C:\\Installed\\Install", StringComparison.OrdinalIgnoreCase);
                return true;
            },
            _ => (false, CleanupSourceKind.MissingSource),
            _ => false,
            default,
            null,
            _ => { });

        Assert.AreEqual(1, result.SucceededInstall);
        Assert.IsTrue(checkedInstallDestination);
        Assert.IsNull(entry.GetBmsOwnerForTest());
        Assert.AreEqual("C:\\Installed\\Install", entry.Chart.InstallDestination);
        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    [TestMethod]
    public void ExecuteInstalledOnlyResourceOverwrite_RestoresAdapterlessBmsonDestinationStateWhenStillPending()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\Install\\chart.bmson",
            folder = "C:\\Pending\\Install",
            title = "Adapterless",
            artist = "Artist",
            md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            sha256 = new string('e', 64)
        };
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
        entry.ApplyInstallDestination("C:\\Original", "Original", "Artist");
        entry.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, "keep warning");
        ChartPackage installPackage = ChartPackage.FromChartEntries([entry]);
        installPackage.path = "C:\\Pending\\Install";

        PendingResourceOverwriteExecutionResult result = service.ExecuteInstalledOnlyResourceOverwrite(
            [installPackage],
            [installPackage],
            false,
            _ => new InstalledOnlyPackageResolutionResult { DestinationDirectory = "C:\\Installed\\Install", Reason = InstalledDirectoryResolveReason.None },
            (_, package) => "log:" + package.path,
            (_, _) => true,
            (_, _) => false,
            _ => (false, CleanupSourceKind.MissingSource),
            _ => true,
            default,
            null,
            _ => { });

        Assert.AreEqual(1, result.Failed);
        Assert.IsNull(entry.GetBmsOwnerForTest());
        Assert.AreEqual("C:\\Original", entry.Chart.InstallDestination);
        Assert.AreEqual("Original", entry.Chart.InstallDestinationTitle);
        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
    }

    [TestMethod]
    public void MovePackageFiles_MovesDirectoryPackageAndUpdatesChartPaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string nestedDirectoryPath = Path.Combine(sourceDirectoryPath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string nestedChartPath = Path.Combine(nestedDirectoryPath, "another.bms");
            string resourcePath = Path.Combine(sourceDirectoryPath, "readme.txt");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Move\r\n");
            File.WriteAllText(nestedChartPath, "#PLAYER 1\r\n#TITLE Nested\r\n");
            File.WriteAllText(resourcePath, "resource");

            var service = new BmsLibraryPackageInstallService();
            TestableBmsFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            TestableBmsFile nestedChart = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", nestedChartPath);
            var package = ChartPackageTestExtensions.CreatePackage([chart, nestedChart]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;

            bool moved = service.MovePackageFiles(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
                ex => ex.Message,
                new RealFileMutationService(),
                null,
                null,
                null,
                _ => { },
                showMessageBoxOnInstallFail: false);

            Assert.IsTrue(moved);
            Assert.AreEqual(destinationDirectoryPath, package.path);
            Assert.AreEqual(Path.Combine(destinationDirectoryPath, "chart.bms"), package.GetBmsOwnersForTest()[0].path);
            Assert.AreEqual(Path.Combine(destinationDirectoryPath, "sub", "another.bms"), package.GetBmsOwnersForTest()[1].path);
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "chart.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "sub", "another.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "readme.txt")));
            Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
        });
    }

    [TestMethod]
    public void MovePackageFiles_AutoNamingUsesInstallTargetEntriesWithoutMaterializingOutsideBmson()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Move\r\n");

            TestableBmsFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            PackageChartEntry outsideBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "OtherPkg", "outside.bmson"),
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                sha256 = new string('b', 64)
            }));
            ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(chart)), outsideBmsonEntry]);
            package.path = sourceDirectoryPath;

            var service = new BmsLibraryPackageInstallService();
            bool moved = service.MovePackageFiles(
                package,
                string.Empty,
                new BmsLibraryOptionsSnapshot
                {
                    BMSInstallDir = tempDirectoryPath,
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (files, _, _) =>
                {
                    List<ChartFile> receivedFiles = [.. files];
                    Assert.AreEqual(1, receivedFiles.Count);
                    Assert.AreSame(chart, receivedFiles[0].GetBmsStorageOwner());
                    return Path.Combine(tempDirectoryPath, "InstalledAuto");
                },
                ex => ex.Message,
                new RealFileMutationService(),
                null,
                null,
                null,
                _ => { },
                showMessageBoxOnInstallFail: false);

            Assert.IsTrue(moved);
            Assert.AreEqual(Path.Combine(tempDirectoryPath, "InstalledAuto"), package.path);
            Assert.AreEqual(Path.Combine(tempDirectoryPath, "InstalledAuto", "chart.bms"), chart.path);
            Assert.IsNull(outsideBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void InstallPackages_ReportsAdapterlessBmsonRowsWithoutMaterializingCompatibilityAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledPkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string sourceBmsonPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            string destinationBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            File.WriteAllText(sourceBmsonPath, CreateBmsonJsonWithSound("new.wav"));

            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = sourceBmsonPath,
                folder = sourceDirectoryPath,
                title = "Adapterless",
                md5 = "dddddddddddddddddddddddddddddddd",
                sha256 = new string('d', 64)
            };
            PackageChartEntry bmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            bmsonEntry.ApplyInstallDestination(destinationDirectoryPath, "Pending Bmson", "Pending Artist");
            ChartPackage package = ChartPackage.FromChartEntries([bmsonEntry]);
            package.path = sourceDirectoryPath;
            List<LR2SongDBExtended.bmson_song> storageRows = [];
            List<LR2SongDBExtended.bmson_song> maintenanceTargets = [];
            List<LR2SongDBExtended.bmson_song> applyTargets = [];

            var service = new BmsLibraryPackageInstallService();
            PackageInstallExecutionResult result = service.InstallPackages(
                [package],
                destinationDirectoryPath,
                (movePackage, destination, deleteSourceContentsAfterSuccessfulInstall, existingHashes, excludedComponentPaths) =>
                    service.MovePackageFiles(
                        movePackage,
                        destination,
                        new BmsLibraryOptionsSnapshot
                        {
                            EnableSmartComponentOverwrite = false,
                            KeepSmartOverwriteProtectedFilesByRenaming = false
                        },
                        (_, _, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
                        ex => ex.Message,
                        new RealFileMutationService(),
                        null,
                        null,
                        null,
                        _ => { },
                        showMessageBoxOnInstallFail: false,
                        deleteAllContents: deleteSourceContentsAfterSuccessfulInstall,
                        existingHashes: existingHashes,
                        excludedComponentPaths: excludedComponentPaths),
                result => storageRows.AddRange(GetAddedBmsonSongs(result)),
                result => maintenanceTargets.AddRange(GetAddedBmsonSongs(result)),
                _ => { },
                result => applyTargets.AddRange(GetAddedBmsonSongs(result)));

            Assert.AreEqual(1, result.AddedEntries.Count);
            Assert.AreEqual(1, result.AddedCharts.Count);
            Assert.AreEqual(0, GetAddedBmsFiles(result).Count);
            Assert.AreEqual(1, GetAddedBmsonSongs(result).Count);
            Assert.AreSame(bmsonSong, GetAddedBmsonSongs(result)[0]);
            CollectionAssert.AreEqual(new[] { bmsonSong }, storageRows);
            CollectionAssert.AreEqual(new[] { bmsonSong }, maintenanceTargets);
            CollectionAssert.AreEqual(new[] { bmsonSong }, applyTargets);
            Assert.AreEqual(destinationBmsonPath, bmsonSong.path);
            Assert.AreEqual(string.Empty, result.AddedEntries[0].Chart.InstallDestination);
            Assert.AreEqual(string.Empty, result.AddedEntries[0].Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, result.AddedEntries[0].Chart.InstallDestinationArtist);
            Assert.AreEqual(string.Empty, result.AddedCharts[0].InstallDestination);
            Assert.AreEqual(string.Empty, result.AddedCharts[0].InstallDestinationTitle);
            Assert.AreEqual(string.Empty, result.AddedCharts[0].InstallDestinationArtist);
            Assert.IsNull(bmsonEntry.GetBmsOwnerForTest());
            Assert.IsNull(result.AddedEntries[0].GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void InstallPackages_DropsAdapterBackedBmsonAdapterAfterInstalledPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "InstalledPkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string sourceBmsonPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            string destinationBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            File.WriteAllText(sourceBmsonPath, CreateBmsonJsonWithSound("new.wav"));

            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(sourceBmsonPath);
            ChartPackage package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong))]);
            package.path = sourceDirectoryPath;
            List<BMSFile> songUpserts = [];
            List<BMSFile> maintenanceTargets = [];
            List<BMSFile> scoreTargets = [];
            List<BMSFile> applyTargets = [];

            var service = new BmsLibraryPackageInstallService();
            PackageInstallExecutionResult result = service.InstallPackages(
                [package],
                destinationDirectoryPath,
                (movePackage, destination, deleteSourceContentsAfterSuccessfulInstall, existingHashes, excludedComponentPaths) =>
                    service.MovePackageFiles(
                        movePackage,
                        destination,
                        new BmsLibraryOptionsSnapshot
                        {
                            EnableSmartComponentOverwrite = false,
                            KeepSmartOverwriteProtectedFilesByRenaming = false
                        },
                        (_, _, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
                        ex => ex.Message,
                        new RealFileMutationService(),
                        null,
                        null,
                        null,
                        _ => { },
                        showMessageBoxOnInstallFail: false,
                        deleteAllContents: deleteSourceContentsAfterSuccessfulInstall,
                        existingHashes: existingHashes,
                        excludedComponentPaths: excludedComponentPaths),
                result => songUpserts.AddRange(GetAddedBmsFiles(result)),
                result => maintenanceTargets.AddRange(GetAddedBmsFiles(result)),
                result => scoreTargets.AddRange(GetAddedBmsFiles(result)),
                result => applyTargets.AddRange(GetAddedBmsFiles(result)));

            Assert.AreEqual(1, result.AddedEntries.Count);
            Assert.AreEqual(1, result.AddedCharts.Count);
            Assert.AreEqual(0, GetAddedBmsFiles(result).Count);
            Assert.AreEqual(1, GetAddedBmsonSongs(result).Count);
            Assert.AreSame(bmsonSong, GetAddedBmsonSongs(result)[0]);
            Assert.AreEqual(0, songUpserts.Count);
            Assert.AreEqual(0, maintenanceTargets.Count);
            Assert.AreEqual(0, scoreTargets.Count);
            Assert.AreEqual(0, applyTargets.Count);
            Assert.AreEqual(destinationBmsonPath, bmsonSong.path);
            Assert.AreEqual(destinationDirectoryPath, bmsonSong.folder);
            Assert.AreEqual(destinationBmsonPath, result.AddedEntries[0].Chart.Path);
            Assert.IsNull(result.AddedEntries[0].GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void MovePackageFiles_MovesAdapterlessBmsonEntryWithoutMaterializingCompatibilityAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            string sourceBmsonPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            string destinationBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            File.WriteAllText(sourceBmsonPath, CreateBmsonJsonWithSound("new.wav"));

            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = sourceBmsonPath,
                folder = sourceDirectoryPath,
                title = "Adapterless",
                md5 = "cccccccccccccccccccccccccccccccc",
                sha256 = new string('c', 64)
            };
            PackageChartEntry bmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            ChartPackage package = ChartPackage.FromChartEntries([bmsonEntry]);
            package.path = sourceDirectoryPath;

            var service = new BmsLibraryPackageInstallService();
            bool moved = service.MovePackageFiles(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
                ex => ex.Message,
                new RealFileMutationService(),
                null,
                null,
                null,
                _ => { },
                showMessageBoxOnInstallFail: false);

            Assert.IsTrue(moved);
            Assert.IsTrue(File.Exists(destinationBmsonPath));
            Assert.AreEqual(destinationDirectoryPath, package.path);
            Assert.AreEqual(destinationBmsonPath, bmsonSong.path);
            Assert.AreEqual(destinationDirectoryPath, bmsonSong.folder);
            Assert.IsNull(bmsonEntry.GetBmsOwnerForTest());
            Assert.IsNull(package.ChartEntries.Single().GetBmsOwnerForTest());
            Assert.AreEqual(destinationBmsonPath, package.ChartEntries.Single().Chart.Path);
            Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
        });
    }

    [TestMethod]
    public void MovePackageFiles_SmartOverwriteTreatsTopLevelBmsonAsChart()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            string sourceBmsonPath = Path.Combine(sourceDirectoryPath, "chart.bmson");
            string destinationBmsonPath = Path.Combine(destinationDirectoryPath, "chart.bmson");
            string renamedBmsonPath = Path.Combine(destinationDirectoryPath, "chart_.bmson");
            File.WriteAllText(sourceBmsonPath, CreateBmsonJsonWithSound("new.wav"));
            File.WriteAllText(destinationBmsonPath, CreateBmsonJsonWithSound("old.wav"));

            var service = new BmsLibraryPackageInstallService();
            PackageChartEntry bmsonEntry = PackageChartEntry.FromPath(sourceBmsonPath);
            var package = ChartPackage.FromChartEntries([bmsonEntry]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;

            bool moved = service.MovePackageFiles(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = true,
                    KeepSmartOverwriteProtectedFilesByRenaming = true
                },
                (_, _, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
                ex => ex.Message,
                new RealFileMutationService(),
                null,
                null,
                null,
                _ => { },
                showMessageBoxOnInstallFail: false);

            Assert.IsTrue(moved);
            Assert.IsTrue(File.Exists(destinationBmsonPath));
            Assert.IsTrue(File.Exists(renamedBmsonPath));
            Assert.AreEqual(destinationDirectoryPath, package.path);
            Assert.AreEqual(renamedBmsonPath, package.ChartEntries.Single().Chart.Path);
            Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
        });
    }

    [TestMethod]
    public void MovePackageFiles_NormalOverwriteTreatsNestedBmsonAsChart()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string nestedSourceDirectoryPath = Path.Combine(sourceDirectoryPath, "sub");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            string nestedDestinationDirectoryPath = Path.Combine(destinationDirectoryPath, "sub");
            Directory.CreateDirectory(nestedSourceDirectoryPath);
            Directory.CreateDirectory(nestedDestinationDirectoryPath);
            string sourceBmsonPath = Path.Combine(nestedSourceDirectoryPath, "chart.bmson");
            string destinationBmsonPath = Path.Combine(nestedDestinationDirectoryPath, "chart.bmson");
            string renamedBmsonPath = Path.Combine(nestedDestinationDirectoryPath, "chart_.bmson");
            File.WriteAllText(sourceBmsonPath, CreateBmsonJsonWithSound("new.wav"));
            File.WriteAllText(destinationBmsonPath, CreateBmsonJsonWithSound("old.wav"));

            var service = new BmsLibraryPackageInstallService();
            PackageChartEntry bmsonEntry = PackageChartEntry.FromPath(sourceBmsonPath);
            var package = ChartPackage.FromChartEntries([bmsonEntry]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;

            bool moved = service.MovePackageFiles(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = false,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
                ex => ex.Message,
                new RealFileMutationService(),
                null,
                null,
                null,
                _ => { },
                showMessageBoxOnInstallFail: false);

            Assert.IsTrue(moved);
            Assert.IsTrue(File.Exists(destinationBmsonPath));
            Assert.IsTrue(File.Exists(renamedBmsonPath));
            Assert.AreEqual(destinationDirectoryPath, package.path);
            Assert.AreEqual(renamedBmsonPath, package.ChartEntries.Single().Chart.Path);
            Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
        });
    }

    [TestMethod]
    public void MovePackageFiles_SmartOverwriteTreatsNestedBmsonAsChartWithoutProtectedRename()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            string nestedSourceDirectoryPath = Path.Combine(sourceDirectoryPath, "sub");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Pkg");
            string nestedDestinationDirectoryPath = Path.Combine(destinationDirectoryPath, "sub");
            Directory.CreateDirectory(nestedSourceDirectoryPath);
            Directory.CreateDirectory(nestedDestinationDirectoryPath);
            string sourceBmsonPath = Path.Combine(nestedSourceDirectoryPath, "chart.bmson");
            string destinationBmsonPath = Path.Combine(nestedDestinationDirectoryPath, "chart.bmson");
            string renamedBmsonPath = Path.Combine(nestedDestinationDirectoryPath, "chart_.bmson");
            File.WriteAllText(sourceBmsonPath, CreateBmsonJsonWithSound("new.wav"));
            File.WriteAllText(destinationBmsonPath, CreateBmsonJsonWithSound("old.wav"));

            var service = new BmsLibraryPackageInstallService();
            PackageChartEntry bmsonEntry = PackageChartEntry.FromPath(sourceBmsonPath);
            var package = ChartPackage.FromChartEntries([bmsonEntry]);
            package.path = sourceDirectoryPath;
            package.delete_parent = false;

            bool moved = service.MovePackageFiles(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = true,
                    KeepSmartOverwriteProtectedFilesByRenaming = false
                },
                (_, _, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
                ex => ex.Message,
                new RealFileMutationService(),
                null,
                null,
                null,
                _ => { },
                showMessageBoxOnInstallFail: false);

            Assert.IsTrue(moved);
            Assert.IsTrue(File.Exists(destinationBmsonPath));
            Assert.IsTrue(File.Exists(renamedBmsonPath));
            Assert.AreEqual(destinationDirectoryPath, package.path);
            Assert.AreEqual(renamedBmsonPath, package.ChartEntries.Single().Chart.Path);
            Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
        });
    }

    [TestMethod]
    public void IsSmartOverwriteProtectedExtension_DoesNotTreatBmsonAsProtectedResource()
    {
        var service = new BmsLibraryPackageInstallService();

        Assert.IsFalse(service.IsSmartOverwriteProtectedExtension("chart.bmson"));
        Assert.IsTrue(service.IsSmartOverwriteProtectedExtension("notes.txt"));
    }

    [TestMethod]
    public void DeletePendingCharts_DeletesWholePackageDirectoryWhenSelectionCoversPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string chartPath = Path.Combine(packageDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n");
            TestableBmsFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var package = ChartPackageTestExtensions.CreatePackage([chart]);
            package.path = packageDirectoryPath;
            package.delete_parent = false;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [ChartFileProjection.FromBmsFile(chart)],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                new RealFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(1, result.Removed);
            Assert.AreEqual(0, result.Failed);
            Assert.AreEqual(0, result.Skipped);
            CollectionAssert.AreEqual(new[] { chartPath }, result.ChartPathsToRemove);
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_DeletesWholeAdapterlessBmsonPackageDirectoryFromPackageSelectionWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(bmsonPath);
            PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            package.path = packageDirectoryPath;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [adapterlessBmsonEntry.Chart],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                new RealFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(1, result.Removed);
            Assert.AreEqual(0, result.Failed);
            Assert.AreEqual(0, result.Skipped);
            CollectionAssert.AreEqual(new[] { bmsonPath }, result.ChartPathsToRemove);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_DeletesAdapterlessBmsonChartByPathWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(bmsonPath);
            PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            package.path = packageDirectoryPath;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [adapterlessBmsonEntry.Chart],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: false,
                new RealFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(1, result.Removed);
            Assert.AreEqual(0, result.Failed);
            Assert.AreEqual(0, result.Skipped);
            CollectionAssert.AreEqual(new[] { bmsonPath }, result.ChartPathsToRemove);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.IsFalse(File.Exists(bmsonPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_DeletesWholeAdapterlessBmsonPackageDirectoryByPathWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(bmsonPath);
            PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            package.path = packageDirectoryPath;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [adapterlessBmsonEntry.Chart],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                new RealFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(1, result.Removed);
            Assert.AreEqual(0, result.Failed);
            Assert.AreEqual(0, result.Skipped);
            CollectionAssert.AreEqual(new[] { bmsonPath }, result.ChartPathsToRemove);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void DeletePendingCharts_FailedPackageFolderDeleteDoesNotMaterializeAdapterlessBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingBmsonPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string bmsonPath = Path.Combine(packageDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(bmsonPath);
            PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(bmsonSong));
            ChartPackage package = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);
            package.path = packageDirectoryPath;

            PendingFileDeletionResult result = service.DeletePendingCharts(
                [adapterlessBmsonEntry.Chart],
                [package],
                sendToRecycleBin: false,
                deleteContainingPackageFoldersWhenNoBms: true,
                new FailingDeleteDirectoryFileMutationService(),
                null,
                null);

            Assert.AreEqual(1, result.Requested);
            Assert.AreEqual(1, result.Processed);
            Assert.AreEqual(0, result.Removed);
            Assert.AreEqual(1, result.Failed);
            Assert.AreEqual(0, result.Skipped);
            Assert.AreEqual(0, result.ChartPathsToRemove.Count);
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
            Assert.IsTrue(File.Exists(bmsonPath));
        });
    }

    [TestMethod]
    public void GetPendingBmsFormatChartFilesSnapshot_ExcludesBmsonCharts()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string bmsonPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempDirectoryPath, "chart.bms"));
            PackageChartEntry adapterlessBmsonEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(bmsonPath)));
            PackageChartEntry plainBmsonPathEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempDirectoryPath, "plain.bmson"),
                md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            }));
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(bmsFile)), adapterlessBmsonEntry, plainBmsonPathEntry]);
            ChartPackage adapterlessBmsonPackage = ChartPackage.FromChartEntries([adapterlessBmsonEntry]);

            List<ChartFile> result = service.GetPendingBmsFormatChartFilesSnapshot([package, adapterlessBmsonPackage]);

            CollectionAssert.AreEqual(new[] { bmsFile }, result.Select(chart => chart.GetBmsStorageOwner()).ToArray());
            Assert.IsNull(adapterlessBmsonEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void GetPendingBmsFormatChartFilesSnapshot_UsesBmsStorageOwnerFromChartFile()
    {
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\chart.bms");
        PackageChartEntry bmsEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(bmsFile));
        ChartPackage package = ChartPackage.FromChartEntries([bmsEntry]);

        List<ChartFile> result = service.GetPendingBmsFormatChartFilesSnapshot([package]);

        CollectionAssert.AreEqual(new[] { bmsFile }, result.Select(chart => chart.GetBmsStorageOwner()).ToArray());
        Assert.AreSame(bmsFile, bmsEntry.GetBmsOwnerForTest());
    }

    [TestMethod]
    public void RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions_ExcludesBmsonCharts()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string bmsonPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, "{}");
            int renameCallCount = 0;

            PendingZeroNoteRenameResult result = service.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
                [],
                delegate
                {
                    renameCallCount++;
                    return new RenameInvalidExtensionOutcome
                    {
                        Action = RenameInvalidExtensionAction.Renamed
                    };
                });

            Assert.AreEqual(0, result.Total);
            Assert.AreEqual(0, result.Processed);
            Assert.AreEqual(0, renameCallCount);
            Assert.IsTrue(File.Exists(bmsonPath));
        });
    }

    [TestMethod]
    public void RenamePendingBmsFormatChartFileExtensions_ReturnsRenamedDuplicateDeletedAndFailedFiles()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            var fileOperationService = new BmsLibraryLibraryFileOperationsService();
            var fileMutationService = new RealFileMutationService();
            string renameSourcePath = Path.Combine(tempDirectoryPath, "rename_me.bms");
            string duplicateSourcePath = Path.Combine(tempDirectoryPath, "duplicate.bms");
            string duplicateDestinationPath = Path.Combine(tempDirectoryPath, "duplicate.bme");
            string failureSourcePath = Path.Combine(tempDirectoryPath, "failure.bms");
            string bmsonSourcePath = Path.Combine(tempDirectoryPath, "skip.bmson");
            File.WriteAllText(renameSourcePath, "rename");
            File.WriteAllText(duplicateSourcePath, "same");
            File.WriteAllText(duplicateDestinationPath, "same");
            File.WriteAllText(failureSourcePath, "failure");
            File.WriteAllText(bmsonSourcePath, "{}");
            TestableBmsFile renameFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", renameSourcePath);
            TestableBmsFile duplicateFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", duplicateSourcePath);
            TestableBmsFile failureFile = CreateFile("cccccccccccccccccccccccccccccccc", failureSourcePath);
            duplicateFile.SetHash(fileOperationService.TryComputeFileMd5ForPath(duplicateSourcePath));

            PendingExtensionRenameResult result = service.RenamePendingBmsFormatChartFileExtensions(
                [
                    ChartFileProjection.FromBmsFile(renameFile),
                    ChartFileProjection.FromBmsFile(duplicateFile),
                    ChartFileProjection.FromBmsFile(failureFile)
                ],
                ".bme",
                delegate (BMSFile file, string requestedPath)
                {
                    if (ReferenceEquals(file, failureFile))
                    {
                        return new RenameInvalidExtensionOutcome
                        {
                            Action = RenameInvalidExtensionAction.Skipped,
                            FinalPath = requestedPath,
                            FailureException = new IOException("failure")
                        };
                    }
                    return fileOperationService.ProcessInvalidExtensionRename(file, requestedPath, fileMutationService, null);
                });

            Assert.AreEqual(3, result.Total);
            Assert.AreEqual(1, result.Renamed);
            Assert.AreEqual(1, result.DuplicateDeleted);
            Assert.AreEqual(1, result.Skipped);
            Assert.AreEqual(1, result.Failed);
            CollectionAssert.AreEquivalent(new[] { renameFile.path, duplicateFile.path }, result.ChartPathsToRemove);
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreSame(failureFile, result.Failures[0].File);
            Assert.IsTrue(File.Exists(bmsonSourcePath));
        });
    }

    [TestMethod]
    public void MovePackageFiles_DeletesProtectedSourceWhenSuffixedCandidateHasSameHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            var fileMutationService = new RealFileMutationService();
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "src");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "dst");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            string sourceFilePath = Path.Combine(sourceDirectoryPath, "notes.txt");
            string destinationFilePath = Path.Combine(destinationDirectoryPath, "notes.txt");
            string suffixedDestinationPath = Path.Combine(destinationDirectoryPath, "notes(1).txt");
            File.WriteAllText(sourceFilePath, "same");
            File.WriteAllText(destinationFilePath, "different");
            File.WriteAllText(suffixedDestinationPath, "same");
            var package = ChartPackageTestExtensions.CreatePackage(Enumerable.Empty<BMSFile>());
            package.path = sourceDirectoryPath;
            package.delete_parent = false;

            bool moved = service.MovePackageFiles(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = true,
                    KeepSmartOverwriteProtectedFilesByRenaming = true
                },
                null,
                ex => ex.Message,
                fileMutationService,
                null,
                null,
                null,
                _ => { });

            Assert.IsTrue(moved);
            Assert.IsFalse(File.Exists(sourceFilePath));
            Assert.IsTrue(File.Exists(destinationFilePath));
            Assert.IsTrue(File.Exists(suffixedDestinationPath));
            Assert.IsFalse(File.Exists(Path.Combine(destinationDirectoryPath, "notes(2).txt")));
        });
    }

    [TestMethod]
    public void MovePackageFiles_RenamesProtectedSourceToFirstAvailableSuffixAfterDifferentCandidates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            var fileMutationService = new RealFileMutationService();
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "src");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "dst");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            string sourceFilePath = Path.Combine(sourceDirectoryPath, "notes.txt");
            string destinationFilePath = Path.Combine(destinationDirectoryPath, "notes.txt");
            string suffixedDestinationPath = Path.Combine(destinationDirectoryPath, "notes(1).txt");
            string finalDestinationPath = Path.Combine(destinationDirectoryPath, "notes(2).txt");
            File.WriteAllText(sourceFilePath, "source");
            File.WriteAllText(destinationFilePath, "different-a");
            File.WriteAllText(suffixedDestinationPath, "different-b");
            var package = ChartPackageTestExtensions.CreatePackage(Enumerable.Empty<BMSFile>());
            package.path = sourceDirectoryPath;
            package.delete_parent = false;

            bool moved = service.MovePackageFiles(
                package,
                destinationDirectoryPath,
                new BmsLibraryOptionsSnapshot
                {
                    EnableSmartComponentOverwrite = true,
                    KeepSmartOverwriteProtectedFilesByRenaming = true
                },
                null,
                ex => ex.Message,
                fileMutationService,
                null,
                null,
                null,
                _ => { });

            Assert.IsTrue(moved);
            Assert.IsFalse(File.Exists(sourceFilePath));
            Assert.IsTrue(File.Exists(destinationFilePath));
            Assert.IsTrue(File.Exists(suffixedDestinationPath));
            Assert.IsTrue(File.Exists(finalDestinationPath));
        });
    }

    [TestMethod]
    public void MovePackageFiles_RenamesProtectedSourceWhenIntermediateCandidateHashIsUnavailable()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            var fileMutationService = new RealFileMutationService();
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "src");
            string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "dst");
            Directory.CreateDirectory(sourceDirectoryPath);
            Directory.CreateDirectory(destinationDirectoryPath);
            string sourceFilePath = Path.Combine(sourceDirectoryPath, "notes.txt");
            string destinationFilePath = Path.Combine(destinationDirectoryPath, "notes.txt");
            string unavailableCandidatePath = Path.Combine(destinationDirectoryPath, "notes(1).txt");
            string finalDestinationPath = Path.Combine(destinationDirectoryPath, "notes(2).txt");
            File.WriteAllText(sourceFilePath, "source");
            File.WriteAllText(destinationFilePath, "different");
            File.WriteAllText(unavailableCandidatePath, "locked");
            var package = ChartPackageTestExtensions.CreatePackage(Enumerable.Empty<BMSFile>());
            package.path = sourceDirectoryPath;
            package.delete_parent = false;

            using (var lockStream = new FileStream(unavailableCandidatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool moved = service.MovePackageFiles(
                    package,
                    destinationDirectoryPath,
                    new BmsLibraryOptionsSnapshot
                    {
                        EnableSmartComponentOverwrite = true,
                        KeepSmartOverwriteProtectedFilesByRenaming = true
                    },
                    null,
                    ex => ex.Message,
                    fileMutationService,
                    null,
                    null,
                    null,
                    _ => { });

                Assert.IsTrue(moved);
            }

            Assert.IsFalse(File.Exists(sourceFilePath));
            Assert.IsTrue(File.Exists(destinationFilePath));
            Assert.IsTrue(File.Exists(unavailableCandidatePath));
            Assert.IsTrue(File.Exists(finalDestinationPath));
        });
    }

    [TestMethod]
    public void MovePackageFiles_DeletesParentDirectory_WhenRemainingFilesAreEmpty()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");

            bool moved = ExecuteSingleChartParentDeleteMove(setup, existingHashes: null, out List<string> logs);

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
            Assert.IsTrue(logs.Any(message => message.IndexOf("Folder deletion success:", StringComparison.OrdinalIgnoreCase) >= 0));
        });
    }

    [TestMethod]
    public void MovePackageFiles_KeepsParentDirectory_WhenUninstalledChartRemains()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string remainingChartPath = CreateBmsFile(setup.ParentDirectoryPath, "remain-uninstalled.bms", "#TITLE Remain Uninstalled");

            bool moved = ExecuteSingleChartParentDeleteMove(setup, existingHashes: null, out List<string> logs);

            Assert.IsTrue(moved);
            Assert.IsTrue(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(remainingChartPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
            Assert.IsTrue(logs.Any(message => message.IndexOf("Folder deletion skipped:", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("remaining_chart_not_installed", StringComparison.OrdinalIgnoreCase) >= 0));
        });
    }

    [TestMethod]
    public void MovePackageFiles_DeletesParentDirectory_WhenOnlyInstalledChartsRemain()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string remainingChartPath = CreateBmsFile(setup.ParentDirectoryPath, "remain-installed.bms", "#TITLE Remain Installed");
            string remainingHash = BMSFile.CreateBMSFileFromFile(remainingChartPath).hash;

            bool moved = ExecuteSingleChartParentDeleteMove(
                setup,
                new PrimaryHashSetLookup([remainingHash]),
                out List<string> logs);

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
            Assert.IsTrue(logs.Any(message => message.IndexOf("Folder deletion success:", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("remaining_files_all_installed_charts", StringComparison.OrdinalIgnoreCase) >= 0));
        });
    }

    [TestMethod]
    public void MovePackageFiles_KeepsParentDirectory_WhenNonChartFileRemains()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string readmePath = Path.Combine(setup.ParentDirectoryPath, "readme.txt");
            File.WriteAllText(readmePath, "remaining text");

            bool moved = ExecuteSingleChartParentDeleteMove(setup, existingHashes: null, out List<string> logs);

            Assert.IsTrue(moved);
            Assert.IsTrue(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(readmePath));
            Assert.IsTrue(logs.Any(message => message.IndexOf("Folder deletion skipped:", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("remaining_non_chart_file", StringComparison.OrdinalIgnoreCase) >= 0));
        });
    }

    [TestMethod]
    public void MovePackageFiles_KeepsParentDirectory_WhenRemainingChartHashCannotBeMatched()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string nestedDirectoryPath = Path.Combine(setup.ParentDirectoryPath, "Nested");
            Directory.CreateDirectory(nestedDirectoryPath);
            string nestedChartPath = CreateBmsFile(nestedDirectoryPath, "remain-nested.bms", "#TITLE Nested Remain");

            bool moved = ExecuteSingleChartParentDeleteMove(setup, existingHashes: null, out List<string> logs);

            Assert.IsTrue(moved);
            Assert.IsTrue(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(nestedChartPath));
            Assert.IsTrue(logs.Any(message => message.IndexOf("Folder deletion skipped:", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("remaining_chart_not_installed", StringComparison.OrdinalIgnoreCase) >= 0));
        });
    }

    [TestMethod]
    public void MovePackageFiles_DeletesParentDirectory_WhenNestedRemainingChartsAreAllInstalled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string nestedDirectoryPath = Path.Combine(setup.ParentDirectoryPath, "Nested");
            Directory.CreateDirectory(nestedDirectoryPath);
            string nestedChartPath = CreateBmsFile(nestedDirectoryPath, "remain-nested-installed.bms", "#TITLE Nested Installed");
            string nestedHash = BMSFile.CreateBMSFileFromFile(nestedChartPath).hash;

            bool moved = ExecuteSingleChartParentDeleteMove(
                setup,
                new PrimaryHashSetLookup([nestedHash]),
                out List<string> logs);

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
            Assert.IsTrue(logs.Any(message => message.IndexOf("Folder deletion success:", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("remaining_files_all_installed_charts", StringComparison.OrdinalIgnoreCase) >= 0));
        });
    }

    [TestMethod]
    public void MovePackageFiles_DeletesParentDirectory_WhenRemainingBmsonChartIsInstalled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            SafeDeleteMoveSetup setup = CreateSingleChartParentDeleteSetup(tempDirectoryPath, "install-target", "#TITLE Installed");
            string remainingBmsonPath = Path.Combine(setup.ParentDirectoryPath, "remain-installed.bmson");
            File.WriteAllText(remainingBmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            string remainingHash = ChartLookupKey.GetPrimaryHash(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(remainingBmsonPath), includeWarningSnapshot: false));

            bool moved = ExecuteSingleChartParentDeleteMove(
                setup,
                new PrimaryHashSetLookup([remainingHash]),
                out List<string> logs);

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
            Assert.IsTrue(logs.Any(message => message.IndexOf("Folder deletion success:", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("remaining_files_all_installed_charts", StringComparison.OrdinalIgnoreCase) >= 0));
        });
    }

    [TestMethod]
    public void MovePackageFiles_SafeCleanupRemainingChartHashCheckDoesNotMaterializePendingAdapter()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryPackageInstallService.cs"));
        int methodStart = source.IndexOf("private static bool TryGetRemainingChartLookupKey", StringComparison.Ordinal);
        int methodEnd = source.IndexOf("private static bool IsSupportedChartFilePath", methodStart, StringComparison.Ordinal);
        Assert.IsTrue(methodStart >= 0);
        Assert.IsTrue(methodEnd > methodStart);
        string method = source.Substring(methodStart, methodEnd - methodStart);
        int bmsonBranchStart = method.IndexOf("ChartFileKindResolver.IsBmsonFilePath(remainingFilePath)", StringComparison.Ordinal);
        int nonBmsonBranchStart = method.IndexOf(": ChartFileContentReader.ReadSnapshot", bmsonBranchStart, StringComparison.Ordinal);
        Assert.IsTrue(bmsonBranchStart >= 0);
        Assert.IsTrue(nonBmsonBranchStart > bmsonBranchStart);
        string bmsonBranch = method.Substring(bmsonBranchStart, nonBmsonBranchStart - bmsonBranchStart);

        StringAssert.Contains(bmsonBranch, "BmsonSongParser.Parse(remainingFilePath)");
        StringAssert.Contains(method.Substring(nonBmsonBranchStart), "ChartFileContentReader.ReadSnapshot(remainingFilePath).Md5");
    }

    [DataTestMethod]
    [DataRow("fixture.zip")]
    [DataRow("fixture.7z")]
    [DataRow("fixture.rar")]
    [DataRow("fixture.lzh")]
    [DoNotParallelize]
    public void ExpandInstallSources_ExtractsSupportedArchiveAndRestoresLastWriteTime(string archiveFileName)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string archivePath = Path.Combine(tempDirectoryPath, archiveFileName);
            File.Copy(GetArchiveFixturePath(archiveFileName), archivePath);
            var service = new BmsLibraryPackageInstallService();
            List<string> logs = [];

            try
            {
                List<string> expandedPaths = service.ExpandInstallSources(
                    [archivePath],
                    new RealFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    logs.Add,
                    null,
                    null);

                Assert.AreEqual(1, expandedPaths.Count);
                string extractedDirectoryPath = expandedPaths[0];
                string extractedChartPath = Path.Combine(extractedDirectoryPath, "maybe_H.bms");
                Assert.IsTrue(File.Exists(extractedChartPath));
                Assert.AreEqual(GetExpectedArchiveLastWriteTime(), File.GetLastWriteTime(extractedChartPath));
                Assert.IsFalse(logs.Any(message => message.IndexOf("extract_failed", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.IsFalse(logs.Any(message => message.IndexOf("metadata_restore_required_failed", StringComparison.OrdinalIgnoreCase) >= 0));
            }
            finally
            {
                global::BeMusicSeeker.TempDirectoryPublisher.RemoveAll();
            }
        });
    }

    [DataTestMethod]
    [DataRow("fixture.zip")]
    [DataRow("fixture.7z")]
    [DataRow("fixture.rar")]
    [DataRow("fixture.lzh")]
    [DoNotParallelize]
    public void ExpandInstallSources_AbortsArchiveWhenRequiredLastWriteRestoreFails(string archiveFileName)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string archivePath = Path.Combine(tempDirectoryPath, archiveFileName);
            File.Copy(GetArchiveFixturePath(archiveFileName), archivePath);
            var service = new BmsLibraryPackageInstallService();
            List<string> logs = [];
            var dialogService = new RecordingDialogService();

            try
            {
                List<string> expandedPaths = service.ExpandInstallSources(
                    [archivePath],
                    new FailingLastWriteFileMutationService(),
                    new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                    logs.Add,
                    dialogService,
                    null);

                Assert.AreEqual(0, expandedPaths.Count);
                Assert.IsTrue(logs.Any(message => message.IndexOf("metadata_restore_required_failed", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.IsTrue(logs.Any(message => message.IndexOf(".bms", StringComparison.OrdinalIgnoreCase) >= 0));
                Assert.AreEqual(1, dialogService.Messages.Count);
                Assert.IsTrue(dialogService.Messages[0].IndexOf("更新日時", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            finally
            {
                global::BeMusicSeeker.TempDirectoryPublisher.RemoveAll();
            }
        });
    }

    private static TestableBmsFile CreateFile(string hash, string path)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(hash);
        return file;
    }

    private static InstalledChartLookupIndexSnapshot CreateInstalledChartLookup(IEnumerable<BMSFile> installedFiles)
    {
        return CreateInstalledChartLookup(installedFiles, []);
    }

    private static InstalledChartLookupIndexSnapshot CreateInstalledChartLookup(
        IEnumerable<BMSFile> installedFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> installedBmsonSongs)
    {
        var state = new InstalledChartLookupIndexState();
        foreach (BMSFile file in installedFiles ?? [])
        {
            if (file != null)
            {
                state.AddChart(file.path, file.hash, file.sha256);
            }
        }
        foreach (LR2SongDBExtended.bmson_song song in installedBmsonSongs ?? [])
        {
            if (song != null)
            {
                state.AddChart(song.path, song.md5, song.sha256);
            }
        }
        return state.CreateSnapshot();
    }

    private static List<BMSFile> GetAddedBmsFiles(PackageInstallExecutionResult result)
    {
        return [.. (result?.AddedCharts ?? [])
            .Select(chart => chart?.GetBmsStorageOwner())
            .Where(ChartFileKindResolver.IsBmsChartFile)];
    }

    private static List<LR2SongDBExtended.bmson_song> GetAddedBmsonSongs(PackageInstallExecutionResult result)
    {
        return [.. (result?.AddedCharts ?? [])
            .Select(chart => chart?.GetBmsonStorageOwner())
            .Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
    }

    private static PackageInstallEstimationSnapshot BuildPackageSnapshot(ChartPackage package, IEnumerable<BMSFile> targetFiles)
    {
        List<BMSFile> targetFileList = [.. (targetFiles ?? []).Where(file => file != null)];
        List<PackageChartEntry> targetEntries = targetFileList.Count == 0
            ? package.ChartEntries
            : ResolvePackageEntries(package, targetFileList);
        return package.GetOrBuildInstallEstimationSnapshotFromEntries(targetEntries);
    }

    private static List<PackageChartEntry> ResolvePackageEntries(ChartPackage package, IEnumerable<BMSFile> targetFiles)
    {
        List<PackageChartEntry> packageEntries = package.ChartEntries;
        var result = new List<PackageChartEntry>();
        foreach (BMSFile targetFile in (targetFiles ?? []).Where(file => file != null))
        {
            PackageChartEntry packageEntry = packageEntries.FirstOrDefault(entry => IsSamePackageChartTarget(entry, targetFile));
            result.Add(packageEntry ?? PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(targetFile)));
        }
        return [.. result.Where(entry => entry?.Chart != null)];
    }

    private static bool IsSamePackageChartTarget(PackageChartEntry entry, BMSFile targetFile)
    {
        if (entry?.Chart == null || targetFile == null)
        {
            return false;
        }
        if (ReferenceEquals(entry.GetBmsOwnerForTest(), targetFile))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(entry.Chart.Path)
            && !string.IsNullOrWhiteSpace(targetFile.path)
            && entry.Chart.Path.Equals(targetFile.path, StringComparison.OrdinalIgnoreCase);
    }

    private static SafeDeleteMoveSetup CreateSingleChartParentDeleteSetup(string tempDirectoryPath, string chartBaseName, string chartBody)
    {
        string parentDirectoryPath = Path.Combine(tempDirectoryPath, "Pending", "Parent");
        string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Package");
        Directory.CreateDirectory(parentDirectoryPath);
        string sourceChartPath = CreateBmsFile(parentDirectoryPath, chartBaseName + ".bms", chartBody);
        var sourceChart = BMSFile.CreateBMSFileFromFile(sourceChartPath);
        var package = ChartPackageTestExtensions.CreatePackage([sourceChart]);
        package.path = sourceChartPath;
        package.delete_parent = true;
        return new SafeDeleteMoveSetup
        {
            ParentDirectoryPath = parentDirectoryPath,
            DestinationDirectoryPath = destinationDirectoryPath,
            SourceChartPath = sourceChartPath,
            Package = package
        };
    }

    private static bool ExecuteSingleChartParentDeleteMove(SafeDeleteMoveSetup setup, IPrimaryHashLookup? existingHashes, out List<string> logs)
    {
        var service = new BmsLibraryPackageInstallService();
        var fileMutationService = new RealFileMutationService();
        List<string> localLogs = [];
        bool moved = service.MovePackageFiles(
            setup.Package,
            setup.DestinationDirectoryPath,
            new BmsLibraryOptionsSnapshot
            {
                EnableSmartComponentOverwrite = false,
                KeepSmartOverwriteProtectedFilesByRenaming = false
            },
            (_, _, _) => throw new AssertFailedException("createFolderPath should not be called when destination is specified."),
            ex => ex.Message,
            fileMutationService,
            null,
            null,
            null,
            message => localLogs.Add(message),
            showMessageBoxOnInstallFail: false,
            deleteAllContents: true,
            existingHashes: existingHashes);
        logs = localLogs;
        return moved;
    }

    private static string CreateBmsFile(string directoryPath, string fileName, string titleLine)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "#PLAYER 1\r\n" + titleLine + "\r\n#ARTIST Test\r\n");
        return filePath;
    }

    private static string CreateBmsonJsonWithSound(string soundName)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"Bmson\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[{\"name\":\"" + soundName + "\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]"
            + "}";
    }

    private static string GetArchiveFixturePath(string fileName)
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "archives", fileName);
    }

    private static string FindRepositoryRoot()
    {
        string? directoryPath = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            if (File.Exists(Path.Combine(directoryPath, "BeMusicSeeker.csproj")))
            {
                return directoryPath!;
            }

            DirectoryInfo? parent = Directory.GetParent(directoryPath);
            directoryPath = parent?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static DateTime GetExpectedArchiveLastWriteTime()
    {
        return new DateTime(2002, 1, 11, 18, 0, 8);
    }

    private static void WithTemporaryDirectory(Action<string> testAction)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PackageInstallTests_" + Guid.NewGuid().ToString("N"));
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

    private static DispatcherCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>([.. (packages ?? [])]), Dispatcher.CurrentDispatcher);
    }

    private static void WithTemporarySongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PackageInstallSongDbTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }
            testAction(songDbPath, tempRootPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed class SafeDeleteMoveSetup
    {
        public string ParentDirectoryPath { get; set; } = string.Empty;

        public string DestinationDirectoryPath { get; set; } = string.Empty;

        public string SourceChartPath { get; set; } = string.Empty;

        public ChartPackage Package { get; set; } = null!;
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

        public void SetMode(int? value)
        {
            mode = value;
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
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
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
    }

    private sealed class FailingLastWriteFileMutationService : IFileMutationService
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
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
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
            if (lastWriteTime.HasValue && !isDirectory)
            {
                throw new IOException("required_last_write_restore_failure");
            }

            if (!lastWriteTime.HasValue)
            {
                return;
            }

            if (isDirectory)
            {
                Directory.SetLastWriteTime(path, lastWriteTime.Value);
            }
            else
            {
                File.SetLastWriteTime(path, lastWriteTime.Value);
            }
        }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public List<string> Messages { get; } = [];

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            Messages.Add(messageBoxText);
            return defaultResult;
        }
    }

    private sealed class FailingDeleteDirectoryFileMutationService : IFileMutationService
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
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
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
            throw new IOException("required_directory_delete_failure");
        }

        public void DeleteDirectoryShell(string directoryPath, UIOption uiOption, RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }
    }

    private sealed class RealFileMutationService : IFileMutationService
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
            string destinationDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectoryPath))
            {
                Directory.CreateDirectory(destinationDirectoryPath);
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
            string destinationParentDirectoryPath = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationParentDirectoryPath))
            {
                Directory.CreateDirectory(destinationParentDirectoryPath);
            }
            Directory.Move(sourcePath, destinationPath);
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
            if (lastWriteTime.HasValue)
            {
                if (isDirectory)
                {
                    Directory.SetLastWriteTime(path, lastWriteTime.Value);
                }
                else
                {
                    File.SetLastWriteTime(path, lastWriteTime.Value);
                }
            }
        }
    }

}
