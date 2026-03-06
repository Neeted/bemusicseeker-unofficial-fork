using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BeMusicSeeker;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// installed-only resource overwrite の導入先確認が、既存配置の厳密確認として動作することを検証します。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class InstalledOnlyResourceOverwriteValidationTests
{
    private static readonly MethodInfo buildInstalledHashToDirectoryMapMethod =
        typeof(BMSLibrary).GetMethod("BuildInstalledHashToDirectoryMap", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo tryPrepareInstalledOnlyPackageDestinationMethod =
        typeof(BMSLibrary).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single((MethodInfo method) => method.Name == "TryPrepareInstalledOnlyPackageDestination");

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_AllChartsUnderOneDirectory_Succeeds()
    {
        WithLibrary(delegate (BMSLibrary library, string tempRoot)
        {
            string installedDir = Path.Combine(tempRoot, "Installed", "PackageA");
            library.BMSFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir, "b.bme")),
                CreateInstalledFile("cccccccccccccccccccccccccccccccc", Path.Combine(installedDir, "c.pms"))
            };

            BMSPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")),
                CreatePendingFile("cccccccccccccccccccccccccccccccc", Path.Combine(tempRoot, "Pending", "c.pms")));

            Dictionary<string, List<string>> snapshot = BuildInstalledHashToDirectoryMap(library);
            (bool success, string destinationDir, string reason) = InvokeTryPrepareInstalledOnlyPackageDestination(library, package, snapshot);

            Assert.IsTrue(success);
            Assert.AreEqual(installedDir, destinationDir);
            Assert.AreEqual("None", reason);
        });
    }

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_PackageSplitAcrossDirectories_Skips()
    {
        WithLibrary(delegate (BMSLibrary library, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            library.BMSFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir2, "b.bme"))
            };

            BMSPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")));

            Dictionary<string, List<string>> snapshot = BuildInstalledHashToDirectoryMap(library);
            (bool success, string destinationDir, string reason) = InvokeTryPrepareInstalledOnlyPackageDestination(library, package, snapshot);

            Assert.IsFalse(success);
            Assert.IsNull(destinationDir);
            Assert.AreEqual("PackageHasSplitInstalledDirectories", reason);
        });
    }

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_ChartInstalledInMultipleDirectories_Skips()
    {
        WithLibrary(delegate (BMSLibrary library, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            library.BMSFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir2, "a.bms"))
            };

            BMSPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")));

            Dictionary<string, List<string>> snapshot = BuildInstalledHashToDirectoryMap(library);
            (bool success, string destinationDir, string reason) = InvokeTryPrepareInstalledOnlyPackageDestination(library, package, snapshot);

            Assert.IsFalse(success);
            Assert.IsNull(destinationDir);
            Assert.AreEqual("ChartHasMultipleInstalledDirectories", reason);
        });
    }

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_MissingInstalledDirectory_Skips()
    {
        WithLibrary(delegate (BMSLibrary library, string tempRoot)
        {
            string installedDir = Path.Combine(tempRoot, "Installed", "Dir1");
            library.BMSFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir, "a.bms"))
            };

            BMSPackage package = CreatePendingPackage(
                CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms")),
                CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme")));

            Dictionary<string, List<string>> snapshot = BuildInstalledHashToDirectoryMap(library);
            (bool success, string destinationDir, string reason) = InvokeTryPrepareInstalledOnlyPackageDestination(library, package, snapshot);

            Assert.IsFalse(success);
            Assert.IsNull(destinationDir);
            Assert.AreEqual("MissingInstallDestination", reason);
        });
    }

    [TestMethod]
    public void TryPrepareInstalledOnlyPackageDestination_PendingInstlDstDoesNotBypassSplitDetection()
    {
        WithLibrary(delegate (BMSLibrary library, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            library.BMSFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms")),
                CreateInstalledFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(installedDir2, "b.bme"))
            };

            TestableBmsFile pendingA = CreatePendingFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "a.bms"));
            TestableBmsFile pendingB = CreatePendingFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(tempRoot, "Pending", "b.bme"));
            pendingA.instl_dst = installedDir1;
            pendingB.instl_dst = installedDir1;
            BMSPackage package = CreatePendingPackage(pendingA, pendingB);

            Dictionary<string, List<string>> snapshot = BuildInstalledHashToDirectoryMap(library);
            (bool success, string destinationDir, string reason) = InvokeTryPrepareInstalledOnlyPackageDestination(library, package, snapshot);

            Assert.IsFalse(success);
            Assert.IsNull(destinationDir);
            Assert.AreEqual("PackageHasSplitInstalledDirectories", reason);
        });
    }

    [TestMethod]
    public void BuildInstalledHashToDirectoryMap_RebuildsAfterBmsFilesReplacement()
    {
        WithLibrary(delegate (BMSLibrary library, string tempRoot)
        {
            string installedDir1 = Path.Combine(tempRoot, "Installed", "Dir1");
            string installedDir2 = Path.Combine(tempRoot, "Installed", "Dir2");
            library.BMSFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir1, "a.bms"))
            };

            Dictionary<string, List<string>> firstSnapshot = BuildInstalledHashToDirectoryMap(library);
            CollectionAssert.AreEqual(new[] { installedDir1 }, firstSnapshot["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]);

            library.BMSFiles = new List<BMSFile>
            {
                CreateInstalledFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installedDir2, "a.bms"))
            };

            Dictionary<string, List<string>> secondSnapshot = BuildInstalledHashToDirectoryMap(library);
            CollectionAssert.AreEqual(new[] { installedDir2 }, secondSnapshot["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]);
        });
    }

    private static void WithLibrary(Action<BMSLibrary, string> testAction)
    {
        InitializeResourceStateForTests();
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InstalledDirIndexTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string sourceSongDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
        string tempSongDbPath = Path.Combine(tempRoot, "song.db");
        File.Copy(sourceSongDbPath, tempSongDbPath, overwrite: true);
        try
        {
            BMSLibrary library = new BMSLibrary(tempSongDbPath);
            testAction(library, tempRoot);
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    private static void InitializeResourceStateForTests()
    {
        PropertyInfo availableCulturesProperty = typeof(App).GetProperty("AvailableCultures", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        ReadOnlyDictionary<string, string> availableCultures = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            { "Default(日本語)", "ja-JP" }
        });
        availableCulturesProperty.SetValue(null, availableCultures);
        Resources.Culture = CultureInfo.GetCultureInfo("ja-JP");
    }

    private static Dictionary<string, List<string>> BuildInstalledHashToDirectoryMap(BMSLibrary library)
    {
        return (Dictionary<string, List<string>>)buildInstalledHashToDirectoryMapMethod.Invoke(library, null);
    }

    private static (bool success, string? destinationDir, string? reason) InvokeTryPrepareInstalledOnlyPackageDestination(BMSLibrary library, BMSPackage package, Dictionary<string, List<string>> snapshot)
    {
        object[] args = new object[] { package, snapshot, null, null };
        bool success = (bool)tryPrepareInstalledOnlyPackageDestinationMethod.Invoke(library, args);
        return (success, args[2] as string, args[3]?.ToString());
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
