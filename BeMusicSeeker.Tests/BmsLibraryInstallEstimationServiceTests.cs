using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BmsLibraryInstallEstimationServiceTests
{
    [TestMethod]
    public void InstallEstimationDegreeResolvers_UseProcessorCountMinusOneAndDoNotClampConfiguredPackages()
    {
        int expectedDefault = Math.Max(1, Environment.ProcessorCount - 1);

        Assert.AreEqual(expectedDefault, BmsLibraryInstallEstimationService.ResolveDefaultCandidateEvaluationDegree());
        Assert.AreEqual(expectedDefault, BmsLibraryInstallEstimationService.ResolveCandidateEvaluationDegree(asParallel: true));
        Assert.AreEqual(1, BmsLibraryInstallEstimationService.ResolveCandidateEvaluationDegree(asParallel: false));
        Assert.AreEqual(1, BmsLibraryInstallEstimationService.NormalizeCandidateEvaluationDegree(0));
        Assert.AreEqual(1, BmsLibraryInstallEstimationService.NormalizeCandidateEvaluationDegree(-10));
        Assert.AreEqual(16, BmsLibraryInstallEstimationService.NormalizeCandidateEvaluationDegree(16));

        Assert.AreEqual(expectedDefault, BMSLibrary.ResolveInstallEstimationDefaultDegree());
        Assert.AreEqual(expectedDefault, BMSLibrary.ResolvePendingInstallEstimateParallelPackageDegree(0));
        Assert.AreEqual(1, BMSLibrary.ResolvePendingInstallEstimateParallelPackageDegree(-10));
        Assert.AreEqual(1, BMSLibrary.ResolvePendingInstallEstimateParallelPackageDegree(1));
        Assert.AreEqual(8, BMSLibrary.ResolvePendingInstallEstimateParallelPackageDegree(8));
        Assert.AreEqual(16, BMSLibrary.ResolvePendingInstallEstimateParallelPackageDegree(16));
    }

    [TestMethod]
    public void EstimateInstallationDirectory_RecordsExplicitCandidateDegree()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Pending", "chart.bms"), "sound.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);
        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();

        InstallEstimationResult sequential = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);
        InstallEstimationResult defaultParallel = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: true,
            BmsInstallationEstimateMode.Normal);
        InstallEstimationResult explicitDegree = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            candidateEvaluationDegree: 16,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(1, sequential.CandidateEvaluationDegree);
        Assert.AreEqual(BmsLibraryInstallEstimationService.ResolveDefaultCandidateEvaluationDegree(), defaultParallel.CandidateEvaluationDegree);
        Assert.AreEqual(16, explicitDegree.CandidateEvaluationDegree);
    }

    [TestMethod]
    public void TryResolveInstalledDestinationFromPackage_PrefersDirectoryWithMostMatchingCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string dirA = Path.Combine("C:\\Installed", "DirA");
        string dirB = Path.Combine("C:\\Installed", "DirB");
        List<BMSFile> installedFiles = new List<BMSFile>
        {
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(dirA, "a.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(dirA, "b.bms")),
            CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine(dirB, "c.bms"))
        };
        TestableBmsFile pendingA = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\a.bms");
        TestableBmsFile pendingB = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\b.bms");
        TestableBmsFile pendingC = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\c.bms");
        BMSPackage package = new BMSPackage(new BMSFile[] { pendingA, pendingB, pendingC })
        {
            path = "C:\\Pending",
            delete_parent = false
        };

        InstalledDirectoryLookupResult result = service.TryResolveInstalledDestinationFromPackage(
            package,
            new List<BMSFile> { pendingC },
            service.BuildInstalledHashToDirectoryMap(installedFiles),
            new BMSDirectoryFileNameHash());

        Assert.AreEqual(dirA, result.InstallDirectory);
        Assert.AreEqual(3, result.MatchedHashCount);
        Assert.AreEqual(2, result.CandidateDirectoryCount);
        Assert.AreEqual(InstalledDirectoryResolveReason.None, result.Reason);
    }

    [TestMethod]
    public void TryResolveInstalledDestinationFromPackage_ReturnsMultipleCandidateDirectoriesForTopTie()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string dirA = Path.Combine("C:\\Installed", "DirA");
        string dirB = Path.Combine("C:\\Installed", "DirB");
        List<BMSFile> installedFiles = new List<BMSFile>
        {
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(dirA, "a.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(dirB, "b.bms"))
        };
        TestableBmsFile pendingA = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\a.bms");
        TestableBmsFile pendingB = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\b.bms");
        TestableBmsFile pendingMissing = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\missing.bms", "sound.wav");
        BMSPackage package = new BMSPackage(new BMSFile[] { pendingA, pendingB, pendingMissing })
        {
            path = "C:\\Pending",
            delete_parent = false
        };

        InstalledDirectoryLookupResult result = service.TryResolveInstalledDestinationFromPackage(
            package,
            new List<BMSFile> { pendingMissing },
            service.BuildInstalledHashToDirectoryMap(installedFiles),
            new BMSDirectoryFileNameHash());

        Assert.AreEqual(InstalledDirectoryResolveReason.MultipleCandidateDirectories, result.Reason);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(2, result.CandidateDirectoryCount);
        CollectionAssert.AreEqual(new[] { dirA, dirB }, result.CandidateDirectories.ToArray());
    }

    [TestMethod]
    public void EstimateInstallationDirectoryForCandidateDirectories_RequiresResourceIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);
            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = sourceDir,
                delete_parent = true
            };
            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });

            InstallEstimationResult result = service.EstimateInstallationDirectoryForCandidateDirectories(
                snapshot,
                new[] { candidateDir },
                directoryLookupCache: null,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.IsFalse(result.HasViableDestination);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.AreEqual("resource_index_unavailable", result.ConfidenceReason);
            Assert.AreEqual("candidate_limited_resource_index_unavailable", result.CandidateMode);
            Assert.AreEqual("resource_index_unavailable", result.CoarseFilterMode);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_MergeMode_SelectsExternalCandidateOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string mergeDir = Path.Combine(tempRoot, "merge");
            string soundDir = Path.Combine(sourceDir, "sound");
            string mergeSoundDir = Path.Combine(mergeDir, "sound");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(mergeDir);
            Directory.CreateDirectory(soundDir);
            Directory.CreateDirectory(mergeSoundDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(soundDir, "01.wav"), "src");
            File.WriteAllText(Path.Combine(mergeSoundDir, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(mergeSoundDir, "01.wav"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), Path.Combine("sound", "00.wav"), Path.Combine("sound", "01.wav"));
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 1), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = sourceDir,
                delete_parent = true
            };
            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(mergeDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms", Path.Combine("sound", "01.wav") });
            lookupCache.AddDir(mergeDir, new[] { Path.Combine("sound", "00.wav"), Path.Combine("sound", "01.wav") });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.MergeCandidateOnly);

            Assert.AreEqual(mergeDir, result.DestinationDirectory);
            Assert.AreEqual(1, result.CandidateDirectoryCount);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_UsesFallbackCandidateExpansion_WhenNameHashFilterMisses()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(candidateDir, "sound.wav"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDirHashed(sourceDir, Array.Empty<uint>());
            cache.AddDirHashed(candidateDir, Array.Empty<uint>());
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(candidateDir, new[] { "sound.wav" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            string debugSummary = (result.ResourceSummary ?? string.Empty) + " || " + (result.TopCandidateSummary ?? string.Empty) + " || " + (result.SelectedCandidateSummary ?? string.Empty);
            Assert.AreEqual(1, result.CandidateDirectoryCount, debugSummary);
            Assert.AreEqual(candidateDir, result.DestinationDirectory, debugSummary);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_DoesNotFallbackToAllCandidates_WhenBroadFilterFindsNoCandidates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(candidateDir, "other.wav"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(candidateDir, new[] { "other.wav" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(0, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(0, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(0, result.CandidateDirectoryCountAfterHashFilter);
            Assert.AreEqual(0, result.CandidateDirectoryCount);
            Assert.IsFalse(result.HasViableDestination);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_RequiresTwoAudioMatches_WhenAudioRefsAreTwoOrMore()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string weakCandidateDir = Path.Combine(tempRoot, "weak");
            string strongCandidateDir = Path.Combine(tempRoot, "strong");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(weakCandidateDir);
            Directory.CreateDirectory(strongCandidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(weakCandidateDir, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(weakCandidateDir, "title.png"), "dst");
            File.WriteAllText(Path.Combine(strongCandidateDir, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(strongCandidateDir, "01.wav"), "dst");
            File.WriteAllText(Path.Combine(strongCandidateDir, "title.png"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav");
            file.BGAfiles = new HashSet<string>(new[] { "title.png" }, StringComparer.OrdinalIgnoreCase);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(weakCandidateDir);
            cache.AddDir(strongCandidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(weakCandidateDir, new[] { "00.wav", "title.png" });
            lookupCache.AddDir(strongCandidateDir, new[] { "00.wav", "01.wav", "title.png" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(2, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(1, result.CandidateDirectoryCount);
            Assert.AreEqual(2, result.AudioMinimumMatchRequired);
            Assert.AreEqual(strongCandidateDir, result.DestinationDirectory);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_AllowsSingleAudioMatch_WhenAudioRefsIsOne()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(candidateDir, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateDir, "title.png"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav");
            file.BGAfiles = new HashSet<string>(new[] { "title.png" }, StringComparer.OrdinalIgnoreCase);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(candidateDir, new[] { "00.wav", "title.png" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(1, result.AudioMinimumMatchRequired);
            Assert.AreEqual(candidateDir, result.DestinationDirectory);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_IsSkipped_WhenAudioRefsIsZero()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(candidateDir, "bg.png"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"));
            file.BGAfiles = new HashSet<string>(new[] { "bg.png" }, StringComparer.OrdinalIgnoreCase);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 0, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(candidateDir, new[] { "bg.png" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(0, result.AudioMinimumMatchRequired);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(candidateDir, result.DestinationDirectory);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_AudioHealthGate_DropsCandidateBelowThreshold_WhenPackageUnionStillInsufficient()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(candidateDir, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateDir, "01.wav"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav", "02.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(candidateDir, new[] { "00.wav", "01.wav" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(0, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(0, result.CandidateDirectoryCount);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_AudioHealthGate_UsesBundledResourcesInNormalMode_ButNotInMergeMode()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string candidateDir = Path.Combine(tempRoot, "Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "02.wav"), "bundled");
            File.WriteAllText(Path.Combine(candidateDir, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateDir, "01.wav"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav", "02.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 1), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = sourceDir,
                delete_parent = true
            };
            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms", "02.wav" });
            lookupCache.AddDir(candidateDir, new[] { "00.wav", "01.wav" });

            InstallEstimationResult normalResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            InstallEstimationResult mergeResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.MergeCandidateOnly);

            Assert.AreEqual(1, normalResult.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(candidateDir, normalResult.DestinationDirectory);

            Assert.AreEqual(0, mergeResult.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(0, mergeResult.CandidateDirectoryCount);
            Assert.AreEqual("no_viable_destination_below_threshold", mergeResult.ConfidenceReason);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_DropsCandidateSatisfiedOnlyByBundledResources()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string candidateDir = Path.Combine(tempRoot, "Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(candidateDir, "title.png"), "visual");
            for (int i = 0; i < 71; i++)
            {
                File.WriteAllText(Path.Combine(sourceDir, i.ToString("D2") + ".wav"), "bundled");
            }

            TestableBmsFile file = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine(sourceDir, "chart.bms"),
                Enumerable.Range(0, 100).Select((int i) => i.ToString("D2") + ".wav").ToArray());
            file.BGAfiles = new HashSet<string>(new[] { "title.png" }, StringComparer.OrdinalIgnoreCase);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 100, wavExisting: 71), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = sourceDir,
                delete_parent = true
            };
            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" }.Concat(Enumerable.Range(0, 71).Select((int i) => i.ToString("D2") + ".wav")).ToArray());
            lookupCache.AddDir(candidateDir, new[] { "title.png" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(0, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(0, result.CandidateDirectoryCount);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_RequiresMatchedAboveSeventyPercentBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string weakCandidateDir = Path.Combine(tempRoot, "weak");
            string strongCandidateDir = Path.Combine(tempRoot, "strong");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(weakCandidateDir);
            Directory.CreateDirectory(strongCandidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");

            string[] allAudio = Enumerable.Range(0, 100).Select((int i) => i.ToString("D3") + ".wav").ToArray();
            foreach (string fileName in allAudio.Take(70))
            {
                File.WriteAllText(Path.Combine(weakCandidateDir, fileName), "weak");
            }
            foreach (string fileName in allAudio.Take(71))
            {
                File.WriteAllText(Path.Combine(strongCandidateDir, fileName), "strong");
            }

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), allAudio);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 100, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(weakCandidateDir);
            cache.AddDir(strongCandidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(weakCandidateDir, allAudio.Take(70).ToArray());
            lookupCache.AddDir(strongCandidateDir, allAudio.Take(71).ToArray());

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(2, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(1, result.CandidateDirectoryCount);
            Assert.AreEqual(strongCandidateDir, result.SelectedCandidate?.DirectoryPath);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_RelativePathExactCanRescueViability()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            Directory.CreateDirectory(Path.Combine(candidateDir, "a"));
            Directory.CreateDirectory(Path.Combine(candidateDir, "b"));
            Directory.CreateDirectory(Path.Combine(candidateDir, "c"));
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(candidateDir, "a", "00.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateDir, "b", "01.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateDir, "c", "01.wav"), "dst");

            TestableBmsFile file = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine(sourceDir, "chart.bms"),
                Path.Combine("a", "00.wav"),
                Path.Combine("b", "01.wav"),
                Path.Combine("c", "01.wav"));
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(candidateDir, new[] { Path.Combine("a", "00.wav"), Path.Combine("b", "01.wav"), Path.Combine("c", "01.wav") });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(1, result.CandidateDirectoryCount);
            Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
            Assert.AreEqual(3, result.SelectedCandidate?.AudioMatched);
            Assert.AreEqual(3, result.SelectedCandidate?.AudioExactMatched);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_UsesCachedHashesWithoutRuntimeDirectoryEnumeration()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Temp", "source");
        string candidateDir = Path.Combine("C:\\Library", "candidate");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "keysound.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(candidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("keysound.wav") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
        lookupCache.AddDir(candidateDir, new[] { "keysound.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(candidateDir, result.DestinationDirectory);
        Assert.AreEqual(1, result.CandidateDirectoryCount);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_CachelessPath_DoesNotApplyAudioHealthGate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Temp", "source");
        string candidateDir = Path.Combine("C:\\Library", "candidate");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav", "02.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(candidateDir, new[]
        {
            BMSDirectoryFileNameHash.GetFileNameHash("00.wav"),
            BMSDirectoryFileNameHash.GetFileNameHash("01.wav")
        });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
        lookupCache.AddDir(candidateDir, new[] { "00.wav", "01.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(0, result.CandidateDirectoryCountAfterAudioGate);
        Assert.AreEqual(0, result.CandidateDirectoryCount);
        Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
    }

    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_DirectoryPackageBuildsBundledResources()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string soundDir = Path.Combine(sourceDir, "sound");
            string imageDir = Path.Combine(sourceDir, "image");
            string movieDir = Path.Combine(sourceDir, "movie");
            Directory.CreateDirectory(soundDir);
            Directory.CreateDirectory(imageDir);
            Directory.CreateDirectory(movieDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart1.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "chart2.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(soundDir, "00.wav"), "audio");
            File.WriteAllText(Path.Combine(imageDir, "bg.png"), "image");
            File.WriteAllText(Path.Combine(movieDir, "pv.mp4"), "movie");

            TestableBmsFile primary = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart1.bms"), Path.Combine("sound", "00.wav"));
            primary.BGAfiles = new HashSet<string>(new[] { Path.Combine("image", "bg.png") }, StringComparer.OrdinalIgnoreCase);
            TestableBmsFile secondary = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(sourceDir, "chart2.bms"), Path.Combine("sound", "00.wav"));
            primary.SetMaintenanceInfo(CreateMaintenanceInfo(primary, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);
            secondary.SetMaintenanceInfo(CreateMaintenanceInfo(secondary, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { primary, secondary })
            {
                path = sourceDir,
                delete_parent = true
            };

            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new BMSFile[] { primary, secondary });
            uint expectedAudioRelativePathHash = BMSDirectoryFileNameHash.GetLookupHash(ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(ChartResourcePathNormalizer.NormalizeRelativePathForLookup(sourceDir, Path.Combine(soundDir, "00.wav"))));
            uint expectedImageRelativePathHash = BMSDirectoryFileNameHash.GetLookupHash(ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(ChartResourcePathNormalizer.NormalizeRelativePathForLookup(sourceDir, Path.Combine(imageDir, "bg.png"))));
            uint expectedMovieRelativePathHash = BMSDirectoryFileNameHash.GetLookupHash(ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(ChartResourcePathNormalizer.NormalizeRelativePathForLookup(sourceDir, Path.Combine(movieDir, "pv.mp4"))));

            Assert.AreEqual(2, snapshot.ChartCount);
            ChartResourceSnapshot expectedDefinedResources = ChartResourceSnapshot.CreateAggregate(new BMSFile[] { primary, secondary });
            CollectionAssert.AreEquivalent(expectedDefinedResources.AudioBaseNameHashes.ToArray(), snapshot.DefinedResources.AudioBaseNameHashes.ToArray());
            CollectionAssert.AreEquivalent(expectedDefinedResources.VisualBaseNameHashes.ToArray(), snapshot.DefinedResources.VisualBaseNameHashes.ToArray());
            CollectionAssert.AreEquivalent(expectedDefinedResources.MovieBaseNameHashes.ToArray(), snapshot.DefinedResources.MovieBaseNameHashes.ToArray());
            Assert.AreEqual(1, snapshot.BundledAudioCount);
            Assert.AreEqual(1, snapshot.BundledImageCount);
            Assert.AreEqual(1, snapshot.BundledMovieCount);
            CollectionAssert.Contains(snapshot.BundledResources.AudioRelativePathHashArray, expectedAudioRelativePathHash);
            CollectionAssert.Contains(snapshot.BundledResources.ImageRelativePathHashArray, expectedImageRelativePathHash);
            CollectionAssert.Contains(snapshot.BundledResources.MovieRelativePathHashArray, expectedMovieRelativePathHash);
        });
    }

    [TestMethod]
    public void EvaluateSourceBaseline_ChartResourceOverloadMatchesPackageSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string soundDir = Path.Combine(sourceDir, "sound");
            Directory.CreateDirectory(soundDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(soundDir, "00.wav"), "audio");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), Path.Combine("sound", "00.wav"));
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = sourceDir,
                delete_parent = true
            };

            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });
            BmsLibraryInstallEstimationService.SourceBaselineEvaluation snapshotBaseline = service.EvaluateSourceBaseline(snapshot);
            BmsLibraryInstallEstimationService.SourceBaselineEvaluation resourceBaseline = service.EvaluateSourceBaseline(
                snapshot.DefinedResources,
                snapshot.SourceDirectory,
                snapshot.BundledResources,
                snapshot.SourceCandidateResources);

            Assert.AreEqual(snapshotBaseline.PrimaryHealth, resourceBaseline.PrimaryHealth);
            Assert.AreEqual(snapshotBaseline.IsViableDestination, resourceBaseline.IsViableDestination);
            Assert.AreEqual(snapshotBaseline.Summary, resourceBaseline.Summary);
        });
    }

    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_BuildsTargetMetadataProfileFromDominantGroup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            Directory.CreateDirectory(sourceDir);
            string primaryPath = Path.Combine(sourceDir, "primary.bms");
            string secondaryPath = Path.Combine(sourceDir, "secondary.bms");
            File.WriteAllText(primaryPath, "#PLAYER 1\r\n#TITLE Song Name (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA 00.wav\r\n#00111:AA\r\n");
            File.WriteAllText(secondaryPath, "#PLAYER 1\r\n#TITLE Song Name\r\n#ARTIST Artist\r\n#WAVAA 00.wav\r\n#00111:AA\r\n");

            BMSFile primary = BMSFile.CreateBMSFileFromFile(primaryPath);
            BMSFile secondary = BMSFile.CreateBMSFileFromFile(secondaryPath);

            BMSPackage package = new BMSPackage(new BMSFile[] { primary, secondary })
            {
                path = sourceDir,
                delete_parent = true
            };

            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new BMSFile[] { primary, secondary });

            Assert.AreEqual("song name", snapshot.TargetMetadataProfile.DominantNormalizedTitle);
            Assert.AreEqual("artist", snapshot.TargetMetadataProfile.DominantNormalizedArtist);
            Assert.AreEqual(2, snapshot.TargetMetadataProfile.TitleSupportCount);
            Assert.AreEqual(2, snapshot.TargetMetadataProfile.ArtistSupportCount);
        });
    }

    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_FilePackageDoesNotIncludeSiblingBundledResources()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "00.wav"), "audio");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = file.path,
                delete_parent = false
            };

            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });

            Assert.AreEqual(0, snapshot.BundledAudioCount);
            Assert.AreEqual(0, snapshot.BundledImageCount);
            Assert.AreEqual(0, snapshot.BundledMovieCount);
            Assert.AreEqual(sourceDir, snapshot.SourceDirectory);
            Assert.AreEqual(1, snapshot.SourceCandidateResources.AudioFileNameHashCount);
        });
    }

    [TestMethod]
    public void BMSPackage_PathPackage_DoesNotPrebuildSourceSurface_WhenBmsFilesAreRequested()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithPendingPackageSourceScanSetting(enabled: false, delegate
        {
            WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string soundDir = Path.Combine(sourceDir, "sound");
                Directory.CreateDirectory(soundDir);
                File.WriteAllText(Path.Combine(sourceDir, "chart1.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDir, "chart2.bme"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(soundDir, "00.wav"), "audio");

                BMSPackage package = new BMSPackage
                {
                    path = sourceDir,
                    delete_parent = true
                };

                List<BMSFile> firstFiles = package.BMSFiles;
                List<BMSFile> secondFiles = package.BMSFiles;
                PackageInstallEstimationSnapshot firstSnapshot = package.GetOrBuildInstallEstimationSnapshot(firstFiles);
                PackageInstallEstimationSnapshot secondSnapshot = package.GetOrBuildInstallEstimationSnapshot(firstFiles);

                Assert.AreSame(firstFiles, secondFiles);
                Assert.AreEqual(2, firstFiles.Count);
                Assert.AreEqual(2, firstSnapshot.ChartCount);
                Assert.AreEqual(sourceDir, firstSnapshot.SourceDirectory);
                Assert.AreEqual("fast", firstSnapshot.SourceSurfaceScanBackend);
                Assert.IsFalse(firstSnapshot.SourceSurfaceCacheHit);
                Assert.IsTrue(firstSnapshot.SourceSurfaceTrackedFileCount >= 3);
                Assert.AreEqual(1, firstSnapshot.BundledAudioCount);
                Assert.IsTrue(secondSnapshot.SourceSurfaceCacheHit);
            });
        });
    }

    [TestMethod]
    public void BMSPackage_PathPackage_InvalidatesChartDiscoveryAndSourceSurfaceSnapshots_WhenPathChanges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithPendingPackageSourceScanSetting(enabled: false, delegate
        {
            WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDirA = Path.Combine(tempRoot, "PathPackageA");
                string sourceDirB = Path.Combine(tempRoot, "PathPackageB");
                Directory.CreateDirectory(sourceDirA);
                Directory.CreateDirectory(sourceDirB);
                File.WriteAllText(Path.Combine(sourceDirA, "a.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDirB, "b.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDirB, "01.wav"), "audio");

                BMSPackage package = new BMSPackage
                {
                    path = sourceDirA,
                    delete_parent = true
                };

                List<BMSFile> filesFromA = package.BMSFiles;
                PackageInstallEstimationSnapshot snapshotA = package.GetOrBuildInstallEstimationSnapshot(filesFromA);

                package.path = sourceDirB;
                List<BMSFile> filesFromB = package.BMSFiles;
                PackageInstallEstimationSnapshot snapshotB = package.GetOrBuildInstallEstimationSnapshot(filesFromB);
                PackageInstallEstimationSnapshot cachedSnapshotB = package.GetOrBuildInstallEstimationSnapshot(filesFromB);

                Assert.AreEqual(1, filesFromA.Count);
                Assert.AreEqual(1, filesFromB.Count);
                Assert.AreNotSame(filesFromA, filesFromB);
                Assert.AreEqual(Path.Combine(sourceDirB, "b.bms"), filesFromB[0].path);
                Assert.AreEqual(sourceDirA, snapshotA.SourceDirectory);
                Assert.AreEqual(sourceDirB, snapshotB.SourceDirectory);
                Assert.IsFalse(snapshotA.SourceSurfaceCacheHit);
                Assert.IsFalse(snapshotB.SourceSurfaceCacheHit);
                Assert.IsTrue(cachedSnapshotB.SourceSurfaceCacheHit);
                Assert.AreEqual("fast", snapshotB.SourceSurfaceScanBackend);
            });
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_PackageUnionPrefersDestinationWithBaseResourcesMissingFromSourcePackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string expectedDir = Path.Combine(tempRoot, "ExpectedInstall");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(expectedDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "02.wav"), "bundled");
            File.WriteAllText(Path.Combine(expectedDir, "00.wav"), "existing-base");
            File.WriteAllText(Path.Combine(expectedDir, "01.wav"), "existing-base");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav", "02.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 1), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = sourceDir,
                delete_parent = true
            };
            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(expectedDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms", "02.wav" });
            lookupCache.AddDir(expectedDir, new[] { "00.wav", "01.wav" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            string debugSummary = (result.ResourceSummary ?? string.Empty) + " || " + (result.TopCandidateSummary ?? string.Empty) + " || " + (result.SelectedCandidateSummary ?? string.Empty);
            Assert.AreEqual(expectedDir, result.DestinationDirectory, debugSummary);
            Assert.AreEqual(expectedDir, result.SelectedCandidate?.DirectoryPath, debugSummary);
            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence, debugSummary);
            Assert.IsTrue(result.ShouldAutoApplyDestination, debugSummary);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_BelowThresholdReturnsHighWithoutDestination()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string weakDir = Path.Combine(tempRoot, "WeakCandidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(weakDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(weakDir, "00.wav"), "candidate");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = sourceDir,
                delete_parent = true
            };
            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(weakDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(weakDir, new[] { "00.wav" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
            Assert.IsFalse(result.HasViableDestination);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
            Assert.AreEqual(0, result.SuggestedDestinationDirectories.Count);
        });
    }

    [TestMethod]
    public void ValidatePendingInstallDestination_ReturnsResolvedDirectoryForPendingPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string pendingDirectoryPath = Path.Combine(tempRoot, "pending");
            string installDirectoryPath = Path.Combine(tempRoot, "install");
            Directory.CreateDirectory(pendingDirectoryPath);
            Directory.CreateDirectory(installDirectoryPath);
            TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(pendingDirectoryPath, "chart.bms"));
            BMSPackage pendingPackage = new BMSPackage(new BMSFile[] { pendingFile })
            {
                path = pendingDirectoryPath,
                delete_parent = false
            };

            PendingInstallDestinationSelectionResult result = service.ValidatePendingInstallDestination(
                pendingFile,
                new[] { pendingPackage },
                new[] { installDirectoryPath },
                installDirectoryPath);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(installDirectoryPath, result.ValidatedDestinationDirectory);
            CollectionAssert.AreEqual(new[] { pendingFile }, result.TargetFiles);
        });
    }

    [TestMethod]
    public void CorrectInstallationDirectory_ClearsSameDirectorySuggestion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Music", "FolderA", "chart.bms"));

        service.CorrectInstallationDirectory(new[] { file }, delegate (BMSFile target)
        {
            target.instl_dst = Path.Combine("C:\\Music", "FolderA");
        });

        Assert.IsNull(file.instl_dst);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_AutoAppliesOnlyImprovedUniqueCandidate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Music", "Current");
        string candidateDir = Path.Combine("C:\\Music", "Candidate");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(candidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
        lookupCache.AddDir(candidateDir, new[] { "sound.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.ReinstallCorrection);

        Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
        Assert.AreEqual("reinstall_single_improved_candidate", result.ConfidenceReason);
        Assert.IsTrue(result.ShouldAutoApplyDestination);
        Assert.AreEqual(candidateDir, result.DestinationDirectory);
        Assert.AreEqual(0, result.SourceBaselinePrimaryHealth);
        Assert.AreEqual(100, result.SelectedCandidatePrimaryHealth);
        Assert.AreEqual("candidate_only_source_excluded", result.CandidateMode);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_DoesNotAutoApplyWhenCandidateDoesNotImproveHealth()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Music", "Current");
        string candidateDir = Path.Combine("C:\\Music", "Candidate");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms"), BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
        cache.AddDirHashed(candidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "chart.bms", "sound.wav" });
        lookupCache.AddDir(candidateDir, new[] { "sound.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.ReinstallCorrection);

        Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
        Assert.AreEqual(InstallEstimationLowConfidenceKind.ReinstallNotImproved, result.LowConfidenceKind);
        Assert.AreEqual("reinstall_candidate_not_improved", result.ConfidenceReason);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
        CollectionAssert.AreEqual(new[] { candidateDir }, result.SuggestedDestinationDirectories.ToArray());
    }

    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_DoesNotUseSourceBundledResources()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Music", "Current");
        string candidateDir = Path.Combine("C:\\Music", "Candidate");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms"), BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
        cache.AddDirHashed(candidateDir, Array.Empty<uint>());
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "chart.bms", "sound.wav" });
        lookupCache.AddDir(candidateDir, Array.Empty<string>());

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.ReinstallCorrection);

        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
        Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
        Assert.AreEqual(100, result.SourceBaselinePrimaryHealth);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_DoesNotAutoApplyMultipleViableCandidatesEvenWhenAmbiguousSettingEnabled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            BmsLibraryInstallEstimationService service = CreateService();
            string sourceDir = Path.Combine("C:\\Music", "Current");
            string candidateADir = Path.Combine("C:\\Music", "CandidateA");
            string candidateBDir = Path.Combine("C:\\Music", "CandidateB");
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
            cache.AddDirHashed(candidateADir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
            cache.AddDirHashed(candidateBDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(candidateADir, new[] { "sound.wav" });
            lookupCache.AddDir(candidateBDir, new[] { "sound.wav" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.ReinstallCorrection);

            Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
            Assert.AreEqual(InstallEstimationLowConfidenceKind.AmbiguousCandidates, result.LowConfidenceKind);
            Assert.AreEqual("reinstall_multiple_viable_candidates", result.ConfidenceReason);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.AreEqual(candidateADir, result.DestinationDirectory);
            CollectionAssert.AreEqual(new[] { candidateADir, candidateBDir }, result.SuggestedDestinationDirectories.ToArray());
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_MetadataMismatchDoesNotAutoApply()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "Current");
            string candidateDir = Path.Combine(tempRoot, "Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Base Artist obj: Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(candidateDir, "installed.bms"), "#PLAYER 1\r\n#TITLE Completely Different\r\n#ARTIST Another Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDir, "sound.wav"), "dst");

            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(candidateDir, new[] { "installed.bms", "sound.wav" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.ReinstallCorrection,
                null,
                delegate (string directoryPath)
                {
                    return string.Equals(directoryPath, candidateDir, StringComparison.OrdinalIgnoreCase)
                        ? InstallEstimationMetadataNormalizer.BuildProfile(new[] { ("Completely Different", "Another Artist", Path.Combine(candidateDir, "installed.bms")) })
                        : InstallEstimationMetadataProfile.Empty;
                });

            Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
            Assert.AreEqual(InstallEstimationLowConfidenceKind.MetadataMismatch, result.LowConfidenceKind);
            Assert.AreEqual("single_viable_candidate_metadata_mismatch", result.ConfidenceReason);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
            CollectionAssert.AreEqual(new[] { candidateDir }, result.SuggestedDestinationDirectories.ToArray());
        });
    }

    [TestMethod]
    public void BuildInstalledHashToDirectoryMap_IncludesBmsonMd5AndSha256Directories()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string installDir = Path.Combine("C:\\Installed", "Bmson");
        LR2SongDBExtended.bmson_song bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine(installDir, "chart.bmson"),
            folder = installDir,
            title = "Title",
            artist = "Artist",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256 = new string('b', 64)
        };

        InstalledChartDirectoryIndexSnapshot result = service.BuildInstalledHashToDirectoryMap(Array.Empty<BMSFile>(), new[] { bmsonSong });

        CollectionAssert.AreEqual(new[] { installDir }, result.Md5Directories[bmsonSong.md5]);
        CollectionAssert.AreEqual(new[] { installDir }, result.Sha256Directories[bmsonSong.sha256]);
        CollectionAssert.AreEqual(new[] { installDir }, result.KnownChartDirectories.ToList());
    }

    [TestMethod]
    public void EstimateInstallationDirectory_BmsonUsesCommonHealthBasedSearch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            string bmsonPath = Path.Combine(sourceDir, "chart.bmson");
            File.WriteAllText(bmsonPath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Title\",\"artist\":\"Artist\"},\"sound_channels\":[{\"name\":\"keysound.wav\",\"notes\":[]}],\"lines\":[{\"y\":0}]}");
            File.WriteAllText(Path.Combine(candidateDir, "keysound.wav"), "dummy");

            PendingChartEntry pending = PendingChartEntry.CreateFromFilePath(bmsonPath);
            pending.SetMaintenanceInfo(CreateMaintenanceInfo(pending, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bmson" });
            lookupCache.AddDir(candidateDir, new[] { "keysound.wav" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { pending },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(candidateDir, result.DestinationDirectory);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_BmsonSampleLikeOggSetSelectsMatchingDirectory()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "bmson");
            string candidateDir = Path.Combine(tempRoot, "piece_of_mine");
            string decoyDir = Path.Combine(tempRoot, "decoy");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            Directory.CreateDirectory(decoyDir);

            List<string> oggNames = Enumerable.Range(1, 77).Select((int index) => string.Format("sound{0:00}.ogg", index)).ToList();
            StringBuilder soundChannelsBuilder = new StringBuilder();
            for (int i = 0; i < oggNames.Count; i++)
            {
                if (i > 0)
                {
                    soundChannelsBuilder.Append(",");
                }
                soundChannelsBuilder.Append("{\"name\":\"").Append(oggNames[i]).Append("\",\"notes\":[]}");
            }
            string bmsonPath = Path.Combine(sourceDir, "_circ_double_hard.bmson");
            File.WriteAllText(
                bmsonPath,
                "{"
                + "\"version\":\"1.0.0\","
                + "\"info\":{\"title\":\"Title\",\"artist\":\"Artist\",\"preview_music\":\"preview.ogg\",\"banner_image\":\"banner.png\"},"
                + "\"sound_channels\":[" + soundChannelsBuilder + "],"
                + "\"bga\":{\"bga_header\":[{\"id\":1,\"name\":\"cover.jpg\"}]},"
                + "\"lines\":[{\"y\":0}]"
                + "}");

            foreach (string oggName in oggNames)
            {
                File.WriteAllText(Path.Combine(candidateDir, oggName), "candidate");
            }
            File.WriteAllText(Path.Combine(candidateDir, "preview.ogg"), "candidate");
            File.WriteAllText(Path.Combine(candidateDir, "banner.png"), "candidate");
            File.WriteAllText(Path.Combine(candidateDir, "cover.jpg"), "candidate");
            for (int i = 0; i < 10; i++)
            {
                File.WriteAllText(Path.Combine(decoyDir, oggNames[i]), "decoy");
            }

            PendingChartEntry pending = PendingChartEntry.CreateFromFilePath(bmsonPath);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            cache.AddDir(decoyDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "_circ_double_hard.bmson" });
            lookupCache.AddDir(candidateDir, oggNames.Concat(new[] { "preview.ogg", "banner.png", "cover.jpg" }));
            lookupCache.AddDir(decoyDir, oggNames.Take(10));

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { pending },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            string debugSummary = (result.ResourceSummary ?? string.Empty) + " || " + (result.TopCandidateSummary ?? string.Empty) + " || " + (result.SelectedCandidateSummary ?? string.Empty);
            Assert.AreEqual(candidateDir, result.DestinationDirectory, debugSummary);
            Assert.AreEqual(1, result.CandidateDirectoryCount, debugSummary);
            StringAssert.Contains(result.ResourceSummary ?? string.Empty, "audioRefs=78");
            StringAssert.Contains(result.SelectedCandidateSummary ?? string.Empty, candidateDir, debugSummary);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_ReturnsLowConfidenceWhenOnlyDirectoryPathBreaksTie()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateADir = Path.Combine("C:\\Installed", "A");
        string candidateBDir = Path.Combine("C:\\Installed", "B");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(candidateADir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
        cache.AddDirHashed(candidateBDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
        lookupCache.AddDir(candidateADir, new[] { "sound.wav" });
        lookupCache.AddDir(candidateBDir, new[] { "sound.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.AreEqual(candidateADir, result.DestinationDirectory);
        Assert.AreEqual(candidateADir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(candidateBDir, result.SecondCandidate?.DirectoryPath);
        Assert.AreEqual("tie_on_viable_non_source_candidates", result.ConfidenceReason);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_AmbiguousStrongMetadataWithSetting_AutoAppliesFirstCandidate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "Pending", "Source");
                string candidateADir = Path.Combine(tempRoot, "Installed", "A");
                string candidateBDir = Path.Combine(tempRoot, "Installed", "B");
                Directory.CreateDirectory(sourceDir);
                Directory.CreateDirectory(candidateADir);
                Directory.CreateDirectory(candidateBDir);
                string chartPath = Path.Combine(sourceDir, "chart.bms");
                File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");

                BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
                file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

                BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
                cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
                cache.AddDirHashed(candidateADir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
                cache.AddDirHashed(candidateBDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
                DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
                lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
                lookupCache.AddDir(candidateADir, new[] { "sound.wav" });
                lookupCache.AddDir(candidateBDir, new[] { "sound.wav" });

                Dictionary<string, InstallEstimationMetadataProfile> metadataProfiles = new Dictionary<string, InstallEstimationMetadataProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    [candidateADir] = InstallEstimationMetadataNormalizer.BuildProfile(new[] { ("Target Song", "Artist", Path.Combine(candidateADir, "a.bms")) }),
                    [candidateBDir] = InstallEstimationMetadataNormalizer.BuildProfile(new[] { ("Target Song", "Artist", Path.Combine(candidateBDir, "b.bms")) })
                };

                InstallEstimationResult result = service.EstimateInstallationDirectory(
                    new[] { file },
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    lookupCache,
                    asParallel: false,
                    BmsInstallationEstimateMode.Normal,
                    null,
                    delegate (string directoryPath)
                    {
                        return metadataProfiles.TryGetValue(directoryPath, out InstallEstimationMetadataProfile profile)
                            ? profile
                            : InstallEstimationMetadataProfile.Empty;
                    });

                Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
                Assert.AreEqual(InstallEstimationLowConfidenceKind.AmbiguousCandidates, result.LowConfidenceKind);
                Assert.IsTrue(result.ShouldAutoApplyDestination);
                Assert.AreEqual(candidateADir, result.DestinationDirectory);
                Assert.AreEqual("tie_on_viable_non_source_candidates_auto_apply_enabled", result.ConfidenceReason);
                CollectionAssert.AreEqual(new[] { candidateADir, candidateBDir }, result.SuggestedDestinationDirectories.ToArray());
            });
        });
    }

    [TestMethod]
    public void NormalizeTitleForTieBreak_StripsKnownSubtitleDelimiters()
    {
        Assert.AreEqual("title", InstallEstimationMetadataNormalizer.NormalizeTitleForTieBreak("Title (Another)"));
        Assert.AreEqual("title", InstallEstimationMetadataNormalizer.NormalizeTitleForTieBreak("Title - Another"));
        Assert.AreEqual("title-another", InstallEstimationMetadataNormalizer.NormalizeTitleForTieBreak("Title-Another"));
        Assert.AreEqual("(wrapped title)", InstallEstimationMetadataNormalizer.NormalizeTitleForTieBreak("(Wrapped Title)"));
    }

    [TestMethod]
    public void NormalizeArtistForTieBreak_StripsDiffSuffixesAndSlashSuffixes()
    {
        Assert.AreEqual(string.Empty, InstallEstimationMetadataNormalizer.NormalizeArtistForTieBreak("obj: diffuser"));
        Assert.AreEqual("artist", InstallEstimationMetadataNormalizer.NormalizeArtistForTieBreak("Artist obj: Diff"));
        Assert.AreEqual("artist", InstallEstimationMetadataNormalizer.NormalizeArtistForTieBreak("Artist ; notes: Diff"));
        Assert.AreEqual("artist", InstallEstimationMetadataNormalizer.NormalizeArtistForTieBreak("Artist / Diff"));
        Assert.AreEqual("key note team", InstallEstimationMetadataNormalizer.NormalizeArtistForTieBreak("Key Note Team"));
        Assert.AreEqual("object", InstallEstimationMetadataNormalizer.NormalizeArtistForTieBreak("Object"));
    }

    [TestMethod]
    public void ClassifyTitleMatch_DetectsExactStrongWeakAndNone()
    {
        Assert.AreEqual(InstallEstimationMetadataTitleMatchKind.Exact, InstallEstimationMetadataNormalizer.ClassifyTitleMatch("Target Song (Another)", "Target Song"));
        Assert.AreEqual(InstallEstimationMetadataTitleMatchKind.FuzzyStrong, InstallEstimationMetadataNormalizer.ClassifyTitleMatch("Target Song 7K", "Target Song 7Key"));
        Assert.AreEqual(InstallEstimationMetadataTitleMatchKind.Weak, InstallEstimationMetadataNormalizer.ClassifyTitleMatch("Alpha Beat", "Alpha Best"));
        Assert.AreEqual(InstallEstimationMetadataTitleMatchKind.None, InstallEstimationMetadataNormalizer.ClassifyTitleMatch("Target Song", "Completely Different"));
    }

    [TestMethod]
    public void EstimateInstallationDirectory_MetadataTieBreakPromotesMatchingCandidateToHigh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "Pending", "Source");
            string candidateADir = Path.Combine(tempRoot, "Installed", "A");
            string candidateBDir = Path.Combine(tempRoot, "Installed", "B");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateADir);
            Directory.CreateDirectory(candidateBDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");

            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
            cache.AddDirHashed(candidateADir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
            cache.AddDirHashed(candidateBDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
            lookupCache.AddDir(candidateADir, new[] { "sound.wav" });
            lookupCache.AddDir(candidateBDir, new[] { "sound.wav" });

            Dictionary<string, InstallEstimationMetadataProfile> metadataProfiles = new Dictionary<string, InstallEstimationMetadataProfile>(StringComparer.OrdinalIgnoreCase)
            {
                [candidateADir] = InstallEstimationMetadataNormalizer.BuildProfile(new[] { ("Another Song", "Someone", Path.Combine(candidateADir, "a.bms")) }),
                [candidateBDir] = InstallEstimationMetadataNormalizer.BuildProfile(new[] { ("Target Song", "Artist", Path.Combine(candidateBDir, "b.bms")) })
            };

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal,
                null,
                delegate (string directoryPath)
                {
                    return metadataProfiles.TryGetValue(directoryPath, out InstallEstimationMetadataProfile profile)
                        ? profile
                        : InstallEstimationMetadataProfile.Empty;
                });

            Assert.AreEqual(candidateBDir, result.DestinationDirectory);
            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
            Assert.IsTrue(result.ShouldAutoApplyDestination);
            Assert.AreEqual("metadata_tiebreak_distinct", result.ConfidenceReason);
            StringAssert.Contains(result.MetadataFrontierSummary ?? string.Empty, candidateADir);
            StringAssert.Contains(result.MetadataTieBreakSummary ?? string.Empty, candidateBDir);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_WhenSourceDirectoryWouldTieExternalCandidate_SourceIsExcluded()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "A_Source");
            string candidateDir = Path.Combine(tempRoot, "Z_Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "sound.wav"), "src");
            File.WriteAllText(Path.Combine(candidateDir, "sound.wav"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 1), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = sourceDir,
                delete_parent = true
            };
            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms", "sound.wav" });
            lookupCache.AddDir(candidateDir, new[] { "sound.wav" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
            Assert.IsTrue(result.ShouldAutoApplyDestination);
            Assert.AreEqual(candidateDir, result.DestinationDirectory);
            Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
            Assert.IsNull(result.SecondCandidate);
            Assert.AreEqual("single_candidate", result.ConfidenceReason);
            Assert.AreEqual(0, result.SuggestedDestinationDirectories.Count);
            Assert.AreEqual(1, result.Candidates.Count);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_SingleCandidateWithMetadataMismatch_ReturnsLowWithoutDestination()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "Pending", "Source");
                string candidateDir = Path.Combine(tempRoot, "Installed", "A");
                Directory.CreateDirectory(sourceDir);
                Directory.CreateDirectory(candidateDir);
                string chartPath = Path.Combine(sourceDir, "chart.bms");
                File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Base Artist obj: Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                File.WriteAllText(Path.Combine(candidateDir, "installed.bms"), "#PLAYER 1\r\n#TITLE Completely Different\r\n#ARTIST Another Artist\r\n");
                File.WriteAllText(Path.Combine(candidateDir, "sound.wav"), "dst");

                BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
                file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

                BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
                cache.AddDir(sourceDir);
                cache.AddDir(candidateDir);
                DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
                lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
                lookupCache.AddDir(candidateDir, new[] { "installed.bms", "sound.wav" });

                InstallEstimationResult result = service.EstimateInstallationDirectory(
                    new[] { file },
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    lookupCache,
                    asParallel: false,
                    BmsInstallationEstimateMode.Normal,
                    null,
                    delegate (string directoryPath)
                    {
                        return string.Equals(directoryPath, candidateDir, StringComparison.OrdinalIgnoreCase)
                            ? InstallEstimationMetadataNormalizer.BuildProfile(new[] { ("Completely Different", "Another Artist", Path.Combine(candidateDir, "installed.bms")) })
                            : InstallEstimationMetadataProfile.Empty;
                    });

                Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
                Assert.AreEqual(InstallEstimationLowConfidenceKind.MetadataMismatch, result.LowConfidenceKind);
                Assert.AreEqual("single_viable_candidate_metadata_mismatch", result.ConfidenceReason);
                Assert.IsFalse(result.ShouldAutoApplyDestination);
                Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
                CollectionAssert.AreEqual(new[] { candidateDir }, result.SuggestedDestinationDirectories.ToArray());
            });
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_WhenNoExternalViableCandidate_ReturnsHighWithoutDestinationEvenIfSourceIsHealthy()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "A_Source");
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            string candidateDir = Path.Combine(tempRoot, "Z_Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(chartPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "00.wav"), "src");
            File.WriteAllText(Path.Combine(sourceDir, "01.wav"), "src");
            File.WriteAllText(Path.Combine(candidateDir, "00.wav"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, "00.wav", "01.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 2), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = chartPath,
                delete_parent = false
            };
            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms", Path.Combine("sound", "00.wav") });
            lookupCache.AddDir(candidateDir, new[] { "00.wav" });

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
            Assert.IsFalse(result.HasViableDestination);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
            Assert.IsNull(result.SelectedCandidate);
            Assert.AreEqual(0, result.CandidateDirectoryCount);
            Assert.AreEqual(0, result.SuggestedDestinationDirectories.Count);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_WhenRoundedRatiosTie_UsesRawRatiosBeforeDirectoryPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "Source");
            string candidateDir = Path.Combine(tempRoot, "A_Candidate");
            string otherCandidateDir = Path.Combine(tempRoot, "Z_Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            Directory.CreateDirectory(otherCandidateDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");

            List<string> wavReferences = new List<string>();
            List<string> sourceFiles = new List<string> { "chart.bms" };
            List<string> candidateFiles = new List<string>();
            for (int i = 0; i < 399; i++)
            {
                string fileName = i.ToString("000") + ".wav";
                wavReferences.Add(fileName);
                candidateFiles.Add(fileName);
                File.WriteAllText(Path.Combine(sourceDir, fileName), "src");
                File.WriteAllText(Path.Combine(candidateDir, fileName), "dst");
                File.WriteAllText(Path.Combine(otherCandidateDir, fileName), "dst");
            }
            candidateFiles.Add("candidate-extra-a.wav");
            candidateFiles.Add("candidate-extra-b.wav");
            File.WriteAllText(Path.Combine(candidateDir, "candidate-extra-a.wav"), "cand-extra-a");
            File.WriteAllText(Path.Combine(candidateDir, "candidate-extra-b.wav"), "cand-extra-b");
            File.WriteAllText(Path.Combine(otherCandidateDir, "candidate-extra.wav"), "cand-extra");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, wavReferences.ToArray());
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: wavReferences.Count, wavExisting: wavReferences.Count), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            cache.AddDir(otherCandidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, sourceFiles);
            lookupCache.AddDir(candidateDir, candidateFiles);
            lookupCache.AddDir(otherCandidateDir, Enumerable.Range(0, 399).Select((int i) => i.ToString("000") + ".wav").Concat(new[] { "candidate-extra.wav" }));

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            Assert.AreEqual(otherCandidateDir, result.SelectedCandidate?.DirectoryPath);
            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
            Assert.AreEqual("distinct_primary_metrics", result.ConfidenceReason);
            Assert.AreEqual(otherCandidateDir, result.DestinationDirectory);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_PopulatesRepresentativeMetadataForTopCandidates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Chosen");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(candidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
        lookupCache.AddDir(candidateDir, new[] { "sound.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal,
            delegate (string directoryPath)
            {
                return string.Equals(directoryPath, candidateDir, StringComparison.OrdinalIgnoreCase)
                    ? new InstallDestinationRepresentativeMetadata
                    {
                        Title = "Representative Title",
                        Artist = "Representative Artist"
                    }
                    : InstallDestinationRepresentativeMetadata.Empty;
            });

        Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
        Assert.IsTrue(result.ShouldAutoApplyDestination);
        Assert.AreEqual("Representative Title", result.SelectedCandidate?.RepresentativeTitle);
        Assert.AreEqual("Representative Artist", result.SelectedCandidate?.RepresentativeArtist);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareAudioBroadFilter_DropsBasenameOnlyCandidate_WhenLookupCachePresent()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Flat");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "Nested");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound\\bgm1.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(flatCandidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav") });
        cache.AddDirHashed(nestedCandidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
        lookupCache.AddDir(flatCandidateDir, new[] { "bgm1.wav" });
        lookupCache.AddDir(nestedCandidateDir, new[] { "sound\\bgm1.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.TargetPathAwareHashCount);
        Assert.AreEqual(1, result.TargetPathAwareAudioHashCount);
        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(nestedCandidateDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(nestedCandidateDir, result.DestinationDirectory);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareAudioBroadFilter_RequiresLookupCache()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Flat");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "Nested");
        uint audioRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("sound\\bgm1");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound\\bgm1.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(flatCandidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav") });
        cache.AddDirHashed(nestedCandidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav") });
        DirectoryRelativePathHashIndex relativePathIndex = new DirectoryRelativePathHashIndex();
        relativePathIndex.AddDir(flatCandidateDir, Array.Empty<uint>(), Array.Empty<uint>(), Array.Empty<uint>());
        relativePathIndex.AddDir(nestedCandidateDir, new[] { audioRelativeHash }, Array.Empty<uint>(), Array.Empty<uint>());

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual("resource_index_unavailable", result.ConfidenceReason);
        Assert.AreEqual("resource_index_unavailable", result.CandidateMode);
        Assert.AreEqual("resource_index_unavailable", result.CoarseFilterMode);
        Assert.AreEqual(0, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.IsNull(result.SelectedCandidate);
        Assert.IsNull(result.DestinationDirectory);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareVisualBroadFilter_UsesImageRelativeHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Flat");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "Nested");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"));
        file.BGAfiles = new HashSet<string>(new[] { "clock\\00_001_00.bmp" }, StringComparer.OrdinalIgnoreCase);
        file.SetMaintenanceInfo(CreateVisualMaintenanceInfo(file, bgaDefined: 1, bgaExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(flatCandidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("00_001_00.bmp") });
        cache.AddDirHashed(nestedCandidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("00_001_00.bmp") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
        lookupCache.AddDir(flatCandidateDir, new[] { "00_001_00.bmp" });
        lookupCache.AddDir(nestedCandidateDir, new[] { "clock\\00_001_00.bmp" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.TargetPathAwareVisualHashCount);
        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(nestedCandidateDir, result.SelectedCandidate?.DirectoryPath);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareOptionalImageBroadFilter_UsesImageRelativeHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Flat");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "Nested");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"));
        file.SetStagefile("image\\logo.bmp");
        file.SetMaintenanceInfo(CreateVisualMaintenanceInfo(file, stagefileDefined: true, stagefileExisting: false), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(flatCandidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("logo.bmp") });
        cache.AddDirHashed(nestedCandidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("logo.bmp") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "chart.bms" });
        lookupCache.AddDir(flatCandidateDir, new[] { "logo.bmp" });
        lookupCache.AddDir(nestedCandidateDir, new[] { "image\\logo.bmp" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.TargetPathAwareOptionalImageHashCount);
        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(nestedCandidateDir, result.SelectedCandidate?.DirectoryPath);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareAdmissionGate_DropsCandidateThatOnlyMatchesBasenameRefs()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string basenameOnlyCandidateDir = Path.Combine("C:\\Installed", "BaseOnly");
        string pathAwareCandidateDir = Path.Combine("C:\\Installed", "PathAware");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound\\bgm1.wav", "bgm2.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(basenameOnlyCandidateDir, new[]
        {
            BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav"),
            BMSDirectoryFileNameHash.GetFileNameHash("bgm2.wav")
        });
        cache.AddDirHashed(pathAwareCandidateDir, new[]
        {
            BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav"),
            BMSDirectoryFileNameHash.GetFileNameHash("bgm2.wav")
        });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(basenameOnlyCandidateDir, new[] { "bgm1.wav", "bgm2.wav" });
        lookupCache.AddDir(pathAwareCandidateDir, new[] { "sound\\bgm1.wav", "bgm2.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(2, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(pathAwareCandidateDir, result.SelectedCandidate?.DirectoryPath);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_NestedRootStylePathAwareAudio_SelectsParentAggregateCandidate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string parentDir = Path.Combine("C:\\Installed", "Parent");
        string childDir = Path.Combine(parentDir, "Child");
        uint zeroBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("00.wav");
        uint oneBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("01.wav");
        uint parentZeroRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("Child\\sound\\00");
        uint parentOneRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("Child\\sound\\01");
        uint childZeroRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("sound\\00");
        uint childOneRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("sound\\01");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "Child\\sound\\00.wav", "Child\\sound\\01.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(parentDir, Array.Empty<uint>());
        cache.AddDirHashed(childDir, new[] { zeroBaseHash, oneBaseHash });

        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(
            parentDir,
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { parentZeroRelativeHash, parentOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>());
        lookupCache.AddDir(
            childDir,
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { childZeroRelativeHash, childOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { childZeroRelativeHash, childOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>());

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(parentDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(parentDir, result.DestinationDirectory);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_NestedBasenameOnlyAudio_UsesAncestorShadowRuleToPreferChildCandidate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string parentDir = Path.Combine("C:\\Installed", "Parent");
        string childDir = Path.Combine(parentDir, "Child");
        uint zeroBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("00.wav");
        uint oneBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("01.wav");
        uint parentZeroRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("Child\\sound\\00");
        uint parentOneRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("Child\\sound\\01");
        uint childZeroRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("sound\\00");
        uint childOneRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("sound\\01");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(parentDir, Array.Empty<uint>());
        cache.AddDirHashed(childDir, new[] { zeroBaseHash, oneBaseHash });

        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(
            parentDir,
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { parentZeroRelativeHash, parentOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>());
        lookupCache.AddDir(
            childDir,
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { childZeroRelativeHash, childOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { childZeroRelativeHash, childOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>());

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(InstallEstimationFinalEvaluationMode.RelativeStrict, result.FinalEvaluationMode);
        Assert.AreEqual(0, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(0, result.HierarchyCandidateDirectoryCount);
        Assert.AreEqual(0, result.AncestorShadowSuppressedCount);
        Assert.AreEqual(0, result.LazySelfOwnedEvaluationCount);
        Assert.AreEqual(0, result.CandidateViewBuildCount);
        Assert.IsNull(result.SelectedCandidate);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
    }

    [TestMethod]
    public void EstimateInstallationDirectory_BasenameOnlyAudioRef_MatchesNestedPathWithoutExactBonus()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "A_Nested");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Z_Flat");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(nestedCandidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav") });
        cache.AddDirHashed(flatCandidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(nestedCandidateDir, new[] { "sound\\bgm1.wav" });
        lookupCache.AddDir(flatCandidateDir, new[] { "bgm1.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(InstallEstimationFinalEvaluationMode.RelativeStrict, result.FinalEvaluationMode);
        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(0, result.HierarchyCandidateDirectoryCount);
        Assert.AreEqual(0, result.AncestorShadowSuppressedCount);
        Assert.AreEqual(0, result.LazySelfOwnedEvaluationCount);
        Assert.AreEqual(1, result.CandidateViewBuildCount);
        Assert.AreEqual(1, result.SelectedCandidate?.AudioMatched);
        Assert.AreEqual(1, result.SelectedCandidate?.AudioExactMatched);
        Assert.AreEqual(flatCandidateDir, result.SelectedCandidate?.DirectoryPath);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_MixedBasenameAndPathAwareAudioRefs_CountEachReferenceOnce()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Candidate");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav", "sound\\bgm2.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(candidateDir, new[]
        {
            BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav"),
            BMSDirectoryFileNameHash.GetFileNameHash("bgm2.wav")
        });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, new[] { "bgm1.wav", "sound\\bgm2.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(InstallEstimationFinalEvaluationMode.RelativeStrict, result.FinalEvaluationMode);
        Assert.AreEqual(2, result.TargetResourceCount);
        Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
        Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(2, result.SelectedCandidate?.AudioMatched);
        Assert.AreEqual(candidateDir, result.DestinationDirectory);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_CandidateCount_CollapsesBasenameDuplicatesInFastPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Candidate");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(candidateDir, new[]
        {
            BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav"),
            BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav")
        });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, new[] { "bgm1.wav", "sound\\bgm1.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(InstallEstimationFinalEvaluationMode.RelativeStrict, result.FinalEvaluationMode);
        Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(1, result.SelectedCandidate?.AudioMatched);
        Assert.AreEqual(50, result.SelectedCandidate?.AudioPrecision);
        Assert.AreEqual(50, result.SelectedCandidate?.AudioJaccard);
        Assert.AreEqual(2, result.SelectedCandidate?.AudioFileCount);
        Assert.AreEqual(1, result.CandidateViewBuildCount);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareAudioGate_UsesRelativePathAwareMatchesForNormalAndMerge()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string sourceSoundDir = Path.Combine(sourceDir, "sound");
            string candidateDir = Path.Combine(tempRoot, "Candidate");
            string candidateSoundDir = Path.Combine(candidateDir, "sound");
            Directory.CreateDirectory(sourceSoundDir);
            Directory.CreateDirectory(candidateSoundDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceSoundDir, "02.wav"), "bundled");
            File.WriteAllText(Path.Combine(candidateSoundDir, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateSoundDir, "01.wav"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound\\00.wav", "sound\\01.wav", "sound\\02.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 1), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSPackage package = new BMSPackage(new BMSFile[] { file })
            {
                path = sourceDir,
                delete_parent = true
            };
            PackageInstallEstimationSnapshot snapshot = package.GetOrBuildInstallEstimationSnapshot(new[] { file });

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(candidateDir);
            DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, new[] { "chart.bms", "sound\\02.wav" });
            lookupCache.AddDir(candidateDir, new[] { "sound\\00.wav", "sound\\01.wav" });

            InstallEstimationResult normalResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            InstallEstimationResult mergeResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.MergeCandidateOnly);

            Assert.AreEqual(1, normalResult.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(candidateDir, normalResult.DestinationDirectory);
            Assert.AreEqual(3, normalResult.SelectedCandidate?.AudioMatched);

            Assert.AreEqual(0, mergeResult.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(0, mergeResult.CandidateDirectoryCount);
            Assert.AreEqual("no_viable_destination_below_threshold", mergeResult.ConfidenceReason);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_WithoutResourceIndex_ReturnsUnavailableForRelativePathSemantics()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Candidate");
        uint bgm1BaseHash = BMSDirectoryFileNameHash.GetLookupHash("bgm1");
        uint bgm2BaseHash = BMSDirectoryFileNameHash.GetLookupHash("bgm2");
        uint pathAwareBgm2Hash = BMSDirectoryFileNameHash.GetLookupHash("sound\\bgm2");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav", "sound\\bgm2.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(candidateDir, new[]
        {
            BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav"),
            BMSDirectoryFileNameHash.GetFileNameHash("bgm2.wav")
        });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, new[] { "bgm1.wav", "sound\\bgm2.wav" });
        DirectoryRelativePathHashIndex relativePathIndex = new DirectoryRelativePathHashIndex();
        relativePathIndex.AddDir(candidateDir, new[] { bgm1BaseHash, bgm2BaseHash }, Array.Empty<uint>(), Array.Empty<uint>(), new[] { BMSDirectoryFileNameHash.GetLookupHash("bgm1"), pathAwareBgm2Hash }, Array.Empty<uint>(), Array.Empty<uint>());

        InstallEstimationResult lookupResult = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        InstallEstimationResult unavailableResult = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(InstallEstimationFinalEvaluationMode.RelativeStrict, lookupResult.FinalEvaluationMode);
        Assert.AreEqual(candidateDir, lookupResult.DestinationDirectory);
        Assert.IsNull(unavailableResult.DestinationDirectory);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.ConfidenceReason);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CandidateMode);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CoarseFilterMode);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_WithoutResourceIndex_DoesNotUseChartRelativeFallback()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Candidate");
        uint bgm1BaseHash = BMSDirectoryFileNameHash.GetLookupHash("bgm1");
        uint nestedBgm1Hash = BMSDirectoryFileNameHash.GetLookupHash("sound\\bgm1");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(candidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, new[] { "bgm1.wav" });
        DirectoryRelativePathHashIndex relativePathIndex = new DirectoryRelativePathHashIndex();
        relativePathIndex.AddDir(candidateDir, new[] { bgm1BaseHash }, Array.Empty<uint>(), Array.Empty<uint>(), new[] { BMSDirectoryFileNameHash.GetLookupHash("bgm1") }, Array.Empty<uint>(), Array.Empty<uint>());

        InstallEstimationResult lookupResult = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        InstallEstimationResult unavailableResult = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(InstallEstimationFinalEvaluationMode.RelativeStrict, lookupResult.FinalEvaluationMode);
        Assert.AreEqual(candidateDir, lookupResult.DestinationDirectory);
        Assert.IsNull(unavailableResult.DestinationDirectory);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.ConfidenceReason);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CandidateMode);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CoarseFilterMode);
        Assert.AreEqual(1, lookupResult.CandidateViewBuildCount);
        Assert.AreEqual(0, unavailableResult.CandidateViewBuildCount);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_WithoutResourceIndex_SkipsAncestorShadowDiagnostics()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string parentDir = Path.Combine("C:\\Installed", "Parent");
        string childDir = Path.Combine(parentDir, "Child");
        uint zeroBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("00.wav");
        uint oneBaseHash = BMSDirectoryFileNameHash.GetFileNameHash("01.wav");
        uint parentZeroRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("Child\\sound\\00");
        uint parentOneRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("Child\\sound\\01");
        uint childZeroRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("sound\\00");
        uint childOneRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("sound\\01");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("chart.bms") });
        cache.AddDirHashed(parentDir, Array.Empty<uint>());
        cache.AddDirHashed(childDir, new[] { zeroBaseHash, oneBaseHash });

        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(
            parentDir,
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { parentZeroRelativeHash, parentOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>());
        lookupCache.AddDir(
            childDir,
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { childZeroRelativeHash, childOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { childZeroRelativeHash, childOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>());

        DirectoryRelativePathHashIndex relativePathIndex = new DirectoryRelativePathHashIndex();
        relativePathIndex.AddDir(
            parentDir,
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { parentZeroRelativeHash, parentOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            Array.Empty<uint>());
        relativePathIndex.AddDir(
            childDir,
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { childZeroRelativeHash, childOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { zeroBaseHash, oneBaseHash },
            Array.Empty<uint>(),
            Array.Empty<uint>(),
            new[] { childZeroRelativeHash, childOneRelativeHash },
            Array.Empty<uint>(),
            Array.Empty<uint>());

        InstallEstimationResult lookupResult = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        InstallEstimationResult unavailableResult = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(InstallEstimationFinalEvaluationMode.RelativeStrict, lookupResult.FinalEvaluationMode);
        Assert.IsNull(unavailableResult.DestinationDirectory);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.ConfidenceReason);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CandidateMode);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CoarseFilterMode);
        Assert.AreEqual(0, lookupResult.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(0, unavailableResult.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(0, lookupResult.CandidateViewBuildCount);
        Assert.AreEqual(0, unavailableResult.CandidateViewBuildCount);
    }

    [TestMethod]
    public void GetDistinctInstalledDirectoriesByHash_FileWithMd5DoesNotFallBackToSha256()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string installDir = Path.Combine("C:\\Installed", "PrimaryOnly");
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installDir, "chart.bms"));
        installedFile.SetSha256(new string('b', 64));
        TestableBmsFile pendingFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\chart.bms");
        pendingFile.SetSha256(new string('b', 64));

        InstalledChartDirectoryIndexSnapshot snapshot = service.BuildInstalledHashToDirectoryMap(new[] { installedFile });
        List<string> directories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByHash(snapshot, pendingFile);

        Assert.AreEqual(0, directories.Count);
    }

    [TestMethod]
    public void GetDistinctInstalledDirectoriesByHash_Sha256OnlyFileUsesSha256()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string installDir = Path.Combine("C:\\Installed", "ShaOnly");
        LR2SongDBExtended.bmson_song bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine(installDir, "chart.bmson"),
            folder = installDir,
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256 = new string('c', 64)
        };
        TestableBmsFile pendingFile = CreateFile(null, "C:\\Pending\\chart.bmson");
        pendingFile.SetSha256(new string('c', 64));

        InstalledChartDirectoryIndexSnapshot snapshot = service.BuildInstalledHashToDirectoryMap(Array.Empty<BMSFile>(), new[] { bmsonSong });
        List<string> directories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesByHash(snapshot, pendingFile);

        CollectionAssert.AreEqual(new[] { installDir }, directories);
    }

    private static void WithWorkspace(Action<string, BmsLibraryInstallEstimationService> testAction)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InstallEstimateTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            testAction(tempRoot, CreateService());
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    private static BmsLibraryInstallEstimationService CreateService()
    {
        return new BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot.CreateCurrent(), 70);
    }

    private static TestableBmsFile CreateFile(string? hash, string path, params string[] wavFiles)
    {
        TestableBmsFile file = new TestableBmsFile
        {
            path = path,
            WAVfiles = new HashSet<string>(wavFiles ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase),
            BGAfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };
        file.SetHash(hash);
        return file;
    }

    private static BMSFileMaintenanceInfo CreateMaintenanceInfo(BMSFile file, int wavDefined, int wavExisting)
    {
        return new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            wav_files_defined = wavDefined,
            wav_files_existing = wavExisting,
            bga_files_defined = 0,
            bga_files_existing = 0,
            movie_files_defined = 0,
            movie_files_existing = 0,
            is_stagefile_defined = false,
            is_stagefile_existing = false,
            is_backbmp_defined = false,
            is_backbmp_existing = false,
            is_banner_defined = false,
            is_banner_existing = false
        };
    }

    private static BMSFileMaintenanceInfo CreateVisualMaintenanceInfo(BMSFile file, int bgaDefined = 0, int bgaExisting = 0, bool stagefileDefined = false, bool stagefileExisting = false, bool backbmpDefined = false, bool backbmpExisting = false, bool bannerDefined = false, bool bannerExisting = false)
    {
        return new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            wav_files_defined = 0,
            wav_files_existing = 0,
            bga_files_defined = bgaDefined,
            bga_files_existing = bgaExisting,
            movie_files_defined = 0,
            movie_files_existing = 0,
            is_stagefile_defined = stagefileDefined,
            is_stagefile_existing = stagefileExisting,
            is_backbmp_defined = backbmpDefined,
            is_backbmp_existing = backbmpExisting,
            is_banner_defined = bannerDefined,
            is_banner_existing = bannerExisting
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

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string? value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }

        public void SetStagefile(string value)
        {
            stagefile = value;
        }
    }
}
