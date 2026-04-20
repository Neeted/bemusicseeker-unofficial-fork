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
    public void EstimateInstallationDirectory_MergeMode_ExcludesSourceDirectoryFromSelection()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string mergeDir = Path.Combine(tempRoot, "merge");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(mergeDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "sound.wav"), "src");
            File.WriteAllText(Path.Combine(mergeDir, "sound.wav"), "dst");

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(mergeDir);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cache,
                asParallel: false,
                BmsInstallationEstimateMode.MergeNoSourceCompensation);

            Assert.AreEqual(mergeDir, result.DestinationDirectory);
            Assert.AreEqual(1, result.CandidateDirectoryCount);
            Assert.IsFalse(result.UsedFallbackCandidateExpansion);
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
                cache,
                lookupCache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            string debugSummary = (result.ResourceSummary ?? string.Empty) + " || " + (result.TopCandidateSummary ?? string.Empty) + " || " + (result.SelectedCandidateSummary ?? string.Empty);
            Assert.IsFalse(result.UsedFallbackCandidateExpansion, debugSummary);
            Assert.AreEqual(1, result.CandidateDirectoryCount, debugSummary);
            Assert.AreEqual(candidateDir, result.DestinationDirectory, debugSummary);
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

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(candidateDir, result.DestinationDirectory);
        Assert.AreEqual(1, result.CandidateDirectoryCount);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_PrefersCandidateWithFewerExtraAudioFiles()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string expectedDir = Path.Combine(tempRoot, "ExpectedInstall");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(expectedDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            string[] requiredSounds = new[] { "00.wav", "01.wav", "02.wav" };
            foreach (string requiredSound in requiredSounds)
            {
                File.WriteAllText(Path.Combine(sourceDir, requiredSound), "src");
                File.WriteAllText(Path.Combine(expectedDir, requiredSound), "dst");
            }
            foreach (string extraSound in Enumerable.Range(3, 10).Select((int index) => string.Format("{0:00}.wav", index)))
            {
                File.WriteAllText(Path.Combine(sourceDir, extraSound), "extra");
            }

            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), requiredSounds);
            file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: requiredSounds.Length, wavExisting: requiredSounds.Length), suppressPropertyChanged: true, registerEventHandlers: false);

            BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
            cache.AddDir(sourceDir);
            cache.AddDir(expectedDir);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { file },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            string debugSummary = (result.ResourceSummary ?? string.Empty) + " || " + (result.TopCandidateSummary ?? string.Empty) + " || " + (result.SelectedCandidateSummary ?? string.Empty);
            Assert.AreEqual(expectedDir, result.DestinationDirectory, debugSummary);
            Assert.AreEqual(expectedDir, result.SelectedCandidate?.DirectoryPath, debugSummary);
            Assert.IsTrue((result.SelectedCandidate?.AudioPrecision ?? 0) > 0, debugSummary);
            Assert.IsTrue((result.SelectedCandidate?.AudioJaccard ?? 0) > 0, debugSummary);
            StringAssert.Contains(result.TopCandidateSummary ?? string.Empty, "audioPrecision=", debugSummary);
            StringAssert.Contains(result.TopCandidateSummary ?? string.Empty, "audioJaccard=", debugSummary);
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

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { pending },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cache,
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

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                new[] { pending },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cache,
                asParallel: false,
                BmsInstallationEstimateMode.Normal);

            string debugSummary = (result.ResourceSummary ?? string.Empty) + " || " + (result.TopCandidateSummary ?? string.Empty) + " || " + (result.SelectedCandidateSummary ?? string.Empty);
            Assert.AreEqual(candidateDir, result.DestinationDirectory, debugSummary);
            Assert.AreEqual(2, result.CandidateDirectoryCount, debugSummary);
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
            cache,
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.AreEqual(candidateADir, result.DestinationDirectory);
        Assert.AreEqual(candidateADir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(candidateBDir, result.SecondCandidate?.DirectoryPath);
        Assert.AreEqual("tie_on_primary_metrics", result.ConfidenceReason);
    }

    [TestMethod]
    public void EstimateInstallationDirectory_WhenSourceDirectoryWins_KeepsCandidatesAndDoesNotAutoApply()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Installed", "A_Source");
        string candidateDir = Path.Combine("C:\\Installed", "Z_Candidate");
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
        file.SetMaintenanceInfo(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 1), suppressPropertyChanged: true, registerEventHandlers: false);

        BMSDirectoryFileNameHash cache = new BMSDirectoryFileNameHash();
        cache.AddDirHashed(sourceDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
        cache.AddDirHashed(candidateDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("sound.wav") });
        DirectoryResourceLookupCache lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, new[] { "sound.wav" });
        lookupCache.AddDir(candidateDir, new[] { "sound.wav" });

        InstallEstimationResult result = service.EstimateInstallationDirectory(
            new[] { file },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cache,
            lookupCache,
            asParallel: false,
            BmsInstallationEstimateMode.Normal);

        Assert.IsNull(result.DestinationDirectory);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.AreEqual(sourceDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(candidateDir, result.SecondCandidate?.DirectoryPath);
        Assert.AreEqual("source_tie_on_primary_metrics", result.ConfidenceReason);
        Assert.AreEqual(2, result.Candidates.Count);
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
            cache,
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

    private static TestableBmsFile CreateFile(string hash, string path, params string[] wavFiles)
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
