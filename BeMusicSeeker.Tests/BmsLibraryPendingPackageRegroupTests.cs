using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPendingPackageRegroupTests
{
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_RegroupsSplitPackagesWhenPendingDestinationsMatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageA");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageA");
            BMSPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            BMSPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile>();
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(firstPackage);

            AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_RegroupsSplitPackages_ReinitializesSingleFileWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageWarningsA");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageWarningsA");
            BMSPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            BMSPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile>();
            ApplySingleFileWarnings(firstPackage, secondPackage);
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(firstPackage);

            BMSPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            Assert.IsTrue(regroupedPackage.BMSFiles.All((BMSFile file) => string.IsNullOrWhiteSpace(file.warning)));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_RegroupsSplitPackagesWhenInstalledAndPendingDestinationsMatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageB");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageB");
            string installedLibraryChartPath = CreateBmsFile(destinationDirectoryPath, "installed.bms", "Shared Installed");
            BMSPackage installedPendingPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "installed.bms", "Shared Installed"));
            BMSPackage newPendingPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "new.bms", "New Pending"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile> { BMSFile.CreateBMSFileFromFile(installedLibraryChartPath) };
            SeedPendingPackages(library, songDbPath, installedPendingPackage, newPendingPackage);

            library.SearchEstimatedInstallationDirectory(installedPendingPackage);

            AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_RegroupsSplitPackages_KeepsAlreadyInstalledWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageWarningsB");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageWarningsB");
            string installedLibraryChartPath = CreateBmsFile(destinationDirectoryPath, "installed.bms", "Shared Installed");
            BMSPackage installedPendingPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "installed.bms", "Shared Installed"));
            BMSPackage newPendingPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "new.bms", "New Pending"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile> { BMSFile.CreateBMSFileFromFile(installedLibraryChartPath) };
            ApplySingleFileWarnings(installedPendingPackage, newPendingPackage);
            SeedPendingPackages(library, songDbPath, installedPendingPackage, newPendingPackage);

            library.SearchEstimatedInstallationDirectory(installedPendingPackage);

            BMSPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            BMSFile installedFile = regroupedPackage.BMSFiles.Single((BMSFile file) => Path.GetFileName(file.path).Equals("installed.bms", StringComparison.OrdinalIgnoreCase));
            BMSFile newFile = regroupedPackage.BMSFiles.Single((BMSFile file) => Path.GetFileName(file.path).Equals("new.bms", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warning_AlreadyInstalled, installedFile.warning);
            Assert.IsTrue(string.IsNullOrWhiteSpace(newFile.warning));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_DoesNotRegroupWhenInstalledChartHasMultipleDirectories()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageC");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageC");
            string installedDirectoryPath1 = Path.Combine(tempRootPath, "Library", "Dir1");
            string installedDirectoryPath2 = Path.Combine(tempRootPath, "Library", "Dir2");
            string sharedFileName = "installed.bms";
            string title = "Shared Installed";
            string installedChartPath1 = CreateBmsFile(installedDirectoryPath1, sharedFileName, title);
            string installedChartPath2 = CreateBmsFile(installedDirectoryPath2, sharedFileName, title);
            BMSPackage installedPendingPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, sharedFileName, title));
            BMSPackage newPendingPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "new.bms", "New Pending"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(installedChartPath1),
                BMSFile.CreateBMSFileFromFile(installedChartPath2)
            };
            SeedPendingPackages(library, songDbPath, installedPendingPackage, newPendingPackage);

            library.SearchEstimatedInstallationDirectory(installedPendingPackage);

            Assert.AreEqual(2, library.BMSPackagesPending.Count);
            CollectionAssert.AreEquivalent(
                new[] { installedPendingPackage.path, newPendingPackage.path },
                LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_DoesNotRegroupWhenAnyPackageIsUnresolved()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageD");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageD");
            BMSPackage resolvedPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "resolved.bms", "Resolved"), destinationDirectoryPath);
            BMSPackage unresolvedPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "unresolved.bms", "Unresolved"));

            library.BMSFiles = new List<BMSFile>();
            SeedPendingPackages(library, songDbPath, resolvedPackage, unresolvedPackage);

            library.SearchEstimatedInstallationDirectory(resolvedPackage);

            Assert.AreEqual(2, library.BMSPackagesPending.Count);
            CollectionAssert.AreEquivalent(
                new[] { resolvedPackage.path, unresolvedPackage.path },
                LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_RegroupsSplitPackages_ReplacesSingleFileWarningWithStrictWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageWarningsC");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageWarningsC");
            BMSPackage strictWarningPackage = CreatePendingSingleFilePackage(
                CreateBmsFileWithContents(
                    sourceDirectoryPath,
                    "strict.bms",
                    "#PLAYER 1\r\n#TITLE Strict\r\n#ARTIST Test\r\n#WAVAA sound_missing.wav\r\n#00111:AA\r\n"),
                destinationDirectoryPath);
            BMSPackage normalPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "normal.bms", "Normal"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile>();
            ApplySingleFileWarnings(strictWarningPackage, normalPackage);
            SeedPendingPackages(library, songDbPath, strictWarningPackage, normalPackage);

            library.SearchEstimatedInstallationDirectory(strictWarningPackage);

            BMSPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            BMSFile strictWarningFile = regroupedPackage.BMSFiles.Single((BMSFile file) => Path.GetFileName(file.path).Equals("strict.bms", StringComparison.OrdinalIgnoreCase));
            BMSFile normalFile = regroupedPackage.BMSFiles.Single((BMSFile file) => Path.GetFileName(file.path).Equals("normal.bms", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(strictWarningFile.warning ?? string.Empty, "WAV");
            Assert.AreNotEqual(BeMusicSeeker.Properties.Resources.Warning_SingleBmsFile, strictWarningFile.warning);
            Assert.IsTrue(string.IsNullOrWhiteSpace(normalFile.warning));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_ByFile_RegroupsSplitPackagesWhenDestinationsMatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageE");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageE");
            BMSPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            BMSPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile>();
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(firstPackage.BMSFiles.Single(), asParallel: false, fixMode: false);

            AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    [TestMethod]
    public void SearchEstimatedInstallationDirectory_ByFile_RegroupsSplitPackages_ReinitializesWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageWarningsD");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageWarningsD");
            BMSPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            BMSPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile>();
            ApplySingleFileWarnings(firstPackage, secondPackage);
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(firstPackage.BMSFiles.Single(), asParallel: false, fixMode: false);

            BMSPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            Assert.IsTrue(regroupedPackage.BMSFiles.All((BMSFile file) => string.IsNullOrWhiteSpace(file.warning)));
        });
    }

    private static BMSPackage AssertRegroupedPendingPackage(BMSLibrary library, string expectedPackagePath, string expectedDestinationDirectory, int expectedFileCount)
    {
        Assert.AreEqual(1, library.BMSPackagesPending.Count);
        BMSPackage regroupedPackage = library.BMSPackagesPending.Single();
        Assert.AreEqual(expectedPackagePath, regroupedPackage.path);
        Assert.IsFalse(regroupedPackage.delete_parent);
        Assert.AreEqual(expectedFileCount, regroupedPackage.BMSFiles.Count);
        Assert.IsTrue(regroupedPackage.BMSFiles.All((BMSFile file) => string.Equals(file.instl_dst, expectedDestinationDirectory, StringComparison.OrdinalIgnoreCase)));
        return regroupedPackage;
    }

    private static void SeedPendingPackages(BMSLibrary library, string songDbPath, params BMSPackage[] packages)
    {
        library.BMSPackagesPending = CreatePackageCollection(packages);
        using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
        {
            songDb.CreateTable<LR2SongDBExtended.install>();
            foreach (BMSPackage package in packages)
            {
                songDb.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
            }
        }
    }

    private static string[] LoadInstallPaths(string songDbPath)
    {
        using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
        {
            songDb.CreateTable<LR2SongDBExtended.install>();
            return songDb.Query<InstallRowRecord>("SELECT path FROM install")
                .Select((InstallRowRecord row) => row.path)
                .OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static BMSPackage CreatePendingSingleFilePackage(string filePath, string installDestination = "")
    {
        BMSFile file = BMSFile.CreateBMSFileFromFile(filePath);
        file.instl_dst = string.IsNullOrWhiteSpace(installDestination) ? null : installDestination;
        return new BMSPackage(new[] { file })
        {
            path = filePath,
            delete_parent = true
        };
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

    private static void ApplySingleFileWarnings(params BMSPackage[] packages)
    {
        foreach (BMSFile file in (packages ?? Array.Empty<BMSPackage>()).Where((BMSPackage package) => package != null).SelectMany((BMSPackage package) => package.BMSFiles ?? new List<BMSFile>()).Where((BMSFile file) => file != null))
        {
            file.warning = BeMusicSeeker.Properties.Resources.Warning_SingleBmsFile;
        }
    }

    private static DispatcherCollection<BMSPackage> CreatePackageCollection(IEnumerable<BMSPackage> packages)
    {
        return new DispatcherCollection<BMSPackage>(new ObservableCollection<BMSPackage>((packages ?? Enumerable.Empty<BMSPackage>()).ToList()), Dispatcher.CurrentDispatcher);
    }

    private static void WithTemporaryLibrary(Action<string, string, BMSLibrary> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PendingRegroupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, Array.Empty<byte>());
        try
        {
            BMSLibrary library = new BMSLibrary(songDbPath, null!, null, null!, new RecordingDialogService());
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

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public System.Windows.MessageBoxResult Show(string messageBoxText, string caption, System.Windows.MessageBoxButton button, System.Windows.MessageBoxImage icon, System.Windows.MessageBoxResult defaultResult = System.Windows.MessageBoxResult.None)
        {
            return System.Windows.MessageBoxResult.OK;
        }
    }

    private sealed class InstallRowRecord
    {
        public string path { get; set; } = string.Empty;
    }
}
