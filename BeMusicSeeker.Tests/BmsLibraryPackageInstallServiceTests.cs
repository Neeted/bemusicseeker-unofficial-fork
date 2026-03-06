using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
        Assert.AreEqual(2, result.DeferredInstalledPackages.Count);
        Assert.AreEqual(1, result.CleanupOnlySucceeded);
        Assert.AreEqual(0, result.CleanupOnlyFailed);
        Assert.AreEqual(1, result.CleanupOnlyMissingSource);
        Assert.AreEqual(1, result.DeferredMaintenanceTargets.Count);
        Assert.IsNull(alreadyInstalledInPackage.instl_dst);
        Assert.IsNull(newFile.instl_dst);
        Assert.IsNull(cleanupOnlyFile.instl_dst);
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

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }
    }
}
