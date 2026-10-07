using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryMaintenanceServiceTests
{
    [TestMethod]
    public void BuildResourceHealthWarnings_DoesNotMutateSourceWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 2, WavFilesExisting = 1 };
        file = ChartFileProjection.WithMaintenance(file, info);

        IReadOnlyList<ChartWarning> warnings = BmsLibraryMaintenanceService.BuildResourceHealthWarnings(info);

        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual(ChartWarningKind.ResourceWavMissing, warnings[0].Kind);
        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(string.Empty, ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
    }

    [TestMethod]
    public void ImmutableResourceHealthSnapshot_PreservesMutableWarningProjection()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, WavFilesDefined = 4, WavFilesExisting = 2, BgaFilesDefined = 1, BgaFilesExisting = 0, MovieFilesDefined = 0, MovieFilesExisting = 0, StagefileDefined = true, StagefileExisting = false, BannerDefined = false, BannerExisting = false, BackbmpDefined = true, BackbmpExisting = true, FilesWarningIgnored = true };
        ResourceHealthMaintenanceSnapshot snapshot = info;

        IReadOnlyList<ChartWarning> mutableWarnings =
            BmsLibraryMaintenanceService.BuildResourceHealthWarnings(info);
        IReadOnlyList<ChartWarning> immutableWarnings =
            BmsLibraryMaintenanceService.BuildResourceHealthWarnings(snapshot);

        CollectionAssert.AreEqual(
            mutableWarnings.Select(warning => warning.Kind).ToArray(),
            immutableWarnings.Select(warning => warning.Kind).ToArray());
        CollectionAssert.AreEqual(
            mutableWarnings.Select(warning => warning.Message).ToArray(),
            immutableWarnings.Select(warning => warning.Message).ToArray());
    }

    [TestMethod]
    public void LazyMaintenancePlaceholder_IsNotAValidResourceHealthSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        _ = file.ResourceHealthMaintenanceSnapshot;
        ChartFile chart = (file);
        IReadOnlyList<ChartWarning> warnings = service.BuildResourceHealthWarnings(chart);
        var snapshot = ResourceHealthIndexSnapshot.Build([chart], service, version: 1);

        Assert.AreEqual(MaintenanceInfoOrigin.Placeholder, (file.ResourceHealthMaintenanceSnapshot?.Origin ?? MaintenanceInfoOrigin.Placeholder));
        Assert.IsFalse((file.ResourceHealthMaintenanceSnapshot?.Origin is MaintenanceInfoOrigin.DbHydrated or MaintenanceInfoOrigin.Calculated));
        Assert.AreEqual(0, warnings.Count);
        Assert.AreEqual(0, snapshot.ActiveTargets.Count);
        Assert.AreEqual(0, snapshot.IgnoredTargets.Count);
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_PersistsLr2CompatibilityFactsForBmsOnly()
    {
        ChartFile bms = CreateFile("0123456789abcdef0123456789abcdef");
        bms = bms with { Path = @"D:\BMS\Pack\Song\chart.bms" };
        bms = bms with { Resources = TestChartResources.ReplaceAudio(bms.Resources, ["sound.wav"]) };
        bms = bms with { Resources = TestChartResources.ReplaceVisual(bms.Resources, []) };
        bms = bms with
        {
            Resources = [
            ChartResourceReference.Parse("sound.wav", ChartResourceKind.Audio)
        ]
        };
        ChartFile bmsChart = (bms);

        ResourceHealthMaintenanceSnapshot bmsInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(bmsChart);

        Assert.AreEqual((int)Lr2CompatibilityWarningFlags.None, bmsInfo.Lr2WarningFlags);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"D:\BMS\Pack\Song"), Lr2CompatibilityEvaluator.EvaluateChartPath(bms.Path).FolderHash);
        Assert.AreEqual(Encoding.GetEncoding("shift_jis").GetByteCount("sound.wav"), bmsInfo.Lr2ResourceMaxRelativeCp932Bytes);
        Assert.AreEqual(false, bmsInfo.Lr2ResourceHasParentTraversal);

        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"D:\BMS\Pack\Song\chart.bmson",
            Md5 = "fedcba9876543210fedcba9876543210",
            Resources = TestChartResources.Create(["sound.wav"], [])
        };
        ChartFile bmsonChart = (bmson);

        ResourceHealthMaintenanceSnapshot bmsonInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(bmsonChart);

        Assert.IsNull(bmsonInfo.Lr2WarningFlags);
        Assert.IsNull(bmsonInfo.Lr2ResourceMaxRelativeCp932Bytes);
        Assert.IsNull(bmsonInfo.Lr2ResourceHasParentTraversal);
    }

    [TestMethod]
    public void NormalizeForBmsonClearsLr2CompatibilityFacts()
    {
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Lr2WarningFlags = 15, Lr2ResourceMaxRelativeCp932Bytes = 120, Lr2ResourceHasParentTraversal = true };

        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = @"D:\BMS\Pack\Song\chart.bmson", Md5 = "fedcba9876543210fedcba9876543210", Resources = ImmutableList<ChartResourceReference>.Empty };
        info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(bmson);

        Assert.IsNull(info.Lr2WarningFlags);
        Assert.IsNull(info.Lr2ResourceMaxRelativeCp932Bytes);
        Assert.IsNull(info.Lr2ResourceHasParentTraversal);
    }

    [TestMethod]
    public void SetMaintenanceInfo_ProjectsLr2CompatibilityWarningsFromFacts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var info = new ResourceHealthMaintenanceSnapshot
        {
            Origin = MaintenanceInfoOrigin.Calculated,
            Path = file.Path,
            Hash = file.Md5,
            Lr2WarningFlags = (int)(Lr2CompatibilityWarningFlags.PathEncodingUnsupported
                | Lr2CompatibilityWarningFlags.PathTooLong
                | Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported
                | Lr2CompatibilityWarningFlags.ResourcePathTooLong)
        };

        file = ChartFileProjection.WithMaintenance(file, info);

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathTooLong));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2ResourcePathUnsupported));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2ResourcePathTooLong));
        Assert.AreEqual("[4] LR2パス非対応, LR2パス長超過, LR2リソース非対応, LR2リソースパス長超過", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
    }

    [TestMethod]
    public void SetMaintenanceInfo_ClearsLr2CompatibilityWarningsWhenEvaluatedClean()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2PathEncodingUnsupported), ChartWarning.Create(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported)] };
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Lr2WarningFlags = (int)Lr2CompatibilityWarningFlags.None };

        file = ChartFileProjection.WithMaintenance(file, info);

        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
        Assert.AreEqual(string.Empty, ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
    }

    [TestMethod]
    public void SetMaintenanceInfo_DoesNotClearLr2CompatibilityWarningsWithoutFacts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2PathEncodingUnsupported), ChartWarning.Create(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported)] };
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5 };

        file = ChartFileProjection.WithMaintenance(file, info);

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
        Assert.AreEqual("[1] LR2パス非対応", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
    }

    [TestMethod]
    public void EvaluateMaintenanceForInline_ForceUpdateReportsUnchangedWhenPersistentRowIsSame()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, "#TITLE test\r\n#WAV01 missing.wav\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = BmsChartFileParser.ParseSnapshot(snapshot);

            BmsLibraryMaintenanceService.MaintenanceEvaluationResult first =
                BmsLibraryMaintenanceService.EvaluateMaintenanceForInline(file, snapshot, lookupContext: null, forceUpdate: true);
            BmsLibraryMaintenanceService.MaintenanceEvaluationResult second =
                BmsLibraryMaintenanceService.EvaluateMaintenanceForInline(first.CurrentChart, snapshot, lookupContext: null, forceUpdate: true);

            Assert.IsTrue(first.MaintenanceInfoChanged);
            Assert.IsFalse(second.MaintenanceInfoChanged);
            Assert.IsTrue(second.MaintenanceInfo.IsInformationChecked);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void EvaluateMaintenanceForInline_ForceUpdateReportsChangedWhenIgnoredFlagIsReset()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, "#TITLE test\r\n#WAV01 missing.wav\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = BmsChartFileParser.ParseSnapshot(snapshot);
            file = BmsLibraryMaintenanceService.EvaluateMaintenanceForInline(file, snapshot, lookupContext: null, forceUpdate: true).CurrentChart;
            file = ChartFileProjection.WithMaintenance(file, file.ResourceHealthMaintenanceSnapshot with { FilesWarningIgnored = true });

            BmsLibraryMaintenanceService.MaintenanceEvaluationResult result =
                BmsLibraryMaintenanceService.EvaluateMaintenanceForInline(file, snapshot, lookupContext: null, forceUpdate: true);

            Assert.IsTrue(result.MaintenanceInfoChanged);
            Assert.IsFalse(result.MaintenanceInfo.FilesWarningIgnored);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void EvaluateMaintenanceForInline_UsesAlreadyAppliedResourceReferences()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, "#TITLE test\r\n#WAV01 missing.wav\r\n", Encoding.ASCII);
            File.WriteAllText(Path.Combine(tempDirectoryPath, "hit.wav"), string.Empty);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = BmsChartFileParser.ParseSnapshot(snapshot);
            file = file with { Resources = TestChartResources.Create(["hit.wav"]) };
            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(tempDirectoryPath, ["hit.wav"]);
            var lookupContext = new ResourceHealthLookupContext(lookupCache);

            BmsLibraryMaintenanceService.MaintenanceEvaluationResult result =
                BmsLibraryMaintenanceService.EvaluateMaintenanceForInline(
                    file,
                    snapshot,
                    lookupContext,
                    forceUpdate: true);

            Assert.AreEqual(1, result.MaintenanceInfo.WavFilesDefined);
            Assert.AreEqual(1, result.MaintenanceInfo.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PendingBmsonEncodingOnlyMaintenance_IsPlaceholder()
    {
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\song.bmson",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('c', 64)
        };
        song = song with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = song.Md5, encoding = "utf-8" }) };

        ChartFile chart = (song);
        ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.GetResourceHealthSnapshot(chart);

        Assert.IsNotNull(info);
        Assert.AreEqual(song.Md5, info.Hash);
        Assert.AreEqual(song.Path, info.Path);
        Assert.IsFalse(song.ResourceHealthMaintenanceSnapshot.IsInformationChecked);
        Assert.IsFalse(info.IsInformationChecked);
        Assert.IsNull(info.WavFilesDefined);
        Assert.IsNull(info.BgaFilesDefined);
        Assert.IsNull(info.MovieFilesDefined);
        Assert.IsNull(chart.Resources);
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_ProjectionOnlyBmsonUsesChartResources()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bmson");
        try
        {
            var chart = new ChartFile(kind: ChartFileKind.Bmson, path: chartPath, md5: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sha256: new string('c', 64), title: "Title", rawTitle: "Title", artist: "Artist", genre: string.Empty, folder: "Folder", tag: string.Empty, levelText: string.Empty, level: null, mode: null, chartInfo: null, resources: TestChartResources.Create(["missing.wav"], ["missing.png", "missing.mp4"], "stage.png"), stagefile: "stage.png");

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart);

            Assert.IsNotNull(info);
            Assert.AreEqual("utf-8", info.Encoding);
            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(0, info.WavFilesExisting);
            Assert.AreEqual(1, info.BgaFilesDefined);
            Assert.AreEqual(0, info.BgaFilesExisting);
            Assert.AreEqual(1, info.MovieFilesDefined);
            Assert.AreEqual(0, info.MovieFilesExisting);
            Assert.AreEqual(true, info.StagefileDefined);
            Assert.AreEqual(false, info.StagefileExisting);
        }
        finally
        {
            Directory.Delete(tempDirectoryPath, recursive: true);
        }
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_OptionalDefinitionFormatDifferenceKeepsCacheEntriesDistinct()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ImmutableList<ChartResourceReference> resources = TestChartResources.Create(stagefile: @"..\stage.png");
            ChartFile bmsOwner = ChartTestValues.Empty() with
            {
                Path = Path.Combine(directory, "chart.bms"),
                Resources = resources,
                Stagefile = "saved-only.png"
            };
            ChartFile bmsonOwner = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(directory, "chart.bmson"),
                Resources = resources,
                Stagefile = "saved-only.png"
            };
            ChartFile bms = ChartFileProjection.ToImmutableSnapshot((bmsOwner));
            ChartFile bmson = ChartFileProjection.ToImmutableSnapshot((bmsonOwner));
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(directory, [], [], []);
            var context = new ResourceHealthLookupContext(cache);

            ResourceHealthMaintenanceSnapshot bmsHealth = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(bms, context);
            ResourceHealthMaintenanceSnapshot bmsonHealth = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(bmson, context);

            Assert.IsNull(bms.Token);
            Assert.IsNull(bmson.Token);
            Assert.AreSame(bms.Resources, bmson.Resources);
            Assert.AreEqual(true, bmsHealth.StagefileDefined);
            Assert.AreEqual(false, bmsonHealth.StagefileDefined);
            Assert.AreEqual(false, bmsHealth.StagefileExisting);
            Assert.IsNull(bmsonHealth.StagefileExisting);
            Assert.AreEqual(2, context.ResourceHealthSetCacheEntryCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_ResourceSetCacheKeepsOptionalImageRolesDistinct()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        try
        {
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [],
                [ChartResourceKeyHash.GetLookupHash("stage")],
                []);
            var lookupContext = new ResourceHealthLookupContext(cache);
            ChartFile first = CreateProjectionOnlyBmsChart(
                Path.Combine(tempDirectoryPath, "first.bms"),
                md5: "11111111111111111111111111111111",
                stagefile: "stage.png",
                banner: "missing.png");
            ChartFile second = CreateProjectionOnlyBmsChart(
                Path.Combine(tempDirectoryPath, "second.bms"),
                md5: "22222222222222222222222222222222",
                stagefile: "missing.png",
                banner: "stage.png");

            ResourceHealthMaintenanceSnapshot firstInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(first, lookupContext);
            ResourceHealthMaintenanceSnapshot secondInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(second, lookupContext);

            Assert.AreEqual(true, firstInfo.StagefileDefined);
            Assert.AreEqual(true, firstInfo.StagefileExisting);
            Assert.AreEqual(true, firstInfo.BannerDefined);
            Assert.AreEqual(false, firstInfo.BannerExisting);
            Assert.AreEqual(true, secondInfo.StagefileDefined);
            Assert.AreEqual(false, secondInfo.StagefileExisting);
            Assert.AreEqual(true, secondInfo.BannerDefined);
            Assert.AreEqual(true, secondInfo.BannerExisting);
            Assert.AreEqual(2, lookupContext.ResourceHealthSetCacheEntryCount);
        }
        finally
        {
            Directory.Delete(tempDirectoryPath, recursive: true);
        }
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_ResourceSetCacheDoesNotMergeHashCollisions()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        try
        {
            const string ExistingResource = "res_xf2a7hbc7_";
            const string MissingResource = "res_0ltou99kgv";
            Assert.AreEqual(ChartResourceKeyHash.GetLookupHash(ExistingResource), ChartResourceKeyHash.GetLookupHash(MissingResource));
            File.WriteAllText(Path.Combine(tempDirectoryPath, ExistingResource + ".wav"), string.Empty);

            var lookupContext = new ResourceHealthLookupContext(null);
            ChartFile first = CreateProjectionOnlyBmsChart(
                Path.Combine(tempDirectoryPath, "first.bms"),
                md5: "11111111111111111111111111111111",
                audioResourcePaths: [ExistingResource + ".wav"]);
            ChartFile second = CreateProjectionOnlyBmsChart(
                Path.Combine(tempDirectoryPath, "second.bms"),
                md5: "22222222222222222222222222222222",
                audioResourcePaths: [ExistingResource + ".wav", MissingResource + ".wav"]);

            ResourceHealthMaintenanceSnapshot firstInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(first, lookupContext);
            ResourceHealthMaintenanceSnapshot secondInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(second, lookupContext);

            Assert.AreEqual(1, firstInfo.WavFilesDefined);
            Assert.AreEqual(1, firstInfo.WavFilesExisting);
            Assert.AreEqual(2, secondInfo.WavFilesDefined);
            Assert.AreEqual(1, secondInfo.WavFilesExisting);
            Assert.AreEqual(2, lookupContext.ResourceHealthSetCacheEntryCount);
        }
        finally
        {
            Directory.Delete(tempDirectoryPath, recursive: true);
        }
    }

    [TestMethod]
    public void SetChartResourceWarningsIgnored_ChartBmsonPlaceholderAttachesMaintenanceInfoToSourceSong()
    {
        var service = new BmsLibraryMaintenanceService();
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\song.bmson",
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('d', 64)
        };
        Assert.IsNull(song.ResourceHealthMaintenanceSnapshot);

        ChartFile chart = (song);
        List<ChartFile> changes = service.SetChartResourceWarningsIgnored([chart], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.IsTrue(changes[0].ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
        Assert.AreNotSame(chart, changes[0]);
        Assert.IsTrue(changes[0].ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
    }

    [TestMethod]
    public void SetChartResourceWarningsIgnored_ChartBmsonAttachesComputedMaintenanceInfoToSourceSong()
    {
        var service = new BmsLibraryMaintenanceService();
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\chart.bmson",
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = new string('e', 64),
            Resources = TestChartResources.Create(["missing.wav"])
        };
        song = song with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = song.Md5, encoding = "utf-8" }) };
        ChartFile chart = (song);

        List<ChartFile> changes = service.SetChartResourceWarningsIgnored([chart], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.AreNotSame(chart, changes[0]);
        Assert.AreEqual(1, changes[0].ResourceHealthMaintenanceSnapshot.WavFilesDefined);
        Assert.AreEqual(0, changes[0].ResourceHealthMaintenanceSnapshot.WavFilesExisting);
        Assert.IsTrue(changes[0].ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
    }

    [TestMethod]
    public void SetChartResourceWarningsIgnored_ChartBmsonReplacesStaleMaintenanceHash()
    {
        var service = new BmsLibraryMaintenanceService();
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\chart.bmson",
            Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            Sha256 = new string('f', 64),
            Resources = TestChartResources.Create(["missing.wav"])
        };
        song = song with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = "ffffffffffffffffffffffffffffffff", encoding = "utf-8" }) };
        song = ChartFileProjection.WithMaintenance(song, song.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 1 });
        song = ChartFileProjection.WithMaintenance(song, song.ResourceHealthMaintenanceSnapshot with { WavFilesExisting = 1 });
        song = ChartFileProjection.WithMaintenance(song, song.ResourceHealthMaintenanceSnapshot with { FilesWarningIgnored = true });
        ChartFile chart = (song);

        List<ChartFile> changes = service.SetChartResourceWarningsIgnored([chart], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.AreNotSame(chart, changes[0]);
        Assert.AreEqual(song.Md5, changes[0].ResourceHealthMaintenanceSnapshot.Hash);
        Assert.AreEqual(1, changes[0].ResourceHealthMaintenanceSnapshot.WavFilesDefined);
        Assert.AreEqual(0, changes[0].ResourceHealthMaintenanceSnapshot.WavFilesExisting);
        Assert.IsTrue(changes[0].ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_RootReferenceDoesNotMatchNestedResource()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#WAV01 foo.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [ChartResourceKeyHash.GetLookupHash(@"sound\foo")],
                [],
                []);

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, new ResourceHealthLookupContext(cache), forceUpdate: true);

            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(0, info.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_NestedReferenceDoesNotMatchRootResource()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#WAV01 sound\\foo.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [ChartResourceKeyHash.GetLookupHash("foo")],
                [],
                []);

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, new ResourceHealthLookupContext(cache), forceUpdate: true);

            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(0, info.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_RootReferenceMatchesRootRelativeResource()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#WAV01 foo.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [ChartResourceKeyHash.GetLookupHash("foo")],
                [],
                []);

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, new ResourceHealthLookupContext(cache), forceUpdate: true);

            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(1, info.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_WithoutLookupContextRootReferenceDoesNotMatchNestedResource()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        string soundDirectoryPath = Path.Combine(tempDirectoryPath, "sound");
        Directory.CreateDirectory(soundDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#WAV01 foo.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        File.WriteAllText(Path.Combine(soundDirectoryPath, "foo.wav"), string.Empty);
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, null, forceUpdate: true);

            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(0, info.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_WithoutLookupContextNestedReferenceDoesNotMatchRootResource()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#WAV01 sound\\foo.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        File.WriteAllText(Path.Combine(tempDirectoryPath, "foo.wav"), string.Empty);
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, null, forceUpdate: true);

            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(0, info.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_MissingBmsFileReturnsNull()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n"
            + "#WAV01 hit.wav\r\n"
            + "#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            File.Delete(bmsFilePath);
            file = file with { Resources = null };

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, null, forceUpdate: true);

            Assert.IsNull(info);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LibraryChartRow_ProjectsResourceHealthWarningsWithoutMutatingSource()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ResourceWavMissing), ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "stale resource warning")] };
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, BgaFilesDefined = 4, BgaFilesExisting = 3 };
        file = ChartFileProjection.WithMaintenance(file, info);
        var row = LibraryChartRow.FromChartFile(file);
        row.SetResourceHealthProjectionProvider(_ => new ResourceHealthWarningProjection(1, BmsLibraryMaintenanceService.BuildResourceHealthWarnings(info), isIgnored: false));

        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_DuplicateChart);
        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_ResourceMissing);
        StringAssert.Contains(row.WarningTooltipText, "BGA");
        Assert.IsFalse(row.WarningTooltipText.Contains("stale resource warning"));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));

        row.SetChartTransientStateProvider((chart, _) => ChartFileTransientState.FromInstallDestinationState(
            ChartFileProjection.WithPackageState(
                chart,
                @"C:\Installed",
                string.Empty,
                string.Empty,
                []),
            forceInstallDestinationProjection: true));
        Assert.IsFalse(row.WarningDigestText.Contains(Resources.WarningDigest_ResourceMissing));
        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_DuplicateChart);
        StringAssert.Contains(row.WarningTooltipText, "BGA");
    }

    [TestMethod]
    public void LibraryChartRow_ResourceProjectionSuppressesStaleSourceResourceWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ResourceWavMissing), ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "pending resource warning")] };
        file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 2, WavFilesExisting = 2 });
        var row = LibraryChartRow.FromChartFile(file);
        row.SetResourceHealthProjectionProvider(_ => ResourceHealthWarningProjection.Empty);

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(string.Empty, row.WarningDigestText);
        Assert.IsFalse(row.WarningTooltipText.Contains("pending resource warning"));
    }

    [TestMethod]
    public void ResourceHealthIndexSnapshot_GroupsActiveAndIgnoredWithoutMutatingWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        active = active with { Path = @"C:\Library\active.bms" };
        active = ChartFileProjection.WithMaintenance(active, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = active.Path, Hash = active.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
        ChartFile ignored = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ignored = ignored with { Path = @"C:\Library\ignored.bms" };
        ignored = ChartFileProjection.WithMaintenance(ignored, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = ignored.Path, Hash = ignored.Md5, BgaFilesDefined = 2, BgaFilesExisting = 1, FilesWarningIgnored = true });

        ChartFile activeChart = (active);
        ChartFile ignoredChart = (ignored);
        var snapshot = ResourceHealthIndexSnapshot.Build([activeChart, ignoredChart], service, version: 3);

        CollectionAssert.AreEqual(new[] { activeChart }, snapshot.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart }, snapshot.IgnoredTargets.ToArray());
        Assert.IsTrue(snapshot.GetProjection(activeChart).HasIssues);
        Assert.IsFalse(snapshot.GetProjection(activeChart).IsIgnored);
        Assert.IsTrue(snapshot.GetProjection(ignoredChart).IsIgnored);
        Assert.IsFalse(active.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.IsFalse(ignored.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
    }

    [TestMethod]
    public void ResourceHealthIndexSnapshot_DoesNotTreatLr2CompatibilityWarningsAsResourceTargets()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Path = @"C:\Library\active.bms" };
        file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot
        {
            Origin = MaintenanceInfoOrigin.Calculated,
            Path = file.Path,
            Hash = file.Md5,
            Lr2WarningFlags = (int)(Lr2CompatibilityWarningFlags.PathTooLong
                | Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported)
        });
        ChartFile chart = (file);

        var snapshot = ResourceHealthIndexSnapshot.Build([chart], service, version: 4);

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathTooLong));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2ResourcePathUnsupported));
        Assert.AreEqual(0, snapshot.ActiveTargets.Count);
        Assert.AreEqual(0, snapshot.IgnoredTargets.Count);
        Assert.IsFalse(snapshot.GetProjection(chart).HasIssues);
    }

    [TestMethod]
    public void ResourceHealthIndexSnapshot_BuildsBmsonTargetFromChartFile()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\chart.bmson",
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('c', 64)
        };
        song = song with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = song.Md5, encoding = "utf-8" }) };
        song = ChartFileProjection.WithMaintenance(song, song.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 3 });
        song = ChartFileProjection.WithMaintenance(song, song.ResourceHealthMaintenanceSnapshot with { WavFilesExisting = 1 });
        ChartFile chart = (song);

        var snapshot = ResourceHealthIndexSnapshot.Build([chart], service, version: 5);

        CollectionAssert.AreEqual(new[] { chart }, snapshot.ActiveTargets.ToArray());
        Assert.IsTrue(snapshot.GetProjection(chart).HasIssues);
        Assert.IsTrue(snapshot.GetProjection(chart).Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
    }

    [TestMethod]
    public void GetChartsNeedResourceFix_UsesCurrentResourceHealthIndexSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            active = active with { Path = @"C:\Library\active.bms" };
            active = ChartFileProjection.WithMaintenance(active, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = active.Path, Hash = active.Md5, WavFilesDefined = 2, WavFilesExisting = 1 });
            ChartFile activeChart = (active);
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [active],
                BmsonCharts = []
            };
            EnsureCurrentResourceHealthIndex(library);

            List<ChartFile> result = library.GetChartsNeedResourceFix(null);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(activeChart.Path, result[0].Path);
        });
    }

    [TestMethod]
    public void ChartFilesNeedResourceFix_WaitsForCatalogStorageWriterBeforeColdResourceHealthRead()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "missing-resource.bms");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = chartPath };
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 1, WavFilesExisting = 0, FilesWarningIgnored = false });
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [file],
                BmsonCharts = []
            };

            // 所持 collection だけを通常の hash 読取りで温め、resource health は cold のままにする。
            _ = library.GetOwnedChartHashIndexSnapshot();

            ReaderWriterLockSlimWrapper storageWriteGate = RegularChartListOwnerTestSupport.GetCatalogStorageRowsWriteGate(library);
            List<ChartFile>? result = null;
            Task worker = null!;
            try
            {
                using (storageWriteGate.GetWriterGuard())
                {
                    worker = RegularChartListOwnerTestSupport.StartLongRunning(
                        () => result = [.. library.ChartFilesNeedResourceFix]);
                    bool readerWaitOrEarlyCompletion = SpinWait.SpinUntil(
                        () => storageWriteGate.WaitingReadCount > 0 || worker.IsCompleted,
                        TimeSpan.FromSeconds(10));

                    Assert.IsTrue(
                        readerWaitOrEarlyCompletion,
                        "resource health cold read did not reach the storage reader or complete within the watchdog.");
                    Assert.IsFalse(
                        worker.IsCompleted,
                        "resource health cold read completed while the catalog storage writer was held.");
                }

                worker.GetAwaiter().GetResult();
                Assert.IsNotNull(result);
                Assert.AreEqual(1, result!.Count);
                Assert.AreEqual(ChartFileKind.Bms, result[0].Kind);
                Assert.AreEqual(chartPath, result[0].Path);
            }
            finally
            {
                if (worker != null)
                {
                    worker.GetAwaiter().GetResult();
                }
            }
        });
    }

    /// <summary>R2C-FULL-LOOKUP: case-only path rows keep independent resource-health projections.</summary>
    [TestMethod]
    public void GetChartsNeedResourceFix_CaseOnlyExactPathsKeepIndependentResourceHealthProjections()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string sharedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            ChartFile upperCasePath = CreateFile(sharedHash);
            upperCasePath = upperCasePath with { Path = @"C:\Library\Case.bms" };
            upperCasePath = ChartFileProjection.WithMaintenance(upperCasePath, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = upperCasePath.Path, Hash = sharedHash, WavFilesDefined = 2, WavFilesExisting = 1 });
            ChartFile lowerCasePath = CreateFile(sharedHash);
            lowerCasePath = lowerCasePath with { Path = @"c:\library\case.bms" };
            lowerCasePath = ChartFileProjection.WithMaintenance(lowerCasePath, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = lowerCasePath.Path, Hash = sharedHash, BgaFilesDefined = 3, BgaFilesExisting = 1 });
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [upperCasePath, lowerCasePath],
                BmsonCharts = []
            };

            ResourceHealthIndexSnapshot snapshot = library.GetResourceHealthIndexSnapshotForView("r2c_exact_full");
            List<ChartFile> result = library.GetChartsNeedResourceFix(null);
            ResourceHealthWarningProjection upperProjection = snapshot.GetProjection(
                ChartFileKind.Bms,
                upperCasePath.Path,
                sharedHash);
            ResourceHealthWarningProjection lowerProjection = snapshot.GetProjection(
                ChartFileKind.Bms,
                lowerCasePath.Path,
                sharedHash);

            Assert.AreEqual(2, snapshot.TargetCount);
            Assert.IsTrue(new HashSet<string>(result.Select(chart => chart.Path), StringComparer.Ordinal).SetEquals(
                [upperCasePath.Path, lowerCasePath.Path]));
            Assert.IsTrue(upperProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(upperProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
            Assert.IsTrue(lowerProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
            Assert.IsFalse(lowerProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(snapshot.GetProjection(
                ChartFileKind.Bms,
                upperCasePath.Path,
                sharedHash.ToUpperInvariant()).Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(snapshot.GetProjection(
                ChartFileKind.Bms,
                @"C:\Library\CASE.bms",
                sharedHash).HasIssues);
        });
    }

    [TestMethod]
    public void RescanResourceHealthCharts_PublishesMaintenanceRefreshThroughOwnedDispatcher()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE maintenance\r\n", Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot
            {
                Origin = MaintenanceInfoOrigin.Calculated,
                Path = file.Path,
                Hash = file.Md5,
                Encoding = "unknown"
            });
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [file], []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int ownedVersion = library.OwnedCollectionVersion;
            int refreshNotificationChanged = 0;
            System.ComponentModel.PropertyChangedEventHandler handler = delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };

            library.PropertyChanged += handler;
            try
            {
                MaintenanceWorkflowResult result = library.RescanResourceHealthCharts([file]);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
                Assert.IsTrue(result.HasUpdates);
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
                Assert.AreEqual(1, refreshNotificationChanged);
                Assert.AreEqual(ownedVersion, library.OwnedCollectionVersion);
                Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.AreEqual("unknown", file.ResourceHealthMaintenanceSnapshot.Encoding);
                Assert.AreEqual("shift_jis", OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library).Collection
                    .ResolveCurrentChart(LibraryChartRef.FromChartFile(file)).ResourceHealthMaintenanceSnapshot.Encoding);
            }
            finally
            {
                library.PropertyChanged -= handler;
                library.RequestShutdown("maintenance_marker_only_test");
            }
        });
    }

    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void RescanResourceHealthCharts_UpdatesCurrentResourceHealthIndexByDelta(int backgroundCount)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string firstPath = Path.Combine(root, "target-first.bms");
            string secondPath = Path.Combine(root, "target-second.bms");
            string firstResourcePath = Path.Combine(root, "first.wav");
            string secondResourcePath = Path.Combine(root, "second.wav");
            File.WriteAllText(
                firstPath,
                "#PLAYER 1\r\n#TITLE target first\r\n#WAV01 first.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            File.WriteAllText(
                secondPath,
                "#PLAYER 1\r\n#TITLE target second\r\n#WAV01 second.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            ChartFile first = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstPath));
            ChartFile second = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondPath));
            first = ChartFileProjection.WithMaintenance(first, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = first.Path, Hash = first.Md5, WavFilesDefined = 1, WavFilesExisting = 0 });
            second = ChartFileProjection.WithMaintenance(second, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = second.Path, Hash = second.Md5, WavFilesDefined = 1, WavFilesExisting = 0 });
            ChartFile firstChart = (first);
            ChartFile secondChart = (second);
            List<ChartFile> files = [first, second];
            for (int index = 0; index < backgroundCount; index++)
            {
                ChartFile background = CreateFile(
                    index.ToString("x8") + new string('0', 24));
                background = background with { Path = Path.Combine(root, "background-" + index + ".bms") };
                background = ChartFileProjection.WithMaintenance(background, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = background.Path, Hash = background.Md5, WavFilesDefined = 1, WavFilesExisting = 0 });
                files.Add(background);
            }
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, files, []);
            EnsureCurrentResourceHealthIndex(library);
            List<string> storeWork = [];
            ResourceHealthIndexSnapshot beforeFirst = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            beforeFirst.StoreWorkObserver = operation => storeWork.Add(operation);
            int initialTargetCount = beforeFirst.TargetCount;
            int ownedVersion = library.OwnedCollectionVersion;
            Assert.AreEqual(backgroundCount + 2, initialTargetCount);
            Assert.IsTrue(beforeFirst.GetProjection(firstChart).HasIssues);
            Assert.IsTrue(beforeFirst.GetProjection(secondChart).HasIssues);

            File.WriteAllBytes(firstResourcePath, [1, 2, 3]);
            int firstNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            MaintenanceWorkflowResult firstResult = library.RescanResourceHealthCharts([firstChart]);
            ResourceHealthIndexSnapshot afterFirst = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            ResourceHealthIndexSnapshot firstGetter = library.GetResourceHealthIndexSnapshotForView("test_rescan_first_getter");
            List<ChartFile> firstFixTargets = library.GetChartsNeedResourceFix([firstChart]);
            NormalLibraryRefreshNotificationBatch firstBatch = library.GetNormalLibraryRefreshNotificationsAfter(firstNotificationVersion);

            Assert.IsTrue(firstResult.HasUpdates);
            Assert.AreEqual(initialTargetCount, afterFirst.TargetCount);
            Assert.IsFalse(afterFirst.GetProjection(firstChart).HasIssues);
            Assert.IsTrue(afterFirst.GetProjection(secondChart).HasIssues);
            Assert.AreEqual(afterFirst.Version, firstGetter.Version);
            Assert.IsFalse(firstGetter.GetProjection(firstChart).HasIssues);
            Assert.AreEqual(0, firstFixTargets.Count);
            Assert.IsTrue(firstBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(firstBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.AreEqual(ownedVersion, library.OwnedCollectionVersion);
            Assert.IsFalse(firstBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(beforeFirst.GetProjection(firstChart).HasIssues);

            beforeFirst.StoreWorkObserver = null;
            afterFirst.StoreWorkObserver = operation => storeWork.Add(operation);
            File.WriteAllBytes(secondResourcePath, [4, 5, 6]);
            int secondNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            MaintenanceWorkflowResult secondResult = library.RescanResourceHealthCharts([secondChart]);
            ResourceHealthIndexSnapshot afterSecond = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            ResourceHealthIndexSnapshot thirdGetter = library.GetResourceHealthIndexSnapshotForView("test_rescan_second_getter");
            List<ChartFile> secondFixTargets = library.GetChartsNeedResourceFix([secondChart]);
            NormalLibraryRefreshNotificationBatch secondBatch = library.GetNormalLibraryRefreshNotificationsAfter(secondNotificationVersion);

            Assert.IsTrue(secondResult.HasUpdates);
            Assert.AreEqual(initialTargetCount, afterSecond.TargetCount);
            Assert.IsFalse(afterSecond.GetProjection(firstChart).HasIssues);
            Assert.IsFalse(afterSecond.GetProjection(secondChart).HasIssues);
            Assert.AreEqual(afterSecond.Version, thirdGetter.Version);
            Assert.AreEqual(0, secondFixTargets.Count);
            Assert.IsTrue(secondBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(secondBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.AreEqual(ownedVersion, library.OwnedCollectionVersion);
            Assert.IsFalse(secondBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(afterFirst.GetProjection(secondChart).HasIssues);
            Assert.IsTrue(storeWork.Contains("entry_lookup"));
            Assert.IsFalse(storeWork.Contains("warning_sequence_enumeration"));
            Assert.IsFalse(storeWork.Contains("warning_sequence_entry_visited"));
        });
    }

    [TestMethod]
    public void RescanAllOwnedChartMaintenance_PreservesBothCurrentValuesAfterRefreshSubscriberFailure()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string firstPath = Path.Combine(root, "first.bms");
            string secondPath = Path.Combine(root, "second.bms");
            File.WriteAllText(
                firstPath,
                "#PLAYER 1\r\n#TITLE first\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            File.WriteAllText(
                secondPath,
                "#PLAYER 1\r\n#TITLE second\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);

            ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            first = first with { Path = firstPath };
            first = first with { Resources = TestChartResources.ReplaceAudio(first.Resources, ["missing.wav"]) };
            first = ChartFileProjection.WithMaintenance(first, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = first.Path, Hash = first.Md5, WavFilesDefined = 0, WavFilesExisting = 0, BgaFilesDefined = 0, BgaFilesExisting = 0, MovieFilesDefined = 0, MovieFilesExisting = 0, StagefileDefined = false, StagefileExisting = false, BannerDefined = false, BannerExisting = false, BackbmpDefined = false, BackbmpExisting = false, Encoding = "shift_jis", EncodingFixed = true });
            ChartFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            second = second with { Path = secondPath };
            second = second with { Resources = TestChartResources.ReplaceAudio(second.Resources, ["missing.wav"]) };
            second = ChartFileProjection.WithMaintenance(second, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = second.Path, Hash = second.Md5, WavFilesDefined = 0, WavFilesExisting = 0, BgaFilesDefined = 0, BgaFilesExisting = 0, MovieFilesDefined = 0, MovieFilesExisting = 0, StagefileDefined = false, StagefileExisting = false, BannerDefined = false, BannerExisting = false, BackbmpDefined = false, BackbmpExisting = false, Encoding = "shift_jis", EncodingFixed = true });

            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [first, second], []);
            int handledVersion = library.NormalLibraryRefreshNotificationVersion;
            int failedNotificationCount = 0;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    Interlocked.Increment(ref failedNotificationCount);
                    throw new InvalidOperationException("maintenance refresh subscriber failure");
                }
            };
            var progress = new List<MaintenanceWorkflowProgress>();

            MaintenanceWorkflowResult result = library.RescanAllOwnedChartMaintenance(progress.Add);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, Volatile.Read(ref failedNotificationCount));
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledVersion);
            CollectionAssert.AreEquivalent(new[] { firstPath, secondPath }, batch.ChangedCharts.Select(chart => chart.Path).ToArray());
            Assert.IsTrue(library.BmsCharts.All(chart => chart.ResourceHealthMaintenanceSnapshot.WavFilesExisting == 0));
            Assert.AreEqual(0, first.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, second.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.IsTrue(progress.Any(update => update?.IsCompleted == true));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", firstPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", secondPath));
        });
    }

    [TestMethod]
    public void GetChartsNeedResourceFix_SubsetForceUpdateDoesNotBuildFullOwnedIndexWhenCurrentIndexUnavailable()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string targetPath = Path.Combine(root, "target-missing.bms");
            string unrelatedPath = Path.Combine(root, "unrelated-missing.bms");
            File.WriteAllText(targetPath, "#PLAYER 1\r\n#TITLE target\r\n#WAV01 missing.wav\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(unrelatedPath, "#PLAYER 1\r\n#TITLE unrelated\r\n#WAV01 missing.wav\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile target = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            target = target with { Path = targetPath };
            ChartFile unrelated = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            unrelated = unrelated with { Path = unrelatedPath };
            ChartFile targetChart = (target);
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [target, unrelated], []);
            library.BmsCharts = new List<ChartFile> { target, unrelated };

            List<ChartFile> result = library.GetChartsNeedResourceFix([targetChart], forceUpdate: true);
            ResourceHealthIndexSnapshot currentSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(targetPath, result[0].Path);
            Assert.AreEqual(0, currentSnapshot.TargetCount);
        });
    }

    [TestMethod]
    public void SetChartResourceWarningsIgnored_PublishesWarningRefreshThroughOwnedDispatcher()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = @"C:\Library\warning.bms" };
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
            ChartFile chart = (file);
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [file], []);
            EnsureCurrentResourceHealthIndex(library);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int refreshNotificationChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };

            library.SetChartResourceWarningsIgnored([chart], unset: false);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            ResourceHealthIndexSnapshot updatedSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.AreEqual(1, refreshNotificationChanged);
            Assert.AreEqual(0, updatedSnapshot.ActiveTargets.Count);
            Assert.AreEqual(1, updatedSnapshot.IgnoredTargets.Count);
        });
    }

    [TestMethod]
    public void DeferredMaintenanceHydration_UsesCurrentSchedulerAndPublishesState()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            var execution = new List<(OperationProgressRequest Request, bool Running)>();
            library.StartupProgressRequestFactory = (name, version) => new(3, 19, name, version);
            library.StartupRequestProgressReporter = (request, running) => execution.Add((request, running));
            library.AttachStartupRequestProgressSources();
            var changedProperties = new List<string>();
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                changedProperties.Add(args.PropertyName!);
            };
            bool schedulerInvoked = false;
            library.StartupBackgroundTaskScheduler = delegate (string _, string _, string _, Func<System.Threading.Tasks.Task> work)
            {
                schedulerInvoked = true;
                work().GetAwaiter().GetResult();
                return true;
            };

            MethodInfo? queueMethod = typeof(BMSLibrary).GetMethod(
                "QueueDeferredMaintenanceHydration",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(queueMethod);
            queueMethod!.Invoke(library, ["test_scheduler"]);

            Assert.IsTrue(schedulerInvoked);
            var expectedRequest = new OperationProgressRequest(3, 19, "maintenance_hydration", 1);
            Assert.AreEqual(expectedRequest, library.MaintenanceHydrationProgressRequest);
            CollectionAssert.AreEqual(new[] { (expectedRequest, true), (expectedRequest, false) }, execution);
            Assert.AreEqual(1, library.MaintenanceHydrationRequestedVersion);
            Assert.AreEqual(1, library.MaintenanceHydrationCompletedVersion);
            Assert.IsFalse(library.MaintenanceHydrationRunning);
            CollectionAssert.Contains(changedProperties, nameof(BMSLibrary.MaintenanceHydrationRequestedVersion));
            CollectionAssert.Contains(changedProperties, nameof(BMSLibrary.MaintenanceHydrationCompletedVersion));
            CollectionAssert.Contains(changedProperties, nameof(BMSLibrary.MaintenanceHydrationRunning));
        });
    }

    [TestMethod]
    public void DeferredMaintenanceHydration_AttachesRowsRemovesStaleAndPublishesResourceHealth()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string chartPath = Path.Combine(root, "hydrated.bms");
            string stalePath = Path.Combine(root, "stale.bms");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = chartPath };
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 0, WavFilesExisting = 0 });

            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertMaintenanceInfos(
            [
                new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = chartPath, Hash = file.Md5, WavFilesDefined = 2, WavFilesExisting = 1 },
                new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = stalePath, Hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", WavFilesDefined = 1, WavFilesExisting = 0 }
            ]);

            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [file], []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            library.StartupBackgroundTaskScheduler = delegate (string _, string _, string _, Func<System.Threading.Tasks.Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };
            MethodInfo? queueMethod = typeof(BMSLibrary).GetMethod(
                "QueueDeferredMaintenanceHydration",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(queueMethod);
            queueMethod!.Invoke(library, ["test_hydration"]);

            file = library.BmsCharts.Single();
            Assert.AreEqual(2, file.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(1, file.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(1, verify.Query<LR2SongDBExtended.maintenance>("SELECT * FROM maintenance;").Count());
                Assert.AreEqual(chartPath, verify.Query<LR2SongDBExtended.maintenance>("SELECT * FROM maintenance;").Single().path);
            }
            ResourceHealthIndexSnapshot snapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.AreEqual(1, snapshot.TargetCount);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
        });
    }

    [TestMethod]
    public void SetChartResourceWarningsIgnored_InvalidatesInsteadOfFullRebuildWhenCurrentIndexUnavailable()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile target = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            target = target with { Path = @"C:\Library\target-warning.bms" };
            target = ChartFileProjection.WithMaintenance(target, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = target.Path, Hash = target.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
            ChartFile unrelated = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            unrelated = unrelated with { Path = @"C:\Library\unrelated-warning.bms" };
            unrelated = ChartFileProjection.WithMaintenance(unrelated, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = unrelated.Path, Hash = unrelated.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
            ChartFile targetChart = (target);
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [target, unrelated], []);
            library.BmsCharts = new List<ChartFile> { target, unrelated };

            library.SetChartResourceWarningsIgnored([targetChart], unset: false);

            ResourceHealthIndexSnapshot currentSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.AreEqual(0, currentSnapshot.TargetCount);
            Assert.IsFalse(target.ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
            Assert.IsTrue(library.BmsCharts.Single(chart => chart.Path == target.Path).ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
        });
    }

    [TestMethod]
    public void NormalLibraryRefreshNotificationBatch_MixedWarningProducersKeepWarningRefreshEffect()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string chartPath = Path.Combine(root, "warning.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE warning before\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            string oldMd5 = file.Md5;
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE warning after\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            ChartFile chart = (file);
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [file], []);
            EnsureCurrentResourceHealthIndex(library);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            library.SetChartResourceWarningsIgnored([chart], unset: false);
            ChartInfoInlineBuildResult result = InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_warning_property_covered_digest",
                [(file)]);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.AreEqual(1, result.DigestChanges.Count);
            Assert.AreEqual(oldMd5, result.DigestChanges.Single().OldMd5);
            Assert.AreEqual(oldMd5, file.Md5);
            file = library.BmsCharts.Single();
            Assert.AreNotEqual(oldMd5, file.Md5);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
        });
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsEncodingMaintenanceDoesNotMutateDigest()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService(1);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#WAV01 sub\\missing.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, new ResourceHealthMaintenanceSnapshot { Path = bmsFile.Path, Hash = bmsFile.Md5, Origin = MaintenanceInfoOrigin.Placeholder });
            string originalMd5 = bmsFile.Md5;
            string originalSha256 = bmsFile.Sha256;
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BgaFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { MovieFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { StagefileDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BackbmpDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BannerDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { Encoding = null });
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null,
                new ResourceHealthLookupContext(null));
            if (result.ChangedCharts.Any(chart => chart.Path == bmsFile.Path)) { bmsFile = result.ChangedCharts.Single(chart => chart.Path == bmsFile.Path); }

            Assert.AreEqual(1, result.MissingEncodingTargetCount);
            Assert.AreEqual(originalMd5, bmsFile.Md5);
            Assert.AreEqual(originalSha256, bmsFile.Sha256);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ResourceHealthIndexSnapshot_ApplyDeltaUpdatesOnlyAffectedTargets()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        active = active with { Path = @"C:\Library\active.bms" };
        active = ChartFileProjection.WithMaintenance(active, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = active.Path, Hash = active.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
        ChartFile ignored = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ignored = ignored with { Path = @"C:\Library\ignored.bms" };
        ignored = ChartFileProjection.WithMaintenance(ignored, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = ignored.Path, Hash = ignored.Md5, BgaFilesDefined = 2, BgaFilesExisting = 1, FilesWarningIgnored = true });
        ChartFile activeChart = (active);
        ChartFile ignoredChart = (ignored);
        var snapshot = ResourceHealthIndexSnapshot.Build([activeChart, ignoredChart], service, version: 1);
        var duplicateBuild = ResourceHealthIndexSnapshot.Build([activeChart, activeChart], service, version: 0);

        Assert.AreEqual(1, duplicateBuild.TargetCount);
        CollectionAssert.AreEqual(new[] { activeChart }, duplicateBuild.ActiveTargets.ToArray());

        ChartFile rehashed = CreateFile("dddddddddddddddddddddddddddddddd");
        rehashed = rehashed with { Path = active.Path };
        rehashed = ChartFileProjection.WithMaintenance(rehashed, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = rehashed.Path, Hash = rehashed.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
        ChartFile rehashedChart = (rehashed);
        ResourceHealthIndexSnapshot afterRehash = snapshot.ApplyDelta([rehashedChart], null, service, version: 2);

        Assert.AreEqual(2, afterRehash.TargetCount);
        Assert.IsFalse(afterRehash.GetProjection(activeChart).HasIssues);
        Assert.IsTrue(afterRehash.GetProjection(rehashedChart).HasIssues);
        CollectionAssert.AreEqual(new[] { rehashedChart }, afterRehash.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart }, afterRehash.IgnoredTargets.ToArray());

        ChartFile initialActiveChart = activeChart;
        active = ChartFileProjection.WithMaintenance(active, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = active.Path, Hash = active.Md5, WavFilesDefined = 2, WavFilesExisting = 2 });
        activeChart = (active);
        ResourceHealthIndexSnapshot afterFix = snapshot.ApplyDelta([activeChart], null, service, version: 2);

        Assert.AreEqual(2, afterFix.TargetCount);
        Assert.IsFalse(afterFix.GetProjection(activeChart).HasIssues);
        CollectionAssert.AreEqual(Array.Empty<ChartFile>(), afterFix.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart }, afterFix.IgnoredTargets.ToArray());

        ResourceHealthIndexSnapshot afterHealthyRemove = afterFix.ApplyDelta(null, [activeChart], service, version: 3);

        Assert.AreEqual(1, afterHealthyRemove.TargetCount);
        Assert.IsFalse(afterHealthyRemove.GetProjection(activeChart).HasIssues);
        CollectionAssert.AreEqual(Array.Empty<ChartFile>(), afterHealthyRemove.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart }, afterHealthyRemove.IgnoredTargets.ToArray());

        ChartFile added = CreateFile("cccccccccccccccccccccccccccccccc");
        added = added with { Path = @"C:\Library\added.bms" };
        added = ChartFileProjection.WithMaintenance(added, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = added.Path, Hash = added.Md5, BgaFilesDefined = 4, BgaFilesExisting = 3, FilesWarningIgnored = false });
        ChartFile addedChart = (added);
        ResourceHealthIndexSnapshot afterAdd = afterFix.ApplyDelta([addedChart], null, service, version: 3);

        Assert.AreEqual(3, afterAdd.TargetCount);
        Assert.IsTrue(afterAdd.GetProjection(addedChart).HasIssues);
        CollectionAssert.AreEqual(new[] { addedChart }, afterAdd.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart }, afterAdd.IgnoredTargets.ToArray());

        ResourceHealthIndexSnapshot afterDuplicateUpdate = afterAdd.ApplyDelta([addedChart, addedChart], null, service, version: 4);

        Assert.AreEqual(3, afterDuplicateUpdate.TargetCount);
        CollectionAssert.AreEqual(new[] { addedChart }, afterDuplicateUpdate.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart }, afterDuplicateUpdate.IgnoredTargets.ToArray());

        ResourceHealthIndexSnapshot afterRemove = afterDuplicateUpdate.ApplyDelta([ignoredChart], [ignoredChart], service, version: 5);

        Assert.AreEqual(2, afterRemove.TargetCount);
        Assert.IsFalse(afterRemove.GetProjection(ignoredChart).HasIssues);
        CollectionAssert.AreEqual(new[] { addedChart }, afterRemove.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(Array.Empty<ChartFile>(), afterRemove.IgnoredTargets.ToArray());

        Assert.AreEqual(2, snapshot.TargetCount);
        CollectionAssert.AreEqual(new[] { initialActiveChart }, snapshot.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart }, snapshot.IgnoredTargets.ToArray());
        Assert.IsTrue(snapshot.GetProjection(initialActiveChart).HasIssues);
        Assert.IsTrue(snapshot.GetProjection(ignoredChart).HasIssues);
    }

    /// <summary>R5c-WarningOrder / OldSnapshot: 更新群の順序と旧 snapshot を保持する。</summary>
    [TestMethod]
    public void ResourceHealthIndexSnapshot_PreservesWarningOrderAndOldSnapshotAcrossLocalDelta()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        const string activeAPath = @"C:\Library\active-a.bms";
        const string activeUnchangedPath = @"C:\Library\active-unchanged.bms";
        const string activeBPath = @"C:\Library\active-b.bms";
        const string activeCPath = @"C:\Library\active-c.bms";
        const string ignoredAPath = @"C:\Library\ignored-a.bms";
        const string ignoredUnchangedPath = @"C:\Library\ignored-unchanged.bms";
        const string ignoredBPath = @"C:\Library\ignored-b.bms";

        ChartFile activeA = CreateResourceHealthChart(
            activeAPath,
            new string('a', 32),
            wavFilesDefined: 2,
            wavFilesExisting: 1);
        ChartFile activeUnchanged = CreateResourceHealthChart(
            activeUnchangedPath,
            new string('b', 32),
            wavFilesDefined: 2,
            wavFilesExisting: 1);
        ChartFile activeB = CreateResourceHealthChart(
            activeBPath,
            new string('c', 32),
            bgaFilesDefined: 2,
            bgaFilesExisting: 1);
        ChartFile activeC = CreateResourceHealthChart(
            activeCPath,
            new string('d', 32),
            movieFilesDefined: 2,
            movieFilesExisting: 1);
        ChartFile ignoredA = CreateResourceHealthChart(
            ignoredAPath,
            new string('e', 32),
            wavFilesDefined: 2,
            wavFilesExisting: 1,
            isIgnored: true);
        ChartFile ignoredUnchanged = CreateResourceHealthChart(
            ignoredUnchangedPath,
            new string('f', 32),
            bgaFilesDefined: 2,
            bgaFilesExisting: 1,
            isIgnored: true);
        ChartFile ignoredB = CreateResourceHealthChart(
            ignoredBPath,
            new string('1', 32),
            bgaFilesDefined: 2,
            bgaFilesExisting: 1,
            isIgnored: true);

        var initial = ResourceHealthIndexSnapshot.Build(
            [activeA, activeUnchanged, activeB, activeC, ignoredA, ignoredUnchanged, ignoredB],
            service,
            version: 10);

        ChartFile activeCUpdated = CreateResourceHealthChart(
            activeCPath,
            new string('d', 32),
            movieFilesDefined: 3,
            movieFilesExisting: 1);
        ChartFile ignoredAUpdated = CreateResourceHealthChart(
            ignoredAPath,
            new string('e', 32),
            wavFilesDefined: 3,
            wavFilesExisting: 1);
        ChartFile ignoredBHealthy = CreateResourceHealthChart(
            ignoredBPath,
            new string('1', 32),
            bgaFilesDefined: 2,
            bgaFilesExisting: 2,
            isIgnored: true);
        ChartFile activeBUpdated = CreateResourceHealthChart(
            activeBPath,
            new string('c', 32),
            bgaFilesDefined: 3,
            bgaFilesExisting: 1,
            isIgnored: true);
        ChartFile added = CreateResourceHealthChart(
            @"C:\Library\added.bms",
            new string('2', 32),
            wavFilesDefined: 2,
            wavFilesExisting: 1);

        ResourceHealthIndexSnapshot updated = initial.ApplyDelta(
            [activeCUpdated, ignoredAUpdated, ignoredBHealthy, activeBUpdated, added],
            null,
            service,
            version: 11);

        CollectionAssert.AreEqual(
            new[] { activeAPath, activeUnchangedPath, activeCPath, ignoredAPath, @"C:\Library\added.bms" },
            updated.ActiveTargets.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEqual(
            new[] { ignoredUnchangedPath, activeBPath },
            updated.IgnoredTargets.Select(chart => chart.Path).ToArray());
        Assert.AreEqual(8, updated.TargetCount);
        Assert.IsFalse(updated.GetProjection(ignoredBHealthy).HasIssues);
        Assert.IsTrue(updated.GetProjection(activeCUpdated).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        Assert.IsTrue(updated.GetProjection(ignoredAUpdated).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.IsTrue(updated.GetProjection(activeBUpdated).IsIgnored);

        CollectionAssert.AreEqual(
            new[] { activeAPath, activeUnchangedPath, activeBPath, activeCPath },
            initial.ActiveTargets.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEqual(
            new[] { ignoredAPath, ignoredUnchangedPath, ignoredBPath },
            initial.IgnoredTargets.Select(chart => chart.Path).ToArray());
        Assert.AreEqual(7, initial.TargetCount);
        Assert.IsTrue(initial.GetProjection(activeC).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        Assert.IsTrue(initial.GetProjection(ignoredA).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.IsTrue(initial.GetProjection(activeB).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
    }

    /// <summary>R5c-LocalWork: 差分 lookup・sequence 操作と cold full enumeration を観測する。</summary>
    [TestMethod]
    public void ResourceHealthIndexSnapshot_LocalDeltaSharesUnchangedSequenceAndProjectionStorage()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        (int HealthyBackground, int WarningBackground)[] backgrounds =
        [
            (16, 16),
            (128, 16),
            (16, 128)
        ];
        List<(
            int HealthyBackground,
            int WarningBackground,
            int EntryLookups,
            int Comparisons,
            int EntryVisits)> observations = [];

        foreach ((int healthyBackgroundCount, int warningBackgroundCount) in backgrounds)
        {
            List<ChartFile> targets = [];
            for (int index = 0; index < warningBackgroundCount; index++)
            {
                targets.Add(CreateResourceHealthChart(
                    $@"C:\Library\warning-background-{warningBackgroundCount}-{index}.bms",
                    index.ToString("x8") + new string('0', 24),
                    wavFilesDefined: 2,
                    wavFilesExisting: 1,
                    isIgnored: index % 2 == 0));
            }
            ChartFile updated = CreateResourceHealthChart(
                $@"C:\Library\updated-{healthyBackgroundCount}-{warningBackgroundCount}.bms",
                new string('a', 32),
                wavFilesDefined: 2,
                wavFilesExisting: 1);
            ChartFile unchanged = CreateResourceHealthChart(
                $@"C:\Library\unchanged-{healthyBackgroundCount}-{warningBackgroundCount}.bms",
                new string('b', 32),
                bgaFilesDefined: 2,
                bgaFilesExisting: 1);
            targets.Add(updated);
            targets.Add(unchanged);
            for (int index = 0; index < healthyBackgroundCount; index++)
            {
                targets.Add(CreateResourceHealthChart(
                    $@"C:\Library\healthy-background-{healthyBackgroundCount}-{index}.bms",
                    index.ToString("x8") + new string('f', 24)));
            }

            var initial = ResourceHealthIndexSnapshot.Build(targets, service, version: 20);
            List<string> storeWork = [];
            initial.StoreWorkObserver = operation => storeWork.Add(operation);
            ChartFile updatedHealthy = CreateResourceHealthChart(
                updated.Path,
                updated.Md5,
                wavFilesDefined: 2,
                wavFilesExisting: 2);
            ResourceHealthIndexSnapshot after = initial.ApplyDelta(
                [updatedHealthy],
                null,
                service,
                version: 21);

            ResourceHealthWarningProjection unchangedProjection = after.GetProjection(unchanged);
            ResourceHealthWarningProjection updatedProjection = after.GetProjection(updatedHealthy);
            int activeCount = after.ActiveTargets.Count;
            int ignoredCount = after.IgnoredTargets.Count;
            string activeFirstPath = activeCount == 0 ? string.Empty : after.ActiveTargets[0].Path;
            string ignoredFirstPath = ignoredCount == 0 ? string.Empty : after.IgnoredTargets[0].Path;
            after.StoreWorkObserver = null;
            initial.StoreWorkObserver = null;

            Assert.AreEqual(targets.Count, initial.TargetCount);
            Assert.AreEqual(initial.TargetCount, after.TargetCount);
            Assert.IsTrue(unchangedProjection.HasIssues);
            Assert.IsFalse(updatedProjection.HasIssues);
            Assert.IsTrue(activeCount > 0);
            Assert.IsTrue(ignoredCount > 0);
            Assert.IsFalse(string.IsNullOrEmpty(activeFirstPath));
            Assert.IsFalse(string.IsNullOrEmpty(ignoredFirstPath));

            int entryLookups = storeWork.Count(operation => operation == "entry_lookup");
            int enumerations = storeWork.Count(operation => operation == "warning_sequence_enumeration");
            int comparisons = storeWork.Count(operation => operation == "warning_sequence_index_comparison");
            int entryVisits = storeWork.Count(operation => operation == "warning_sequence_entry_visited");
            Assert.AreEqual(3, entryLookups);
            Assert.AreEqual(0, enumerations);
            Assert.AreEqual(1, storeWork.Count(operation => operation == "warning_sequence_remove"));
            Assert.IsTrue(comparisons > 0);
            Assert.IsTrue(comparisons < warningBackgroundCount);
            Assert.AreEqual(0, entryVisits);
            observations.Add((
                healthyBackgroundCount,
                warningBackgroundCount,
                entryLookups,
                comparisons,
                entryVisits));
        }

        Assert.IsTrue(observations.All(result => result.EntryLookups == observations[0].EntryLookups));
        Assert.IsTrue(observations.All(result => result.EntryVisits == 0));

        ChartFile coldActive = CreateResourceHealthChart(
            @"C:\Library\cold-active.bms",
            new string('c', 32),
            bgaFilesDefined: 2,
            bgaFilesExisting: 1);
        ChartFile coldIgnored = CreateResourceHealthChart(
            @"C:\Library\cold-ignored.bms",
            new string('d', 32),
            movieFilesDefined: 2,
            movieFilesExisting: 1,
            isIgnored: true);
        ChartFile coldHealthy = CreateResourceHealthChart(
            @"C:\Library\cold-healthy.bms",
            new string('e', 32));
        var cold = ResourceHealthIndexSnapshot.Build(
            [coldActive, coldIgnored, coldHealthy],
            service,
            version: 22);
        List<string> coldWork = [];
        cold.StoreWorkObserver = operation => coldWork.Add(operation);
        int coldActiveCount = cold.ActiveTargets.Count;
        int coldIgnoredCount = cold.IgnoredTargets.Count;
        ChartFile[] coldActiveMaterialized = cold.ActiveTargets.ToArray();
        ChartFile[] coldIgnoredMaterialized = cold.IgnoredTargets.ToArray();
        cold.StoreWorkObserver = null;

        Assert.AreEqual(coldActiveCount, coldActiveMaterialized.Length);
        Assert.AreEqual(coldIgnoredCount, coldIgnoredMaterialized.Length);
        Assert.IsTrue(coldWork.Count(operation => operation == "warning_sequence_enumeration") > 0);
        Assert.IsTrue(coldWork.Count(operation => operation == "warning_sequence_entry_visited") > 0);
    }

    /// <summary>R2C-DELTA-REHASH: case-only rows remain independent across delta, rehash, and removal.</summary>
    [TestMethod]
    public void ResourceHealthIndexSnapshot_CaseOnlyExactPathsRemainIndependentAcrossDeltaAndRehash()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile upperInitial = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        upperInitial = upperInitial with { Path = @"C:\Library\Delta.bms" };
        upperInitial = ChartFileProjection.WithMaintenance(upperInitial, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = upperInitial.Path, Hash = upperInitial.Md5, WavFilesDefined = 2, WavFilesExisting = 1 });
        ChartFile lowerInitial = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        lowerInitial = lowerInitial with { Path = @"c:\library\delta.bms" };
        lowerInitial = ChartFileProjection.WithMaintenance(lowerInitial, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = lowerInitial.Path, Hash = lowerInitial.Md5, BgaFilesDefined = 2, BgaFilesExisting = 1 });
        ChartFile upperInitialChart = (upperInitial);
        ChartFile lowerInitialChart = (lowerInitial);
        var initial = ResourceHealthIndexSnapshot.Build(
            [upperInitialChart, lowerInitialChart],
            service,
            version: 1);

        const string sharedRehash = "cccccccccccccccccccccccccccccccc";
        ChartFile upperShared = CreateFile(sharedRehash);
        upperShared = upperShared with { Path = upperInitial.Path };
        upperShared = ChartFileProjection.WithMaintenance(upperShared, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = upperShared.Path, Hash = sharedRehash, MovieFilesDefined = 2, MovieFilesExisting = 1 });
        ChartFile lowerShared = CreateFile(sharedRehash);
        lowerShared = lowerShared with { Path = lowerInitial.Path };
        lowerShared = ChartFileProjection.WithMaintenance(lowerShared, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = lowerShared.Path, Hash = sharedRehash, BgaFilesDefined = 4, BgaFilesExisting = 2 });
        ChartFile upperSharedChart = (upperShared);
        ChartFile lowerSharedChart = (lowerShared);

        ResourceHealthIndexSnapshot bothRehashed = initial.ApplyDelta(
            [upperSharedChart, lowerSharedChart],
            null,
            service,
            version: 2);

        Assert.AreEqual(2, bothRehashed.TargetCount);
        Assert.IsTrue(bothRehashed.GetProjection(upperSharedChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        Assert.IsTrue(bothRehashed.GetProjection(lowerSharedChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
        Assert.IsFalse(bothRehashed.GetProjection(upperInitialChart).HasIssues);
        Assert.IsFalse(bothRehashed.GetProjection(lowerInitialChart).HasIssues);
        Assert.IsTrue(initial.GetProjection(upperInitialChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.IsTrue(initial.GetProjection(lowerInitialChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));

        ChartFile upperRehashedAgain = CreateFile("dddddddddddddddddddddddddddddddd");
        upperRehashedAgain = upperRehashedAgain with { Path = upperInitial.Path };
        upperRehashedAgain = ChartFileProjection.WithMaintenance(upperRehashedAgain, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = upperRehashedAgain.Path, Hash = upperRehashedAgain.Md5, WavFilesDefined = 3, WavFilesExisting = 1 });
        ChartFile upperRehashedAgainChart = (upperRehashedAgain);

        ResourceHealthIndexSnapshot upperUpdated = bothRehashed.ApplyDelta(
            [upperRehashedAgainChart],
            null,
            service,
            version: 3);

        Assert.AreEqual(2, upperUpdated.TargetCount);
        Assert.IsFalse(upperUpdated.GetProjection(upperSharedChart).HasIssues);
        Assert.IsTrue(upperUpdated.GetProjection(upperRehashedAgainChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.IsTrue(upperUpdated.GetProjection(lowerSharedChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
        Assert.IsTrue(bothRehashed.GetProjection(upperSharedChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));

        ResourceHealthIndexSnapshot upperRemoved = upperUpdated.ApplyDelta(
            null,
            [upperRehashedAgainChart],
            service,
            version: 4);

        Assert.AreEqual(1, upperRemoved.TargetCount);
        Assert.IsFalse(upperRemoved.GetProjection(upperRehashedAgainChart).HasIssues);
        Assert.IsTrue(upperRemoved.GetProjection(lowerSharedChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
        Assert.IsTrue(upperUpdated.GetProjection(upperRehashedAgainChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
    }

    [TestMethod]
    public void MaintenanceResourceHealthDelta_InvalidatesInsteadOfFullRebuildWhenDeltaCannotApply()
    {
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Path = @"C:\Library\delta.bms" };
        ChartFile chart = (file);

        ResourceHealthIndexMutation currentMutation = BuildMaintenanceResourceHealthMutation(
            [chart],
            isFullOwnedTarget: false,
            resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
            resourceHealthIndexCurrent: true,
            workflowHasUpdates: true);
        Assert.IsFalse(currentMutation.RebuildFull);
        Assert.IsFalse(currentMutation.Invalidate);
        Assert.IsTrue(currentMutation.InvalidateIfDeltaFails);
        Assert.AreEqual(1, currentMutation.UpdatedTargets.Count);

        ResourceHealthIndexMutation unavailableMutation = BuildMaintenanceResourceHealthMutation(
            [chart],
            isFullOwnedTarget: false,
            resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
            resourceHealthIndexCurrent: false,
            workflowHasUpdates: true);
        Assert.IsFalse(unavailableMutation.RebuildFull);
        Assert.IsTrue(unavailableMutation.Invalidate);
        Assert.IsFalse(unavailableMutation.InvalidateIfDeltaFails);
        Assert.AreEqual(0, unavailableMutation.UpdatedTargets.Count);
    }

    [TestMethod]
    public void GetZeroNoteCharts_FiltersOnlyZeroNoteCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile = ChartFileProjection.WithChartInfo(zeroNoteFile, new BeMusicSeeker.Models.ChartDetails { md5 = zeroNoteFile.Md5, sha256 = zeroNoteFile.Sha256, notes = 1200 });
        BeMusicSeeker.Models.ChartDetails zeroNoteInfo = CreateChartInfo(zeroNoteFile.Md5, notes: 0);
        ChartFile zeroNoteChart = (zeroNoteFile);
        ChartFile normalFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        normalFile = ChartFileProjection.WithChartInfo(normalFile, new BeMusicSeeker.Models.ChartDetails { md5 = normalFile.Md5, sha256 = normalFile.Sha256, notes = 0 });
        BeMusicSeeker.Models.ChartDetails normalInfo = CreateChartInfo(normalFile.Md5, notes: 1200);
        ChartFile normalChart = (normalFile);
        ChartFile missingChartInfoFile = CreateFile("dddddddddddddddddddddddddddddddd");
        missingChartInfoFile = ChartFileProjection.WithChartInfo(missingChartInfoFile, new BeMusicSeeker.Models.ChartDetails { md5 = missingChartInfoFile.Md5, sha256 = missingChartInfoFile.Sha256, notes = 0 });
        ChartFile missingChartInfoChart = (missingChartInfoFile);
        ChartFile zeroNoteBmson = CreateBmsonSong("C:\\Library\\chart.bmson", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        BeMusicSeeker.Models.ChartDetails zeroNoteBmsonInfo = CreateChartInfo(zeroNoteBmson.Md5, notes: 0);
        ChartFile zeroNoteBmsonChart = (zeroNoteBmson);

        List<ChartFile> result = service.GetZeroNoteCharts(
            [zeroNoteChart, normalChart, missingChartInfoChart, zeroNoteBmsonChart],
            CreateChartInfoResolver(zeroNoteInfo, normalInfo, zeroNoteBmsonInfo));

        CollectionAssert.AreEqual(new[] { zeroNoteChart }, result);
    }

    [TestMethod]
    public void GetZeroNoteCharts_UsesResolverChartInfo()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile chart = (file);

        List<ChartFile> result = service.GetZeroNoteCharts(
            [chart],
            candidate => ReferenceEquals(candidate.Token, file.Token)
                ? CreateChartInfo(file.Md5, notes: 0)
                : null);

        CollectionAssert.AreEqual(new[] { chart }, result);
    }

    [TestMethod]
    public void GetZeroNoteCharts_DoesNotFallbackWhenResolverMisses()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile chart = (file);

        List<ChartFile> result = service.GetZeroNoteCharts([chart], _ => null);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void BmsonMaintenanceOperations_UseChartEntryPoints()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile bmsonSong = CreateBmsonSong("C:\\Library\\chart.bmson", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        bmsonSong = bmsonSong with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = bmsonSong.Path, hash = bmsonSong.Md5, encoding = "utf-8" }) };
        bmsonSong = ChartFileProjection.WithMaintenance(bmsonSong, bmsonSong.ResourceHealthMaintenanceSnapshot with { Encoding = "gb2312" });
        bmsonSong = ChartFileProjection.WithMaintenance(bmsonSong, bmsonSong.ResourceHealthMaintenanceSnapshot with { EncodingFixed = false });
        bmsonSong = ChartFileProjection.WithMaintenance(bmsonSong, bmsonSong.ResourceHealthMaintenanceSnapshot with { FilesWarningIgnored = false });
        bmsonSong = ChartFileProjection.WithMaintenance(bmsonSong, bmsonSong.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 1 });
        bmsonSong = ChartFileProjection.WithMaintenance(bmsonSong, bmsonSong.ResourceHealthMaintenanceSnapshot with { WavFilesExisting = 0 });
        ChartFile bmsonChart = (bmsonSong);

        IReadOnlyList<ChartWarning> warnings = service.BuildResourceHealthWarnings(bmsonChart);
        Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        List<ChartFile> changes = service.SetChartResourceWarningsIgnored([bmsonChart], unset: false);
        Assert.AreEqual(1, changes.Count);

        Assert.IsFalse(bmsonSong.ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
        Assert.IsTrue(changes.Single().ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
    }

    [TestMethod]
    public void SetChartResourceWarningsIgnored_TogglesOnlyMatchingEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, FilesWarningIgnored = false };
        file = ChartFileProjection.WithMaintenance(file, info);

        List<ChartFile> changes = service.SetChartResourceWarningsIgnored([(file)], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.IsFalse(info.FilesWarningIgnored);
        Assert.IsTrue(changes.Single().ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
    }

    [TestMethod]
    public void GetGarbledFiles_IncludesUnknownEncodingInRegularAndFixedLists()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile unknownFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        unknownFile = ChartFileProjection.WithMaintenance(unknownFile, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = unknownFile.Path, Hash = unknownFile.Md5, Encoding = "unknown", EncodingFixed = false });
        ChartFile fixedUnknownFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        fixedUnknownFile = ChartFileProjection.WithMaintenance(fixedUnknownFile, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = fixedUnknownFile.Path, Hash = fixedUnknownFile.Md5, Encoding = "unknown", EncodingFixed = true });
        ChartFile shiftJisFile = CreateFile("cccccccccccccccccccccccccccccccc");
        shiftJisFile = ChartFileProjection.WithMaintenance(shiftJisFile, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = shiftJisFile.Path, Hash = shiftJisFile.Md5, Encoding = "shift_jis", EncodingFixed = false });
        ChartFile shiftJisQuestionFile = CreateFile("dddddddddddddddddddddddddddddddd");
        shiftJisQuestionFile = ChartFileProjection.WithMaintenance(shiftJisQuestionFile, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = shiftJisQuestionFile.Path, Hash = shiftJisQuestionFile.Md5, Encoding = "shift_jis?", EncodingFixed = false });
        ChartFile gb2312File = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        gb2312File = ChartFileProjection.WithMaintenance(gb2312File, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = gb2312File.Path, Hash = gb2312File.Md5, Encoding = "gb2312", EncodingFixed = false });
        ChartFile big5File = CreateFile("ffffffffffffffffffffffffffffffff");
        big5File = ChartFileProjection.WithMaintenance(big5File, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = big5File.Path, Hash = big5File.Md5, Encoding = "big5", EncodingFixed = false });

        List<ChartFile> regularList = service.GetGarbledFiles([unknownFile, fixedUnknownFile, shiftJisFile, shiftJisQuestionFile, gb2312File, big5File], isInFixedList: false);
        List<ChartFile> fixedList = service.GetGarbledFiles([unknownFile, fixedUnknownFile, shiftJisFile, shiftJisQuestionFile, gb2312File, big5File], isInFixedList: true);

        CollectionAssert.AreEquivalent(new ChartFile[] { unknownFile, gb2312File, big5File }, regularList);
        CollectionAssert.AreEquivalent(new ChartFile[] { fixedUnknownFile }, fixedList);
    }

    [TestMethod]
    public void ChartFileSubsets_ProjectGarbledAndLr2CompatibilityWarningRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile garbled = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            garbled = garbled with { Path = @"C:\Library\garbled.bms" };
            garbled = ChartFileProjection.WithMaintenance(garbled, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = garbled.Path, Hash = garbled.Md5, Encoding = "unknown", EncodingFixed = false });
            ChartFile fixedGarbled = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            fixedGarbled = fixedGarbled with { Path = @"C:\Library\fixed.bms" };
            fixedGarbled = ChartFileProjection.WithMaintenance(fixedGarbled, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = fixedGarbled.Path, Hash = fixedGarbled.Md5, Encoding = "gb2312", EncodingFixed = true });
            ChartFile lr2Warning = CreateFile("cccccccccccccccccccccccccccccccc");
            lr2Warning = lr2Warning with { Path = @"C:\Library\lr2-warning.bms" };
            lr2Warning = ChartFileProjection.WithMaintenance(lr2Warning, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = lr2Warning.Path, Hash = lr2Warning.Md5, Encoding = "shift_jis", EncodingFixed = false });
            lr2Warning = lr2Warning with { Warnings = [.. lr2Warning.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2ResourcePathTooLong), ChartWarning.Create(ChartWarningKind.Lr2ResourcePathTooLong, "resource path is too long for LR2")] };
            ChartFile parentBlankWithoutWarning = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            parentBlankWithoutWarning = parentBlankWithoutWarning with { Path = @"C:\Library\parent-blank.bms" };
            parentBlankWithoutWarning = ChartFileProjection.WithMaintenance(parentBlankWithoutWarning, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = parentBlankWithoutWarning.Path, Hash = parentBlankWithoutWarning.Md5, Encoding = "shift_jis", EncodingFixed = false });
            ChartFile registered = CreateFile("dddddddddddddddddddddddddddddddd");
            registered = registered with { Path = @"C:\Library\registered.bms" };
            registered = ChartFileProjection.WithMaintenance(registered, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = registered.Path, Hash = registered.Md5, Encoding = "shift_jis", EncodingFixed = false });
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [garbled, fixedGarbled, lr2Warning, parentBlankWithoutWarning, registered], []);

            garbled = library.BmsCharts.Single(chart => chart.Path == garbled.Path);
            fixedGarbled = library.BmsCharts.Single(chart => chart.Path == fixedGarbled.Path);
            lr2Warning = library.BmsCharts.Single(chart => chart.Path == lr2Warning.Path);
            List<ChartFile> garbledCharts = [.. library.ChartFilesGarbled];
            List<ChartFile> fixedCharts = [.. library.ChartFilesGarbledFixed];
            List<ChartFile> unregisteredCharts = [.. library.ChartFilesUnregistered];

            CollectionAssert.AreEqual(new[] { garbled.Path }, garbledCharts.Select(chart => chart.Path).ToArray());
            CollectionAssert.AreEqual(new[] { fixedGarbled.Path }, fixedCharts.Select(chart => chart.Path).ToArray());
            CollectionAssert.AreEqual(new[] { lr2Warning.Path }, unregisteredCharts.Select(chart => chart.Path).ToArray());
            Assert.AreSame(garbled.Token, garbledCharts[0].Token);
            Assert.AreSame(fixedGarbled.Token, fixedCharts[0].Token);
            Assert.AreSame(lr2Warning.Token, unregisteredCharts[0].Token);
        });
    }

    [TestMethod]
    public void RecheckZeroNoteWarnings_SkipsMissingFilesAndClearsWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile = ChartFileProjection.WithChartInfo(zeroNoteFile, new BeMusicSeeker.Models.ChartDetails { md5 = zeroNoteFile.Md5, sha256 = zeroNoteFile.Sha256, notes = 0 });
        BeMusicSeeker.Models.ChartDetails zeroNoteInfo = CreateChartInfo(zeroNoteFile.Md5, notes: 0);
        zeroNoteFile = zeroNoteFile with { Path = "C:\\missing\\chart.bms" };
        zeroNoteFile = zeroNoteFile with { Warnings = [.. zeroNoteFile.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch)] };

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(
            [(zeroNoteFile)],
            chartInfoResolver: CreateChartInfoResolver(zeroNoteInfo));

        Assert.AreEqual(1, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.SkippedCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.IsTrue(zeroNoteFile.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
        Assert.IsFalse(result.ChangedCharts.Single().Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
    }

    [TestMethod]
    public void RecheckZeroNoteWarnings_UsesResolverChartInfo()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile = zeroNoteFile with { Warnings = [.. zeroNoteFile.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch)] };
        ChartFile chart = (zeroNoteFile);

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(
            [chart],
            chartInfoResolver: candidate => ReferenceEquals(candidate.Token, zeroNoteFile.Token)
                ? CreateChartInfo(zeroNoteFile.Md5, notes: 1200)
                : null);

        Assert.AreEqual(0, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.IsTrue(zeroNoteFile.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
        Assert.IsFalse(result.ChangedCharts.Single().Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
    }

    [TestMethod]
    public void RecheckZeroNoteWarnings_DoesNotFallbackWhenResolverMisses()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        ChartFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile = zeroNoteFile with { Warnings = [.. zeroNoteFile.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch)] };
        ChartFile chart = (zeroNoteFile);

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(
            [chart],
            chartInfoResolver: _ => null);

        Assert.AreEqual(0, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.IsTrue(zeroNoteFile.Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
        Assert.IsFalse(result.ChangedCharts.Single().Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
    }

    [TestMethod]
    public void ApplyEncoding_ReloadsWhenEncodingMatchesButDecodedMetadataDiffers()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "同一Encoding再読込";
        const string expectedSubtitle = "[Manual]";
        const string expectedArtist = "差分あり";
        const string expectedSubartist = "obj:Manual";
        const string expectedGenre = "GENRE";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#SUBTITLE " + expectedSubtitle + "\r\n#ARTIST " + expectedArtist + "\r\n#SUBARTIST " + expectedSubartist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            file = file with { Title = "stale", RawTitle = "stale" };
            file = file with { Artist = "stale", RawArtist = "stale" };
            file = file with { Genre = "stale" };
            var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "gb2312", EncodingFixed = false };
            file = ChartFileProjection.WithMaintenance(file, info);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "gb2312");
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(expectedTitle, file.RawTitle);
            Assert.AreEqual(expectedSubtitle, file.RawSubtitle);
            Assert.AreEqual(expectedTitle + " " + expectedSubtitle, file.Title);
            Assert.AreEqual(expectedArtist, file.RawArtist);
            Assert.AreEqual(expectedSubartist, file.Subartist);
            Assert.AreEqual(expectedArtist + " " + expectedSubartist, file.Artist);
            Assert.AreEqual(expectedGenre, file.Genre);
            CollectionAssert.AreEqual(new ChartFile[] { file }, result.SongsToUpsert);
            CollectionAssert.AreEqual(new ResourceHealthMaintenanceSnapshot[] { file.ResourceHealthMaintenanceSnapshot }, result.MaintenanceInfosToUpsert);
            Assert.IsTrue(file.ResourceHealthMaintenanceSnapshot.EncodingFixed);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyEncoding_SkipsSongReloadWhenDecodedMetadataMatchesButUpdatesEncoding()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "一致タイトル";
        const string expectedArtist = "一致アーティスト";
        const string expectedGenre = "一致GENRE";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            file = file with { Title = expectedTitle, RawTitle = expectedTitle };
            file = file with { Artist = expectedArtist, RawArtist = expectedArtist };
            file = file with { Genre = expectedGenre };
            var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "unknown", EncodingFixed = false };
            file = ChartFileProjection.WithMaintenance(file, info);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "gb2312");
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(expectedTitle, file.Title);
            Assert.AreEqual(expectedArtist, file.Artist);
            Assert.AreEqual(expectedGenre, file.Genre);
            Assert.AreEqual("gb2312", file.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.IsTrue(file.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            Assert.AreEqual(0, result.SongsToUpsert.Count);
            CollectionAssert.AreEqual(new ResourceHealthMaintenanceSnapshot[] { file.ResourceHealthMaintenanceSnapshot }, result.MaintenanceInfosToUpsert);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyEncoding_ReloadsWhenComposedMetadataMatchesButRawMetadataDiffers()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "RawTitle";
        const string expectedSubtitle = "[SP HYPER]";
        const string expectedArtist = "RawArtist";
        const string expectedSubartist = "obj:Raw";
        const string expectedGenre = "GENRE";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#SUBTITLE " + expectedSubtitle + "\r\n#ARTIST " + expectedArtist + "\r\n#SUBARTIST " + expectedSubartist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("shift_jis"));
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            file = file with { Title = expectedTitle + " " + expectedSubtitle, RawTitle = expectedTitle + " " + expectedSubtitle };
            file = file with { Artist = expectedArtist + " " + expectedSubartist, RawArtist = expectedArtist + " " + expectedSubartist };
            file = file with { Genre = expectedGenre, AddDate = 123456, Favorite = 7, Tag = "retained-tag" };
            var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "shift_jis", EncodingFixed = false };
            file = ChartFileProjection.WithMaintenance(file, info);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "shift_jis");
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(expectedTitle, file.RawTitle);
            Assert.AreEqual(expectedSubtitle, file.RawSubtitle);
            Assert.AreEqual(expectedTitle + " " + expectedSubtitle, file.Title);
            Assert.AreEqual(expectedArtist, file.RawArtist);
            Assert.AreEqual(expectedSubartist, file.Subartist);
            Assert.AreEqual(expectedArtist + " " + expectedSubartist, file.Artist);
            Assert.AreEqual(expectedGenre, file.Genre);
            Assert.AreEqual(123456, file.AddDate);
            Assert.AreEqual(7, file.Favorite);
            Assert.AreEqual("retained-tag", file.Tag);
            Assert.IsTrue(file.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            CollectionAssert.AreEqual(new ChartFile[] { file }, result.SongsToUpsert);
            CollectionAssert.AreEqual(new ResourceHealthMaintenanceSnapshot[] { file.ResourceHealthMaintenanceSnapshot }, result.MaintenanceInfosToUpsert);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyEncoding_RaisesEncodingPropertyChangedWhenEncodingOnlyChanges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "一致タイトル";
        const string expectedArtist = "一致アーティスト";
        const string expectedGenre = "一致GENRE";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            file = file with { Title = expectedTitle, RawTitle = expectedTitle };
            file = file with { Artist = expectedArtist, RawArtist = expectedArtist };
            file = file with { Genre = expectedGenre };
            var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "unknown", EncodingFixed = false };
            file = ChartFileProjection.WithMaintenance(file, info);
            ChartFile captured = file;

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "gb2312");
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(0, result.SongsToUpsert.Count);
            Assert.AreEqual(1, result.MaintenanceInfosToUpsert.Count);
            Assert.AreEqual("gb2312", result.MaintenanceInfosToUpsert[0].Encoding);
            Assert.AreEqual(1, result.ChangedCharts.Count);
            Assert.AreNotSame(captured, file);
            Assert.AreSame(info, captured.ResourceHealthMaintenanceSnapshot);
            Assert.AreEqual("unknown", captured.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.AreEqual("gb2312", result.ChangedCharts[0].ResourceHealthMaintenanceSnapshot.Encoding);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ApplyEncoding_SkipsEntireUpdateWhenDecodedMetadataAndEncodingMatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "完全一致";
        const string expectedArtist = "一致";
        const string expectedGenre = "MATCH";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            file = file with { Title = expectedTitle, RawTitle = expectedTitle };
            file = file with { Artist = expectedArtist, RawArtist = expectedArtist };
            file = file with { Genre = expectedGenre };
            var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "gb2312", EncodingFixed = false };
            file = ChartFileProjection.WithMaintenance(file, info);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "gb2312");
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(0, result.SongsToUpsert.Count);
            Assert.AreEqual(0, result.MaintenanceInfosToUpsert.Count);
            Assert.AreEqual("gb2312", file.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.IsFalse(file.ResourceHealthMaintenanceSnapshot.EncodingFixed);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_RejectsDurableWriterThatReturnsNoReceipt()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Durable writer check\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
            Encoding.ASCII);
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            Func<CatalogMaintenanceWriteRequest, CatalogMaintenanceWriteReceipt> noOpWriter = _ => CatalogMaintenanceWriteReceipt.NotApplied;
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "shift_jis" });
            string originalTitle = file.Title;
            string originalHash = file.Md5;
            ResourceHealthMaintenanceSnapshot originalMaintenanceInfo = file.ResourceHealthMaintenanceSnapshot;
            ChartFile captured = file;

            Assert.ThrowsException<InvalidOperationException>(() =>
                new BmsLibraryMaintenanceService(1).UpdateMaintenanceInfo(
                    [(file)],
                    forceUpdate: true,
                    noOpWriter,
                    null));

            Assert.AreEqual(originalTitle, file.Title);
            Assert.AreEqual(originalHash, file.Md5);
            Assert.AreSame(originalMaintenanceInfo, file.ResourceHealthMaintenanceSnapshot);
            Assert.AreSame(captured, file);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_ReturnsChangedCurrentValueAfterDurableCommit()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#STAGEFILE missing.png\r\n#BANNER missing_banner.png\r\n#BACKBMP missing_back.bmp\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5 });
            ChartFile captured = file;

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(file)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.ChangedCharts.Count);
            ChartFile changed = result.ChangedCharts.Single();
            Assert.AreSame(captured, file);
            Assert.IsNull(file.StagefileHealth);
            Assert.IsFalse(changed.StagefileHealth.GetValueOrDefault(true));
            Assert.IsFalse(changed.BannerHealth.GetValueOrDefault(true));
            Assert.IsFalse(changed.BackbmpHealth.GetValueOrDefault(true));
            Assert.IsNull(changed.Resources);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonResourceHealth_UpsertsMaintenanceOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\", \"artist\": \"Artist\", \"eyecatch_image\": \"missing.png\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(song)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null);
            if (result.ChangedCharts.Any(chart => chart.Path == song.Path)) { song = result.ChangedCharts.Single(chart => chart.Path == song.Path); }

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.SongUpsertCount);
            Assert.AreEqual("Bmson", song.RawTitle);
            Assert.AreEqual("Artist", song.RawArtist);
            Assert.AreEqual("utf-8", song.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.IsFalse(song.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            Assert.AreEqual(1, song.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, song.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.AreEqual(true, song.ResourceHealthMaintenanceSnapshot.StagefileDefined);
            Assert.AreEqual(false, song.ResourceHealthMaintenanceSnapshot.StagefileExisting);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(1, songDb.Table<LR2SongDBExtended.maintenance>().Count());
                Assert.AreEqual(0, songDb.Table<LR2SongDB.song>().Count());
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonSongInputUpdatesSourceMaintenanceInfo()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(song)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null);
            if (result.ChangedCharts.Any(chart => chart.Path == song.Path)) { song = result.ChangedCharts.Single(chart => chart.Path == song.Path); }

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(0, result.BmsResourceTargetCount);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.IsNotNull(song.ResourceHealthMaintenanceSnapshot);
            Assert.AreEqual(song.Path, song.ResourceHealthMaintenanceSnapshot.Path);
            Assert.AreEqual(song.Md5, song.ResourceHealthMaintenanceSnapshot.Hash);
            Assert.AreEqual(1, song.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, song.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonUsesFreshResourceReferencesWithoutReparse()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\", \"artist\": \"Artist\", \"eyecatch_image\": \"missing.png\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            ImmutableList<ChartResourceReference>? resources = song.Resources;
            var packageEntry = PackageChartEntry.FromChart((song));
            DateTime timestamp = song.LastWriteTimeUtc;
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, timestamp);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [packageEntry.Chart],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null);
            if (result.ChangedCharts.Any(chart => chart.Path == song.Path)) { song = result.ChangedCharts.Single(chart => chart.Path == song.Path); }

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonReparseFailedCount);
            Assert.AreEqual(1, result.BmsonResourceReferenceReusedCount);
            Assert.AreEqual(1, song.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, song.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.AreEqual(true, song.ResourceHealthMaintenanceSnapshot.StagefileDefined);
            Assert.AreEqual(false, song.ResourceHealthMaintenanceSnapshot.StagefileExisting);
            Assert.IsNull(song.Resources);
            Assert.AreSame(resources, packageEntry.Chart.Resources);
            // 本文は既に壊れているため、必要期間の共有が失われて再取得するとこの呼出しは失敗します。
            packageEntry.AcquireResources();
            Assert.AreSame(resources, packageEntry.Chart.Resources);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonFreshReferencesAreStaleWhenTimestampChanges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, song.LastWriteTimeUtc.AddMinutes(1));
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(song)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null);

            Assert.IsFalse(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.BmsonReparsedCount);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
            Assert.IsNotNull(song.Resources);
            Assert.AreEqual(0, result.ChangedCharts.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UpdateMaintenanceInfo_BmsonParseFailureDoesNotPersistStaleMaintenanceInfo(bool forceUpdate)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            song = song with { ResourceHealthMaintenanceSnapshot = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = song.Path, Hash = "ffffffffffffffffffffffffffffffff", Encoding = "utf-8", WavFilesDefined = 1, WavFilesExisting = 1, BgaFilesDefined = 0, MovieFilesDefined = 0, StagefileDefined = false, BannerDefined = false, BackbmpDefined = false } };
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, song.LastWriteTimeUtc.AddMinutes(1));
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(song)],
                forceUpdate: forceUpdate,
                CreateDurableWriter(songDbPath),
                null);

            Assert.IsFalse(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.MaintenanceInfoUpsertCount);
            Assert.IsNotNull(song.Resources);
            Assert.AreEqual(0, result.ChangedCharts.Count);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(0, songDb.Table<LR2SongDBExtended.maintenance>().Count());
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonNonFreshRowReparsesResourceReferences()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile parsed = ChartTestValues.ReadBmson(bmsonFilePath);
            ChartFile loadedLikeRow = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = parsed.Path,
                Folder = parsed.Folder,
                RawTitle = parsed.RawTitle,
                Md5 = parsed.Md5,
                Sha256 = parsed.Sha256,
                LastWriteTimeUtc = parsed.LastWriteTimeUtc
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(loadedLikeRow)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null);

            loadedLikeRow = result.ChangedCharts.Single();
            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
            Assert.AreEqual(1, loadedLikeRow.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.IsNull(loadedLikeRow.Resources);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonForceUpdateReparsesFreshResourceReferences()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(song)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null);
            if (result.ChangedCharts.Any(chart => chart.Path == song.Path)) { song = result.ChangedCharts.Single(chart => chart.Path == song.Path); }

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
            Assert.IsNull(song.Resources);
            Assert.AreEqual(1, song.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void UpdateMaintenanceInfo_BmsonDurableFailureRestoresOnlyOriginalResources(bool forceUpdate, bool hadResources)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsLibraryMaintenanceServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "chart.bmson");
            File.WriteAllText(path, "{\"info\":{\"title\":\"Before\",\"eyecatch_image\":\"before.png\"},\"sound_channels\":[{\"name\":\"before.wav\",\"notes\":[]}]}");
            ChartFile song = ChartTestValues.ReadBmson(path);
            song = song with { Resources = hadResources ? song.Resources : null };
            ImmutableList<ChartResourceReference>? originalResources = song.Resources;
            ResourceHealthMaintenanceSnapshot originalInfo = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = song.Md5, encoding = "utf-8" });
            song = song with { ResourceHealthMaintenanceSnapshot = originalInfo };
            File.WriteAllText(path, "{\"info\":{\"title\":\"After\",\"eyecatch_image\":\"after.png\"},\"sound_channels\":[{\"name\":\"after.wav\",\"notes\":[]}]}");
            File.SetLastWriteTimeUtc(path, song.LastWriteTimeUtc.AddMinutes(1));
            int writeCount = 0;

            Assert.ThrowsException<InvalidOperationException>(() =>
                new BmsLibraryMaintenanceService(1).UpdateMaintenanceInfo(
                    [(song)],
                    forceUpdate,
                    request =>
                    {
                        writeCount++;
                        Assert.AreSame(originalResources, song.Resources);
                        Assert.AreEqual(1, request.MaintenanceInfos.Count);
                        return CatalogMaintenanceWriteReceipt.NotApplied;
                    },
                    null));

            Assert.AreEqual(1, writeCount);
            Assert.AreSame(originalResources, song.Resources);
            Assert.AreSame(originalInfo, song.ResourceHealthMaintenanceSnapshot);
            Assert.AreEqual("before.png", song.Stagefile);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_MixedBmsAndBmsonResourceHealth_ScansBothButDoesNotCreateBmsonSongRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#ARTIST Artist\r\n#WAV01 missing-bms.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\", \"artist\": \"Artist\" }, \"sound_channels\": [{ \"name\": \"missing-bmson.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            ChartFile bmsonSong = ChartTestValues.ReadBmson(bmsonFilePath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            int durableWriteCount = 0;
            Func<CatalogMaintenanceWriteRequest, CatalogMaintenanceWriteReceipt> durableWriter = request =>
            {
                durableWriteCount++;
                return CreateDurableWriter(songDbPath)(request);
            };
            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(bmsFile), (bmsonSong)],
                forceUpdate: true,
                durableWriter,
                null);
            if (result.ChangedCharts.Any(chart => chart.Path == bmsonSong.Path)) { bmsonSong = result.ChangedCharts.Single(chart => chart.Path == bmsonSong.Path); }
            if (result.ChangedCharts.Any(chart => chart.Path == bmsFile.Path)) { bmsFile = result.ChangedCharts.Single(chart => chart.Path == bmsFile.Path); }

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, durableWriteCount);
            Assert.AreEqual(1, result.BmsResourceTargetCount);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual("Bmson", bmsonSong.RawTitle);
            Assert.AreEqual("Artist", bmsonSong.RawArtist);
            Assert.AreEqual("utf-8", bmsonSong.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.AreEqual(1, bmsonSong.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, bmsonSong.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.AreEqual(1, bmsFile.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, bmsFile.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.IsNull(bmsFile.Resources);
            Assert.IsNull(bmsonSong.Resources);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(2, songDb.Table<LR2SongDBExtended.maintenance>().Count());
                Assert.IsFalse(songDb.Table<LR2SongDB.song>().ToList().Any(song => song.hash == bmsonSong.Md5));
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ResolveDefaultMaintenanceHealthDegree_UsesProcessorCountMinusOne()
    {
        Assert.AreEqual(Math.Max(1, Environment.ProcessorCount - 1), BmsLibraryMaintenanceService.ResolveDefaultMaintenanceHealthDegree());
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_UsesMaintenanceHealthDegreeOverride()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService(0);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            var lookupContext = new ResourceHealthLookupContext(null);
            var logs = new List<string>();

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null,
                lookupContext,
                progressLogger: logs.Add);
            if (result.ChangedCharts.Any(chart => chart.Path == bmsFile.Path)) { bmsFile = result.ChangedCharts.Single(chart => chart.Path == bmsFile.Path); }

            Assert.AreEqual(1, result.HealthDegree);
            Assert.AreEqual(1, result.HealthTargetCount);
            Assert.AreEqual(ChartFileReadPipelinePolicy.ResolveReaderDegree(Environment.ProcessorCount, result.HealthTargetCount), result.ReaderDegree);
            Assert.AreEqual(ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(result.HealthDegree, result.ReaderDegree), result.ReadQueueCapacity);
            Assert.AreEqual(2000, result.ComputedQueueCapacity);
            Assert.IsTrue(result.HealthMs >= 0);
            Assert.IsTrue(result.EncodingMs >= 0);
            Assert.IsTrue(result.DigestMs >= 0);
            Assert.IsTrue(result.HealthFileExistsFallbackCount > 0);
            Assert.AreEqual(result.HealthFileExistsFallbackCount, lookupContext.FileExistsFallbackCount);
            Assert.IsTrue(logs.Any(log => log.Contains("maintenance_rescan_chunk") && log.Contains("cacheHit=") && log.Contains("resourceIndexHit=") && log.Contains("fileExistsFallback=")));
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_NoTargetsReportsSummaryAndSkipsWork()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService(1);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        List<string> logs = [];
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, new ResourceHealthMaintenanceSnapshot { Path = bmsFile.Path, Hash = bmsFile.Md5, Origin = MaintenanceInfoOrigin.Placeholder });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BgaFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { MovieFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { StagefileDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BackbmpDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BannerDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { Encoding = "shift_jis" });
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null,
                progressLogger: logs.Add);
            if (result.ChangedCharts.Any(chart => chart.Path == bmsFile.Path)) { bmsFile = result.ChangedCharts.Single(chart => chart.Path == bmsFile.Path); }

            Assert.AreEqual(0, result.CheckedFileCount);
            Assert.AreEqual(0, result.HealthTargetCount);
            Assert.AreEqual(0, result.MaintenanceInfoUpsertCount);
            Assert.IsTrue(logs.Any(log => log.Contains("maintenance_target_summary") && log.Contains("total=0")));
            Assert.IsTrue(logs.Any(log => log.Contains("maintenance_update no_targets")));
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsMissingEncodingOnlySkipsResourceHealthScan()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService(1);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#WAV01 sub\\missing.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, new ResourceHealthMaintenanceSnapshot { Path = bmsFile.Path, Hash = bmsFile.Md5, Origin = MaintenanceInfoOrigin.Placeholder });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BgaFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { MovieFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { StagefileDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BackbmpDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BannerDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { Encoding = null });
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }
            var lookupContext = new ResourceHealthLookupContext(null);

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null,
                lookupContext);
            if (result.ChangedCharts.Any(chart => chart.Path == bmsFile.Path)) { bmsFile = result.ChangedCharts.Single(chart => chart.Path == bmsFile.Path); }

            Assert.AreEqual(1, result.CheckedFileCount);
            Assert.AreEqual(0, result.MissingInfoTargetCount);
            Assert.AreEqual(1, result.MissingEncodingTargetCount);
            Assert.AreEqual(0, result.HealthTargetCount);
            Assert.AreEqual(0, result.BmsResourceTargetCount);
            Assert.AreEqual(0, result.HealthFileExistsFallbackCount);
            Assert.AreEqual(0, lookupContext.FileExistsFallbackCount);
            Assert.AreEqual("shift_jis", bmsFile.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.AreEqual(1, result.MaintenanceInfoUpsertCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsResourceHealthPreservesIgnoredFlagUnlessForced()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService(1);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, new ResourceHealthMaintenanceSnapshot { Path = bmsFile.Path, Hash = bmsFile.Md5, Origin = MaintenanceInfoOrigin.Placeholder });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { Encoding = "shift_jis" });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { FilesWarningIgnored = true });
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            bmsFile = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null).ChangedCharts.Single();

            Assert.IsTrue(bmsFile.ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
            Assert.AreEqual("shift_jis", bmsFile.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.IsTrue(bmsFile.ResourceHealthMaintenanceSnapshot.GetWavHealth() < 100);

            bmsFile = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null).ChangedCharts.Single();

            Assert.IsFalse(bmsFile.ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
            Assert.AreEqual("shift_jis", bmsFile.ResourceHealthMaintenanceSnapshot.Encoding);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_CacheAwareHealthMatchesFileExistsPathAndAvoidsFallback()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "audio"));
        Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "image"));
        Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "movie"));
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "audio", "hit.ogg"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "image", "pic.jpg"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "movie", "clip.mp4"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "stage.jpg"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "banner.bmp"), "");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n"
            + "#WAV01 audio\\hit.wav\r\n"
            + "#WAV02 audio\\missing.wav\r\n"
            + "#BMP01 image\\pic.png\r\n"
            + "#BMP02 movie\\clip.mp4\r\n"
            + "#STAGEFILE stage.png\r\n"
            + "#BANNER banner.png\r\n"
            + "#BACKBMP missing_back.png\r\n"
            + "#00111:01\r\n"
            + "#00104:0102\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile legacyFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            ChartFile cacheAwareFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            ResourceHealthMaintenanceSnapshot legacyInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(legacyFile, null, forceUpdate: false);

            var directoryLookupCache = new DirectoryResourceLookupCache();
            string[] relativeResources =
            [
                "audio\\hit.ogg",
                "image\\pic.jpg",
                "movie\\clip.mp4",
                "stage.jpg",
                "banner.bmp"
            ];
            directoryLookupCache.AddDir(tempDirectoryPath, relativeResources);
            var lookupContext = new ResourceHealthLookupContext(directoryLookupCache);
            ResourceHealthMaintenanceSnapshot cacheInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(cacheAwareFile, lookupContext, forceUpdate: false);

            Assert.AreEqual(legacyInfo.WavFilesDefined, cacheInfo.WavFilesDefined);
            Assert.AreEqual(legacyInfo.WavFilesExisting, cacheInfo.WavFilesExisting);
            Assert.AreEqual(legacyInfo.BgaFilesDefined, cacheInfo.BgaFilesDefined);
            Assert.AreEqual(legacyInfo.BgaFilesExisting, cacheInfo.BgaFilesExisting);
            Assert.AreEqual(legacyInfo.MovieFilesDefined, cacheInfo.MovieFilesDefined);
            Assert.AreEqual(legacyInfo.MovieFilesExisting, cacheInfo.MovieFilesExisting);
            Assert.AreEqual(legacyInfo.StagefileExisting, cacheInfo.StagefileExisting);
            Assert.AreEqual(legacyInfo.BannerExisting, cacheInfo.BannerExisting);
            Assert.AreEqual(legacyInfo.BackbmpExisting, cacheInfo.BackbmpExisting);
            Assert.IsTrue(lookupContext.CacheHitCount >= 6);
            Assert.AreEqual(0, lookupContext.FileExistsFallbackCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_ReportsFallbackKinds()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n"
            + "#WAV01 sub\\missing.wav\r\n"
            + "#BMP01 image\\missing.png\r\n"
            + "#BMP02 movie\\missing.mp4\r\n"
            + "#STAGEFILE missing_stage.png\r\n"
            + "#00111:01\r\n"
            + "#00104:0102\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            var lookupContext = new ResourceHealthLookupContext(null);
            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, lookupContext, forceUpdate: false);

            Assert.IsTrue(lookupContext.FileExistsFallbackCount >= 4);
            Assert.IsTrue(lookupContext.AudioFileExistsFallbackCount >= 1);
            Assert.IsTrue(lookupContext.ImageFileExistsFallbackCount >= 1);
            Assert.IsTrue(lookupContext.MovieFileExistsFallbackCount >= 1);
            Assert.IsTrue(lookupContext.OptionalImageFileExistsFallbackCount >= 1);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonParseFailure_DoesNotAbortMaintenance()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string invalidBmsonPath = Path.Combine(tempDirectoryPath, "invalid.bmson");
        string validBmsonPath = Path.Combine(tempDirectoryPath, "valid.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(invalidBmsonPath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
        File.WriteAllText(
            validBmsonPath,
            "{ \"info\": { \"title\": \"Valid\", \"artist\": \"Artist\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile invalidRow = CreateBmsonSong(invalidBmsonPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            ChartFile validRow = ChartTestValues.ReadBmson(validBmsonPath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(invalidRow), (validRow)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(2, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(1, result.MaintenanceInfoUpsertCount);
            Assert.AreEqual(0, result.SongUpsertCount);
            validRow = result.ChangedCharts.Single(chart => chart.Path == validRow.Path);
            Assert.AreEqual("Valid", validRow.RawTitle);
            Assert.AreEqual(1, validRow.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, validRow.ResourceHealthMaintenanceSnapshot.WavFilesExisting);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                List<LR2SongDBExtended.maintenance> rows = [.. songDb.Table<LR2SongDBExtended.maintenance>()];
                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(validRow.Path, rows[0].path);
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void DeleteMaintenanceRows_DeletesOnlyRequestedRows()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        try
        {
            ChartFile bms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            bms = bms with { Path = Path.Combine(tempDirectoryPath, "chart.bms") };
            ChartFile bmson = CreateBmsonSong(Path.Combine(tempDirectoryPath, "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = bms.Path, hash = bms.Md5 }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = bmson.Path, hash = bmson.Md5, encoding = "utf-8" }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = Path.Combine(tempDirectoryPath, "stale.bms"), hash = "cccccccccccccccccccccccccccccccc" }, typeof(LR2SongDBExtended.maintenance));
            }

            int deleted = new BmsLibraryDbGateway(songDbPath).DeleteMaintenanceRows([Path.Combine(tempDirectoryPath, "stale.bms")]);

            Assert.AreEqual(1, deleted);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(2, songDb.Table<LR2SongDBExtended.maintenance>().Count());
                Assert.IsTrue(songDb.Table<LR2SongDBExtended.maintenance>().Any(info => info.path == bms.Path));
                Assert.IsTrue(songDb.Table<LR2SongDBExtended.maintenance>().Any(info => info.path == bmson.Path));
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [DataTestMethod]
    [DataRow("gb2312", "\u7b80\u4f53\u6807\u9898", "\u7b80\u4f53\u4f5c\u8005")]
    [DataRow("big5", "\u7e41\u9ad4\u6a19\u984c", "\u7e41\u9ad4\u4f5c\u8005")]
    [DataRow("ks_c_5601-1987", "\ud55c\uad6d\uc5b4\uc81c\ubaa9", "\ud55c\uad6d\uc5b4\uc791\uac00")]
    public void ApplyEncoding_ReloadsRequestedEncodingAndMarksFixed(string encoding, string expectedTitle, string expectedArtist)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string bmsContent = "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE TEST\r\n";
        File.WriteAllText(
            bmsFilePath,
            bmsContent,
            Encoding.GetEncoding(encoding, new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "unknown", EncodingFixed = false };
            file = ChartFileProjection.WithMaintenance(file, info);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], encoding);
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(expectedTitle, file.Title);
            Assert.AreEqual(expectedArtist, file.Artist);
            Assert.AreEqual(encoding, file.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.IsTrue(file.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            CollectionAssert.AreEqual(new ChartFile[] { file }, result.SongsToUpsert);
            CollectionAssert.AreEqual(new ResourceHealthMaintenanceSnapshot[] { file.ResourceHealthMaintenanceSnapshot }, result.MaintenanceInfosToUpsert);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BuildResourceHealthSnapshot_SharedExistenceCountsKeepOriginalLr2LengthsDistinct()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string stem = new string('a', 259 - directory.Length - 1 - ".wav".Length);
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(directory, [stem + ".wav"]);
            var context = new ResourceHealthLookupContext(cache);
            ChartFile wav = CreateProjectionOnlyBmsChart(Path.Combine(directory, "chart.bms"), new string('a', 32),
                audioResourcePaths: [stem + ".wav"]);
            ChartFile flac = CreateProjectionOnlyBmsChart(Path.Combine(directory, "chart.bms"), new string('b', 32),
                audioResourcePaths: [stem + ".flac"]);
            ResourceHealthMaintenanceSnapshot wavInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(wav, context);
            ResourceHealthMaintenanceSnapshot flacInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(flac, context);
            Assert.AreEqual(1, wavInfo.WavFilesExisting);
            Assert.AreEqual(1, flacInfo.WavFilesExisting);
            Assert.AreEqual(1L, context.ResourceHealthSetCacheHitCount);
            Assert.AreEqual((int)Lr2CompatibilityWarningFlags.None, wavInfo.Lr2WarningFlags);
            Assert.AreEqual((int)Lr2CompatibilityWarningFlags.ResourcePathTooLong, flacInfo.Lr2WarningFlags);
            Assert.AreEqual(wavInfo.Lr2ResourceMaxRelativeCp932Bytes + 1, flacInfo.Lr2ResourceMaxRelativeCp932Bytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ChartFile CreateProjectionOnlyBmsChart(
        string path,
        string md5,
        string stagefile = "",
        string backbmp = "",
        string banner = "",
        IReadOnlyList<string>? audioResourcePaths = null,
        IReadOnlyList<string>? visualResourcePaths = null)
    {
        return new ChartFile(kind: ChartFileKind.Bms, path: path, md5: md5, sha256: new string('a', 64), title: "Title", rawTitle: "Title", artist: "Artist", genre: string.Empty, folder: Path.GetFileName(Path.GetDirectoryName(path)), tag: string.Empty, levelText: string.Empty, level: null, mode: null, chartInfo: null, resources: TestChartResources.Create(audioResourcePaths, visualResourcePaths, stagefile, backbmp, banner), stagefile: stagefile, backbmp: backbmp, banner: banner);
    }

    private static ChartFile CreateFile(string hash)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = hash };
        return file;
    }

    private static ChartFile CreateResourceHealthChart(
        string path,
        string hash,
        int wavFilesDefined = 0,
        int wavFilesExisting = 0,
        int bgaFilesDefined = 0,
        int bgaFilesExisting = 0,
        int movieFilesDefined = 0,
        int movieFilesExisting = 0,
        bool isIgnored = false)
    {
        ChartFile file = CreateFile(hash);
        file = file with { Path = path };
        file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = wavFilesDefined, WavFilesExisting = wavFilesExisting, BgaFilesDefined = bgaFilesDefined, BgaFilesExisting = bgaFilesExisting, MovieFilesDefined = movieFilesDefined, MovieFilesExisting = movieFilesExisting, FilesWarningIgnored = isIgnored });
        return (file);
    }

    private static ChartFile CreateBmsonSong(string path, string md5)
    {
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = path,
            Folder = Path.GetDirectoryName(path),
            RawTitle = "Bmson Title",
            RawArtist = "Bmson Artist",
            Md5 = md5,
            Sha256 = new string('b', 64)
        };
        return song;
    }

    private static BeMusicSeeker.Models.ChartDetails CreateChartInfo(string md5, int notes)
    {
        return new BeMusicSeeker.Models.ChartDetails
        {
            md5 = md5,
            sha256 = new string('a', 64),
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            notes = notes
        };
    }

    private static Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateChartInfoResolver(params BeMusicSeeker.Models.ChartDetails[] rows)
    {
        return chart =>
        {
            if (chart == null)
            {
                return null!;
            }
            return (rows ?? [])
                .Where(row => row != null)
                .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Sha256) && string.Equals(row.sha256, chart.Sha256, StringComparison.OrdinalIgnoreCase))
                ?? (rows ?? [])
                    .Where(row => row != null)
                    .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Md5) && string.Equals(row.md5, chart.Md5, StringComparison.OrdinalIgnoreCase))
                ?? null!;
        };
    }

    private static void SetStorageRows(
        BMSLibrary library,
        IReadOnlyList<ChartFile> bmsFiles,
        IReadOnlyList<ChartFile> bmsonSongs)
    {
        library.BmsCharts = bmsFiles;
        library.BmsonCharts = bmsonSongs;
    }

    private static void EnsureCurrentResourceHealthIndex(BMSLibrary library)
    {
        _ = library.GetResourceHealthIndexSnapshotForView("test_resource_health");
    }

    private static ResourceHealthIndexMutation BuildMaintenanceResourceHealthMutation(
        List<ChartFile> charts,
        bool isFullOwnedTarget,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode,
        bool resourceHealthIndexCurrent,
        bool workflowHasUpdates)
    {
        ResourceMaintenanceTargetSet targetSet = CreateResourceMaintenanceTargetSet(charts, isFullOwnedTarget);
        return ResourceHealthIndexMutationPlanner.BuildMaintenanceMutation(
            targetSet,
            resourceHealthIndexUpdateMode,
            resourceHealthIndexCurrent,
            workflowHasUpdates);
    }

    private static ResourceMaintenanceTargetSet CreateResourceMaintenanceTargetSet(
        List<ChartFile> charts,
        bool isFullOwned,
        int ownedCollectionVersion = 1,
        int resourceHealthInputVersion = 1)
    {
        if (!isFullOwned)
        {
            return ResourceMaintenanceTargetSet.ForSubset(charts);
        }

        var storageRowsVersion = new OwnedChartCollectionVersionSnapshot(ownedCollectionVersion);
        return ResourceMaintenanceTargetSet.ForFullOwned(
            charts,
            storageRowsVersion,
            ownedCollectionVersion,
            resourceHealthInputVersion);
    }

    private static bool GetBoolProperty(object target, string propertyName)
    {
        PropertyInfo? propertyInfo = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(propertyInfo);
        return (bool)propertyInfo!.GetValue(target)!;
    }

    private static int GetListCountProperty(object target, string propertyName)
    {
        PropertyInfo? propertyInfo = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(propertyInfo);
        var value = (System.Collections.ICollection)propertyInfo!.GetValue(target)!;
        return value?.Count ?? 0;
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MaintenanceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static Func<CatalogMaintenanceWriteRequest, CatalogMaintenanceWriteReceipt> CreateDurableWriter(string songDbPath)
    {
        var owner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), new BmsLibraryDbGateway(songDbPath));
        return owner.ApplyMaintenanceWrite;
    }

}
