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
public sealed class BmsLibraryPackageInstallServiceTests
{
    [TestMethod]
    public void BuildComponentMovePlan_SkipsExcludedPaths()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            string sourceDirectoryPath = Path.Combine(tempDirectoryPath, "src");
            Directory.CreateDirectory(sourceDirectoryPath);
            string keepFilePath = Path.Combine(sourceDirectoryPath, "keep.txt");
            string skipFilePath = Path.Combine(sourceDirectoryPath, "skip.txt");
            File.WriteAllText(keepFilePath, "keep");
            File.WriteAllText(skipFilePath, "skip");

            ComponentMovePlanBuildResult result = service.BuildComponentMovePlan(
                new[] { sourceDirectoryPath },
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
            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
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
        BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
        TestableBmsFile keepFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\keep.bms");
        TestableBmsFile removeFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\remove.bms");
        TestableBmsFile removeWholePackageFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\only.bms");
        BMSPackage keepPackage = new BMSPackage(new BMSFile[] { keepFile, removeFile })
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        BMSPackage removePackage = new BMSPackage(new BMSFile[] { removeWholePackageFile })
        {
            path = "C:\\Pending\\Pkg2",
            delete_parent = false
        };

        PendingPackageMutationDelta delta = service.BuildPendingPackageMutationDelta(
            new[] { keepPackage, removePackage },
            filesToRemove: new[] { removeFile, removeWholePackageFile });

        Assert.IsTrue(delta.HasChanges);
        Assert.AreEqual(1, delta.RemainingPackages.Count);
        Assert.AreSame(keepPackage, delta.RemainingPackages[0]);
        CollectionAssert.AreEqual(new[] { keepFile }, keepPackage.BMSFiles);
        CollectionAssert.AreEquivalent(new[] { "C:\\Pending\\Pkg2" }, delta.InstallPathsToDelete);
    }

