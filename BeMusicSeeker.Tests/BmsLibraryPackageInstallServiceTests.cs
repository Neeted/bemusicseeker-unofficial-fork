using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPackageInstallServiceTests
{
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
    public void BuildPendingPackageMutationDelta_RemovesMatchedFilesAndDeletesEmptyPackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile keepFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\keep.bms");
        TestableBmsFile removeFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\remove.bms");
        TestableBmsFile removeWholePackageFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\only.bms");
        var keepPackage = new ChartPackage([keepFile, removeFile])
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        var removePackage = new ChartPackage([removeWholePackageFile])
        {
            path = "C:\\Pending\\Pkg2",
            delete_parent = false
        };

        PendingPackageMutationDelta delta = service.BuildPendingPackageMutationDelta(
            [keepPackage, removePackage],
            filesToRemove: [removeFile, removeWholePackageFile]);

        Assert.IsTrue(delta.HasChanges);
        Assert.AreEqual(1, delta.RemainingPackages.Count);
        Assert.AreSame(keepPackage, delta.RemainingPackages[0]);
        CollectionAssert.AreEqual(new[] { keepFile }, keepPackage.GetChartAdapters());
        CollectionAssert.AreEquivalent(new[] { "C:\\Pending\\Pkg2" }, delta.InstallPathsToDelete);
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
        alreadyInstalledInPackage.instl_dst = destinationDirectory;
        TestableBmsFile newFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\b.bms");
        newFile.instl_dst = destinationDirectory;
        TestableBmsFile cleanupOnlyFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\c.bms");
        cleanupOnlyFile.instl_dst = destinationDirectory;
        var mixedPackage = new ChartPackage([alreadyInstalledInPackage, newFile])
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        var cleanupOnlyPackage = new ChartPackage([cleanupOnlyFile])
        {
            path = "C:\\Pending\\Pkg2",
            delete_parent = false
        };

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [mixedPackage, cleanupOnlyPackage],
            [mixedPackage, cleanupOnlyPackage],
            [installedFile, cleanupInstalledFile],
            [],
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
        Assert.AreEqual(1, groupedItem.InstallWorkPackage.GetChartAdapters().Count);
        Assert.AreSame(newFile, groupedItem.InstallWorkPackage.GetChartAdapters()[0]);
        CollectionAssert.Contains(groupedItem.ExcludedComponentPaths.ToList(), alreadyInstalledInPackage.path);
        Assert.IsTrue(alreadyInstalledInPackage.Warnings.Contains(ChartWarningKind.AlreadyInstalled));
        Assert.AreEqual("[1] " + BeMusicSeeker.Properties.Resources.WarningDigest_AlreadyInstalled, alreadyInstalledInPackage.WarningDigestText);
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
        pendingFile.instl_dst = destinationDirectory;
        var pendingPackage = new ChartPackage([pendingFile])
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [pendingPackage],
            [pendingPackage],
            [installedFile],
            [],
            deletePendingPackageSourceAfterInstall: false,
            countComponentMoveTargets: (_, _, _) => 1);

        Assert.AreEqual(1, plan.Groups.Count);
        Assert.AreEqual(1, plan.Groups[0].Items.Count);
        Assert.AreEqual(1, plan.Groups[0].Items[0].InstallWorkPackage.GetChartAdapters().Count);
        Assert.AreSame(pendingFile, plan.Groups[0].Items[0].InstallWorkPackage.GetChartAdapters()[0]);
        Assert.AreEqual(string.Empty, pendingFile.WarningDigestText);
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_CountsDeferredManualHoldPackagesSeparately()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        var deferredPackage = new ChartPackage([pendingFile])
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false,
            DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline
        };

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [deferredPackage],
            [deferredPackage],
            [],
            [],
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
        alreadyInstalledInPackage.instl_dst = destinationDirectory;
        TestableBmsFile newFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\b.bms");
        newFile.instl_dst = destinationDirectory;
        TestableBmsFile cleanupOnlyFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\c.bms");
        cleanupOnlyFile.instl_dst = destinationDirectory;
        var mixedPackage = new ChartPackage([alreadyInstalledInPackage, newFile])
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        var cleanupOnlyPackage = new ChartPackage([cleanupOnlyFile])
        {
            path = "C:\\Pending\\Pkg2",
            delete_parent = false
        };
        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            [mixedPackage, cleanupOnlyPackage],
            [mixedPackage, cleanupOnlyPackage],
            [installedFile, cleanupInstalledFile],
            [],
            deletePendingPackageSourceAfterInstall: true,
            countComponentMoveTargets: (pkg, dst, excluded) => pkg.path == cleanupOnlyPackage.path ? 0 : 2);

        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            true,
            (installPackages, destinationDirectoryArg, deferredMaintenanceTargets, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall) =>
            {
                deferredInstalledPackages.AddRange(installPackages);
                foreach (ChartPackage installPackage in installPackages)
                {
                    deferredMaintenanceTargets.AddRange(installPackage.GetChartAdapters());
                }
                return [];
            },
            (originalPackage, destinationDirectoryArg) => new ChartPackage(originalPackage.GetChartAdapters())
            {
                path = destinationDirectoryArg,
                delete_parent = false
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
        Assert.AreEqual(1, result.DeferredMaintenanceTargets.Count);
        Assert.IsNull(alreadyInstalledInPackage.instl_dst);
        Assert.IsNull(newFile.instl_dst);
        Assert.IsNull(cleanupOnlyFile.instl_dst);
    }

    [TestMethod]
    public void ForceInstallPackages_SkipsWhenConfirmationRejected()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        pendingFile.instl_dst = "C:\\Installed\\Target";
        var pendingPackage = new ChartPackage([pendingFile])
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };

        ForceInstallBatchResult result = service.ForceInstallPackages(
            [pendingPackage],
            [pendingPackage],
            _ => false,
            (_, __) => []);

        Assert.AreEqual(1, result.Requested);
        Assert.AreEqual(1, result.Skipped);
        Assert.AreEqual(0, result.Processed);
        Assert.AreEqual(0, result.PendingPackagesToRemove.Count);
        Assert.AreEqual("C:\\Installed\\Target", pendingFile.instl_dst);
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
            var pendingPackage = new ChartPackage([])
            {
                path = packageDirectoryPath,
                delete_parent = false
            };

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
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bmson"), "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Title\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"}}");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "sound.wav"), "dummy");

            var service = new BmsLibraryPackageInstallService();
            ChartPackageDiscoveryResult result = service.SearchChartPackagesRecursivelyWithMetadata(packageDirectoryPath, 0.6);

            Assert.AreEqual(1, result.Packages.Count);
            Assert.AreEqual(packageDirectoryPath, result.Packages[0].path);
            Assert.AreEqual(1, result.Packages[0].PendingCharts.Count(chart => chart.IsBmsonChart));
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
                result.Packages[0].GetChartAdapters().Select(file => Path.GetFileName(file.path)).ToArray());
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
                0.6,
                _ => false);

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.AreEqual(bmsonFilePath, result.DiscoveredPackages[0].path);
            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(bmsonFilePath, result.PendingPackagesToAdd[0].path);
            Assert.IsTrue(result.PendingPackagesToAdd[0].PendingCharts.Single().IsBmsonChart);
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
            var maintenanceService = new BmsLibraryMaintenanceService();
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6,
                file => maintenanceService.ApplyResourceHealthWarnings(file, file.maintenanceInfo, strictCheck: true));

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PendingChartEntry chart = result.PendingPackagesToAdd[0].PendingCharts.Single();
            Assert.IsTrue(chart.IsBmsonChart);
            Assert.AreEqual(1, chart.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, chart.maintenanceInfo.wav_files_existing);
            Assert.IsTrue(chart.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
            StringAssert.Contains(chart.WarningTooltipText, "WAV");
        });
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
            var maintenanceService = new BmsLibraryMaintenanceService();
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6,
                file => maintenanceService.ApplyResourceHealthWarnings(file, file.maintenanceInfo, strictCheck: true));

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PendingChartEntry chart = result.PendingPackagesToAdd[0].PendingCharts.Single();
            Assert.AreEqual(1, chart.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, chart.maintenanceInfo.wav_files_existing);
            Assert.AreEqual(1, chart.maintenanceInfo.movie_files_defined);
            Assert.AreEqual(0, chart.maintenanceInfo.movie_files_existing);
            Assert.IsTrue(chart.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(chart.Warnings.Contains(ChartWarningKind.ResourceMovieMissing));
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
            var maintenanceService = new BmsLibraryMaintenanceService();
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6,
                file => maintenanceService.ApplyResourceHealthWarnings(file, file.maintenanceInfo, strictCheck: true));

            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            PendingChartEntry chart = result.PendingPackagesToAdd[0].PendingCharts.Single();
            Assert.AreEqual(0, chart.maintenanceInfo.wav_files_existing);
            Assert.AreEqual(0, chart.maintenanceInfo.movie_files_existing);
            Assert.IsTrue(chart.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(chart.Warnings.Contains(ChartWarningKind.ResourceMovieMissing));
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
            var maintenanceService = new BmsLibraryMaintenanceService();
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6,
                file => maintenanceService.ApplyResourceHealthWarnings(file, file.maintenanceInfo, strictCheck: true));

            Assert.AreEqual(1, result.AutoInstallCandidates.Count);
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);
            PendingChartEntry chart = result.AutoInstallCandidates[0].PendingCharts.Single();
            Assert.AreEqual(1, chart.maintenanceInfo.wav_files_existing);
            Assert.AreEqual(1, chart.maintenanceInfo.movie_files_existing);
            Assert.IsFalse(chart.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(chart.Warnings.Contains(ChartWarningKind.ResourceMovieMissing));
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
            var maintenanceService = new BmsLibraryMaintenanceService();
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [packageDirectoryPath],
                [],
                [],
                _ => false,
                0.6,
                file => maintenanceService.ApplyResourceHealthWarnings(file, file.maintenanceInfo, strictCheck: true));

            Assert.AreEqual(1, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.AutoInstallCandidates.Count);
            BMSFile nestedChart = result.PendingPackagesToAdd[0].GetChartAdapters().Single(file => Path.GetFileName(file.path).Equals("another.bms", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(nestedChart.Warnings.Contains(ChartWarningKind.NestedChartFileInPackage));
            Assert.IsTrue(nestedChart.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual("[2] " + BeMusicSeeker.Properties.Resources.WarningDigest_NestedChart + ", " + BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing, nestedChart.WarningDigestText);
            StringAssert.Contains(nestedChart.WarningTooltipText, BeMusicSeeker.Properties.Resources.Warning_NestedChartFileInPackage);
            StringAssert.Contains(nestedChart.WarningTooltipText, "WAV");
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
            var maintenanceService = new BmsLibraryMaintenanceService();
            var service = new BmsLibraryPackageInstallService();

            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [bmsonFilePath],
                [],
                [],
                _ => false,
                0.6,
                file => maintenanceService.ApplyResourceHealthWarnings(file, file.maintenanceInfo, strictCheck: true));

            Assert.AreEqual(1, result.DiscoveredPackages.Count);
            Assert.IsTrue(string.Equals(packageDirectoryPath, result.DiscoveredPackages[0].path, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(1, result.AutoInstallCandidates.Count);
            Assert.IsTrue(string.Equals(packageDirectoryPath, result.AutoInstallCandidates[0].path, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);
            PendingChartEntry chart = result.AutoInstallCandidates[0].PendingCharts.Single();
            Assert.IsTrue(chart.IsBmsonChart);
            Assert.AreEqual(1, chart.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(1, chart.maintenanceInfo.wav_files_existing);
            Assert.AreEqual(string.Empty, chart.WarningDigestText);
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

            string filePackageDirectoryPath = Path.Combine(tempDirectoryPath, "SinglePkg");
            Directory.CreateDirectory(filePackageDirectoryPath);
            string singleFilePath = Path.Combine(filePackageDirectoryPath, "chart_single.bms");
            File.WriteAllText(singleFilePath, "#PLAYER 1\r\n#TITLE Single\r\n#WAVAA sound_single.wav\r\n#00111:AA\r\n");

            var service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                [directoryPackagePath, singleFilePath],
                [],
                [],
                _ => false,
                0.6,
                _ => false);

            Assert.AreEqual(2, result.DiscoveredPackages.Count);
            Assert.AreEqual(2, result.AutoInstallCandidates.Count);
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.RegroupEligibleSourceDirectories.Count);
            Assert.IsTrue(Directory.Exists(result.AutoInstallCandidates[0].path));
            Assert.IsTrue(result.AutoInstallCandidates.Any(package => package.path.Equals(filePackageDirectoryPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(result.DiscoveredPackages.All(package => package.GetChartAdapters().Count > 0));
            Assert.IsTrue(result.DiscoveryMs >= 0);
            Assert.IsTrue(result.ClassificationMs >= 0);
            Assert.IsTrue(result.TotalMs >= 0);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void PrepareAutoInstallWorkflow_DoesNotPrebuildSourceSurfaceForDiscoveredPackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithPendingPackageSourceScanSetting(enabled: false, delegate
        {
            WithTemporaryDirectory(delegate (string tempDirectoryPath)
            {
                string directoryPackagePath = Path.Combine(tempDirectoryPath, "DirPkg");
                Directory.CreateDirectory(directoryPackagePath);
                File.WriteAllText(Path.Combine(directoryPackagePath, "chart_dir.bms"), "#PLAYER 1\r\n#TITLE Dir\r\n#WAVAA sound_dir.wav\r\n#00111:AA\r\n");

                string filePackageDirectoryPath = Path.Combine(tempDirectoryPath, "SinglePkg");
                Directory.CreateDirectory(filePackageDirectoryPath);
                string singleFilePath = Path.Combine(filePackageDirectoryPath, "chart_single.bms");
                File.WriteAllText(singleFilePath, "#PLAYER 1\r\n#TITLE Single\r\n#WAVAA sound_single.wav\r\n#00111:AA\r\n");

                var service = new BmsLibraryPackageInstallService();
                AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                    [directoryPackagePath, singleFilePath],
                    [],
                    [],
                    _ => false,
                    0.6,
                    _ => false);

                Assert.AreEqual(2, result.DiscoveredPackages.Count);
                foreach (ChartPackage package in result.DiscoveredPackages)
                {
                    List<BMSFile> discoveredCharts = [.. package.GetChartAdapters()];
                    PackageInstallEstimationSnapshot firstSnapshot = package.GetOrBuildInstallEstimationSnapshot(discoveredCharts);
                    PackageInstallEstimationSnapshot secondSnapshot = package.GetOrBuildInstallEstimationSnapshot(discoveredCharts);

                    Assert.IsTrue(discoveredCharts.Count > 0);
                    Assert.IsFalse(firstSnapshot.SourceSurfaceCacheHit);
                    Assert.IsTrue(secondSnapshot.SourceSurfaceCacheHit);
                    Assert.AreEqual("fast", firstSnapshot.SourceSurfaceScanBackend);
                }
            });
        });
    }

    [TestMethod]
    public void PendingChartEntry_CreateFromBmsFile_CopiesMode()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        TestableBmsFile source = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\chart.bms");
        source.SetMode(7);

        var pending = PendingChartEntry.CreateFromBmsFile(source);

        Assert.AreEqual(7, pending.mode);
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
                0.6,
                _ => false);

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
        file.SetWarning(ChartWarningKind.InstallEstimationAmbiguous, installWarning);
        file.SetWarning(ChartWarningKind.ResourceWavMissing, "pending resource warning");
        file.InstallDestinationSuggestions = ["C:\\Installed\\A", "C:\\Installed\\B"];
        var package = new ChartPackage([file])
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        List<BMSFile> songUpserts = [];
        List<BMSFile> maintenanceTargets = [];
        List<BMSFile> zeroNoteTargets = [];
        List<BMSFile> scoreTargets = [];
        List<BMSFile> applyTargets = [];

        PackageInstallExecutionResult result = service.InstallPackages(
            [package],
            "C:\\Installed",
            (_, _, _, _, _) => true,
            (files) => songUpserts.AddRange(files),
            (files) => maintenanceTargets.AddRange(files),
            (files) => zeroNoteTargets.AddRange(files),
            (files) => scoreTargets.AddRange(files),
            (files) => applyTargets.AddRange(files));

        Assert.AreEqual(1, result.AddedFiles.Count);
        Assert.AreEqual(1, result.InstalledPackagesToRegister.Count);
        Assert.AreEqual(0, result.FailedPackages.Count);
        CollectionAssert.AreEqual(new[] { file }, songUpserts);
        CollectionAssert.AreEqual(new[] { file }, maintenanceTargets);
        CollectionAssert.AreEqual(new[] { file }, zeroNoteTargets);
        CollectionAssert.AreEqual(new[] { file }, scoreTargets);
        CollectionAssert.AreEqual(new[] { file }, applyTargets);
        Assert.IsFalse(file.HasLowConfidenceInstallWarning);
        Assert.AreEqual(0, file.InstallDestinationSuggestions.Count);
        Assert.IsFalse(file.WarningTooltipText.Contains(Resources.Warning_InstallEstimationAmbiguousPrefix));
        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(string.Empty, file.WarningDigestText);
        Assert.IsTrue(result.MoveMs >= 0);
        Assert.IsTrue(result.SongDbMs >= 0);
        Assert.IsTrue(result.MaintenanceMs >= 0);
        Assert.IsTrue(result.ZeroNoteMs >= 0);
        Assert.IsTrue(result.ScoreMs >= 0);
        Assert.IsTrue(result.ApplyMs >= 0);
        Assert.IsTrue(result.TotalMs >= 0);
    }

    [TestMethod]
    public void InstallPackages_LeavesZeroNoteTimingAtZeroWhenCallbackIsNotProvided()
    {
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Installed\\chart.bms");
        var package = new ChartPackage([file])
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };

        PackageInstallExecutionResult result = service.InstallPackages(
            [package],
            "C:\\Installed",
            (_, _, _, _, _) => true,
            files => { },
            files => { },
            null,
            files => { },
            files => { });

        Assert.AreEqual(1, result.AddedFiles.Count);
        Assert.AreEqual(0L, result.ZeroNoteMs);
    }

    [TestMethod]
    public void InstallPackages_RegistersNestedChartsWhenPackageContainsThem()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        TestableBmsFile rootFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\root.bms");
        TestableBmsFile nestedFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\sub\\another.bms");
        var package = new ChartPackage([rootFile, nestedFile])
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        List<BMSFile> songUpserts = [];
        List<BMSFile> maintenanceTargets = [];
        List<BMSFile> applyTargets = [];

        PackageInstallExecutionResult result = service.InstallPackages(
            [package],
            "C:\\Installed",
            (_, _, _, _, _) => true,
            (files) => songUpserts.AddRange(files),
            (files) => maintenanceTargets.AddRange(files),
            _ => { },
            _ => { },
            (files) => applyTargets.AddRange(files));

        Assert.AreEqual(2, result.AddedFiles.Count);
        CollectionAssert.AreEquivalent(new BMSFile[] { rootFile, nestedFile }, songUpserts);
        CollectionAssert.AreEquivalent(new BMSFile[] { rootFile, nestedFile }, maintenanceTargets);
        CollectionAssert.AreEquivalent(new BMSFile[] { rootFile, nestedFile }, applyTargets);
    }

    [TestMethod]
    public void ExecuteInstalledOnlyResourceOverwrite_CategorizesCleanupInstallAndMissingCases()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryPackageInstallService();
        var cleanupPackage = new ChartPackage([CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Cleanup\\a.bms")])
        {
            path = "C:\\Pending\\Cleanup",
            delete_parent = false
        };
        var installPackage = new ChartPackage([CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Install\\b.bms")])
        {
            path = "C:\\Pending\\Install",
            delete_parent = false
        };
        var missingDestinationPackage = new ChartPackage([CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Missing\\c.bms")])
        {
            path = "C:\\Pending\\Missing",
            delete_parent = false
        };

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
            var package = new ChartPackage([chart, nestedChart])
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };

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
            Assert.AreEqual(Path.Combine(destinationDirectoryPath, "chart.bms"), package.GetChartAdapters()[0].path);
            Assert.AreEqual(Path.Combine(destinationDirectoryPath, "sub", "another.bms"), package.GetChartAdapters()[1].path);
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "chart.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "sub", "another.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "readme.txt")));
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
            var bmsonChart = PendingChartEntry.CreateFromFilePath(sourceBmsonPath);
            var package = new ChartPackage([bmsonChart])
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };

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
            Assert.AreEqual(renamedBmsonPath, package.GetChartAdapters()[0].path);
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
            var bmsonChart = PendingChartEntry.CreateFromFilePath(sourceBmsonPath);
            var package = new ChartPackage([bmsonChart])
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };

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
            Assert.AreEqual(renamedBmsonPath, package.GetChartAdapters()[0].path);
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
            var bmsonChart = PendingChartEntry.CreateFromFilePath(sourceBmsonPath);
            var package = new ChartPackage([bmsonChart])
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };

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
            Assert.AreEqual(renamedBmsonPath, package.GetChartAdapters()[0].path);
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
    public void DeletePendingFiles_DeletesWholePackageDirectoryWhenSelectionCoversPackage()
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
            var package = new ChartPackage([chart])
            {
                path = packageDirectoryPath,
                delete_parent = false
            };

            PendingFileDeletionResult result = service.DeletePendingFiles(
                [chart],
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
            CollectionAssert.AreEqual(new[] { chart }, result.FilesToRemove);
            Assert.IsFalse(Directory.Exists(packageDirectoryPath));
        });
    }

    [TestMethod]
    public void GetPendingBmsFormatChartFilesSnapshot_ExcludesBmsonCharts()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string bmsonPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, "{}");
            TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempDirectoryPath, "chart.bms"));
            var bmsonEntry = PendingChartEntry.CreateFromFilePath(bmsonPath);
            TestableBmsFile plainBmsonPath = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempDirectoryPath, "plain.bmson"));
            var package = new ChartPackage([bmsFile, bmsonEntry, plainBmsonPath]);

            List<BMSFile> result = service.GetPendingBmsFormatChartFilesSnapshot([package]);

            CollectionAssert.AreEqual(new[] { bmsFile }, result);
        });
    }

    [TestMethod]
    public void RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions_ExcludesBmsonCharts()
    {
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            var service = new BmsLibraryPackageInstallService();
            string bmsonPath = Path.Combine(tempDirectoryPath, "chart.bmson");
            File.WriteAllText(bmsonPath, "{}");
            var bmsonFile = PendingChartEntry.CreateFromFilePath(bmsonPath);
            int renameCallCount = 0;

            PendingZeroNoteRenameResult result = service.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
                [bmsonFile],
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
            var bmsonFile = PendingChartEntry.CreateFromFilePath(bmsonSourcePath);
            duplicateFile.SetHash(fileOperationService.TryComputeFileMd5ForPath(duplicateSourcePath));

            PendingExtensionRenameResult result = service.RenamePendingBmsFormatChartFileExtensions(
                [renameFile, duplicateFile, failureFile, bmsonFile],
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
            CollectionAssert.AreEquivalent(new[] { renameFile, duplicateFile }, result.FilesToRemove);
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
            var package = new ChartPackage([])
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };

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
            var package = new ChartPackage([])
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };

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
            var package = new ChartPackage([])
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };

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
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { remainingHash },
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
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { nestedHash },
                out List<string> logs);

            Assert.IsTrue(moved);
            Assert.IsFalse(Directory.Exists(setup.ParentDirectoryPath));
            Assert.IsTrue(File.Exists(Path.Combine(setup.DestinationDirectoryPath, "install-target.bms")));
            Assert.IsTrue(logs.Any(message => message.IndexOf("Folder deletion success:", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("remaining_files_all_installed_charts", StringComparison.OrdinalIgnoreCase) >= 0));
        });
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

    private static SafeDeleteMoveSetup CreateSingleChartParentDeleteSetup(string tempDirectoryPath, string chartBaseName, string chartBody)
    {
        string parentDirectoryPath = Path.Combine(tempDirectoryPath, "Pending", "Parent");
        string destinationDirectoryPath = Path.Combine(tempDirectoryPath, "Installed", "Package");
        Directory.CreateDirectory(parentDirectoryPath);
        string sourceChartPath = CreateBmsFile(parentDirectoryPath, chartBaseName + ".bms", chartBody);
        var sourceChart = BMSFile.CreateBMSFileFromFile(sourceChartPath);
        var package = new ChartPackage([sourceChart])
        {
            path = sourceChartPath,
            delete_parent = true
        };
        return new SafeDeleteMoveSetup
        {
            ParentDirectoryPath = parentDirectoryPath,
            DestinationDirectoryPath = destinationDirectoryPath,
            SourceChartPath = sourceChartPath,
            Package = package
        };
    }

    private static bool ExecuteSingleChartParentDeleteMove(SafeDeleteMoveSetup setup, HashSet<string>? existingHashes, out List<string> logs)
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

    private static void WithPendingPackageSourceScanSetting(bool enabled, Action action)
    {
        bool original = BeMusicSeeker.Properties.Settings.Default.UseEverythingForPendingPackageSourceScan;
        try
        {
            BeMusicSeeker.Properties.Settings.Default.UseEverythingForPendingPackageSourceScan = enabled;
            action();
        }
        finally
        {
            BeMusicSeeker.Properties.Settings.Default.UseEverythingForPendingPackageSourceScan = original;
        }
    }
}
