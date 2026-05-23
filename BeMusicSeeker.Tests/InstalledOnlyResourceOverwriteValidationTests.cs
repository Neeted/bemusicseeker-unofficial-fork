using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// installed-only resource overwrite の導入先確認が、既存配置の厳密確認として動作することを検証します。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class InstalledOnlyResourceOverwriteValidationTests
{
    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_AllChartsUnderOneDirectory_Succeeds()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir = Path.Combine(tempRoot, "Installed", "PackageA");
            List<BMSFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir, "b.bme")),
                CreateInstalledFile("cccccccccccccccccccccccccccccccc", Path.Combine(installedDir, "c.pms"))
            ];

            ChartPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")),
                CreatePendingFile("cccccccccccccccccccccccccccccccc", Path.Combine(tempRoot, "Pending", "c.pms")));

            InstalledChartLookupIndexSnapshot snapshot = BuildInstalledHashToDirectoryMap(service, installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsTrue(resolution.Success);
            Assert.AreEqual(installedDir, resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.None, resolution.Reason);
        });
    }

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_PackageSplitAcrossDirectories_Skips()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<BMSFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir2, "b.bme"))
            ];

            ChartPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")));

            InstalledChartLookupIndexSnapshot snapshot = BuildInstalledHashToDirectoryMap(service, installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories, resolution.Reason);
        });
    }

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_ChartInstalledInMultipleDirectories_Skips()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<BMSFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir2, "a.bms"))
            ];

            ChartPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")));

            InstalledChartLookupIndexSnapshot snapshot = BuildInstalledHashToDirectoryMap(service, installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories, resolution.Reason);
        });
    }

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_MissingInstalledDirectory_Skips()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir = Path.Combine(tempRoot, "Installed", "Dir1");
            List<BMSFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms"))
            ];

            ChartPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")));

            InstalledChartLookupIndexSnapshot snapshot = BuildInstalledHashToDirectoryMap(service, installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.MissingInstallDestination, resolution.Reason);
        });
    }

    [TestMethod]
    public void FindChartWithMissingInstalledDirectory_ReturnsChartFile()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir = Path.Combine(tempRoot, "Installed", "Dir1");
            List<BMSFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms"))
            ];
            TestableBmsFile missingPendingFile = CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme"));
            ChartPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                missingPendingFile);

            InstalledChartLookupIndexSnapshot snapshot = BuildInstalledHashToDirectoryMap(service, installedFiles);
            ChartFile chart = BmsLibraryInstallEstimationService.FindChartWithMissingInstalledDirectory(package, snapshot);

            Assert.IsNotNull(chart);
            Assert.AreEqual(missingPendingFile.path, chart.Path);
            Assert.AreEqual(missingPendingFile.hash, chart.PrimaryLookupHash);
        });
    }

    [TestMethod]
    public void FindChartWithMultipleInstalledDirectories_ReturnsChartFile()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<BMSFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir2, "a.bms"))
            ];
            TestableBmsFile pendingFile = CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms"));
            ChartPackage package = CreatePendingPackage(pendingFile);

            InstalledChartLookupIndexSnapshot snapshot = BuildInstalledHashToDirectoryMap(service, installedFiles);
            ChartFile chart = BmsLibraryInstallEstimationService.FindChartWithMultipleInstalledDirectories(package, snapshot);

            Assert.IsNotNull(chart);
            Assert.AreEqual(pendingFile.path, chart.Path);
            Assert.AreEqual(pendingFile.hash, chart.PrimaryLookupHash);
        });
    }

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_PendingInstlDstDoesNotBypassSplitDetection()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<BMSFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir2, "b.bme"))
            ];

            TestableBmsFile pendingA = CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms"));
            TestableBmsFile pendingB = CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme"));
            ChartPackage package = CreatePendingPackage(
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingA, installedDir1),
                ChartPackageTestExtensions.CreateEntryWithInstallDestination(pendingB, installedDir1));

            InstalledChartLookupIndexSnapshot snapshot = BuildInstalledHashToDirectoryMap(service, installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories, resolution.Reason);
        });
    }

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_Md5PresentDoesNotFallBackToSha256()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir = Path.Combine(tempRoot, "Installed", "Dir1");
            TestableBmsFile installedFile = CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms"));
            installedFile.SetSha256(new string('b', 64));

            TestableBmsFile pendingFile = CreatePendingFile("cccccccccccccccccccccccccccccccc", Path.Combine(tempRoot, "Pending", "a.bms"));
            pendingFile.SetSha256(new string('b', 64));
            ChartPackage package = CreatePendingPackage(pendingFile);

            InstalledChartLookupIndexSnapshot snapshot = BuildInstalledHashToDirectoryMap(service, [installedFile]);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.MissingInstallDestination, resolution.Reason);
        });
    }

    [TestMethod]
    public void BuildInstalledHashToDirectoryMap_RebuildsAfterBmsFilesReplacement()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<BMSFile> installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms"))
            ];

            InstalledChartLookupIndexSnapshot firstSnapshot = BuildInstalledHashToDirectoryMap(service, installedFiles);
            CollectionAssert.AreEqual(new[] { installedDir1 }, BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByPrimaryHash(firstSnapshot, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

            installedFiles =
            [
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir2, "a.bms"))
            ];

            InstalledChartLookupIndexSnapshot secondSnapshot = BuildInstalledHashToDirectoryMap(service, installedFiles);
            CollectionAssert.AreEqual(new[] { installedDir2 }, BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByPrimaryHash(secondSnapshot, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        });
    }

    private static void WithWorkspace(Action<BmsLibraryInstallEstimationService, string> testAction)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InstalledDirIndexTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var service = new BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot.CreateCurrent(), 70);
            testAction(service, tempRoot);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    private static InstalledChartLookupIndexSnapshot BuildInstalledHashToDirectoryMap(BmsLibraryInstallEstimationService service, IEnumerable<BMSFile> installedFiles)
    {
        return service.BuildInstalledHashToDirectoryMap((installedFiles ?? [])
            .Where(file => file != null)
            .Select(file => ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false)));
    }

    private static TestableBmsFile CreateInstalledFile(string hash, string path)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(hash);
        return file;
    }

    private static TestableBmsFile CreatePendingFile(string hash, string path)
    {
        return CreateInstalledFile(hash, path);
    }

    private static ChartPackage CreatePendingPackage(params TestableBmsFile[] files)
    {
        ChartPackage package = ChartPackageTestExtensions.CreatePackage(files);
        package.path = files.FirstOrDefault()?.path ?? string.Empty;
        package.delete_parent = false;
        return package;
    }

    private static ChartPackage CreatePendingPackage(params PackageChartEntry[] entries)
    {
        ChartPackage package = ChartPackageTestExtensions.CreatePackage(entries);
        package.path = entries.FirstOrDefault()?.Chart?.Path ?? string.Empty;
        package.delete_parent = false;
        return package;
    }

    private static void DeleteDirectoryIfExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }
        foreach (string childFilePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(childFilePath, FileAttributes.Normal);
        }
        foreach (string childDirectoryPath in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(childDirectoryPath, FileAttributes.Normal);
        }
        File.SetAttributes(directoryPath, FileAttributes.Normal);
        Directory.Delete(directoryPath, recursive: true);
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
    }
}