    [TestMethod]
    public void BuildEstimatedInstallBatchPlan_GroupsNewChartsAndKeepsCleanupOnlyCandidates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Lib\\a.bms");
        TestableBmsFile cleanupInstalledFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Lib\\c.bms");
        TestableBmsFile alreadyInstalledInPackage = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        alreadyInstalledInPackage.instl_dst = destinationDirectory;
        TestableBmsFile newFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\b.bms");
        newFile.instl_dst = destinationDirectory;
        TestableBmsFile cleanupOnlyFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\c.bms");
        cleanupOnlyFile.instl_dst = destinationDirectory;
        BMSPackage mixedPackage = new BMSPackage(new BMSFile[] { alreadyInstalledInPackage, newFile })
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        BMSPackage cleanupOnlyPackage = new BMSPackage(new BMSFile[] { cleanupOnlyFile })
        {
            path = "C:\\Pending\\Pkg2",
            delete_parent = false
        };

        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            new[] { mixedPackage, cleanupOnlyPackage },
            new[] { mixedPackage, cleanupOnlyPackage },
            new[] { installedFile, cleanupInstalledFile },
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
        Assert.AreEqual(1, groupedItem.InstallWorkPackage.BMSFiles.Count);
        Assert.AreSame(newFile, groupedItem.InstallWorkPackage.BMSFiles[0]);
        CollectionAssert.Contains(groupedItem.ExcludedComponentPaths.ToList(), alreadyInstalledInPackage.path);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warning_AlreadyInstalled, alreadyInstalledInPackage.warning);
        Assert.IsTrue(plan.FilterMs >= 0);
        Assert.IsTrue(plan.GroupBuildMs >= 0);
        Assert.IsTrue(plan.PlanBuildMs >= 0);
        Assert.AreEqual(2, plan.SelectedPendingCount);
        Assert.AreEqual(1, plan.GroupedPackageCount);
        Assert.AreEqual(1, plan.CleanupOnlyCandidateCount);
    }

    [TestMethod]
    public void ExecuteEstimatedInstallBatchPlan_ReturnsPendingMutationsAndCleanupSummary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
        string destinationDirectory = "C:\\Installed\\Target";
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Lib\\a.bms");
        TestableBmsFile cleanupInstalledFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Lib\\c.bms");
        TestableBmsFile alreadyInstalledInPackage = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        alreadyInstalledInPackage.instl_dst = destinationDirectory;
        TestableBmsFile newFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Pkg1\\b.bms");
        newFile.instl_dst = destinationDirectory;
        TestableBmsFile cleanupOnlyFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Pkg2\\c.bms");
        cleanupOnlyFile.instl_dst = destinationDirectory;
        BMSPackage mixedPackage = new BMSPackage(new BMSFile[] { alreadyInstalledInPackage, newFile })
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        BMSPackage cleanupOnlyPackage = new BMSPackage(new BMSFile[] { cleanupOnlyFile })
        {
            path = "C:\\Pending\\Pkg2",
            delete_parent = false
        };
        PendingInstallBatchPlan plan = service.BuildEstimatedInstallBatchPlan(
            new[] { mixedPackage, cleanupOnlyPackage },
            new[] { mixedPackage, cleanupOnlyPackage },
            new[] { installedFile, cleanupInstalledFile },
            deletePendingPackageSourceAfterInstall: true,
            countComponentMoveTargets: (pkg, dst, excluded) => pkg.path == cleanupOnlyPackage.path ? 0 : 2);

        PendingInstallBatchResult result = service.ExecuteEstimatedInstallBatchPlan(
            plan,
            true,
            (installPackages, destinationDirectoryArg, deferredMaintenanceTargets, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall) =>
            {
                deferredInstalledPackages.AddRange(installPackages);
                foreach (BMSPackage installPackage in installPackages)
                {
                    deferredMaintenanceTargets.AddRange(installPackage.BMSFiles);
                }
                return new List<BMSPackage>();
            },
            (originalPackage, destinationDirectoryArg) => new BMSPackage(originalPackage.BMSFiles)
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
        BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
        TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Pkg1\\a.bms");
        pendingFile.instl_dst = "C:\\Installed\\Target";
        BMSPackage pendingPackage = new BMSPackage(new BMSFile[] { pendingFile })
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };

        ForceInstallBatchResult result = service.ForceInstallPackages(
            new[] { pendingPackage },
            new[] { pendingPackage },
            _ => false,
            (_, __) => new List<BMSPackage>());

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
            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg1");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart.bms"), "#PLAYER 1");
            BMSPackage pendingPackage = new BMSPackage(new BMSFile[0])
            {
                path = packageDirectoryPath,
                delete_parent = false
            };

            PendingPackageSourceDeletionResult result = service.DeletePendingPackageSources(
                new[] { pendingPackage },
                new[] { pendingPackage },
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
    public void SearchBmsFilesRecursively_SplitsIndependentChartsIntoSingleFilePackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_a.bms"), "#PLAYER 1\r\n#TITLE A\r\n#WAVAA sound_a.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_b.bms"), "#PLAYER 1\r\n#TITLE B\r\n#WAVAA sound_b.wav\r\n#00111:AA\r\n");

            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            List<BMSPackage> result = service.SearchBmsFilesRecursively(packageDirectoryPath, 0.6);

            Assert.AreEqual(2, result.Count);
            Assert.IsTrue(result.All((BMSPackage package) => File.Exists(package.path)));
        });
    }

    [TestMethod]
    public void SearchBmsFilesRecursivelyWithMetadata_MarksSplitDirectoryAsRegroupEligible()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "Pkg");
            Directory.CreateDirectory(packageDirectoryPath);
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_a.bms"), "#PLAYER 1\r\n#TITLE A\r\n#WAVAA sound_a.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(packageDirectoryPath, "chart_b.bms"), "#PLAYER 1\r\n#TITLE B\r\n#WAVAA sound_b.wav\r\n#00111:AA\r\n");

            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            BmsPackageDiscoveryResult result = service.SearchBmsFilesRecursivelyWithMetadata(packageDirectoryPath, 0.6);

            Assert.AreEqual(2, result.Packages.Count);
            CollectionAssert.AreEqual(new[] { packageDirectoryPath }, result.RegroupEligibleSourceDirectories);
        });
    }

    [TestMethod]
    public void SearchBmsFilesRecursivelyWithMetadata_MarksNestedSplitDirectoryAsRegroupEligible()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            string rootDirectoryPath = Path.Combine(tempDirectoryPath, "Root");
            string childDirectoryPath = Path.Combine(rootDirectoryPath, "Child");
            Directory.CreateDirectory(childDirectoryPath);
            File.WriteAllText(Path.Combine(childDirectoryPath, "chart_a.bms"), "#PLAYER 1\r\n#TITLE A\r\n#WAVAA sound_a.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(childDirectoryPath, "chart_b.bms"), "#PLAYER 1\r\n#TITLE B\r\n#WAVAA sound_b.wav\r\n#00111:AA\r\n");

            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            BmsPackageDiscoveryResult result = service.SearchBmsFilesRecursivelyWithMetadata(rootDirectoryPath, 0.6);

            Assert.AreEqual(2, result.Packages.Count);
            CollectionAssert.AreEqual(new[] { childDirectoryPath }, result.RegroupEligibleSourceDirectories);
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

            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                new[] { directoryPackagePath, singleFilePath },
                Array.Empty<BMSPackage>(),
                Array.Empty<BMSFile>(),
                Array.Empty<string>(),
                0.6,
                _ => false);

            Assert.AreEqual(2, result.DiscoveredPackages.Count);
            Assert.AreEqual(2, result.AutoInstallCandidates.Count);
            Assert.AreEqual(0, result.PendingPackagesToAdd.Count);
            Assert.AreEqual(0, result.RegroupEligibleSourceDirectories.Count);
            Assert.IsTrue(Directory.Exists(result.AutoInstallCandidates[0].path));
            Assert.IsTrue(result.AutoInstallCandidates.Any((BMSPackage package) => package.path.Equals(filePackageDirectoryPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(result.DiscoveryMs >= 0);
            Assert.IsTrue(result.ClassificationMs >= 0);
            Assert.IsTrue(result.TotalMs >= 0);
        });
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

            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            AutoInstallWorkflowResult result = service.PrepareAutoInstallWorkflow(
                new[] { firstFilePath, secondFilePath },
                Array.Empty<BMSPackage>(),
                Array.Empty<BMSFile>(),
                Array.Empty<string>(),
                0.6,
                _ => false);

            Assert.AreEqual(2, result.DiscoveredPackages.Count);
            Assert.AreEqual(0, result.RegroupEligibleSourceDirectories.Count);
            Assert.IsTrue(result.DiscoveredPackages.All((BMSPackage package) => File.Exists(package.path)));
        });
    }

    [TestMethod]
    public void ApplyAutoInstallWorkflow_ReturnsPendingAddsRemovesAndEstimateTargets()
    {
        BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
        BMSPackage removePackage = new BMSPackage { path = "C:\\Pending\\Remove" };
        BMSPackage pendingPackage = new BMSPackage { path = "C:\\Pending\\Keep" };
        BMSPackage successAutoInstallPackage = new BMSPackage { path = "C:\\Pending\\AutoOk" };
        BMSPackage failedAutoInstallPackage = new BMSPackage { path = "C:\\Pending\\AutoNg" };
        AutoInstallWorkflowResult workflow = new AutoInstallWorkflowResult();
        workflow.PendingPackagesToRemove.Add(removePackage);
        workflow.PendingPackagesToAdd.Add(pendingPackage);
        workflow.AutoInstallCandidates.Add(successAutoInstallPackage);
        workflow.AutoInstallCandidates.Add(failedAutoInstallPackage);

        AutoInstallApplyResult result = service.ApplyAutoInstallWorkflow(
            workflow,
            keepInstallablePackagesPending: false,
            canAutoInstallImmediately: true,
            packages => new List<BMSPackage> { failedAutoInstallPackage });

        CollectionAssert.AreEqual(new[] { removePackage }, result.PendingPackagesToRemove);
        CollectionAssert.AreEqual(new[] { pendingPackage, failedAutoInstallPackage }, result.PendingPackagesToAdd);
        CollectionAssert.AreEqual(new[] { successAutoInstallPackage }, result.AutoInstalledPackages);
        CollectionAssert.AreEqual(new[] { failedAutoInstallPackage }, result.AutoInstallFailures);
        CollectionAssert.AreEqual(new[] { pendingPackage, failedAutoInstallPackage }, result.EstimateTargets);
        CollectionAssert.AreEquivalent(new[] { removePackage.path }, result.InstallRowsToDelete);
        CollectionAssert.AreEquivalent(new[] { pendingPackage.path, failedAutoInstallPackage.path }, result.InstallRowsToUpsert.Select((BMSPackage pkg) => pkg.path).ToArray());
        Assert.IsTrue(result.InstallMs >= 0);
        Assert.IsTrue(result.ApplyMs >= 0);
        Assert.IsTrue(result.TotalMs >= 0);
    }

    [TestMethod]
    public void InstallPackages_ReturnsRegisteredPackagesAndTimingBreakdown()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Installed\\chart.bms");
        BMSPackage package = new BMSPackage(new BMSFile[] { file })
        {
            path = "C:\\Pending\\Pkg1",
            delete_parent = false
        };
        List<BMSFile> songUpserts = new List<BMSFile>();
        List<BMSFile> maintenanceTargets = new List<BMSFile>();
        List<BMSFile> zeroNoteTargets = new List<BMSFile>();
        List<BMSFile> scoreTargets = new List<BMSFile>();
        List<BMSFile> applyTargets = new List<BMSFile>();

        PackageInstallExecutionResult result = service.InstallPackages(
            new[] { package },
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
        Assert.IsTrue(result.MoveMs >= 0);
        Assert.IsTrue(result.SongDbMs >= 0);
        Assert.IsTrue(result.MaintenanceMs >= 0);
        Assert.IsTrue(result.ZeroNoteMs >= 0);
        Assert.IsTrue(result.ScoreMs >= 0);
        Assert.IsTrue(result.ApplyMs >= 0);
        Assert.IsTrue(result.TotalMs >= 0);
    }

    [TestMethod]
    public void ExecuteInstalledOnlyResourceOverwrite_CategorizesCleanupInstallAndMissingCases()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
        BMSPackage cleanupPackage = new BMSPackage(new BMSFile[] { CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\Cleanup\\a.bms") })
        {
            path = "C:\\Pending\\Cleanup",
            delete_parent = false
        };
        BMSPackage installPackage = new BMSPackage(new BMSFile[] { CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\Install\\b.bms") })
        {
            path = "C:\\Pending\\Install",
            delete_parent = false
        };
        BMSPackage missingDestinationPackage = new BMSPackage(new BMSFile[] { CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\Missing\\c.bms") })
        {
            path = "C:\\Pending\\Missing",
            delete_parent = false
        };

        PendingResourceOverwriteExecutionResult result = service.ExecuteInstalledOnlyResourceOverwrite(
            new[] { cleanupPackage, installPackage, missingDestinationPackage, new BMSPackage { path = "C:\\Pending\\Unknown" } },
            new[] { cleanupPackage, installPackage, missingDestinationPackage },
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
            string chartPath = Path.Combine(sourceDirectoryPath, "chart.bms");
            string resourcePath = Path.Combine(sourceDirectoryPath, "readme.txt");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Move\r\n");
            File.WriteAllText(resourcePath, "resource");

            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            TestableBmsFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            BMSPackage package = new BMSPackage(new BMSFile[] { chart })
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
            Assert.AreEqual(Path.Combine(destinationDirectoryPath, "chart.bms"), package.BMSFiles[0].path);
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "chart.bms")));
            Assert.IsTrue(File.Exists(Path.Combine(destinationDirectoryPath, "readme.txt")));
            Assert.IsFalse(Directory.Exists(sourceDirectoryPath));
        });
    }

    [TestMethod]
    public void DeletePendingFiles_DeletesWholePackageDirectoryWhenSelectionCoversPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            string packageDirectoryPath = Path.Combine(tempDirectoryPath, "PendingPkg");
            Directory.CreateDirectory(packageDirectoryPath);
            string chartPath = Path.Combine(packageDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n");
            TestableBmsFile chart = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            BMSPackage package = new BMSPackage(new BMSFile[] { chart })
            {
                path = packageDirectoryPath,
                delete_parent = false
            };

            PendingFileDeletionResult result = service.DeletePendingFiles(
                new[] { chart },
                new[] { package },
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
    public void RenamePendingFileExtensions_ReturnsRenamedDuplicateDeletedAndFailedFiles()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            BmsLibraryLibraryFileOperationsService fileOperationService = new BmsLibraryLibraryFileOperationsService();
            RealFileMutationService fileMutationService = new RealFileMutationService();
            string renameSourcePath = Path.Combine(tempDirectoryPath, "rename_me.bms");
            string duplicateSourcePath = Path.Combine(tempDirectoryPath, "duplicate.bms");
            string duplicateDestinationPath = Path.Combine(tempDirectoryPath, "duplicate.bme");
            string failureSourcePath = Path.Combine(tempDirectoryPath, "failure.bms");
            File.WriteAllText(renameSourcePath, "rename");
            File.WriteAllText(duplicateSourcePath, "same");
            File.WriteAllText(duplicateDestinationPath, "same");
            File.WriteAllText(failureSourcePath, "failure");
            TestableBmsFile renameFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", renameSourcePath);
            TestableBmsFile duplicateFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", duplicateSourcePath);
            TestableBmsFile failureFile = CreateFile("cccccccccccccccccccccccccccccccc", failureSourcePath);
            duplicateFile.SetHash(fileOperationService.TryComputeFileMd5ForPath(duplicateSourcePath));

            PendingExtensionRenameResult result = service.RenamePendingFileExtensions(
                new[] { renameFile, duplicateFile, failureFile },
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
        });
    }

    [TestMethod]
    public void MovePackageFiles_DeletesProtectedSourceWhenSuffixedCandidateHasSameHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryDirectory(delegate (string tempDirectoryPath)
        {
            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            RealFileMutationService fileMutationService = new RealFileMutationService();
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
            BMSPackage package = new BMSPackage(new BMSFile[0])
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
            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            RealFileMutationService fileMutationService = new RealFileMutationService();
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
            BMSPackage package = new BMSPackage(new BMSFile[0])
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
            BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
            RealFileMutationService fileMutationService = new RealFileMutationService();
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
            BMSPackage package = new BMSPackage(new BMSFile[0])
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };

            using (FileStream lockStream = new FileStream(unavailableCandidatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
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

    private static TestableBmsFile CreateFile(string hash, string path)
    {
        TestableBmsFile file = new TestableBmsFile
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
        BMSFile sourceChart = BMSFile.CreateBMSFileFromFile(sourceChartPath);
        BMSPackage package = new BMSPackage(new[] { sourceChart })
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

    private static bool ExecuteSingleChartParentDeleteMove(SafeDeleteMoveSetup setup, HashSet<string> existingHashes, out List<string> logs)
    {
        BmsLibraryPackageInstallService service = new BmsLibraryPackageInstallService();
        RealFileMutationService fileMutationService = new RealFileMutationService();
        List<string> localLogs = new List<string>();
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

        public BMSPackage Package { get; set; } = null!;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
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
