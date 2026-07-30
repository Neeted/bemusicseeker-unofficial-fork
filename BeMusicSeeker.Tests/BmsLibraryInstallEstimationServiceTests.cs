using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Tests.Performance;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BmsLibraryInstallEstimationServiceTests
{
    public TestContext TestContext { get; set; }

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
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

        InstallEstimationResult sequential = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);
        InstallEstimationResult defaultParallel = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: true,
            ChartInstallationEstimateMode.Normal);
        InstallEstimationResult explicitDegree = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            candidateEvaluationDegree: 16,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(1, sequential.CandidateEvaluationDegree);
        Assert.AreEqual(BmsLibraryInstallEstimationService.ResolveDefaultCandidateEvaluationDegree(), defaultParallel.CandidateEvaluationDegree);
        Assert.AreEqual(16, explicitDegree.CandidateEvaluationDegree);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_NormalModeDoesNotUseSha256OnlyInstalledHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        TestableBmsFile file = CreateFile(null, Path.Combine("C:\\Pending", "chart.bms"), "sound.wav");
        file.SetSha256(new string('b', 64));

        InstallEstimationResult normal = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>([file.sha256], StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);
        InstallEstimationResult correction = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>([file.sha256], StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.ReinstallCorrection);

        Assert.AreEqual("resource_index_unavailable", normal.ConfidenceReason);
        Assert.AreEqual("resource_index_unavailable", correction.ConfidenceReason);
    }

    [TestMethod]
    public void TryResolveInstalledDestinationFromPackage_PrefersDirectoryWithMostMatchingCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string dirA = Path.Combine("C:\\Installed", "DirA");
        string dirB = Path.Combine("C:\\Installed", "DirB");
        List<BMSFile> installedFiles =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(dirA, "a.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(dirA, "b.bms")),
            CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine(dirB, "c.bms"))
        ];
        TestableBmsFile pendingA = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\a.bms");
        TestableBmsFile pendingB = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\b.bms");
        TestableBmsFile pendingC = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\c.bms");
        var package = ChartPackageTestExtensions.CreatePackage([pendingA, pendingB, pendingC]);
        package.path = "C:\\Pending";
        package.delete_parent = false;

        InstalledDirectoryLookupResult result = service.TryResolveInstalledDestinationFromPackage(
            package,
            [PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingC))],
            CreateInstalledChartLookupIndexSnapshot(installedFiles));

        Assert.AreEqual(dirA, result.InstallDirectory);
        Assert.AreEqual(3, result.MatchedHashCount);
        Assert.AreEqual(2, result.CandidateDirectoryCount);
        Assert.AreEqual(InstalledDirectoryResolveReason.None, result.Reason);
    }

    [TestMethod]
    public void TryResolveInstalledDestinationFromPackage_MixedBmsAndBmsonPackagePrefersDirectoryWithMostMatchingCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string dirA = Path.Combine("C:\\Installed", "DirA");
        string dirB = Path.Combine("C:\\Installed", "DirB");
        List<BMSFile> installedFiles =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(dirA, "a.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(dirA, "b.bms"))
        ];
        var installedBmson = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine(dirB, "c.bmson"),
            folder = dirB,
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('c', 64)
        };
        TestableBmsFile pendingA = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\a.bms");
        TestableBmsFile pendingB = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\b.bms");
        var pendingBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\c.bmson",
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('c', 64)
        };
        var package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingA)),
            PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingB)),
            PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingBmson))
        ]);
        package.path = "C:\\Pending";
        package.delete_parent = false;

        InstalledDirectoryLookupResult result = service.TryResolveInstalledDestinationFromPackage(
            package,
            package.ChartEntries,
            CreateInstalledChartLookupIndexSnapshot(installedFiles, [installedBmson]));

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
        List<BMSFile> installedFiles =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(dirA, "a.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(dirB, "b.bms"))
        ];
        TestableBmsFile pendingA = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\a.bms");
        TestableBmsFile pendingB = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\b.bms");
        TestableBmsFile pendingMissing = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\missing.bms", "sound.wav");
        var package = ChartPackageTestExtensions.CreatePackage([pendingA, pendingB, pendingMissing]);
        package.path = "C:\\Pending";
        package.delete_parent = false;

        InstalledDirectoryLookupResult result = service.TryResolveInstalledDestinationFromPackage(
            package,
            [PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingMissing))],
            CreateInstalledChartLookupIndexSnapshot(installedFiles));

        Assert.AreEqual(InstalledDirectoryResolveReason.MultipleCandidateDirectories, result.Reason);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(2, result.CandidateDirectoryCount);
        CollectionAssert.AreEqual(new[] { dirA, dirB }, result.CandidateDirectories.ToArray());
    }

    [TestMethod]
    public void TryResolveInstalledDestinationFromPackage_MixedBmsAndBmsonTopTieReturnsMultipleCandidateDirectories()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string dirA = Path.Combine("C:\\Installed", "DirA");
        string dirB = Path.Combine("C:\\Installed", "DirB");
        List<BMSFile> installedFiles =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(dirA, "a.bms"))
        ];
        var installedBmson = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine(dirB, "b.bmson"),
            folder = dirB,
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        TestableBmsFile pendingBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\a.bms");
        var pendingBmson = new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\b.bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        var package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingBms)),
            PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingBmson))
        ]);
        package.path = "C:\\Pending";
        package.delete_parent = false;

        InstalledDirectoryLookupResult result = service.TryResolveInstalledDestinationFromPackage(
            package,
            package.ChartEntries,
            CreateInstalledChartLookupIndexSnapshot(installedFiles, [installedBmson]));

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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);
            var package = ChartPackageTestExtensions.CreatePackage([file]);
            package.path = sourceDir;
            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            InstallEstimationResult result = service.EstimateInstallationDirectoryForCandidateDirectories(
                snapshot,
                [candidateDir],
                directoryLookupCache: null,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 1), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", Path.Combine("sound", "01.wav")]);
            lookupCache.AddDir(mergeDir, [Path.Combine("sound", "00.wav"), Path.Combine("sound", "01.wav")]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.MergeCandidateOnly);

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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["sound.wav"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["other.wav"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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
            file.BGAfiles = new HashSet<string>(["title.png"], StringComparer.OrdinalIgnoreCase);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(weakCandidateDir, ["00.wav", "title.png"]);
            lookupCache.AddDir(strongCandidateDir, ["00.wav", "01.wav", "title.png"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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
            file.BGAfiles = new HashSet<string>(["title.png"], StringComparer.OrdinalIgnoreCase);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["00.wav", "title.png"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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
            file.BGAfiles = new HashSet<string>(["bg.png"], StringComparer.OrdinalIgnoreCase);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 0, wavExisting: 0), suppressPropertyChanged: true);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["bg.png"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0), suppressPropertyChanged: true);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["00.wav", "01.wav"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 1), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", "02.wav"]);
            lookupCache.AddDir(candidateDir, ["00.wav", "01.wav"]);

            InstallEstimationResult normalResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            InstallEstimationResult mergeResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.MergeCandidateOnly);

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
                [.. Enumerable.Range(0, 100).Select(i => i.ToString("D2") + ".wav")]);
            file.BGAfiles = new HashSet<string>(["title.png"], StringComparer.OrdinalIgnoreCase);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 100, wavExisting: 71), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", .. Enumerable.Range(0, 71).Select(i => i.ToString("D2") + ".wav")]);
            lookupCache.AddDir(candidateDir, ["title.png"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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

            string[] allAudio = [.. Enumerable.Range(0, 100).Select(i => i.ToString("D3") + ".wav")];
            foreach (string fileName in allAudio.Take(70))
            {
                File.WriteAllText(Path.Combine(weakCandidateDir, fileName), "weak");
            }
            foreach (string fileName in allAudio.Take(71))
            {
                File.WriteAllText(Path.Combine(strongCandidateDir, fileName), "strong");
            }

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), allAudio);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 100, wavExisting: 0), suppressPropertyChanged: true);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(weakCandidateDir, [.. allAudio.Take(70)]);
            lookupCache.AddDir(strongCandidateDir, [.. allAudio.Take(71)]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(2, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(1, result.CandidateDirectoryCount);
            Assert.AreEqual(strongCandidateDir, result.SelectedCandidate?.DirectoryPath);
        });
    }

    [TestMethod]
    [TestCategory("Net10Performance")]
    [TestCategory("estimation")]
    public void EstimateInstallationDirectory_BuildsEachCandidateResourceViewOnce()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        foreach (Net10PerformanceCorpusScale scale in Net10PerformanceCorpus.GetConfiguredScales())
        {
            int candidateCount = scale switch
            {
                Net10PerformanceCorpusScale.Small => 8,
                Net10PerformanceCorpusScale.Medium => 64,
                _ => 512
            };
            BmsLibraryInstallEstimationService service = CreateService();
            string sourceDir = Path.Combine(@"C:\Pending", "Source");
            string[] allAudio =
                [.. Enumerable.Range(0, 100).Select(index => index.ToString("D3") + ".wav")];
            TestableBmsFile file = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine(sourceDir, "chart.bms"),
                allAudio);
            file.SetMaintenanceInfo(
                CreateMaintenanceInfo(file, wavDefined: allAudio.Length, wavExisting: 0),
                suppressPropertyChanged: true);
            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            for (int index = 0; index < candidateCount; index++)
            {
                lookupCache.AddDir(
                    Path.Combine(@"C:\Installed", "Candidate" + index.ToString("D4")),
                    allAudio.Take(71));
            }

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(
                service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                candidateEvaluationDegree: 1,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(candidateCount, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(candidateCount, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(candidateCount, result.CandidateViewBuildCount);
            TestContext.WriteLine(
                "route=install_estimation_candidate_views"
                + " scale=" + scale
                + " candidates=" + candidateCount
                + " legacyViewBuildCount=" + (candidateCount * 2)
                + " currentViewBuildCount=" + result.CandidateViewBuildCount
                + " selected=" + result.SelectedCandidate?.DirectoryPath);
        }
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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0), suppressPropertyChanged: true);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, [Path.Combine("a", "00.wav"), Path.Combine("b", "01.wav"), Path.Combine("c", "01.wav")]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateDir, ["keysound.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateDir, ["00.wav", "01.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
            primary.BGAfiles = new HashSet<string>([Path.Combine("image", "bg.png")], StringComparer.OrdinalIgnoreCase);
            TestableBmsFile secondary = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(sourceDir, "chart2.bms"), Path.Combine("sound", "00.wav"));
            primary.SetMaintenanceInfo(CreateMaintenanceInfo(primary, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);
            secondary.SetMaintenanceInfo(CreateMaintenanceInfo(secondary, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([primary, secondary]);

            package.path = sourceDir;

            package.delete_parent = true;

            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [primary, secondary]);
            uint expectedAudioRelativePathHash = ChartResourceKeyHash.GetLookupHash(ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(ChartResourcePathNormalizer.NormalizeRelativePathForLookup(sourceDir, Path.Combine(soundDir, "00.wav"))));
            uint expectedImageRelativePathHash = ChartResourceKeyHash.GetLookupHash(ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(ChartResourcePathNormalizer.NormalizeRelativePathForLookup(sourceDir, Path.Combine(imageDir, "bg.png"))));
            uint expectedMovieRelativePathHash = ChartResourceKeyHash.GetLookupHash(ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(ChartResourcePathNormalizer.NormalizeRelativePathForLookup(sourceDir, Path.Combine(movieDir, "pv.mp4"))));

            Assert.AreEqual(ChartFileKind.Bms, snapshot.RepresentativeChart.Kind);
            Assert.AreEqual(primary.path, snapshot.RepresentativeChart.Path);
            Assert.AreSame(primary, snapshot.RepresentativeChart.GetBmsStorageOwner());
            Assert.AreEqual(2, snapshot.ChartCount);
            var expectedDefinedResources = ChartResourceSnapshot.CreateAggregate(package.ChartEntries.Select(entry => entry.Chart));
            CollectionAssert.AreEquivalent(expectedDefinedResources.AudioRelativePathHashes.ToArray(), snapshot.DefinedResources.AudioRelativePathHashes.ToArray());
            CollectionAssert.AreEquivalent(expectedDefinedResources.VisualRelativePathHashes.ToArray(), snapshot.DefinedResources.VisualRelativePathHashes.ToArray());
            CollectionAssert.AreEquivalent(expectedDefinedResources.MovieRelativePathHashes.ToArray(), snapshot.DefinedResources.MovieRelativePathHashes.ToArray());
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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;

            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);
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

            var primary = BMSFile.CreateBMSFileFromFile(primaryPath);
            var secondary = BMSFile.CreateBMSFileFromFile(secondaryPath);

            var package = ChartPackageTestExtensions.CreatePackage([primary, secondary]);

            package.path = sourceDir;

            package.delete_parent = true;

            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [primary, secondary]);

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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = file.path;

            package.delete_parent = false;

            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            Assert.AreEqual(0, snapshot.BundledAudioCount);
            Assert.AreEqual(0, snapshot.BundledImageCount);
            Assert.AreEqual(0, snapshot.BundledMovieCount);
            Assert.AreEqual(sourceDir, snapshot.SourceDirectory);
            Assert.AreEqual(1, snapshot.SourceCandidateResources.AudioFileNameHashCount);
        });
    }

    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_FilePackageStopsSourceSurfaceScanAtLimit()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            Directory.CreateDirectory(sourceDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "00.wav"), "audio");
            File.WriteAllText(Path.Combine(sourceDir, "01.wav"), "audio");
            File.WriteAllText(Path.Combine(sourceDir, "ignore.txt"), "text");

            PackageInstallSurfaceSnapshot surface = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(
                chartPath,
                maxVisitedFileSystemEntryCount: 3);

            Assert.IsTrue(surface.ScanLimitExceeded);
            Assert.AreEqual(3, surface.MaxVisitedFileSystemEntryCount);
            Assert.IsTrue(surface.VisitedFileSystemEntryCount > surface.MaxVisitedFileSystemEntryCount);
            Assert.AreEqual(0, surface.SourceCandidateResources.AudioFileNameHashCount);

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, "00.wav");
            var package = ChartPackageTestExtensions.CreatePackage([file]);

            PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.Build(
                package,
                package.ChartEntries,
                surface,
                sourceSurfaceCacheHit: false);

            Assert.IsTrue(snapshot.SourceSurfaceScanLimitExceeded);
            Assert.AreEqual(surface.VisitedFileSystemEntryCount, snapshot.SourceSurfaceVisitedFileSystemEntryCount);
        });
    }

    [TestMethod]
    public void ChartPackage_PathPackage_DoesNotPrebuildSourceSurface_WhenChartAdaptersAreRequested()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string soundDir = Path.Combine(sourceDir, "sound");
                Directory.CreateDirectory(soundDir);
                File.WriteAllText(Path.Combine(sourceDir, "chart1.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDir, "chart2.bme"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDir, "chart3.bmson"), "{}");
                File.WriteAllText(Path.Combine(soundDir, "00.wav"), "audio");

                var package = new ChartPackage
                {
                    path = sourceDir,
                    delete_parent = true
                };

                List<PackageChartEntry> firstEntries = package.ChartEntries;
                List<PackageChartEntry> secondEntries = package.ChartEntries;
                PackageInstallEstimationSnapshot firstSnapshot = package.GetOrBuildInstallEstimationSnapshotFromEntries(firstEntries);
                PackageInstallEstimationSnapshot secondSnapshot = package.GetOrBuildInstallEstimationSnapshotFromEntries(firstEntries);

                CollectionAssert.AreEqual(
                    firstEntries.Select(entry => entry.Chart.Path).ToList(),
                    secondEntries.Select(entry => entry.Chart.Path).ToList());
                Assert.AreEqual(3, firstEntries.Count);
                Assert.AreEqual(3, secondEntries.Count);
                Assert.IsTrue(firstEntries.Where(entry => entry.Chart.Kind == ChartFileKind.Bms).All(entry => entry.Chart.GetBmsStorageOwner() != null));
                Assert.IsTrue(secondEntries.Where(entry => entry.Chart.Kind == ChartFileKind.Bms).All(entry => entry.Chart.GetBmsStorageOwner() != null));
                Assert.IsTrue(firstEntries.Any(entry => entry.Chart.Kind == ChartFileKind.Bmson));
                Assert.IsTrue(firstEntries.Where(entry => entry.Chart.Kind == ChartFileKind.Bmson).All(entry => entry.GetBmsOwnerForTest() == null));
                Assert.AreEqual(1, firstEntries.Count(entry => entry.Chart.Kind == ChartFileKind.Bmson));
                Assert.AreEqual(3, firstSnapshot.ChartCount);
                Assert.AreEqual(sourceDir, firstSnapshot.SourceDirectory);
                Assert.AreEqual("bounded_fast_source_surface", firstSnapshot.SourceSurfaceScanBackend);
                Assert.IsFalse(firstSnapshot.SourceSurfaceCacheHit);
                Assert.IsTrue(firstSnapshot.SourceSurfaceTrackedFileCount >= 4);
                Assert.AreEqual(1, firstSnapshot.BundledAudioCount);
                Assert.IsTrue(secondSnapshot.SourceSurfaceCacheHit);
            });
    }

    [TestMethod]
    public void ChartPackage_PathPackage_UsesChartEntriesBeforeCompatibilityAdaptersAreRequested()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(Path.Combine(sourceDir, "chart1.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDir, "chart2.bmson"), "{}");

                var package = new ChartPackage
                {
                    path = sourceDir,
                    delete_parent = true
                };

                List<PackageChartEntry> firstEntries = package.ChartEntries;
                string updatedPath = Path.Combine(sourceDir, "updated.bms");
                firstEntries.Single(entry => entry.Chart.Kind == ChartFileKind.Bms).Chart.GetBmsStorageOwner().path = updatedPath;
                List<PackageChartEntry> secondEntries = package.ChartEntries;

                Assert.AreEqual(2, firstEntries.Count);
                Assert.AreEqual(2, secondEntries.Count);
                Assert.IsTrue(secondEntries.Any(entry => string.Equals(entry.Chart.Path, updatedPath, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(firstEntries.Any(entry => entry.Chart.Kind == ChartFileKind.Bmson));
                Assert.IsTrue(firstEntries.Where(entry => entry.Chart.Kind == ChartFileKind.Bms).All(entry => entry.Chart.GetBmsStorageOwner() != null));
                Assert.IsTrue(secondEntries.Where(entry => entry.Chart.Kind == ChartFileKind.Bms).All(entry => entry.Chart.GetBmsStorageOwner() != null));
                CollectionAssert.AreEqual(
                    secondEntries.Select(entry => entry.Chart.Path).OrderBy(path => path).ToList(),
                    package.ChartEntries.Select(entry => entry.Chart.Path).OrderBy(path => path).ToList());
            });
    }

    [TestMethod]
    public void ChartPackage_PathPackage_CountsChartEntriesBeforeCompatibilityAdaptersAreRequested()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(Path.Combine(sourceDir, "chart1.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDir, "chart2.bmson"), "{}");

                var package = new ChartPackage
                {
                    path = sourceDir,
                    delete_parent = true
                };

                Assert.AreEqual(2, package.ChartEntries.Count);
                Assert.IsTrue(package.ChartEntries.Count > 0);
            });
    }

    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_BuildsFromPackageChartEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string soundDir = Path.Combine(sourceDir, "sound");
                Directory.CreateDirectory(soundDir);
                File.WriteAllText(Path.Combine(sourceDir, "chart1.bms"), "#PLAYER 1\r\n#TITLE BMS\r\n#WAVAA sound\\00.wav\r\n#00111:AA");
                File.WriteAllText(Path.Combine(sourceDir, "chart2.bmson"), "{}");
                File.WriteAllText(Path.Combine(soundDir, "00.wav"), "audio");

                var package = new ChartPackage
                {
                    path = sourceDir,
                    delete_parent = true
                };

                List<PackageChartEntry> targetEntries = package.ChartEntries;
                PackageInstallSurfaceSnapshot sourceSurface = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(sourceDir);
                PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.Build(package, targetEntries, sourceSurface, sourceSurfaceCacheHit: false);

                Assert.AreEqual(2, targetEntries.Count);
                Assert.AreEqual(2, snapshot.ChartCount);
                Assert.IsTrue(targetEntries.Any(entry => entry.Chart.Kind == ChartFileKind.Bmson));
                Assert.AreEqual(sourceDir, snapshot.SourceDirectory);
                Assert.AreEqual(1, snapshot.BundledAudioCount);
                Assert.IsNotNull(snapshot.RepresentativeChart);
            });
    }

    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_AcceptsChartEntryWithoutCompatibilityAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            Directory.CreateDirectory(sourceDir);
            var song = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(sourceDir, "chart.bmson"),
                folder = Path.GetFileName(sourceDir),
                title = "BMSON",
                artist = "Artist",
                md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                level = 7,
                wav_files = [Path.Combine("sound", "keysound.wav")]
            };
            ChartFile chart = ChartFileProjection.FromBmsonSong(song);
            PackageChartEntry entry = PackageChartEntry.FromChart(chart);
            var package = new ChartPackage
            {
                path = sourceDir
            };
            PackageInstallSurfaceSnapshot sourceSurface = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(sourceDir);

            PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.Build(package, [entry], sourceSurface, sourceSurfaceCacheHit: false);

            Assert.AreEqual(1, snapshot.ChartCount);
            Assert.AreEqual(chart.Path, snapshot.RepresentativeChart.Path);
            Assert.AreEqual(chart.Kind, snapshot.RepresentativeChart.Kind);
            Assert.AreEqual(1, snapshot.DefinedResources.TotalReferenceCount);
            Assert.AreEqual(ChartFileKind.Bmson, snapshot.RepresentativeChart.Kind);
            Assert.AreEqual("bmson", snapshot.TargetMetadataProfile.DominantNormalizedTitle);
            Assert.IsNull(entry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void PackageChartDiscoverySnapshot_ChartEntriesPreservesAdapterlessEntry()
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Pending", "chart.bmson"),
            title = "BMSON",
            artist = "Artist",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            wav_files = ["keysound.wav"]
        };
        ChartFile chart = ChartFileProjection.FromBmsonSong(song);
        PackageChartEntry entry = PackageChartEntry.FromChart(chart);
        var discoverySnapshot = new PackageChartDiscoverySnapshot
        {
            ChartEntries = [entry]
        };

        List<PackageChartEntry> entries = discoverySnapshot.ChartEntries;

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(chart.Path, entries[0].Chart.Path);
        Assert.AreEqual(chart.Kind, entries[0].Chart.Kind);
        Assert.IsNull(entries[0].GetBmsOwnerForTest());
    }

    [TestMethod]
    public void ChartFileIdentity_IsSameChartTarget_DoesNotMatchSameHashDifferentPath()
    {
        TestableBmsFile firstBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\PendingA", "chart.bms"));
        TestableBmsFile secondBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\PendingB", "chart.bms"));
        ChartFile firstBmsChart = ChartFileProjection.FromBmsFile(firstBms);
        ChartFile secondBmsChart = ChartFileProjection.FromBmsFile(secondBms);
        var firstBmson = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\PendingA", "chart.bmson"),
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('b', 64)
        };
        var secondBmson = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\PendingB", "chart.bmson"),
            md5 = firstBmson.md5,
            sha256 = firstBmson.sha256
        };
        ChartFile firstBmsonChart = ChartFileProjection.FromBmsonSong(firstBmson);
        ChartFile secondBmsonChart = ChartFileProjection.FromBmsonSong(secondBmson);

        Assert.IsFalse(ChartFileIdentity.IsSameChartTarget(firstBmsChart, secondBmsChart));
        Assert.IsFalse(ChartFileIdentity.IsSameChartTarget(firstBmsonChart, secondBmsonChart));
    }

    [TestMethod]
    public void PackageChartEntry_ToChartEntrySnapshot_DropsBmsonCompatibilityAdapter()
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Pending", "chart.bmson"),
            title = "BMSON",
            artist = "Artist",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(song));
        string destinationDirectory = Path.Combine("C:\\Installed", "Package");
        entry.ApplyInstallDestination(destinationDirectory, "Resolved Title", "Resolved Artist");
        entry.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, "resolve failed");

        PackageChartEntry snapshot = entry.ToChartEntrySnapshot();

        Assert.IsNotNull(snapshot);
        Assert.IsNull(snapshot.GetBmsOwnerForTest());
        Assert.AreEqual(ChartFileKind.Bmson, snapshot.Chart.Kind);
        Assert.AreSame(song, snapshot.Chart.GetBmsonStorageOwner());
        Assert.AreEqual(destinationDirectory, snapshot.Chart.InstallDestination);
        Assert.IsTrue(snapshot.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
    }

    [TestMethod]
    public void PackageChartEntry_BmsStorageOwnerKeepsInstallDestinationOnChartState()
    {
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Pending", "chart.bms"));
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));
        string destinationDirectory = Path.Combine("C:\\Installed", "Package");
        string installedPath = Path.Combine(destinationDirectory, "chart.bms");

        entry.ApplyInstallDestination(destinationDirectory, "Resolved Title", "Resolved Artist");
        entry.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, "resolve failed");
        entry.ApplyInstalledPath(installedPath);

        Assert.AreEqual(installedPath, file.path);
        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.InstalledDestinationResolveFailed));
        Assert.AreEqual(installedPath, entry.Chart.Path);
        Assert.AreEqual(destinationDirectory, entry.Chart.InstallDestination);
        Assert.AreEqual("Resolved Title", entry.Chart.InstallDestinationTitle);
        Assert.AreEqual("Resolved Artist", entry.Chart.InstallDestinationArtist);
        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
    }

    [TestMethod]
    public void PackageChartEntry_BmsStorageOwnerRecomputesTextGroupOnInstalledPath()
    {
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService _)
        {
            string destinationDirectory = Path.Combine(tempRoot, "Installed");
            Directory.CreateDirectory(destinationDirectory);
            string installedPath = Path.Combine(destinationDirectory, "chart.bms");
            File.WriteAllText(installedPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(destinationDirectory, "readme.txt"), "text group");
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "chart.bms"));
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));

            entry.ApplyInstalledPath(installedPath);

            Assert.AreEqual(1, file.txt);
            Assert.AreEqual(1, entry.Chart.GetBmsStorageOwner()?.txt);
        });
    }

    [TestMethod]
    public void PackageChartEntry_BmsStorageOwnerClearsStaleTextGroupOnInstalledPath()
    {
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService _)
        {
            string destinationDirectory = Path.Combine(tempRoot, "Installed");
            Directory.CreateDirectory(destinationDirectory);
            string installedPath = Path.Combine(destinationDirectory, "chart.bms");
            File.WriteAllText(installedPath, "#PLAYER 1");
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "chart.bms"));
            file.SetTextGroupFlag(1);
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));

            entry.ApplyInstalledPath(installedPath);

            Assert.AreEqual(0, file.txt);
            Assert.AreEqual(0, entry.Chart.GetBmsStorageOwner()?.txt);
        });
    }

    [TestMethod]
    public void PackageChartEntry_BmsStorageOwnerSetsDateFromInstalledFileMtime()
    {
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService _)
        {
            string destinationDirectory = Path.Combine(tempRoot, "Installed");
            Directory.CreateDirectory(destinationDirectory);
            string installedPath = Path.Combine(destinationDirectory, "chart.bms");
            DateTime installedTimestamp = new(2026, 6, 9, 2, 30, 0, DateTimeKind.Utc);
            File.WriteAllText(installedPath, "#PLAYER 1");
            File.SetLastWriteTimeUtc(installedPath, installedTimestamp);
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "chart.bms"));
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));

            entry.ApplyInstalledPath(installedPath);

            int expectedDate = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(installedPath));
            Assert.AreEqual(expectedDate, file.date);
            Assert.AreEqual(expectedDate, entry.Chart.GetBmsStorageOwner()?.date);
        });
    }

    [TestMethod]
    public void PackageChartEntry_BmsStorageOwnerReplacesStaleDateOnInstalledPath()
    {
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService _)
        {
            string destinationDirectory = Path.Combine(tempRoot, "Installed");
            Directory.CreateDirectory(destinationDirectory);
            string installedPath = Path.Combine(destinationDirectory, "chart.bms");
            DateTime installedTimestamp = new(2026, 6, 9, 3, 45, 0, DateTimeKind.Utc);
            File.WriteAllText(installedPath, "#PLAYER 1");
            File.SetLastWriteTimeUtc(installedPath, installedTimestamp);
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "chart.bms"));
            file.date = 12345;
            file.adddate = 67890;
            PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));

            entry.ApplyInstalledPath(installedPath);

            int expectedDate = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(installedPath));
            Assert.AreEqual(expectedDate, file.date);
            Assert.IsFalse(file.adddate.HasValue);
        });
    }

    [TestMethod]
    public void PackageChartEntry_BmsStorageOwnerProjectsPackageWarningsWithoutHidingStorageWarnings()
    {
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Pending", "chart.bms"));
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));

        entry.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, "resolve failed");
        file.SetWarning(ChartWarningKind.DuplicateChart, "duplicate warning");

        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
    }

    [TestMethod]
    public void PackageChartEntry_SetSearchingStatus_ProjectsBmsStatusWithoutMutatingStorageOwner()
    {
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Pending", "chart.bms"));
        file.status = BMSFile.BMSFileStatus.PLAY;
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));

        entry.SetSearchingStatus(isSearching: true);

        Assert.AreEqual(BMSFile.BMSFileStatus.PLAY, file.status);
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.PLAY));
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));

        file.status |= BMSFile.BMSFileStatus.PAUSE;

        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.PAUSE));
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));

        entry.SetSearchingStatus(isSearching: false);

        Assert.AreEqual(BMSFile.BMSFileStatus.PLAY | BMSFile.BMSFileStatus.PAUSE, file.status);
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.PLAY));
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.PAUSE));
        Assert.IsFalse(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
    }

    [TestMethod]
    public void PackageChartEntry_SetSearchingStatus_ProjectsAdapterlessBmsonStatus()
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine("C:\\Pending", "chart.bmson"),
            title = "BMSON",
            artist = "Artist",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(song));

        entry.SetSearchingStatus(isSearching: true);

        Assert.IsNull(entry.GetBmsOwnerForTest());
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));

        entry.SetSearchingStatus(isSearching: false);

        Assert.IsNull(entry.GetBmsOwnerForTest());
        Assert.AreEqual(ChartFileStatus.NONE, entry.Chart.Status);
    }

    [TestMethod]
    public void ChartPackage_GetOrBuildInstallEstimationSnapshot_ResolvesPathMatchedBmsonTargetToPackageEntry()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string soundDir = Path.Combine(sourceDir, "sound");
                Directory.CreateDirectory(soundDir);
                string bmsonPath = Path.Combine(sourceDir, "chart.bmson");
                File.WriteAllText(
                    bmsonPath,
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7},"
                        + "\"sound_channels\":[{\"name\":\"sound/keysound.wav\",\"notes\":[]}]}");
                File.WriteAllText(Path.Combine(soundDir, "keysound.wav"), "audio");

                var package = new ChartPackage
                {
                    path = sourceDir,
                    delete_parent = true
                };
                TestableBmsFile detachedCompatibilityAdapter = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsonPath);

                PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [detachedCompatibilityAdapter]);

                Assert.AreEqual(1, snapshot.ChartCount);
                Assert.AreEqual(ChartFileKind.Bmson, snapshot.RepresentativeChart.Kind);
                Assert.IsNotNull(snapshot.RepresentativeChart.GetBmsonStorageOwner());
                Assert.AreEqual(1, snapshot.DefinedResources.AudioReferenceCount);
            });
    }

    [TestMethod]
    public void ChartPackage_GetOrBuildInstallEstimationSnapshot_UsesPackageEntriesWhenTargetAdaptersAreEmpty()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string soundDir = Path.Combine(sourceDir, "sound");
                Directory.CreateDirectory(soundDir);
                File.WriteAllText(
                    Path.Combine(sourceDir, "chart.bmson"),
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7},"
                        + "\"sound_channels\":[{\"name\":\"sound/keysound.wav\",\"notes\":[]}]}");
                File.WriteAllText(Path.Combine(soundDir, "keysound.wav"), "audio");

                var package = new ChartPackage
                {
                    path = sourceDir
                };

                PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, []);

                Assert.AreEqual(1, snapshot.ChartCount);
                Assert.AreEqual(ChartFileKind.Bmson, snapshot.RepresentativeChart.Kind);
                Assert.AreEqual(1, snapshot.DefinedResources.TotalReferenceCount);
                Assert.AreEqual("bmson", snapshot.TargetMetadataProfile.DominantNormalizedTitle);
            });
    }

    [TestMethod]
    public void ChartPackage_PathPackage_DiscoversBmsonAsChartEntryWithoutCompatibilityAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string soundDir = Path.Combine(sourceDir, "sound");
                Directory.CreateDirectory(soundDir);
                File.WriteAllText(
                    Path.Combine(sourceDir, "chart.bmson"),
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7},"
                        + "\"sound_channels\":[{\"name\":\"sound/keysound.wav\",\"notes\":[]}]}");

                var package = new ChartPackage
                {
                    path = sourceDir
                };

                List<PackageChartEntry> entries = package.ChartEntries;
                PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, []);

                Assert.AreEqual(1, entries.Count);
                Assert.AreEqual(ChartFileKind.Bmson, entries[0].Chart.Kind);
                Assert.IsNull(entries[0].GetBmsOwnerForTest());
                Assert.IsNull(entries[0].GetBmsOwnerForTest());
                Assert.AreEqual(1, snapshot.ChartCount);
                Assert.AreEqual(1, snapshot.DefinedResources.AudioReferenceCount);
            });
    }

    [TestMethod]
    public void ChartPackage_DisplayTitle_UsesChartEntriesWithoutMaterializingBmsonAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(
                    Path.Combine(sourceDir, "chart.bmson"),
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7}}");
                var package = new ChartPackage
                {
                    path = sourceDir
                };
                List<PackageChartEntry> entries = package.ChartEntries;

                string displayTitle = package.DisplayTitle;

                Assert.AreEqual("BMSON", displayTitle);
                Assert.AreEqual(1, entries.Count);
                Assert.IsNull(entries[0].GetBmsOwnerForTest());
            });
    }

    [TestMethod]
    public void ChartPackage_RemoveChartEntriesPredicate_RemovesAdapterlessBmsonEntryWithoutMaterializing()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string keepBmsonPath = Path.Combine(sourceDir, "keep.bmson");
                string removeBmsonPath = Path.Combine(sourceDir, "remove.bmson");
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(
                    keepBmsonPath,
                    "{\"info\":{\"title\":\"Keep\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7}}");
                File.WriteAllText(
                    removeBmsonPath,
                    "{\"info\":{\"title\":\"Remove\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7}}");
                var package = new ChartPackage
                {
                    path = sourceDir
                };
                List<PackageChartEntry> entries = package.ChartEntries;
                PackageChartEntry removeEntry = entries.Single(entry => string.Equals(entry.Chart.Path, removeBmsonPath, StringComparison.OrdinalIgnoreCase));

                package.RemoveChartEntries(entry =>
                    !string.IsNullOrWhiteSpace(entry?.Chart?.Path)
                    && string.Equals(entry?.Chart?.Path, removeBmsonPath, StringComparison.OrdinalIgnoreCase));

                Assert.IsNull(removeEntry.GetBmsOwnerForTest());
                Assert.AreEqual(1, package.ChartEntries.Count);
                Assert.AreEqual(keepBmsonPath, package.ChartEntries[0].Chart.Path);
            });
    }

    [TestMethod]
    public void ChartPackage_DisplayTitle_PreservesMultiChartRawTitle()
    {
        TestableBmsFile primary = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Pending", "a.bms"));
        primary.SetTitleParts("Main Title", "Sub Title");
        TestableBmsFile secondary = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Pending", "b.bms"));
        var package = ChartPackageTestExtensions.CreatePackage([primary, secondary]);

        Assert.AreEqual("Main Title", package.DisplayTitle);
    }

    [TestMethod]
    public void ChartPackage_BuildInstallEstimationSnapshot_ResolvesBatchPathMatchedBmsonTargetToPackageEntry()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string soundDir = Path.Combine(sourceDir, "sound");
                Directory.CreateDirectory(soundDir);
                string bmsonPath = Path.Combine(sourceDir, "chart.bmson");
                File.WriteAllText(
                    bmsonPath,
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7},"
                        + "\"sound_channels\":[{\"name\":\"sound/keysound.wav\",\"notes\":[]}]}");
                File.WriteAllText(Path.Combine(soundDir, "keysound.wav"), "audio");

                var package = new ChartPackage
                {
                    path = sourceDir,
                    delete_parent = true
                };
                TestableBmsFile detachedCompatibilityAdapter = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", bmsonPath);
                PackageInstallSurfaceSnapshot sourceSurface = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(sourceDir);

                PackageInstallEstimationSnapshot snapshot = package.BuildInstallEstimationSnapshotFromEntries(
                    ResolvePackageEntries(package, [detachedCompatibilityAdapter]),
                    sourceSurface,
                    sourceSurfaceCacheHit: false,
                    sourceSurfaceBatchHit: true);

                Assert.AreEqual(1, snapshot.ChartCount);
                Assert.AreEqual(ChartFileKind.Bmson, snapshot.RepresentativeChart.Kind);
                Assert.IsNotNull(snapshot.RepresentativeChart.GetBmsonStorageOwner());
                Assert.AreEqual(1, snapshot.DefinedResources.AudioReferenceCount);
                Assert.IsTrue(snapshot.SourceSurfaceBatchHit);
            });
    }

    [TestMethod]
    public void ChartPackage_PathPackage_InvalidatesChartDiscoveryAndSourceSurfaceSnapshots_WhenPathChanges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDirA = Path.Combine(tempRoot, "PathPackageA");
                string sourceDirB = Path.Combine(tempRoot, "PathPackageB");
                Directory.CreateDirectory(sourceDirA);
                Directory.CreateDirectory(sourceDirB);
                File.WriteAllText(Path.Combine(sourceDirA, "a.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDirB, "b.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDirB, "01.wav"), "audio");

                var package = new ChartPackage
                {
                    path = sourceDirA,
                    delete_parent = true
                };

                List<BMSFile> filesFromA = package.GetBmsOwnersForTest();
                PackageInstallEstimationSnapshot snapshotA = BuildPackageSnapshot(package, filesFromA);

                package.path = sourceDirB;
                List<BMSFile> filesFromB = package.GetBmsOwnersForTest();
                PackageInstallEstimationSnapshot snapshotB = BuildPackageSnapshot(package, filesFromB);
                PackageInstallEstimationSnapshot cachedSnapshotB = BuildPackageSnapshot(package, filesFromB);

                Assert.AreEqual(1, filesFromA.Count);
                Assert.AreEqual(1, filesFromB.Count);
                Assert.AreEqual(Path.Combine(sourceDirB, "b.bms"), filesFromB[0].path);
                Assert.AreEqual(sourceDirA, snapshotA.SourceDirectory);
                Assert.AreEqual(sourceDirB, snapshotB.SourceDirectory);
                Assert.IsFalse(snapshotA.SourceSurfaceCacheHit);
                Assert.IsFalse(snapshotB.SourceSurfaceCacheHit);
                Assert.IsTrue(cachedSnapshotB.SourceSurfaceCacheHit);
                Assert.AreEqual("bounded_fast_source_surface", snapshotB.SourceSurfaceScanBackend);
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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 1), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", "02.wav"]);
            lookupCache.AddDir(expectedDir, ["00.wav", "01.wav"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            string debugSummary = (result.ResourceSummary ?? string.Empty) + " || " + (result.TopCandidateSummary ?? string.Empty) + " || " + (result.SelectedCandidateSummary ?? string.Empty);
            Assert.AreEqual(expectedDir, result.DestinationDirectory, debugSummary);
            Assert.AreEqual(expectedDir, result.SelectedCandidate?.DirectoryPath, debugSummary);
            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence, debugSummary);
            Assert.IsTrue(result.ShouldAutoApplyDestination, debugSummary);
        });
    }

    [TestMethod]
    public void EstimateInstallationDirectory_PackageUnionDoesNotMatchBundledResourceByBasenameOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string sourceSoundDir = Path.Combine(sourceDir, "sound");
            string candidateDir = Path.Combine(tempRoot, "Candidate");
            Directory.CreateDirectory(sourceSoundDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceSoundDir, "02.wav"), "bundled");
            File.WriteAllText(Path.Combine(candidateDir, "00.wav"), "existing");
            File.WriteAllText(Path.Combine(candidateDir, "01.wav"), "existing");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav", "02.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", "sound\\02.wav"]);
            lookupCache.AddDir(candidateDir, ["00.wav", "01.wav"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.IsFalse(result.ShouldAutoApplyDestination, result.SelectedCandidateSummary);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory), result.SelectedCandidateSummary);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter, result.SelectedCandidateSummary);
            Assert.AreEqual(0, result.CandidateDirectoryCountAfterAudioGate, result.SelectedCandidateSummary);
            Assert.IsNull(result.SelectedCandidate, result.SelectedCandidateSummary);
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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(weakDir, ["00.wav"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
            Assert.IsFalse(result.HasViableDestination);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
            Assert.AreEqual(0, result.SuggestedDestinationDirectories.Count);
        });
    }

    [TestMethod]
    public void ValidateInstallDestination_ReturnsResolvedDirectoryForPendingPackage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string pendingDirectoryPath = Path.Combine(tempRoot, "pending");
            string installDirectoryPath = Path.Combine(tempRoot, "install");
            Directory.CreateDirectory(pendingDirectoryPath);
            Directory.CreateDirectory(installDirectoryPath);
            TestableBmsFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(pendingDirectoryPath, "chart.bms"));
            var pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingDirectoryPath;
            pendingPackage.delete_parent = false;

            PendingInstallDestinationSelectionResult result = service.ValidateInstallDestination(
                PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingFile)),
                [pendingPackage],
                [installDirectoryPath],
                installDirectoryPath);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(installDirectoryPath, result.ValidatedDestinationDirectory);
            Assert.AreEqual(1, result.TargetEntries.Count);
            Assert.AreSame(pendingFile, result.TargetEntries[0].GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void ValidateInstallDestination_MatchesAdapterlessBmsonPackageByChartPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string pendingDirectoryPath = Path.Combine(tempRoot, "pending");
                string installDirectoryPath = Path.Combine(tempRoot, "install");
                Directory.CreateDirectory(pendingDirectoryPath);
                Directory.CreateDirectory(installDirectoryPath);
                string bmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
                File.WriteAllText(
                    bmsonPath,
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7}}");
                PackageChartEntry targetEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(bmsonPath)));
                var pendingPackage = new ChartPackage
                {
                    path = pendingDirectoryPath,
                    delete_parent = false
                };
                List<PackageChartEntry> entries = pendingPackage.ChartEntries;
                Assert.AreEqual(1, entries.Count);
                Assert.IsNull(entries[0].GetBmsOwnerForTest());

                PendingInstallDestinationSelectionResult result = service.ValidateInstallDestination(
                    targetEntry,
                    [pendingPackage],
                    [installDirectoryPath],
                    installDirectoryPath);

                Assert.IsTrue(result.Success);
                Assert.AreEqual(installDirectoryPath, result.ValidatedDestinationDirectory);
                Assert.AreEqual(1, result.TargetEntries.Count);
                Assert.AreEqual(bmsonPath, result.TargetEntries[0].Chart.Path);
                Assert.IsNull(entries[0].GetBmsOwnerForTest());
            });
    }

    [TestMethod]
    public void ValidateInstallDestination_DoesNotMaterializeAdapterlessBmsonWhenDestinationIsInvalid()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string pendingDirectoryPath = Path.Combine(tempRoot, "pending");
                Directory.CreateDirectory(pendingDirectoryPath);
                string bmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
                File.WriteAllText(
                    bmsonPath,
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7}}");
                PackageChartEntry targetEntry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(bmsonPath)));
                var pendingPackage = new ChartPackage
                {
                    path = pendingDirectoryPath,
                    delete_parent = false
                };
                List<PackageChartEntry> entries = pendingPackage.ChartEntries;
                Assert.AreEqual(1, entries.Count);
                Assert.IsNull(entries[0].GetBmsOwnerForTest());

                PendingInstallDestinationSelectionResult result = service.ValidateInstallDestination(
                    targetEntry,
                    [pendingPackage],
                    [Path.Combine(tempRoot, "install")],
                    Path.Combine(tempRoot, "missing"));

                Assert.IsFalse(result.Success);
                Assert.AreEqual(1, result.TargetEntries.Count);
                Assert.IsNull(entries[0].GetBmsOwnerForTest());
            });
    }

    [TestMethod]
    public void CorrectChartInstallationDirectory_ClearsSameDirectorySuggestion()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Music", "FolderA", "chart.bms"));
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));

        service.CorrectChartInstallationDirectory([entry], delegate (PackageChartEntry target)
        {
            target.SetInstallDestinationPathOnly(Path.Combine("C:\\Music", "FolderA"));
        });

        Assert.AreEqual(string.Empty, entry.Chart.InstallDestination);
    }

    [TestMethod]
    public void ClearInstallDestinations_ClearsResolveFailedWarningOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Music", "FolderA", "chart.bms"));
        PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));
        entry.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, "resolve failed");

        service.ClearInstallDestinations([entry]);

        Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
        Assert.IsFalse(file.Warnings.ToStructuredList().Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
    }

    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_AutoAppliesOnlyImprovedUniqueCandidate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Music", "Current");
        string candidateDir = Path.Combine("C:\\Music", "Candidate");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateDir, ["sound.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.ReinstallCorrection);

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

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms", "sound.wav"]);
        lookupCache.AddDir(candidateDir, ["sound.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.ReinstallCorrection);

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

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms", "sound.wav"]);
        lookupCache.AddDir(candidateDir, []);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.ReinstallCorrection);

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

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateADir, ["sound.wav"]);
            lookupCache.AddDir(candidateBDir, ["sound.wav"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.ReinstallCorrection);

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

            var file = BMSFile.CreateBMSFileFromFile(chartPath);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["installed.bms", "sound.wav"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.ReinstallCorrection,
                null,
                delegate (string directoryPath)
                {
                    return string.Equals(directoryPath, candidateDir, StringComparison.OrdinalIgnoreCase)
                        ? InstallEstimationMetadataNormalizer.BuildProfile([("Completely Different", "Another Artist", Path.Combine(candidateDir, "installed.bms"))])
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
    public void CreateInstalledChartLookupIndexSnapshot_IncludesBmsonMd5AndSha256Directories()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string installDir = Path.Combine("C:\\Installed", "Bmson");
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine(installDir, "chart.bmson"),
            folder = installDir,
            title = "Title",
            artist = "Artist",
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256 = new string('b', 64)
        };

        InstalledChartLookupIndexSnapshot result = CreateInstalledChartLookupIndexSnapshot([], [bmsonSong]);

        CollectionAssert.AreEqual(new[] { installDir }, result.Md5Directories[bmsonSong.md5].ToArray());
        CollectionAssert.AreEqual(new[] { installDir }, result.Sha256Directories[bmsonSong.sha256].ToArray());
        CollectionAssert.AreEqual(new[] { installDir }, result.KnownChartDirectories.ToList());
    }

    [TestMethod]
    public void CreateInstalledChartLookupIndexSnapshot_DoesNotRegisterMd5lessRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string installDir = Path.Combine("C:\\Installed", "ShaOnly");
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine(installDir, "chart.bmson"),
            folder = installDir,
            md5 = null,
            sha256 = new string('b', 64)
        };

        InstalledChartLookupIndexSnapshot result = CreateInstalledChartLookupIndexSnapshot([], [bmsonSong]);

        Assert.AreEqual(0, result.DistinctPrimaryHashCount);
        Assert.IsFalse(result.Sha256Directories.ContainsKey(bmsonSong.sha256));
        Assert.AreEqual(0, result.KnownChartDirectories.Count);
    }

    [TestMethod]
    public void CreateInstalledChartLookupIndexSnapshot_CountsPrimaryHashesForExcludingLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string duplicateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string otherHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        TestableBmsFile first = CreateFile(duplicateHash, "C:\\Installed\\A\\chart.bms");
        TestableBmsFile second = CreateFile(duplicateHash, "C:\\Installed\\B\\chart.bms");
        TestableBmsFile other = CreateFile(otherHash, "C:\\Installed\\C\\chart.bms");

        InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot([first, second, other]);

        Assert.AreEqual(2, snapshot.DistinctPrimaryHashCount);
        Assert.AreEqual(2, snapshot.GetPrimaryHashCount(duplicateHash));
        Assert.IsTrue(snapshot.ContainsPrimaryHashAfterExcluding(duplicateHash, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [duplicateHash.ToUpperInvariant()] = 1
        }));
        Assert.IsFalse(snapshot.ContainsPrimaryHashAfterExcluding(duplicateHash, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [duplicateHash] = 2
        }));
        IPrimaryHashLookup excludingOne = snapshot.CreateExcludingLookup(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [duplicateHash] = 1
        });

        Assert.IsTrue(excludingOne.ContainsPrimaryHash(duplicateHash));
        Assert.AreEqual(1, excludingOne.GetPrimaryHashCount(duplicateHash));
        Assert.IsTrue(excludingOne.ContainsPrimaryHash(otherHash));
        IPrimaryHashLookup excludingAll = snapshot.CreateExcludingLookup(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [duplicateHash] = 2
        });

        Assert.IsFalse(excludingAll.ContainsPrimaryHash(duplicateHash));
        Assert.AreEqual(0, excludingAll.GetPrimaryHashCount(duplicateHash));
        IPrimaryHashLookup excludingUnknown = snapshot.CreateExcludingLookup(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["cccccccccccccccccccccccccccccccc"] = 1
        });

        Assert.AreEqual(2, excludingUnknown.DistinctPrimaryHashCount);
    }

    [TestMethod]
    public void OwnedChartCollectionState_CreateInstalledChartLookupIndexBuildsBmsAndBmsonLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string bmsDir = Path.Combine("C:\\Installed", "Bms");
        string bmsonDir = Path.Combine("C:\\Installed", "Bmson");
        TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(bmsDir, "chart.bms"));
        bmsFile.SetSha256(new string('b', 64));
        TestableBmsFile pathlessBms = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", string.Empty);
        pathlessBms.SetSha256(new string('f', 64));
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine(bmsonDir, "chart.bmson"),
            folder = bmsonDir,
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('d', 64)
        };
        var pathlessBmson = new LR2SongDBExtended.bmson_song
        {
            path = string.Empty,
            md5 = "11111111111111111111111111111111",
            sha256 = new string('2', 64)
        };

        InstalledChartLookupIndexSnapshot snapshot = OwnedChartCollectionState
            .FromStorageRows([bmsFile, pathlessBms], [bmsonSong, pathlessBmson])
            .CreateInstalledChartLookupIndexState(out int bmsCount, out int bmsonCount)
            .CreateSnapshot();

        Assert.AreEqual(1, bmsCount);
        Assert.AreEqual(1, bmsonCount);
        CollectionAssert.AreEqual(new[] { bmsDir }, snapshot.Md5Directories[bmsFile.hash].ToArray());
        CollectionAssert.AreEqual(new[] { bmsDir }, snapshot.Sha256Directories[bmsFile.sha256].ToArray());
        CollectionAssert.AreEqual(new[] { bmsonDir }, snapshot.Md5Directories[bmsonSong.md5].ToArray());
        CollectionAssert.AreEqual(new[] { bmsonDir }, snapshot.Sha256Directories[bmsonSong.sha256].ToArray());
        Assert.IsTrue(snapshot.KnownChartDirectories.Contains(bmsDir));
        Assert.IsTrue(snapshot.KnownChartDirectories.Contains(bmsonDir));
        Assert.AreEqual(2, snapshot.DistinctPrimaryHashCount);
        Assert.IsFalse(snapshot.ContainsPrimaryHash(pathlessBms.hash));
        Assert.IsFalse(snapshot.ContainsPrimaryHash(pathlessBmson.md5));
    }

    [TestMethod]
    public void InstalledChartLookupIndexState_DecrementsPrimaryHashAndDirectoryCounts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string firstDir = Path.Combine("C:\\Installed", "First");
        string secondDir = Path.Combine("C:\\Installed", "Second");
        var state = new InstalledChartLookupIndexState();
        state.AddChart(Path.Combine(firstDir, "a.bms"), hash, null);
        state.AddChart(Path.Combine(secondDir, "b.bms"), hash, null);

        Assert.AreEqual(2, state.DirectoryReferenceCount);

        state.RemoveChart(Path.Combine(firstDir, "a.bms"), hash, null);
        InstalledChartLookupIndexSnapshot partial = state.CreateSnapshot();

        Assert.IsTrue(partial.ContainsPrimaryHash(hash));
        Assert.AreEqual(1, partial.GetPrimaryHashCount(hash));
        Assert.AreEqual(1, state.DirectoryReferenceCount);
        CollectionAssert.AreEqual(new[] { secondDir }, partial.Md5Directories[hash].ToArray());

        state.RemoveChart(Path.Combine(secondDir, "b.bms"), hash, null);
        InstalledChartLookupIndexSnapshot empty = state.CreateSnapshot();

        Assert.IsFalse(empty.ContainsPrimaryHash(hash));
        Assert.AreEqual(0, state.DirectoryReferenceCount);
        Assert.IsFalse(empty.Md5Directories.ContainsKey(hash));
        Assert.IsFalse(empty.KnownChartDirectories.Contains(firstDir));
        Assert.IsFalse(empty.KnownChartDirectories.Contains(secondDir));
    }

    [TestMethod]
    public void InstalledChartLookupIndexState_CountsDistinctDirectoryReferences()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string sha256 = new string('b', 64);
        string dir = Path.Combine("C:\\Installed", "SameDir");
        string firstPath = Path.Combine(dir, "a.bms");
        string secondPath = Path.Combine(dir, "b.bms");
        var state = new InstalledChartLookupIndexState();

        state.AddChart(firstPath, md5, sha256);
        state.AddChart(secondPath, md5, sha256);

        Assert.AreEqual(2, state.DirectoryReferenceCount);

        state.RemoveChart(firstPath, md5, sha256);

        Assert.AreEqual(2, state.DirectoryReferenceCount);

        state.RemoveChart(secondPath, md5, sha256);

        Assert.AreEqual(0, state.DirectoryReferenceCount);
    }

    [TestMethod]
    public void InstalledChartLookupIndexState_CountsUniquePrimaryHashesByDirectory()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string duplicateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string otherHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string bmsonHash = "cccccccccccccccccccccccccccccccc";
        string firstDir = Path.Combine("C:\\Installed", "First");
        string secondDir = Path.Combine("C:\\Installed", "Second");
        var state = new InstalledChartLookupIndexState();
        state.AddChart(Path.Combine(firstDir, "a.bms"), duplicateHash, null);
        state.AddChart(Path.Combine(firstDir, "a-copy.bms"), duplicateHash, null);
        state.AddChart(Path.Combine(firstDir, "b.bms"), otherHash, null);
        state.AddChart(Path.Combine(firstDir, "c.bmson"), bmsonHash, null);
        state.AddChart(Path.Combine(secondDir, "a.bms"), duplicateHash, null);

        InstalledChartLookupIndexSnapshot initial = state.CreateSnapshot();

        Assert.AreEqual(3, initial.GetUniquePrimaryHashCountByDirectory(firstDir));
        Assert.AreEqual(1, initial.GetUniquePrimaryHashCountByDirectory(secondDir));

        state.RemoveChart(Path.Combine(firstDir, "b.bms"), otherHash, null);
        state.MoveChart(Path.Combine(secondDir, "a.bms"), Path.Combine(firstDir, "moved-a.bms"), duplicateHash, null);
        InstalledChartLookupIndexSnapshot updated = state.CreateSnapshot();

        Assert.AreEqual(2, updated.GetUniquePrimaryHashCountByDirectory(firstDir));
        Assert.AreEqual(0, updated.GetUniquePrimaryHashCountByDirectory(secondDir));
    }

    [TestMethod]
    public void InstalledChartLookupIndexState_TracksPathsByPrimaryHash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string firstPath = Path.Combine("C:\\Installed", "First", "a.bms");
        string secondPath = Path.Combine("C:\\Installed", "Second", "b.bms");
        var state = new InstalledChartLookupIndexState();

        state.AddChart(firstPath, hash, null);
        state.AddChart(secondPath, hash, null);

        CollectionAssert.AreEqual(new[] { firstPath, secondPath }, state.GetPathsByPrimaryHash(hash).ToArray());

        state.RemoveChart(firstPath, hash, null);

        CollectionAssert.AreEqual(new[] { secondPath }, state.GetPathsByPrimaryHash(hash).ToArray());
    }

    [TestMethod]
    public void InstalledChartLookupIndexState_CreateExcludingLookupFreezesBaselineCounts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string firstPath = Path.Combine("C:\\Installed", "First", "a.bms");
        string secondPath = Path.Combine("C:\\Installed", "Second", "b.bms");
        var state = new InstalledChartLookupIndexState();
        state.AddChart(firstPath, hash, null);
        state.AddChart(secondPath, hash, null);

        IPrimaryHashLookup lookup = state.CreateExcludingLookup(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [hash] = 1
        });
        state.RemoveChart(firstPath, hash, null);

        Assert.IsTrue(lookup.ContainsPrimaryHash(hash));
        Assert.AreEqual(1, lookup.GetPrimaryHashCount(hash));
    }

    [TestMethod]
    public void InstalledChartLookupIndexState_KeepsPrimaryHashUntilAllDistinctPathsAreRemoved()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string firstPath = Path.Combine("C:\\Installed", "First", "chart.bms");
        string secondPath = Path.Combine("C:\\Installed", "Second", "chart.bms");
        var state = new InstalledChartLookupIndexState();

        state.AddChart(firstPath, hash, null);
        state.AddChart(secondPath, hash, null);

        state.RemoveChart(firstPath, hash, null);

        CollectionAssert.AreEqual(new[] { secondPath }, state.GetPathsByPrimaryHash(hash).ToArray());
        Assert.AreEqual(1, state.GetPrimaryHashCount(hash));

        state.RemoveChart(secondPath, hash, null);

        Assert.AreEqual(0, state.GetPathsByPrimaryHash(hash).Count);
        Assert.AreEqual(0, state.GetPrimaryHashCount(hash));
    }

    [TestMethod]
    public void InstalledChartLookupIndexState_MoveChartUpdatesDirectoryLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string oldDir = Path.Combine("C:\\Installed", "Old");
        string newDir = Path.Combine("C:\\Installed", "New");
        var state = new InstalledChartLookupIndexState();
        state.AddChart(Path.Combine(oldDir, "chart.bms"), hash, null);

        state.MoveChart(Path.Combine(oldDir, "chart.bms"), Path.Combine(newDir, "chart.bms"), hash, null);
        InstalledChartLookupIndexSnapshot snapshot = state.CreateSnapshot();

        CollectionAssert.AreEqual(new[] { newDir }, snapshot.Md5Directories[hash].ToArray());
        Assert.IsFalse(snapshot.KnownChartDirectories.Contains(oldDir));
        Assert.IsTrue(snapshot.KnownChartDirectories.Contains(newDir));
        Assert.AreEqual(1, snapshot.GetPrimaryHashCount(hash));
        CollectionAssert.AreEqual(new[] { Path.Combine(newDir, "chart.bms") }, state.GetPathsByPrimaryHash(hash).ToArray());
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

            LR2SongDBExtended.bmson_song pendingSong = BmsonSongParser.Parse(bmsonPath);
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingSong))]);
            package.path = sourceDir;
            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, []);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bmson"]);
            lookupCache.AddDir(candidateDir, ["keysound.wav"]);

            Assert.AreEqual(ChartFileKind.Bmson, snapshot.RepresentativeChart.Kind);
            Assert.AreEqual(pendingSong.path, snapshot.RepresentativeChart.Path);
            Assert.AreSame(pendingSong, snapshot.RepresentativeChart.GetBmsonStorageOwner());
            Assert.IsNull(snapshot.RepresentativeChart.GetBmsStorageOwner());
            CollectionAssert.Contains(snapshot.DefinedResources.AudioRelativePaths.ToArray(), "keysound");

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(candidateDir, result.DestinationDirectory);
            StringAssert.Contains(result.ResourceSummary ?? string.Empty, "chart=" + bmsonPath);
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

            List<string> oggNames = [.. Enumerable.Range(1, 77).Select(index => string.Format("sound{0:00}.ogg", index))];
            var soundChannelsBuilder = new StringBuilder();
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

            LR2SongDBExtended.bmson_song pending = BmsonSongParser.Parse(bmsonPath);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["_circ_double_hard.bmson"]);
            lookupCache.AddDir(candidateDir, oggNames.Concat(["preview.ogg", "banner.png", "cover.jpg"]));
            lookupCache.AddDir(decoyDir, oggNames.Take(10));

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pending))],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateADir, ["sound.wav"]);
        lookupCache.AddDir(candidateBDir, ["sound.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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

                var file = BMSFile.CreateBMSFileFromFile(chartPath);
                file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

                var lookupCache = new DirectoryResourceLookupCache();
                lookupCache.AddDir(sourceDir, ["chart.bms"]);
                lookupCache.AddDir(candidateADir, ["sound.wav"]);
                lookupCache.AddDir(candidateBDir, ["sound.wav"]);

                var metadataProfiles = new Dictionary<string, InstallEstimationMetadataProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    [candidateADir] = InstallEstimationMetadataNormalizer.BuildProfile([("Target Song", "Artist", Path.Combine(candidateADir, "a.bms"))]),
                    [candidateBDir] = InstallEstimationMetadataNormalizer.BuildProfile([("Target Song", "Artist", Path.Combine(candidateBDir, "b.bms"))])
                };

                InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                    [file],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    lookupCache,
                    asParallel: false,
                    ChartInstallationEstimateMode.Normal,
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
    public void EstimateInstallationDirectory_CandidateDirectoryHashCountTieBreakUsesFullTiedFrontier()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateADir = Path.Combine("C:\\Installed", "A");
        string candidateBDir = Path.Combine("C:\\Installed", "B");
        string candidateCDir = Path.Combine("C:\\Installed", "C");
        string candidateDDir = Path.Combine("C:\\Installed", "D");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateADir, ["sound.wav"]);
        lookupCache.AddDir(candidateBDir, ["sound.wav"]);
        lookupCache.AddDir(candidateCDir, ["sound.wav"]);
        lookupCache.AddDir(candidateDDir, ["sound.wav"]);
        var directoryHashCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [candidateADir] = 1,
            [candidateBDir] = 1,
            [candidateCDir] = 1,
            [candidateDDir] = 5
        };

        InstallEstimationResult result = service.EstimateInstallationDirectoryForCandidateDirectories(
            BuildLooseChartSnapshot([file], new HashSet<string>(StringComparer.OrdinalIgnoreCase), ChartInstallationEstimateMode.Normal)!,
            [candidateADir, candidateBDir, candidateCDir, candidateDDir],
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal,
            representativeMetadataResolver: null,
            metadataProfileResolver: null,
            candidateDirectoryUniquePrimaryHashCountResolver: path => directoryHashCounts.TryGetValue(path, out int count) ? count : 0);

        Assert.AreEqual(candidateDDir, result.DestinationDirectory);
        Assert.AreEqual(candidateDDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
        Assert.IsTrue(result.ShouldAutoApplyDestination);
        Assert.IsTrue(result.DirectoryHashCountTieBreakApplied);
        Assert.AreEqual("directory_hash_count_tiebreak_distinct", result.ConfidenceReason);
        StringAssert.Contains(result.DirectoryHashCountTieBreakSummary ?? string.Empty, candidateDDir + ":5");
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

            var file = BMSFile.CreateBMSFileFromFile(chartPath);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateADir, ["sound.wav"]);
            lookupCache.AddDir(candidateBDir, ["sound.wav"]);

            var metadataProfiles = new Dictionary<string, InstallEstimationMetadataProfile>(StringComparer.OrdinalIgnoreCase)
            {
                [candidateADir] = InstallEstimationMetadataNormalizer.BuildProfile([("Another Song", "Someone", Path.Combine(candidateADir, "a.bms"))]),
                [candidateBDir] = InstallEstimationMetadataNormalizer.BuildProfile([("Target Song", "Artist", Path.Combine(candidateBDir, "b.bms"))])
            };

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal,
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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 1), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", "sound.wav"]);
            lookupCache.AddDir(candidateDir, ["sound.wav"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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

                var file = BMSFile.CreateBMSFileFromFile(chartPath);
                file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

                var lookupCache = new DirectoryResourceLookupCache();
                lookupCache.AddDir(sourceDir, ["chart.bms"]);
                lookupCache.AddDir(candidateDir, ["installed.bms", "sound.wav"]);

                InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                    [file],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    lookupCache,
                    asParallel: false,
                    ChartInstallationEstimateMode.Normal,
                    null,
                    delegate (string directoryPath)
                    {
                        return string.Equals(directoryPath, candidateDir, StringComparison.OrdinalIgnoreCase)
                            ? InstallEstimationMetadataNormalizer.BuildProfile([("Completely Different", "Another Artist", Path.Combine(candidateDir, "installed.bms"))])
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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 2), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = chartPath;

            package.delete_parent = false;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", Path.Combine("sound", "00.wav")]);
            lookupCache.AddDir(candidateDir, ["00.wav"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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

            List<string> wavReferences = [];
            List<string> sourceFiles = ["chart.bms"];
            List<string> candidateFiles = [];
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

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, [.. wavReferences]);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: wavReferences.Count, wavExisting: wavReferences.Count), suppressPropertyChanged: true);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, sourceFiles);
            lookupCache.AddDir(candidateDir, candidateFiles);
            lookupCache.AddDir(otherCandidateDir, Enumerable.Range(0, 399).Select(i => i.ToString("000") + ".wav").Concat(["candidate-extra.wav"]));

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

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
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateDir, ["sound.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal,
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
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(flatCandidateDir, ["bgm1.wav"]);
        lookupCache.AddDir(nestedCandidateDir, ["sound\\bgm1.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound\\bgm1.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);


        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
        file.BGAfiles = new HashSet<string>(["clock\\00_001_00.bmp"], StringComparer.OrdinalIgnoreCase);
        file.SetMaintenanceInfo(CreateVisualMaintenanceInfo(file, bgaDefined: 1, bgaExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(flatCandidateDir, ["00_001_00.bmp"]);
        lookupCache.AddDir(nestedCandidateDir, ["clock\\00_001_00.bmp"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
        file.SetMaintenanceInfo(CreateVisualMaintenanceInfo(file, stagefileDefined: true, stagefileExisting: false), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(flatCandidateDir, ["logo.bmp"]);
        lookupCache.AddDir(nestedCandidateDir, ["image\\logo.bmp"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(basenameOnlyCandidateDir, ["bgm1.wav", "bgm2.wav"]);
        lookupCache.AddDir(pathAwareCandidateDir, ["sound\\bgm1.wav", "bgm2.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
        uint parentZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\00");
        uint parentOneRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\01");
        uint childZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\00");
        uint childOneRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\01");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "Child\\sound\\00.wav", "Child\\sound\\01.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true);


        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(
            parentDir,
            [parentZeroRelativeHash, parentOneRelativeHash],
            [],
            []);
        lookupCache.AddDir(
            childDir,
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            [],
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            []);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(parentDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(parentDir, result.DestinationDirectory);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_NestedFlatRelativeAudio_UsesAncestorShadowRuleToPreferChildCandidate()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string parentDir = Path.Combine("C:\\Installed", "Parent");
        string childDir = Path.Combine(parentDir, "Child");
        uint parentZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\00");
        uint parentOneRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\01");
        uint childZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\00");
        uint childOneRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\01");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true);


        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(
            parentDir,
            [parentZeroRelativeHash, parentOneRelativeHash],
            [],
            []);
        lookupCache.AddDir(
            childDir,
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            [],
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            []);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
    public void EstimateInstallationDirectory_FlatAudioRef_MatchesOnlyFlatRelativePath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "A_Nested");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Z_Flat");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(nestedCandidateDir, ["sound\\bgm1.wav"]);
        lookupCache.AddDir(flatCandidateDir, ["bgm1.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, ["bgm1.wav", "sound\\bgm2.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(InstallEstimationFinalEvaluationMode.RelativeStrict, result.FinalEvaluationMode);
        Assert.AreEqual(2, result.TargetResourceCount);
        Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
        Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(2, result.SelectedCandidate?.AudioMatched);
        Assert.AreEqual(candidateDir, result.DestinationDirectory);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_CandidateCount_TreatsSameBasenameDifferentRelativePathsAsDistinct()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Candidate");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, ["bgm1.wav", "sound\\bgm1.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 1), suppressPropertyChanged: true);

            var package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", "sound\\02.wav"]);
            lookupCache.AddDir(candidateDir, ["sound\\00.wav", "sound\\01.wav"]);

            InstallEstimationResult normalResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            InstallEstimationResult mergeResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.MergeCandidateOnly);

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
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav", "sound\\bgm2.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, ["bgm1.wav", "sound\\bgm2.wav"]);

        InstallEstimationResult lookupResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        InstallEstimationResult unavailableResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true);

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, ["bgm1.wav"]);

        InstallEstimationResult lookupResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        InstallEstimationResult unavailableResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
        uint parentZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\00");
        uint parentOneRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\01");
        uint childZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\00");
        uint childOneRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\01");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0), suppressPropertyChanged: true);


        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(
            parentDir,
            [parentZeroRelativeHash, parentOneRelativeHash],
            [],
            []);
        lookupCache.AddDir(
            childDir,
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            [],
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            []);


        InstallEstimationResult lookupResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        InstallEstimationResult unavailableResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

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
    public void GetDistinctInstalledDirectoriesForChart_ChartWithMd5DoesNotFallBackToSha256()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string installDir = Path.Combine("C:\\Installed", "PrimaryOnly");
        TestableBmsFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installDir, "chart.bms"));
        installedFile.SetSha256(new string('b', 64));
        TestableBmsFile pendingFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\chart.bms");
        pendingFile.SetSha256(new string('b', 64));
        ChartFile pendingChart = ChartFileProjection.FromBmsFile(pendingFile, includeWarningSnapshot: false);

        InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot([installedFile]);
        List<string> directories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(snapshot, pendingChart);

        Assert.AreEqual(0, directories.Count);
    }

    [TestMethod]
    public void GetDistinctInstalledDirectoriesForChart_Sha256OnlyChartDoesNotUseSha256()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string installDir = Path.Combine("C:\\Installed", "ShaOnly");
        var bmsonSong = new LR2SongDBExtended.bmson_song
        {
            path = Path.Combine(installDir, "chart.bmson"),
            folder = installDir,
            md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            sha256 = new string('c', 64)
        };
        ChartFile pendingChart = ChartFileProjection.FromBmsonSong(new LR2SongDBExtended.bmson_song
        {
            path = "C:\\Pending\\chart.bmson",
            md5 = null,
            sha256 = new string('c', 64)
        }, includeWarningSnapshot: false);

        InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot([], [bmsonSong]);
        List<string> directories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(snapshot, pendingChart);

        Assert.AreEqual(0, directories.Count);
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
        return new BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot.CreateCurrent(Settings.Default), 70);
    }

    private static InstalledChartLookupIndexSnapshot CreateInstalledChartLookupIndexSnapshot(IEnumerable<BMSFile> installedFiles, IEnumerable<LR2SongDBExtended.bmson_song>? installedBmsonSongs = null)
    {
        var state = new InstalledChartLookupIndexState();
        foreach (BMSFile file in installedFiles ?? [])
        {
            if (file != null)
            {
                state.AddChart(file.path, file.hash, file.sha256);
            }
        }
        foreach (LR2SongDBExtended.bmson_song song in installedBmsonSongs ?? [])
        {
            if (song != null)
            {
                state.AddChart(song.path, song.md5, song.sha256);
            }
        }
        return state.CreateSnapshot();
    }

    private static InstallEstimationResult EstimateLooseChartInstallationDirectory(BmsLibraryInstallEstimationService service, IEnumerable<BMSFile> chartFiles, HashSet<string> installedHashes, DirectoryResourceLookupCache? directoryLookupCache, bool asParallel, ChartInstallationEstimateMode estimateMode, Func<string, InstallDestinationRepresentativeMetadata>? representativeMetadataResolver = null, Func<string, InstallEstimationMetadataProfile>? metadataProfileResolver = null)
    {
        return service.EstimateInstallationDirectory(
            BuildLooseChartSnapshot(chartFiles, installedHashes, estimateMode)!,
            directoryLookupCache!,
            asParallel,
            estimateMode,
            representativeMetadataResolver,
            metadataProfileResolver);
    }

    private static InstallEstimationResult EstimateLooseChartInstallationDirectory(BmsLibraryInstallEstimationService service, IEnumerable<PackageChartEntry> chartEntries, HashSet<string> installedHashes, DirectoryResourceLookupCache? directoryLookupCache, bool asParallel, ChartInstallationEstimateMode estimateMode, Func<string, InstallDestinationRepresentativeMetadata>? representativeMetadataResolver = null, Func<string, InstallEstimationMetadataProfile>? metadataProfileResolver = null)
    {
        return service.EstimateInstallationDirectory(
            BuildLooseChartSnapshot(chartEntries, installedHashes, estimateMode)!,
            directoryLookupCache!,
            asParallel,
            estimateMode,
            representativeMetadataResolver,
            metadataProfileResolver);
    }

    private static InstallEstimationResult EstimateLooseChartInstallationDirectory(BmsLibraryInstallEstimationService service, IEnumerable<BMSFile> chartFiles, HashSet<string> installedHashes, DirectoryResourceLookupCache? directoryLookupCache, int candidateEvaluationDegree, ChartInstallationEstimateMode estimateMode, Func<string, InstallDestinationRepresentativeMetadata>? representativeMetadataResolver = null, Func<string, InstallEstimationMetadataProfile>? metadataProfileResolver = null)
    {
        return service.EstimateInstallationDirectory(
            BuildLooseChartSnapshot(chartFiles, installedHashes, estimateMode)!,
            directoryLookupCache!,
            candidateEvaluationDegree,
            estimateMode,
            representativeMetadataResolver,
            metadataProfileResolver);
    }

    private static PackageInstallEstimationSnapshot? BuildLooseChartSnapshot(IEnumerable<BMSFile> chartFiles, HashSet<string> installedHashes, ChartInstallationEstimateMode estimateMode)
    {
        List<PackageChartEntry> targetEntries = [.. (chartFiles ?? [])
            .Select(file => PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file)))
            .Where(entry => entry?.Chart != null)];
        return BuildLooseChartSnapshot(targetEntries, installedHashes, estimateMode);
    }

    private static PackageInstallEstimationSnapshot? BuildLooseChartSnapshot(IEnumerable<PackageChartEntry> chartEntries, HashSet<string> installedHashes, ChartInstallationEstimateMode estimateMode)
    {
        List<PackageChartEntry> targetEntries = [.. (chartEntries ?? []).Where(entry => entry?.Chart != null)];
        if (targetEntries.Count == 0 || targetEntries.Any(entry => !string.IsNullOrWhiteSpace(entry.Chart?.InstallDestination)))
        {
            return null;
        }
        bool isCorrectionLikeMode = estimateMode == ChartInstallationEstimateMode.ReinstallCorrection || estimateMode == ChartInstallationEstimateMode.MergeCandidateOnly;
        if (!isCorrectionLikeMode && installedHashes != null)
        {
            targetEntries = [.. targetEntries.Where(entry => !installedHashes.Contains(ChartLookupKey.GetPrimaryHash(entry.Chart)))];
        }
        return targetEntries.Count == 0 ? null : PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries(targetEntries);
    }

    private static PackageInstallEstimationSnapshot BuildPackageSnapshot(ChartPackage package, IEnumerable<BMSFile> targetFiles)
    {
        List<BMSFile> targetFileList = [.. (targetFiles ?? []).Where(file => file != null)];
        List<PackageChartEntry> targetEntries = targetFileList.Count == 0
            ? package.ChartEntries
            : ResolvePackageEntries(package, targetFileList);
        return package.GetOrBuildInstallEstimationSnapshotFromEntries(targetEntries);
    }

    private static List<PackageChartEntry> ResolvePackageEntries(ChartPackage package, IEnumerable<BMSFile> targetFiles)
    {
        List<PackageChartEntry> packageEntries = package.ChartEntries;
        var result = new List<PackageChartEntry>();
        foreach (BMSFile targetFile in (targetFiles ?? []).Where(file => file != null))
        {
            PackageChartEntry packageEntry = packageEntries.FirstOrDefault(entry => IsSamePackageChartTarget(entry, targetFile));
            result.Add(packageEntry ?? PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(targetFile)));
        }
        return [.. result.Where(entry => entry?.Chart != null)];
    }

    private static bool IsSamePackageChartTarget(PackageChartEntry entry, BMSFile targetFile)
    {
        if (entry?.Chart == null || targetFile == null)
        {
            return false;
        }
        if (ReferenceEquals(entry.GetBmsOwnerForTest(), targetFile))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(entry.Chart.Path)
            && !string.IsNullOrWhiteSpace(targetFile.path)
            && entry.Chart.Path.Equals(targetFile.path, StringComparison.OrdinalIgnoreCase);
    }

    private static TestableBmsFile CreateFile(string? hash, string path, params string[] wavFiles)
    {
        var file = new TestableBmsFile
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

        public void SetTitleParts(string titleValue, string subtitleValue)
        {
            title = titleValue;
            subtitle = subtitleValue;
        }
    }
}
