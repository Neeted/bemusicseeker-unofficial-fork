using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
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
    public void SearchEstimatedInstallationDirectory_DoesNotRegroupSplitPackagesWhenPendingDestinationsMatch()
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
            BMSPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            BMSPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile>();
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(firstPackage.BMSFiles.Single(), asParallel: false, fixMode: false);

            AssertPendingPackagePaths(library, firstPackage.path, secondPackage.path);
            CollectionAssert.AreEquivalent(new[] { firstPackage.path, secondPackage.path }, LoadInstallPaths(songDbPath));
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
            BMSPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            BMSPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile>();
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
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

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            BMSPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            BMSFile strictWarningFile = regroupedPackage.BMSFiles.Single((BMSFile file) => Path.GetFileName(file.path).Equals("strict.bms", StringComparison.OrdinalIgnoreCase));
            BMSFile normalFile = regroupedPackage.BMSFiles.Single((BMSFile file) => Path.GetFileName(file.path).Equals("normal.bms", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(strictWarningFile.warning ?? string.Empty, "WAV");
            Assert.AreNotEqual(BeMusicSeeker.Properties.Resources.Warning_SingleBmsFile, strictWarningFile.warning);
            Assert.IsTrue(string.IsNullOrWhiteSpace(normalFile.warning));
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
            BMSPackage directoryPackage = new BMSPackage
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };
            BMSPackage splitPackage = CreatePendingSingleFilePackage(Path.Combine(sourceDirectoryPath, "a.bms"), destinationDirectoryPath);

            library.BMSFiles = new List<BMSFile>();
            SeedPendingPackages(library, songDbPath, directoryPackage, splitPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertPendingPackagePaths(library, sourceDirectoryPath, splitPackage.path);
            CollectionAssert.AreEquivalent(new[] { sourceDirectoryPath, splitPackage.path }, LoadInstallPaths(songDbPath));
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

            BMSFile pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFile })
            {
                path = pendingFilePath,
                delete_parent = true
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.instl_dst));
            Assert.AreEqual("Candidate A", pendingFile.InstallDestinationTitle);
            Assert.AreEqual("Artist A", pendingFile.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingFile.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(pendingFile.HasLowConfidenceInstallWarning);
            StringAssert.Contains(pendingFile.warning ?? string.Empty, BeMusicSeeker.Properties.Resources.Warning_InstallEstimationAmbiguousPrefix);
            StringAssert.Contains(pendingFile.warning ?? string.Empty, candidateADirectoryPath);
            StringAssert.Contains(pendingFile.warning ?? string.Empty, candidateBDirectoryPath);
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

            BMSFile pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFile })
            {
                path = pendingFilePath,
                delete_parent = true
            };
            library.BMSFiles = new List<BMSFile>();
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.instl_dst));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.InstallDestinationArtist));
            Assert.AreEqual(0, pendingFile.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingFile.HasLowConfidenceInstallWarning);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.warning));
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

            BMSFile pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFile })
            {
                path = pendingFilePath,
                delete_parent = true
            };
            library.BMSFiles = new List<BMSFile> { BMSFile.CreateBMSFileFromFile(installedFilePath) };
            SeedPendingPackages(library, songDbPath, pendingPackage);

            bool succeeded = library.SetPendingInstallDestination(pendingFile, destinationDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(destinationDirectoryPath, pendingFile.instl_dst);
            Assert.AreEqual("Installed Title", pendingFile.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", pendingFile.InstallDestinationArtist);
            Assert.AreEqual(0, pendingFile.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingFile.HasLowConfidenceInstallWarning);
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

            BMSFile pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFile })
            {
                path = pendingFilePath,
                delete_parent = true
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            bool succeeded = library.SetPendingInstallDestination(pendingFile, candidateBDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(candidateBDirectoryPath, pendingFile.instl_dst);
            Assert.AreEqual("Candidate B", pendingFile.InstallDestinationTitle);
            Assert.AreEqual("Artist B", pendingFile.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingFile.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(pendingFile.HasLowConfidenceInstallWarning);
            StringAssert.Contains(pendingFile.warning ?? string.Empty, BeMusicSeeker.Properties.Resources.Warning_InstallEstimationAmbiguousPrefix);
            StringAssert.Contains(pendingFile.warning ?? string.Empty, candidateADirectoryPath);
            StringAssert.Contains(pendingFile.warning ?? string.Empty, candidateBDirectoryPath);
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

            BMSFile pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFile })
            {
                path = pendingFilePath,
                delete_parent = true
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms")),
                BMSFile.CreateBMSFileFromFile(manualInstalledFilePath)
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath, manualDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath, manualDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            bool succeeded = library.SetPendingInstallDestination(pendingFile, manualDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(manualDirectoryPath, pendingFile.instl_dst);
            Assert.AreEqual("Manual Title", pendingFile.InstallDestinationTitle);
            Assert.AreEqual("Manual Artist", pendingFile.InstallDestinationArtist);
            Assert.AreEqual(0, pendingFile.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingFile.HasLowConfidenceInstallWarning);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.warning));
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

            BMSFile pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFile })
            {
                path = pendingFilePath,
                delete_parent = true
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);
            Assert.IsFalse(string.IsNullOrWhiteSpace(pendingFile.warning));

            library.RemoveInstallDestination(new[] { pendingFile });

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.instl_dst));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.InstallDestinationArtist));
            Assert.AreEqual(0, pendingFile.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingFile.HasLowConfidenceInstallWarning);
            Assert.AreEqual(string.Empty, pendingFile.warning ?? string.Empty);
        });
    }

    private static void InvokeRegroupForSourceDirectories(BMSLibrary library, params string[] sourceDirectoryPaths)
    {
        MethodInfo regroupMethod = typeof(BMSLibrary).GetMethod("TryRegroupPendingPackagesForSourceDirectoriesUnsafe", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(regroupMethod);
        regroupMethod.Invoke(library, new object[] { sourceDirectoryPaths });
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

    private static void AssertPendingPackagePaths(BMSLibrary library, params string[] expectedPaths)
    {
        Assert.AreEqual(expectedPaths.Length, library.BMSPackagesPending.Count);
        CollectionAssert.AreEquivalent(
            expectedPaths,
            library.BMSPackagesPending.Select((BMSPackage package) => package.path).ToArray());
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

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo fieldInfo = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo, fieldName);
        fieldInfo.SetValue(target, value);
    }

    private static BMSDirectoryFileNameHash BuildDirectoryHashCache(params string[] directories)
    {
        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        foreach (string directoryPath in directories.Where((string path) => !string.IsNullOrWhiteSpace(path)))
        {
            cache.AddDir(directoryPath);
        }
        return cache;
    }

    private static DirectoryResourceLookupCache BuildDirectoryLookupCache(params string[] directories)
    {
        DirectoryResourceLookupCache cache = new DirectoryResourceLookupCache();
        foreach (string directoryPath in directories.Where((string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
        {
            cache.AddDir(directoryPath, Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName));
        }
        return cache;
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
