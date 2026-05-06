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
            Assert.IsTrue(strictWarningFile.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
            StringAssert.Contains(strictWarningFile.WarningTooltipText, "WAV");
            Assert.IsFalse(strictWarningFile.Warnings.Contains(ChartWarningKind.SingleBmsFile));
            Assert.AreEqual(string.Empty, normalFile.WarningDigestText);
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
            BMSPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            BMSPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            BMSFile firstPendingFile = firstPackage.BMSFiles.Single();
            firstPendingFile.InstallDestinationSuggestions = new[] { destinationDirectoryPath };
            firstPendingFile.SetWarning(ChartWarningKind.InstallEstimationLowConfidence, BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationLowConfidence);

            library.BMSFiles = new List<BMSFile> { BMSFile.CreateBMSFileFromFile(installedFilePath) };
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            BMSPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            foreach (BMSFile regroupedFile in regroupedPackage.BMSFiles)
            {
                Assert.AreEqual("Installed Title", regroupedFile.InstallDestinationTitle);
                Assert.AreEqual("Installed Artist", regroupedFile.InstallDestinationArtist);
            }
            Assert.AreEqual(1, firstPendingFile.InstallDestinationSuggestions.Count);
            Assert.AreEqual(destinationDirectoryPath, firstPendingFile.InstallDestinationSuggestions.Single());
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
    public void TryRegroupPendingPackagesForSourceDirectories_SkipsDeferredEstimatePackages()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageDeferred");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageDeferred");
            BMSPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            BMSPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            firstPackage.DeferredEstimateReason = PendingEstimateDeferredReason.HealthySourceBaseline;

            library.BMSFiles = new List<BMSFile>();
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
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingFile.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(pendingFile.HasLowConfidenceInstallWarning);
            Assert.IsTrue(pendingFile.Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous));
            StringAssert.Contains(pendingFile.WarningTooltipText, BeMusicSeeker.Properties.Resources.Warning_InstallEstimationAmbiguousPrefix);
            StringAssert.Contains(pendingFile.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationAmbiguous);
            StringAssert.Contains(pendingFile.WarningTooltipText, candidateADirectoryPath);
            StringAssert.Contains(pendingFile.WarningTooltipText, candidateADirectoryPath);
            StringAssert.Contains(pendingFile.WarningTooltipText, candidateBDirectoryPath);
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

            BMSFile pendingInstalledA = BMSFile.CreateBMSFileFromFile(pendingInstalledAPath);
            BMSFile pendingInstalledB = BMSFile.CreateBMSFileFromFile(pendingInstalledBPath);
            BMSFile pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingInstalledA, pendingInstalledB, pendingMissing })
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(installedAPath),
                BMSFile.CreateBMSFileFromFile(installedBPath)
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.InstalledDestinationResolveFailed, pendingPackage.DeferredEstimateReason);
            Assert.IsTrue(pendingInstalledA.Warnings.Contains(ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(pendingInstalledB.Warnings.Contains(ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissing.instl_dst));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissing.InstallDestinationTitle));
            Assert.AreEqual(0, pendingMissing.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingMissing.HasLowConfidenceInstallWarning);
            Assert.IsTrue(pendingMissing.Warnings.Contains(ChartWarningKind.InstalledDestinationResolveFailed));
            StringAssert.Contains(pendingMissing.WarningTooltipText, BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationResolveFailed);
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

            BMSFile pendingInstalledA = BMSFile.CreateBMSFileFromFile(pendingInstalledAPath);
            BMSFile pendingInstalledB = BMSFile.CreateBMSFileFromFile(pendingInstalledBPath);
            BMSFile pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingInstalledA, pendingInstalledB, pendingMissing })
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(installedAPath),
                BMSFile.CreateBMSFileFromFile(installedBPath)
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            Assert.AreEqual(installedBDirectoryPath, pendingMissing.instl_dst);
            Assert.AreEqual(0, pendingMissing.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingMissing.Warnings.Contains(ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissing.Warnings.Contains(ChartWarningKind.InstalledDestinationResolveFailed));
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

            BMSFile pendingInstalledA = BMSFile.CreateBMSFileFromFile(pendingInstalledAPath);
            BMSFile pendingInstalledB = BMSFile.CreateBMSFileFromFile(pendingInstalledBPath);
            BMSFile pendingMissing = BMSFile.CreateBMSFileFromFile(pendingMissingPath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingInstalledA, pendingInstalledB, pendingMissing })
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(installedAPath),
                BMSFile.CreateBMSFileFromFile(installedBPath)
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissing.instl_dst));
            CollectionAssert.AreEquivalent(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissing.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(pendingMissing.HasLowConfidenceInstallWarning);
            Assert.IsTrue(pendingMissing.Warnings.Contains(ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissing.Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous));
            StringAssert.Contains(pendingMissing.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_InstalledDestinationAmbiguous);
            StringAssert.Contains(pendingMissing.WarningTooltipText, BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationAmbiguous.Split('\n')[0]);
            StringAssert.Contains(pendingMissing.WarningTooltipText, installedADirectoryPath);
            StringAssert.Contains(pendingMissing.WarningTooltipText, installedBDirectoryPath);
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

            BMSFile pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFile })
            {
                path = pendingFilePath,
                delete_parent = true
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA1.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateADirectoryPath, "candidateA2.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB1.bms")),
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateBDirectoryPath, "candidateB2.bms"))
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(candidateBDirectoryPath, pendingFile.instl_dst);
            Assert.AreEqual("Target Song", pendingFile.InstallDestinationTitle);
            Assert.AreEqual("Artist", pendingFile.InstallDestinationArtist);
            Assert.IsFalse(pendingFile.HasLowConfidenceInstallWarning);
            Assert.AreEqual(0, pendingFile.InstallDestinationSuggestions.Count);
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

                Assert.AreEqual(candidateADirectoryPath, pendingFile.instl_dst);
                Assert.AreEqual("Target Song", pendingFile.InstallDestinationTitle);
                Assert.AreEqual("Artist", pendingFile.InstallDestinationArtist);
                Assert.IsTrue(pendingFile.HasLowConfidenceInstallWarning);
                CollectionAssert.AreEqual(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingFile.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(pendingFile.Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous));
                StringAssert.Contains(pendingFile.WarningTooltipText, candidateADirectoryPath);
                StringAssert.Contains(pendingFile.WarningTooltipText, candidateBDirectoryPath);
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

            BMSFile pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFile })
            {
                path = pendingFilePath,
                delete_parent = true
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateDirectoryPath, "candidate.bms"))
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath, candidateDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.instl_dst));
            Assert.AreEqual("Completely Different", pendingFile.InstallDestinationTitle);
            Assert.AreEqual("Another Artist", pendingFile.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { candidateDirectoryPath }, pendingFile.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(pendingFile.HasLowConfidenceInstallWarning);
            Assert.IsTrue(pendingFile.Warnings.Contains(ChartWarningKind.InstallEstimationMetadataMismatch));
            StringAssert.Contains(pendingFile.WarningTooltipText, BeMusicSeeker.Properties.Resources.Warning_InstallEstimationMetadataMismatchPrefix);
            StringAssert.Contains(pendingFile.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationMetadataMismatch);
            StringAssert.Contains(pendingFile.WarningTooltipText, candidateDirectoryPath);
            StringAssert.Contains(pendingFile.WarningTooltipText, candidateDirectoryPath);
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
            Assert.AreEqual(string.Empty, pendingFile.WarningDigestText);
        });
    }

    [TestMethod]
    public void SearchMergeDestination_PackageResolvedPathUpdatesRepresentativeMetadata()
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

            BMSFile pendingFileA = BMSFile.CreateBMSFileFromFile(pendingFileAPath);
            BMSFile pendingFileB = BMSFile.CreateBMSFileFromFile(pendingFileBPath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFileA, pendingFileB })
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(installedFileAPath),
                BMSFile.CreateBMSFileFromFile(installedFileBPath)
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchMergeDestination(pendingPackage);

            foreach (BMSFile pendingFile in pendingPackage.BMSFiles)
            {
                Assert.AreEqual(destinationDirectoryPath, pendingFile.instl_dst);
                Assert.AreEqual("Installed Title", pendingFile.InstallDestinationTitle);
                Assert.AreEqual("Installed Artist", pendingFile.InstallDestinationArtist);
            }
        });
    }

    [TestMethod]
    public void SearchMergeDestination_ByFile_UsesExternalCandidateOnly()
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

            BMSFile pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFile })
            {
                path = pendingFilePath,
                delete_parent = true
            };
            library.BMSFiles = new List<BMSFile> { BMSFile.CreateBMSFileFromFile(Path.Combine(candidateDirectoryPath, "installed.bms")) };
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath, candidateDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchMergeDestination(new[] { pendingFile });

            Assert.AreEqual(candidateDirectoryPath, pendingFile.instl_dst);
            Assert.AreEqual("Installed Title", pendingFile.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", pendingFile.InstallDestinationArtist);
            Assert.AreEqual(0, pendingFile.InstallDestinationSuggestions.Count);
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
    public void SetPendingInstallDestination_AllowsStandaloneLibraryFileForFullScan()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Library", "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Library", "Destination");
            string sourceFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "source.bms", "#PLAYER 1\r\n#TITLE Source\r\n#ARTIST Test\r\n");
            string destinationFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "destination.bms", "#PLAYER 1\r\n#TITLE Destination Title\r\n#ARTIST Destination Artist\r\n");

            BMSFile sourceFile = BMSFile.CreateBMSFileFromFile(sourceFilePath);
            library.BMSFiles = new List<BMSFile>
            {
                sourceFile,
                BMSFile.CreateBMSFileFromFile(destinationFilePath)
            };

            bool succeeded = library.SetPendingInstallDestination(sourceFile, destinationDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(destinationDirectoryPath, sourceFile.instl_dst);
            Assert.AreEqual("Destination Title", sourceFile.InstallDestinationTitle);
            Assert.AreEqual("Destination Artist", sourceFile.InstallDestinationArtist);
            Assert.AreEqual(0, sourceFile.InstallDestinationSuggestions.Count);
            Assert.IsFalse(sourceFile.HasLowConfidenceInstallWarning);
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

            BMSFile sourceFile = BMSFile.CreateBMSFileFromFile(sourceFilePath);
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(destinationFilePath)
            };

            bool succeeded = library.SetPendingInstallDestination(sourceFile, destinationDirectoryPath);

            Assert.IsFalse(succeeded);
            Assert.IsNull(sourceFile.instl_dst);
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
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingFile.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(pendingFile.HasLowConfidenceInstallWarning);
            Assert.IsTrue(pendingFile.Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous));
            StringAssert.Contains(pendingFile.WarningTooltipText, BeMusicSeeker.Properties.Resources.Warning_InstallEstimationAmbiguousPrefix);
            StringAssert.Contains(pendingFile.WarningTooltipText, candidateADirectoryPath);
            StringAssert.Contains(pendingFile.WarningTooltipText, candidateBDirectoryPath);
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
            Assert.IsFalse(pendingFile.Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous));
            Assert.AreEqual(string.Empty, pendingFile.WarningDigestText);
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
            Assert.IsTrue(pendingFile.Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous));

            library.RemoveInstallDestination(new[] { pendingFile });

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.instl_dst));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.InstallDestinationArtist));
            Assert.AreEqual(0, pendingFile.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingFile.HasLowConfidenceInstallWarning);
            Assert.IsFalse(pendingFile.Warnings.Contains(ChartWarningKind.InstallEstimationAmbiguous));
            Assert.AreEqual(string.Empty, pendingFile.WarningDigestText);
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

            BMSFile pendingFile = BMSFile.CreateBMSFileFromFile(pendingFilePath);
            BMSPackage pendingPackage = new BMSPackage(new[] { pendingFile })
            {
                path = pendingFilePath,
                delete_parent = true
            };
            library.BMSFiles = new List<BMSFile>
            {
                BMSFile.CreateBMSFileFromFile(Path.Combine(candidateDirectoryPath, "candidate.bms"))
            };
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetPrivateField(library, "bmsFolderAllFileList", BuildDirectoryHashCache(sourceDirectoryPath, candidateDirectoryPath));
            SetPrivateField(library, "directoryResourceLookupCache", BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);
            Assert.IsTrue(pendingFile.Warnings.Contains(ChartWarningKind.InstallEstimationMetadataMismatch));

            library.RemoveInstallDestination(new[] { pendingFile });

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.instl_dst));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingFile.InstallDestinationArtist));
            Assert.AreEqual(0, pendingFile.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingFile.HasLowConfidenceInstallWarning);
            Assert.IsFalse(pendingFile.Warnings.Contains(ChartWarningKind.InstallEstimationMetadataMismatch));
            Assert.AreEqual(string.Empty, pendingFile.WarningDigestText);
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
            file.SetWarning(ChartWarningKind.SingleBmsFile, BeMusicSeeker.Properties.Resources.Warning_SingleBmsFile);
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
