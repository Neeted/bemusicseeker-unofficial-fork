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
            List<BMSFile> installedFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir, "b.bme")),
                CreateInstalledFile("cccccccccccccccccccccccccccccccc", Path.Combine(installedDir, "c.pms"))
            };

            BMSPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")),
                CreatePendingFile("cccccccccccccccccccccccccccccccc", Path.Combine(tempRoot, "Pending", "c.pms")));

            Dictionary<string, List<string>> snapshot = service.BuildInstalledHashToDirectoryMap(installedFiles);
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
            List<BMSFile> installedFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir2, "b.bme"))
            };

            BMSPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")));

            Dictionary<string, List<string>> snapshot = service.BuildInstalledHashToDirectoryMap(installedFiles);
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
            List<BMSFile> installedFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir2, "a.bms"))
            };

            BMSPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")));

            Dictionary<string, List<string>> snapshot = service.BuildInstalledHashToDirectoryMap(installedFiles);
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
            List<BMSFile> installedFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms"))
            };

            BMSPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")));

            Dictionary<string, List<string>> snapshot = service.BuildInstalledHashToDirectoryMap(installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.MissingInstallDestination, resolution.Reason);
        });
    }

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_PendingInstlDstDoesNotBypassSplitDetection()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<BMSFile> installedFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir2, "b.bme"))
            };

            TestableBmsFile pendingA = CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms"));
            TestableBmsFile pendingB = CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme"));
            pendingA.instl_dst = installedDir1;
            pendingB.instl_dst = installedDir1;
            BMSPackage package = CreatePendingPackage(pendingA, pendingB);

            Dictionary<string, List<string>> snapshot = service.BuildInstalledHashToDirectoryMap(installedFiles);
            InstalledOnlyPackageResolutionResult resolution = service.TryPrepareInstalledOnlyPackageDestination(package, snapshot);

            Assert.IsFalse(resolution.Success);
            Assert.IsNull(resolution.DestinationDirectory);
            Assert.AreEqual(InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories, resolution.Reason);
        });
    }

    [TestMethod]
    public void BuildInstalledHashToDirectoryMap_RebuildsAfterBmsFilesReplacement()
    {
        WithWorkspace(delegate (BmsLibraryInstallEstimationService service, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            List<BMSFile> installedFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms"))
            };

            Dictionary<string, List<string>> firstSnapshot = service.BuildInstalledHashToDirectoryMap(installedFiles);
            CollectionAssert.AreEqual(new[] { installedDir1 }, firstSnapshot["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]);

            installedFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir2, "a.bms"))
            };

            Dictionary<string, List<string>> secondSnapshot = service.BuildInstalledHashToDirectoryMap(installedFiles);
            CollectionAssert.AreEqual(new[] { installedDir2 }, secondSnapshot["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]);
        });
    }

    private static void WithWorkspace(Action<BmsLibraryInstallEstimationService, string> testAction)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InstalledDirIndexTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            BmsLibraryInstallEstimationService service = new BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot.CreateCurrent(), 70);
            testAction(service, tempRoot);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    private static TestableBmsFile CreateInstalledFile(string hash, string path)
    {
        TestableBmsFile file = new TestableBmsFile
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

    private static BMSPackage CreatePendingPackage(params TestableBmsFile[] files)
    {
        return new BMSPackage(files)
        {
            path = files.FirstOrDefault()?.path ?? string.Empty,
            delete_parent = false
        };
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
    }
}
