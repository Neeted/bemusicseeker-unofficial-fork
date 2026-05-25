using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryMaintenanceServiceTests
{
    [TestMethod]
    public void BuildResourceHealthWarnings_DoesNotMutateSourceWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var info = new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            wav_files_defined = 2,
            wav_files_existing = 1
        };
        file.SetMaintenanceInfo(info, suppressPropertyChanged: true);

        IReadOnlyList<ChartWarning> warnings = BmsLibraryMaintenanceService.BuildResourceHealthWarnings(info);

        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual(ChartWarningKind.ResourceWavMissing, warnings[0].Kind);
        Assert.IsFalse(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(string.Empty, file.Warnings.BuildDigestText());
    }

    [TestMethod]
    public void LazyMaintenancePlaceholder_IsNotAValidResourceHealthSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        _ = file.maintenanceInfo;
        ChartFile chart = ChartFileProjection.FromBmsFile(file);
        IReadOnlyList<ChartWarning> warnings = service.BuildResourceHealthWarnings(chart);
        var snapshot = ResourceHealthIndexSnapshot.Build([chart], service, version: 1);

        Assert.AreEqual(MaintenanceInfoOrigin.Placeholder, file.MaintenanceInfoOrigin);
        Assert.IsFalse(file.HasValidMaintenanceInfoSnapshot);
        Assert.AreEqual(0, warnings.Count);
        Assert.AreEqual(0, snapshot.ActiveTargets.Count);
        Assert.AreEqual(0, snapshot.IgnoredTargets.Count);
    }

    [TestMethod]
    public void PendingBmsonEncodingOnlyMaintenance_IsPlaceholder()
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Library\song.bmson",
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            sha256 = new string('c', 64)
        };
        song.MaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(song.path, song.md5);

        ChartFile chart = ChartFileProjection.FromBmsonSong(song);
        BMSFileMaintenanceInfo info = BmsLibraryMaintenanceService.GetResourceHealthMaintenanceInfo(chart);

        Assert.IsNotNull(info);
        Assert.AreEqual(song.md5, info.hash);
        Assert.AreEqual(song.path, info.path);
        Assert.IsFalse(song.MaintenanceInfo.IsInformationChecked());
    }

    [TestMethod]
    public void BuildResourceHealthMaintenanceInfo_ProjectionOnlyBmsonUsesChartResources()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bmson");
        try
        {
            var chart = new ChartFile(
                kind: ChartFileKind.Bmson,
                path: chartPath,
                md5: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                sha256: new string('c', 64),
                title: "Title",
                rawTitle: "Title",
                artist: "Artist",
                genre: string.Empty,
                folder: "Folder",
                tag: string.Empty,
                levelText: string.Empty,
                level: null,
                mode: null,
                chartInfo: null,
                bmsFile: null,
                bmsonSong: null,
                audioResourcePaths: ["missing.wav"],
                visualResourcePaths: ["missing.png", "missing.mp4"],
                stagefile: "stage.png");

            BMSFileMaintenanceInfo info = BmsLibraryMaintenanceService.BuildResourceHealthMaintenanceInfo(chart);

            Assert.IsNotNull(info);
            Assert.AreEqual("utf-8", info.encoding);
            Assert.AreEqual(1, info.wav_files_defined);
            Assert.AreEqual(0, info.wav_files_existing);
            Assert.AreEqual(1, info.bga_files_defined);
            Assert.AreEqual(0, info.bga_files_existing);
            Assert.AreEqual(1, info.movie_files_defined);
            Assert.AreEqual(0, info.movie_files_existing);
            Assert.AreEqual(true, info.is_stagefile_defined);
            Assert.AreEqual(false, info.is_stagefile_existing);
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
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Library\song.bmson",
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('d', 64)
        };
        Assert.IsNull(song.MaintenanceInfo);

        ChartFile chart = ChartFileProjection.FromBmsonSong(song);
        List<BMSFileMaintenanceInfo> changes = service.SetChartResourceWarningsIgnored([chart], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.IsTrue(changes[0].is_files_warning_ignored);
        Assert.AreSame(changes[0], song.MaintenanceInfo);
        Assert.IsTrue(song.MaintenanceInfo.is_files_warning_ignored);
    }

    [TestMethod]
    public void SetChartResourceWarningsIgnored_ChartBmsonAttachesComputedMaintenanceInfoToSourceSong()
    {
        var service = new BmsLibraryMaintenanceService();
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Library\chart.bmson",
            md5 = "dddddddddddddddddddddddddddddddd",
            sha256 = new string('e', 64),
            wav_files = ["missing.wav"]
        };
        song.MaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(song.path, song.md5);
        ChartFile chart = ChartFileProjection.FromBmsonSong(song);

        List<BMSFileMaintenanceInfo> changes = service.SetChartResourceWarningsIgnored([chart], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.AreSame(changes[0], song.MaintenanceInfo);
        Assert.AreEqual(1, song.MaintenanceInfo.wav_files_defined);
        Assert.AreEqual(0, song.MaintenanceInfo.wav_files_existing);
        Assert.IsTrue(song.MaintenanceInfo.is_files_warning_ignored);
    }

    [TestMethod]
    public void SetChartResourceWarningsIgnored_ChartBmsonReplacesStaleMaintenanceHash()
    {
        var service = new BmsLibraryMaintenanceService();
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Library\chart.bmson",
            md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            sha256 = new string('f', 64),
            wav_files = ["missing.wav"]
        };
        song.MaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(song.path, "ffffffffffffffffffffffffffffffff");
        song.MaintenanceInfo.wav_files_defined = 1;
        song.MaintenanceInfo.wav_files_existing = 1;
        song.MaintenanceInfo.is_files_warning_ignored = true;
        ChartFile chart = ChartFileProjection.FromBmsonSong(song);

        List<BMSFileMaintenanceInfo> changes = service.SetChartResourceWarningsIgnored([chart], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.AreSame(changes[0], song.MaintenanceInfo);
        Assert.AreEqual(song.md5, song.MaintenanceInfo.hash);
        Assert.AreEqual(1, song.MaintenanceInfo.wav_files_defined);
        Assert.AreEqual(0, song.MaintenanceInfo.wav_files_existing);
        Assert.IsTrue(song.MaintenanceInfo.is_files_warning_ignored);
    }

    [TestMethod]
    public void BuildBmsResourceHealthMaintenanceInfo_RootReferenceDoesNotMatchNestedResource()
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
            var file = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [ChartResourceKeyHash.GetLookupHash(@"sound\foo")],
                [],
                []);

            BMSFileMaintenanceInfo info = BmsLibraryMaintenanceService.BuildBmsResourceHealthMaintenanceInfo(file, new ResourceHealthLookupContext(cache), forceUpdate: true);

            Assert.AreEqual(1, info.wav_files_defined);
            Assert.AreEqual(0, info.wav_files_existing);
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
    public void BuildBmsResourceHealthMaintenanceInfo_NestedReferenceDoesNotMatchRootResource()
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
            var file = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [ChartResourceKeyHash.GetLookupHash("foo")],
                [],
                []);

            BMSFileMaintenanceInfo info = BmsLibraryMaintenanceService.BuildBmsResourceHealthMaintenanceInfo(file, new ResourceHealthLookupContext(cache), forceUpdate: true);

            Assert.AreEqual(1, info.wav_files_defined);
            Assert.AreEqual(0, info.wav_files_existing);
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
    public void BuildBmsResourceHealthMaintenanceInfo_RootReferenceMatchesRootRelativeResource()
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
            var file = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [ChartResourceKeyHash.GetLookupHash("foo")],
                [],
                []);

            BMSFileMaintenanceInfo info = BmsLibraryMaintenanceService.BuildBmsResourceHealthMaintenanceInfo(file, new ResourceHealthLookupContext(cache), forceUpdate: true);

            Assert.AreEqual(1, info.wav_files_defined);
            Assert.AreEqual(1, info.wav_files_existing);
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
    public void BuildBmsResourceHealthMaintenanceInfo_WithoutLookupContextRootReferenceDoesNotMatchNestedResource()
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
            var file = BMSFile.CreateBMSFileFromFile(bmsFilePath);

            BMSFileMaintenanceInfo info = BmsLibraryMaintenanceService.BuildBmsResourceHealthMaintenanceInfo(file, null, forceUpdate: true);

            Assert.AreEqual(1, info.wav_files_defined);
            Assert.AreEqual(0, info.wav_files_existing);
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
    public void BuildBmsResourceHealthMaintenanceInfo_WithoutLookupContextNestedReferenceDoesNotMatchRootResource()
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
            var file = BMSFile.CreateBMSFileFromFile(bmsFilePath);

            BMSFileMaintenanceInfo info = BmsLibraryMaintenanceService.BuildBmsResourceHealthMaintenanceInfo(file, null, forceUpdate: true);

            Assert.AreEqual(1, info.wav_files_defined);
            Assert.AreEqual(0, info.wav_files_existing);
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
    public void BuildBmsResourceHealthMaintenanceInfo_MissingBmsFileReturnsNull()
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
            var file = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            File.Delete(bmsFilePath);
            file.ClearResourceReferenceCollections();

            BMSFileMaintenanceInfo info = BmsLibraryMaintenanceService.BuildBmsResourceHealthMaintenanceInfo(file, null, forceUpdate: true);

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
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.SetWarning(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile);
        file.SetWarning(ChartWarningKind.ResourceWavMissing, "stale resource warning");
        var info = new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            bga_files_defined = 4,
            bga_files_existing = 3
        };
        file.SetMaintenanceInfo(info, suppressPropertyChanged: true);
        var row = LibraryChartRow.FromBmsFile(file);
        row.SetResourceHealthProjectionProvider(_ => new ResourceHealthWarningProjection(1, BmsLibraryMaintenanceService.BuildResourceHealthWarnings(info), isIgnored: false));

        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_DuplicateChart);
        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_ResourceMissing);
        StringAssert.Contains(row.WarningTooltipText, "BGA");
        Assert.IsFalse(row.WarningTooltipText.Contains("stale resource warning"));
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));

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
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.SetWarning(ChartWarningKind.ResourceWavMissing, "pending resource warning");
        file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            wav_files_defined = 2,
            wav_files_existing = 2
        }, suppressPropertyChanged: true);
        var row = LibraryChartRow.FromBmsFile(file);
        row.SetResourceHealthProjectionProvider(_ => ResourceHealthWarningProjection.Empty);

        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(string.Empty, row.WarningDigestText);
        Assert.IsFalse(row.WarningTooltipText.Contains("pending resource warning"));
    }

    [TestMethod]
    public void ResourceHealthIndexSnapshot_GroupsActiveAndIgnoredWithoutMutatingWarnings()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        active.path = @"C:\Library\active.bms";
        active.SetMaintenanceInfo(new BMSFileMaintenanceInfo(active)
        {
            hash = active.hash,
            wav_files_defined = 2,
            wav_files_existing = 1,
            is_files_warning_ignored = false
        }, suppressPropertyChanged: true);
        TestableBmsFile ignored = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ignored.path = @"C:\Library\ignored.bms";
        ignored.SetMaintenanceInfo(new BMSFileMaintenanceInfo(ignored)
        {
            hash = ignored.hash,
            bga_files_defined = 2,
            bga_files_existing = 1,
            is_files_warning_ignored = true
        }, suppressPropertyChanged: true);

        ChartFile activeChart = ChartFileProjection.FromBmsFile(active);
        ChartFile ignoredChart = ChartFileProjection.FromBmsFile(ignored);
        var snapshot = ResourceHealthIndexSnapshot.Build([activeChart, ignoredChart], service, version: 3);

        CollectionAssert.AreEqual(new[] { activeChart }, snapshot.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart }, snapshot.IgnoredTargets.ToArray());
        Assert.IsTrue(snapshot.GetProjection(activeChart).HasIssues);
        Assert.IsFalse(snapshot.GetProjection(activeChart).IsIgnored);
        Assert.IsTrue(snapshot.GetProjection(ignoredChart).IsIgnored);
        Assert.IsFalse(active.Warnings.Contains(ChartWarningKind.ResourceWavMissing));
        Assert.IsFalse(ignored.Warnings.Contains(ChartWarningKind.ResourceBgaMissing));
    }

    [TestMethod]
    public void ResourceHealthIndexSnapshot_BuildsBmsonTargetFromChartFile()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\Library\chart.bmson",
            md5 = "cccccccccccccccccccccccccccccccc",
            sha256 = new string('c', 64)
        };
        song.MaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(song.path, song.md5);
        song.MaintenanceInfo.wav_files_defined = 3;
        song.MaintenanceInfo.wav_files_existing = 1;
        ChartFile chart = ChartFileProjection.FromBmsonSong(song);

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
            var service = new BmsLibraryMaintenanceService();
            TestableBmsFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            active.path = @"C:\Library\active.bms";
            active.SetMaintenanceInfo(new BMSFileMaintenanceInfo(active)
            {
                hash = active.hash,
                wav_files_defined = 2,
                wav_files_existing = 1
            }, suppressPropertyChanged: true);
            ChartFile activeChart = ChartFileProjection.FromBmsFile(active);
            ResourceHealthIndexSnapshot snapshot = ResourceHealthIndexSnapshot.Build([activeChart], service, version: 7);
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [],
                BmsonSongs = []
            };
            SetPrivateField(library, "resourceHealthIndexSnapshot", snapshot);
            SetPrivateField(library, "resourceHealthIndexInvalidated", false);

            List<ChartFile> result = library.GetChartsNeedResourceFix(null);

            CollectionAssert.AreEqual(new[] { activeChart }, result.ToArray());
        });
    }

    [TestMethod]
    public void RescanResourceHealthCharts_PublishesMaintenanceRefreshThroughOwnedDispatcher()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath), "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE maintenance\r\n", Encoding.ASCII);
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = chartPath;
            var library = new BMSLibrary(songDbPath);
            SetPrivateField(library, "_BMSFiles", new List<BMSFile> { file });
            SetPrivateField(library, "_BmsonSongs", new List<LR2SongDBExtended.bmson_song>());
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int refreshNotificationChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };

            MaintenanceWorkflowResult result = library.RescanResourceHealthCharts(
                [ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false)]);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(result.HasUpdates);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.AreEqual(1, refreshNotificationChanged);
        });
    }

    [TestMethod]
    public void SetChartResourceWarningsIgnored_PublishesWarningRefreshThroughOwnedDispatcher()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = @"C:\Library\warning.bms";
            file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                wav_files_defined = 2,
                wav_files_existing = 1,
                is_files_warning_ignored = false
            }, suppressPropertyChanged: true);
            ChartFile chart = ChartFileProjection.FromBmsFile(file);
            ResourceHealthIndexSnapshot snapshot = ResourceHealthIndexSnapshot.Build([chart], new BmsLibraryMaintenanceService(), version: 3);
            var library = new BMSLibrary(songDbPath);
            SetPrivateField(library, "_BMSFiles", new List<BMSFile> { file });
            SetPrivateField(library, "_BmsonSongs", new List<LR2SongDBExtended.bmson_song>());
            SetPrivateField(library, "resourceHealthIndexSnapshot", snapshot);
            SetPrivateField(library, "resourceHealthIndexInvalidated", false);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int refreshNotificationChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
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
            Assert.IsFalse(batch.NotifiesWarningPresentationProperties);
            Assert.AreEqual(1, refreshNotificationChanged);
            Assert.AreEqual(0, updatedSnapshot.ActiveTargets.Count);
            Assert.AreEqual(1, updatedSnapshot.IgnoredTargets.Count);
        });
    }

    [TestMethod]
    public void NormalLibraryRefreshNotificationBatch_MixedWarningPropertyCoverageRequiresNotificationRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = @"C:\Library\warning.bms";
            file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                wav_files_defined = 2,
                wav_files_existing = 1,
                is_files_warning_ignored = false
            }, suppressPropertyChanged: true);
            ChartFile chart = ChartFileProjection.FromBmsFile(file);
            ResourceHealthIndexSnapshot snapshot = ResourceHealthIndexSnapshot.Build([chart], new BmsLibraryMaintenanceService(), version: 3);
            var library = new BMSLibrary(songDbPath);
            SetPrivateField(library, "_BMSFiles", new List<BMSFile> { file });
            SetPrivateField(library, "_BmsonSongs", new List<LR2SongDBExtended.bmson_song>());
            SetPrivateField(library, "resourceHealthIndexSnapshot", snapshot);
            SetPrivateField(library, "resourceHealthIndexInvalidated", false);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            library.SetChartResourceWarningsIgnored([chart], unset: false);
            file.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            InvokeDispatchOwnedChartHashChanges(
                library,
                [new LibraryChartHashChange(LibraryChartKind.Bms, file.path, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, file.hash, null)],
                "test_warning_property_covered_hash");

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsFalse(batch.NotifiesWarningPresentationProperties);
        });
    }

    [TestMethod]
    public void ResourceHealthIndexSnapshot_ApplyDeltaUpdatesOnlyAffectedTargets()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        active.path = @"C:\Library\active.bms";
        active.SetMaintenanceInfo(new BMSFileMaintenanceInfo(active)
        {
            hash = active.hash,
            wav_files_defined = 2,
            wav_files_existing = 1,
            is_files_warning_ignored = false
        }, suppressPropertyChanged: true);
        TestableBmsFile ignored = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ignored.path = @"C:\Library\ignored.bms";
        ignored.SetMaintenanceInfo(new BMSFileMaintenanceInfo(ignored)
        {
            hash = ignored.hash,
            bga_files_defined = 2,
            bga_files_existing = 1,
            is_files_warning_ignored = true
        }, suppressPropertyChanged: true);
        ChartFile activeChart = ChartFileProjection.FromBmsFile(active);
        ChartFile ignoredChart = ChartFileProjection.FromBmsFile(ignored);
        var snapshot = ResourceHealthIndexSnapshot.Build([activeChart, ignoredChart], service, version: 1);
        var duplicateBuild = ResourceHealthIndexSnapshot.Build([activeChart, activeChart], service, version: 0);

        Assert.AreEqual(1, duplicateBuild.TargetCount);
        CollectionAssert.AreEqual(new[] { activeChart }, duplicateBuild.ActiveTargets.ToArray());

        TestableBmsFile rehashed = CreateFile("dddddddddddddddddddddddddddddddd");
        rehashed.path = active.path;
        rehashed.SetMaintenanceInfo(new BMSFileMaintenanceInfo(rehashed)
        {
            hash = rehashed.hash,
            wav_files_defined = 2,
            wav_files_existing = 1,
            is_files_warning_ignored = false
        }, suppressPropertyChanged: true);
        ChartFile rehashedChart = ChartFileProjection.FromBmsFile(rehashed);
        ResourceHealthIndexSnapshot afterRehash = snapshot.ApplyDelta([rehashedChart], null, service, version: 2);

        Assert.AreEqual(2, afterRehash.TargetCount);
        Assert.IsFalse(afterRehash.GetProjection(activeChart).HasIssues);
        Assert.IsTrue(afterRehash.GetProjection(rehashedChart).HasIssues);
        CollectionAssert.AreEqual(new[] { rehashedChart }, afterRehash.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart }, afterRehash.IgnoredTargets.ToArray());

        active.SetMaintenanceInfo(new BMSFileMaintenanceInfo(active)
        {
            hash = active.hash,
            wav_files_defined = 2,
            wav_files_existing = 2
        }, suppressPropertyChanged: true);
        activeChart = ChartFileProjection.FromBmsFile(active);
        ResourceHealthIndexSnapshot afterFix = snapshot.ApplyDelta([activeChart], null, service, version: 2);

        Assert.AreEqual(2, afterFix.TargetCount);
        Assert.IsFalse(afterFix.GetProjection(activeChart).HasIssues);
        CollectionAssert.AreEqual(Array.Empty<ChartFile>(), afterFix.ActiveTargets.ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart }, afterFix.IgnoredTargets.ToArray());

        TestableBmsFile added = CreateFile("cccccccccccccccccccccccccccccccc");
        added.path = @"C:\Library\added.bms";
        added.SetMaintenanceInfo(new BMSFileMaintenanceInfo(added)
        {
            hash = added.hash,
            bga_files_defined = 4,
            bga_files_existing = 3,
            is_files_warning_ignored = false
        }, suppressPropertyChanged: true);
        ChartFile addedChart = ChartFileProjection.FromBmsFile(added);
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
    }

    [TestMethod]
    public void GetZeroNoteCharts_FiltersOnlyZeroNoteCharts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile.SetNotes(1200);
        LR2SongDBExtended.chart_info zeroNoteInfo = CreateChartInfo(zeroNoteFile.hash, notes: 0);
        ChartFile zeroNoteChart = ChartFileProjection.FromBmsFile(zeroNoteFile);
        TestableBmsFile normalFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        normalFile.SetNotes(0);
        LR2SongDBExtended.chart_info normalInfo = CreateChartInfo(normalFile.hash, notes: 1200);
        ChartFile normalChart = ChartFileProjection.FromBmsFile(normalFile);
        TestableBmsFile missingChartInfoFile = CreateFile("dddddddddddddddddddddddddddddddd");
        missingChartInfoFile.SetNotes(0);
        ChartFile missingChartInfoChart = ChartFileProjection.FromBmsFile(missingChartInfoFile);
        LR2SongDBExtended.bmson_song zeroNoteBmson = CreateBmsonSong("C:\\Library\\chart.bmson", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        LR2SongDBExtended.chart_info zeroNoteBmsonInfo = CreateChartInfo(zeroNoteBmson.md5, notes: 0);
        ChartFile zeroNoteBmsonChart = ChartFileProjection.FromBmsonSong(zeroNoteBmson);

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
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile chart = ChartFileProjection.FromBmsFile(file);

        List<ChartFile> result = service.GetZeroNoteCharts(
            [chart],
            candidate => ReferenceEquals(candidate.GetBmsStorageOwner(), file)
                ? CreateChartInfo(file.hash, notes: 0)
                : null);

        CollectionAssert.AreEqual(new[] { chart }, result);
    }

    [TestMethod]
    public void GetZeroNoteCharts_DoesNotFallbackWhenResolverMisses()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        ChartFile chart = ChartFileProjection.FromBmsFile(file);

        List<ChartFile> result = service.GetZeroNoteCharts([chart], _ => null);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void BmsonMaintenanceOperations_UseChartEntryPoints()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong("C:\\Library\\chart.bmson", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        bmsonSong.MaintenanceInfo = BMSFileMaintenanceInfo.CreateForBmson(bmsonSong.path, bmsonSong.md5);
        bmsonSong.MaintenanceInfo.encoding = "gb2312";
        bmsonSong.MaintenanceInfo.is_encoding_fixed = false;
        bmsonSong.MaintenanceInfo.is_files_warning_ignored = false;
        bmsonSong.MaintenanceInfo.wav_files_defined = 1;
        bmsonSong.MaintenanceInfo.wav_files_existing = 0;
        ChartFile bmsonChart = ChartFileProjection.FromBmsonSong(bmsonSong);

        IReadOnlyList<ChartWarning> warnings = service.BuildResourceHealthWarnings(bmsonChart);
        Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.AreEqual(1, service.SetChartResourceWarningsIgnored([bmsonChart], unset: false).Count);

        Assert.IsTrue(bmsonSong.MaintenanceInfo.is_files_warning_ignored);
    }

    [TestMethod]
    public void SetChartResourceWarningsIgnored_TogglesOnlyMatchingEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var info = new BMSFileMaintenanceInfo(file)
        {
            hash = file.hash,
            is_files_warning_ignored = false
        };
        file.SetMaintenanceInfo(info, suppressPropertyChanged: true);

        List<BMSFileMaintenanceInfo> changes = service.SetChartResourceWarningsIgnored([ChartFileProjection.FromBmsFile(file)], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.IsTrue(info.is_files_warning_ignored);
    }

    [TestMethod]
    public void CreateBmsonMaintenanceInfo_SetsHashAndPath()
    {
        LR2SongDBExtended.bmson_song bmsonRow = CreateBmsonSong("C:\\Library\\chart.bmson", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        BMSFileMaintenanceInfo info = BMSFileMaintenanceInfo.CreateForBmson(bmsonRow.path, bmsonRow.md5);

        Assert.AreEqual(bmsonRow.md5, info.hash);
        Assert.AreEqual(bmsonRow.path, info.path);
        Assert.AreEqual("utf-8", info.encoding);
        Assert.IsFalse(info.is_encoding_fixed);
    }

    [TestMethod]
    public void GetGarbledFiles_IncludesUnknownEncodingInRegularAndFixedLists()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile unknownFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        unknownFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(unknownFile)
        {
            hash = unknownFile.hash,
            encoding = "unknown",
            is_encoding_fixed = false
        }, suppressPropertyChanged: true);
        TestableBmsFile fixedUnknownFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        fixedUnknownFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(fixedUnknownFile)
        {
            hash = fixedUnknownFile.hash,
            encoding = "unknown",
            is_encoding_fixed = true
        }, suppressPropertyChanged: true);
        TestableBmsFile shiftJisFile = CreateFile("cccccccccccccccccccccccccccccccc");
        shiftJisFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(shiftJisFile)
        {
            hash = shiftJisFile.hash,
            encoding = "shift_jis",
            is_encoding_fixed = false
        }, suppressPropertyChanged: true);
        TestableBmsFile shiftJisQuestionFile = CreateFile("dddddddddddddddddddddddddddddddd");
        shiftJisQuestionFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(shiftJisQuestionFile)
        {
            hash = shiftJisQuestionFile.hash,
            encoding = "shift_jis?",
            is_encoding_fixed = false
        }, suppressPropertyChanged: true);
        TestableBmsFile gb2312File = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        gb2312File.SetMaintenanceInfo(new BMSFileMaintenanceInfo(gb2312File)
        {
            hash = gb2312File.hash,
            encoding = "gb2312",
            is_encoding_fixed = false
        }, suppressPropertyChanged: true);
        TestableBmsFile big5File = CreateFile("ffffffffffffffffffffffffffffffff");
        big5File.SetMaintenanceInfo(new BMSFileMaintenanceInfo(big5File)
        {
            hash = big5File.hash,
            encoding = "big5",
            is_encoding_fixed = false
        }, suppressPropertyChanged: true);

        List<BMSFile> regularList = service.GetGarbledFiles([unknownFile, fixedUnknownFile, shiftJisFile, shiftJisQuestionFile, gb2312File, big5File], isInFixedList: false);
        List<BMSFile> fixedList = service.GetGarbledFiles([unknownFile, fixedUnknownFile, shiftJisFile, shiftJisQuestionFile, gb2312File, big5File], isInFixedList: true);

        CollectionAssert.AreEquivalent(new BMSFile[] { unknownFile, gb2312File, big5File }, regularList);
        CollectionAssert.AreEquivalent(new BMSFile[] { fixedUnknownFile }, fixedList);
    }

    [TestMethod]
    public void RecheckZeroNoteWarnings_SkipsMissingFilesAndClearsWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile.SetNotes(0);
        LR2SongDBExtended.chart_info zeroNoteInfo = CreateChartInfo(zeroNoteFile.hash, notes: 0);
        zeroNoteFile.path = "C:\\missing\\chart.bms";
        zeroNoteFile.SetWarning(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(
            [ChartFileProjection.FromBmsFile(zeroNoteFile)],
            chartInfoResolver: CreateChartInfoResolver(zeroNoteInfo));

        Assert.AreEqual(1, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.SkippedCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.IsFalse(zeroNoteFile.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
    }

    [TestMethod]
    public void RecheckZeroNoteWarnings_UsesResolverChartInfo()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile.SetWarning(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);
        ChartFile chart = ChartFileProjection.FromBmsFile(zeroNoteFile);

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(
            [chart],
            chartInfoResolver: candidate => ReferenceEquals(candidate.GetBmsStorageOwner(), zeroNoteFile)
                ? CreateChartInfo(zeroNoteFile.hash, notes: 1200)
                : null);

        Assert.AreEqual(0, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.IsFalse(zeroNoteFile.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
    }

    [TestMethod]
    public void RecheckZeroNoteWarnings_DoesNotFallbackWhenResolverMisses()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var service = new BmsLibraryMaintenanceService();
        TestableBmsFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile.SetWarning(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch);
        ChartFile chart = ChartFileProjection.FromBmsFile(zeroNoteFile);

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(
            [chart],
            chartInfoResolver: _ => null);

        Assert.AreEqual(0, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.IsFalse(zeroNoteFile.Warnings.Contains(ChartWarningKind.ZeroNoteMismatch));
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
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            file.SetTitle("stale");
            file.SetArtist("stale");
            file.SetGenre("stale");
            var info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "gb2312",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "gb2312");

            Assert.AreEqual(expectedTitle, file.title);
            Assert.AreEqual(expectedSubtitle, file.subtitle);
            Assert.AreEqual(expectedTitle + " " + expectedSubtitle, file.Title);
            Assert.AreEqual(expectedArtist, file.artist);
            Assert.AreEqual(expectedSubartist, file.subartist);
            Assert.AreEqual(expectedArtist + " " + expectedSubartist, file.Artist);
            Assert.AreEqual(expectedGenre, file.genre);
            CollectionAssert.AreEqual(new BMSFile[] { file }, result.SongsToUpsert);
            CollectionAssert.AreEqual(new BMSFileMaintenanceInfo[] { info }, result.MaintenanceInfosToUpsert);
            Assert.IsTrue(file.maintenanceInfo.is_encoding_fixed);
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
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            file.SetTitle(expectedTitle);
            file.SetArtist(expectedArtist);
            file.SetGenre(expectedGenre);
            var info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "unknown",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "gb2312");

            Assert.AreEqual(expectedTitle, file.Title);
            Assert.AreEqual(expectedArtist, file.Artist);
            Assert.AreEqual(expectedGenre, file.genre);
            Assert.AreEqual("gb2312", file.maintenanceInfo.encoding);
            Assert.IsTrue(file.maintenanceInfo.is_encoding_fixed);
            Assert.AreEqual(0, result.SongsToUpsert.Count);
            CollectionAssert.AreEqual(new BMSFileMaintenanceInfo[] { info }, result.MaintenanceInfosToUpsert);
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
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            file.SetTitle(expectedTitle + " " + expectedSubtitle);
            file.SetArtist(expectedArtist + " " + expectedSubartist);
            file.SetGenre(expectedGenre);
            var info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "shift_jis",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "shift_jis");

            Assert.AreEqual(expectedTitle, file.title);
            Assert.AreEqual(expectedSubtitle, file.subtitle);
            Assert.AreEqual(expectedTitle + " " + expectedSubtitle, file.Title);
            Assert.AreEqual(expectedArtist, file.artist);
            Assert.AreEqual(expectedSubartist, file.subartist);
            Assert.AreEqual(expectedArtist + " " + expectedSubartist, file.Artist);
            Assert.AreEqual(expectedGenre, file.genre);
            Assert.IsTrue(file.maintenanceInfo.is_encoding_fixed);
            CollectionAssert.AreEqual(new BMSFile[] { file }, result.SongsToUpsert);
            CollectionAssert.AreEqual(new BMSFileMaintenanceInfo[] { info }, result.MaintenanceInfosToUpsert);
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
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            file.SetTitle(expectedTitle);
            file.SetArtist(expectedArtist);
            file.SetGenre(expectedGenre);
            var info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "unknown",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true);
            List<string> propertyNames = [];
            file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                propertyNames.Add(e.PropertyName);
            };

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "gb2312");

            Assert.AreEqual(0, result.SongsToUpsert.Count);
            CollectionAssert.AreEqual(new[] { info }, result.MaintenanceInfosToUpsert);
            CollectionAssert.Contains(propertyNames, nameof(BMSFile.maintenanceInfo));
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
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            file.SetTitle(expectedTitle);
            file.SetArtist(expectedArtist);
            file.SetGenre(expectedGenre);
            var info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "gb2312",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "gb2312");

            Assert.AreEqual(0, result.SongsToUpsert.Count);
            Assert.AreEqual(0, result.MaintenanceInfosToUpsert.Count);
            Assert.AreEqual("gb2312", file.maintenanceInfo.encoding);
            Assert.IsFalse(file.maintenanceInfo.is_encoding_fixed);
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
    public void UpdateMaintenanceInfo_RaisesHealthPropertyChangedWhenHealthChanges()
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
            var file = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash
            }, suppressPropertyChanged: true);
            List<string> propertyNames = [];
            file.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                propertyNames.Add(e.PropertyName);
            };

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsFile(file)],
                forceUpdate: true,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            CollectionAssert.Contains(propertyNames, nameof(BMSFile.maintenanceInfo));
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
            LR2SongDBExtended.bmson_song song = BmsonSongParser.Parse(bmsonFilePath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsonSong(song)],
                forceUpdate: true,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.SongUpsertCount);
            Assert.AreEqual("Bmson", song.title);
            Assert.AreEqual("Artist", song.artist);
            Assert.AreEqual("utf-8", song.MaintenanceInfo.encoding);
            Assert.IsFalse(song.MaintenanceInfo.is_encoding_fixed);
            Assert.AreEqual(1, song.MaintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, song.MaintenanceInfo.wav_files_existing);
            Assert.AreEqual(true, song.MaintenanceInfo.is_stagefile_defined);
            Assert.AreEqual(false, song.MaintenanceInfo.is_stagefile_existing);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(1, songDb.Table<BMSFileMaintenanceInfo>().Count());
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
            LR2SongDBExtended.bmson_song song = BmsonSongParser.Parse(bmsonFilePath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsonSong(song)],
                forceUpdate: true,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(0, result.BmsResourceTargetCount);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.IsNotNull(song.MaintenanceInfo);
            Assert.AreEqual(song.path, song.MaintenanceInfo.path);
            Assert.AreEqual(song.md5, song.MaintenanceInfo.hash);
            Assert.AreEqual(1, song.MaintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, song.MaintenanceInfo.wav_files_existing);
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
            LR2SongDBExtended.bmson_song song = BmsonSongParser.Parse(bmsonFilePath);
            DateTime timestamp = song.updated_at;
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, timestamp);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsonSong(song)],
                forceUpdate: false,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonReparseFailedCount);
            Assert.AreEqual(1, result.BmsonResourceReferenceReusedCount);
            Assert.AreEqual(1, song.MaintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, song.MaintenanceInfo.wav_files_existing);
            Assert.AreEqual(true, song.MaintenanceInfo.is_stagefile_defined);
            Assert.AreEqual(false, song.MaintenanceInfo.is_stagefile_existing);
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
            LR2SongDBExtended.bmson_song song = BmsonSongParser.Parse(bmsonFilePath);
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, song.updated_at.AddMinutes(1));
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsonSong(song)],
                forceUpdate: false,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsFalse(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.BmsonReparsedCount);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
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
    public void UpdateMaintenanceInfo_BmsonParseFailureDoesNotPersistStaleMaintenanceInfo()
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
            LR2SongDBExtended.bmson_song song = BmsonSongParser.Parse(bmsonFilePath);
            song.MaintenanceInfo = new BMSFileMaintenanceInfo
            {
                path = song.path,
                hash = "ffffffffffffffffffffffffffffffff",
                encoding = "utf-8",
                wav_files_defined = 1,
                wav_files_existing = 1,
                bga_files_defined = 0,
                movie_files_defined = 0,
                is_stagefile_defined = false,
                is_banner_defined = false,
                is_backbmp_defined = false
            };
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, song.updated_at.AddMinutes(1));
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsonSong(song)],
                forceUpdate: false,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsFalse(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.MaintenanceInfoUpsertCount);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(0, songDb.Table<BMSFileMaintenanceInfo>().Count());
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
            LR2SongDBExtended.bmson_song parsed = BmsonSongParser.Parse(bmsonFilePath);
            var loadedLikeRow = new LR2SongDBExtended.bmson_song
            {
                path = parsed.path,
                folder = parsed.folder,
                title = parsed.title,
                md5 = parsed.md5,
                sha256 = parsed.sha256,
                updated_at = parsed.updated_at
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsonSong(loadedLikeRow)],
                forceUpdate: false,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
            Assert.AreEqual(1, loadedLikeRow.MaintenanceInfo.wav_files_defined);
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
            LR2SongDBExtended.bmson_song song = BmsonSongParser.Parse(bmsonFilePath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsonSong(song)],
                forceUpdate: true,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
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
            var bmsFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            LR2SongDBExtended.bmson_song bmsonSong = BmsonSongParser.Parse(bmsonFilePath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsFile(bmsFile), ChartFileProjection.FromBmsonSong(bmsonSong)],
                forceUpdate: true,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsResourceTargetCount);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual("Bmson", bmsonSong.title);
            Assert.AreEqual("Artist", bmsonSong.artist);
            Assert.AreEqual("utf-8", bmsonSong.MaintenanceInfo.encoding);
            Assert.AreEqual(1, bmsonSong.MaintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, bmsonSong.MaintenanceInfo.wav_files_existing);
            Assert.AreEqual(1, bmsFile.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, bmsFile.maintenanceInfo.wav_files_existing);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(2, songDb.Table<BMSFileMaintenanceInfo>().Count());
                Assert.IsFalse(songDb.Table<LR2SongDB.song>().ToList().Any(song => song.hash == bmsonSong.md5));
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
            var bmsFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsFile(bmsFile)],
                forceUpdate: true,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.AreEqual(1, result.HealthDegree);
            Assert.AreEqual(1, result.HealthTargetCount);
            Assert.IsTrue(result.HealthMs >= 0);
            Assert.IsTrue(result.EncodingMs >= 0);
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
            var bmsFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            bmsFile.maintenanceInfo.wav_files_defined = 0;
            bmsFile.maintenanceInfo.bga_files_defined = 0;
            bmsFile.maintenanceInfo.movie_files_defined = 0;
            bmsFile.maintenanceInfo.is_stagefile_defined = false;
            bmsFile.maintenanceInfo.is_backbmp_defined = false;
            bmsFile.maintenanceInfo.is_banner_defined = false;
            bmsFile.maintenanceInfo.encoding = "shift_jis";
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsFile(bmsFile)],
                forceUpdate: false,
                new BmsLibraryDbGateway(songDbPath),
                null,
                progressLogger: logs.Add);

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
            var bmsFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            bmsFile.maintenanceInfo.wav_files_defined = 0;
            bmsFile.maintenanceInfo.bga_files_defined = 0;
            bmsFile.maintenanceInfo.movie_files_defined = 0;
            bmsFile.maintenanceInfo.is_stagefile_defined = false;
            bmsFile.maintenanceInfo.is_backbmp_defined = false;
            bmsFile.maintenanceInfo.is_banner_defined = false;
            bmsFile.maintenanceInfo.encoding = null;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }
            var lookupContext = new ResourceHealthLookupContext(null);

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsFile(bmsFile)],
                forceUpdate: false,
                new BmsLibraryDbGateway(songDbPath),
                null,
                lookupContext);

            Assert.AreEqual(1, result.CheckedFileCount);
            Assert.AreEqual(0, result.MissingInfoTargetCount);
            Assert.AreEqual(1, result.MissingEncodingTargetCount);
            Assert.AreEqual(0, result.HealthTargetCount);
            Assert.AreEqual(0, result.BmsResourceTargetCount);
            Assert.AreEqual(0, result.HealthFileExistsFallbackCount);
            Assert.AreEqual(0, lookupContext.FileExistsFallbackCount);
            Assert.AreEqual("shift_jis", bmsFile.maintenanceInfo.encoding);
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
            var bmsFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            bmsFile.maintenanceInfo.encoding = "shift_jis";
            bmsFile.maintenanceInfo.is_files_warning_ignored = true;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsFile(bmsFile)],
                forceUpdate: false,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(bmsFile.maintenanceInfo.is_files_warning_ignored);
            Assert.AreEqual("shift_jis", bmsFile.maintenanceInfo.encoding);
            Assert.IsTrue(bmsFile.maintenanceInfo.GetWAVHealth() < 100);

            service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsFile(bmsFile)],
                forceUpdate: true,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsFalse(bmsFile.maintenanceInfo.is_files_warning_ignored);
            Assert.AreEqual("shift_jis", bmsFile.maintenanceInfo.encoding);
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
            var legacyFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            var cacheAwareFile = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            BMSFileMaintenanceInfo legacyInfo = BmsLibraryMaintenanceService.BuildBmsResourceHealthMaintenanceInfo(legacyFile, null, forceUpdate: false);

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
            BMSFileMaintenanceInfo cacheInfo = BmsLibraryMaintenanceService.BuildBmsResourceHealthMaintenanceInfo(cacheAwareFile, lookupContext, forceUpdate: false);

            Assert.AreEqual(legacyInfo.wav_files_defined, cacheInfo.wav_files_defined);
            Assert.AreEqual(legacyInfo.wav_files_existing, cacheInfo.wav_files_existing);
            Assert.AreEqual(legacyInfo.bga_files_defined, cacheInfo.bga_files_defined);
            Assert.AreEqual(legacyInfo.bga_files_existing, cacheInfo.bga_files_existing);
            Assert.AreEqual(legacyInfo.movie_files_defined, cacheInfo.movie_files_defined);
            Assert.AreEqual(legacyInfo.movie_files_existing, cacheInfo.movie_files_existing);
            Assert.AreEqual(legacyInfo.is_stagefile_existing, cacheInfo.is_stagefile_existing);
            Assert.AreEqual(legacyInfo.is_banner_existing, cacheInfo.is_banner_existing);
            Assert.AreEqual(legacyInfo.is_backbmp_existing, cacheInfo.is_backbmp_existing);
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
            var file = BMSFile.CreateBMSFileFromFile(bmsFilePath);
            var lookupContext = new ResourceHealthLookupContext(null);
            BMSFileMaintenanceInfo info = BmsLibraryMaintenanceService.BuildBmsResourceHealthMaintenanceInfo(file, lookupContext, forceUpdate: false);

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
            LR2SongDBExtended.bmson_song invalidRow = CreateBmsonSong(invalidBmsonPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            LR2SongDBExtended.bmson_song validRow = BmsonSongParser.Parse(validBmsonPath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            }

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [ChartFileProjection.FromBmsonSong(invalidRow), ChartFileProjection.FromBmsonSong(validRow)],
                forceUpdate: true,
                new BmsLibraryDbGateway(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(2, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(1, result.MaintenanceInfoUpsertCount);
            Assert.AreEqual(0, result.SongUpsertCount);
            Assert.AreEqual("Valid", validRow.title);
            Assert.AreEqual(1, validRow.MaintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, validRow.MaintenanceInfo.wav_files_existing);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                List<BMSFileMaintenanceInfo> rows = [.. songDb.Table<BMSFileMaintenanceInfo>()];
                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(validRow.path, rows[0].path);
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
            TestableBmsFile bms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            bms.path = Path.Combine(tempDirectoryPath, "chart.bms");
            LR2SongDBExtended.bmson_song bmson = CreateBmsonSong(Path.Combine(tempDirectoryPath, "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = bms.path, hash = bms.hash }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(BMSFileMaintenanceInfo.CreateForBmson(bmson.path, bmson.md5), typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = Path.Combine(tempDirectoryPath, "stale.bms"), hash = "cccccccccccccccccccccccccccccccc" }, typeof(LR2SongDBExtended.maintenance));
            }

            int deleted = new BmsLibraryDbGateway(songDbPath).DeleteMaintenanceRows([Path.Combine(tempDirectoryPath, "stale.bms")]);

            Assert.AreEqual(1, deleted);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(2, songDb.Table<BMSFileMaintenanceInfo>().Count());
                Assert.IsTrue(songDb.Table<BMSFileMaintenanceInfo>().Any(info => info.path == bms.path));
                Assert.IsTrue(songDb.Table<BMSFileMaintenanceInfo>().Any(info => info.path == bmson.path));
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
            TestableBmsFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file.path = bmsFilePath;
            var info = new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                encoding = "unknown",
                is_encoding_fixed = false
            };
            file.SetMaintenanceInfo(info, suppressPropertyChanged: true);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], encoding);

            Assert.AreEqual(expectedTitle, file.Title);
            Assert.AreEqual(expectedArtist, file.Artist);
            Assert.AreEqual(encoding, file.maintenanceInfo.encoding);
            Assert.IsTrue(file.maintenanceInfo.is_encoding_fixed);
            CollectionAssert.AreEqual(new BMSFile[] { file }, result.SongsToUpsert);
            CollectionAssert.AreEqual(new BMSFileMaintenanceInfo[] { info }, result.MaintenanceInfosToUpsert);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    private static TestableBmsFile CreateFile(string hash)
    {
        var file = new TestableBmsFile();
        file.SetHash(hash);
        return file;
    }

    private static LR2SongDBExtended.bmson_song CreateBmsonSong(string path, string md5)
    {
        var song = new LR2SongDBExtended.bmson_song
        {
            path = path,
            folder = Path.GetDirectoryName(path),
            title = "Bmson Title",
            artist = "Bmson Artist",
            md5 = md5,
            sha256 = new string('b', 64)
        };
        return song;
    }

    private static LR2SongDBExtended.chart_info CreateChartInfo(string md5, int notes)
    {
        return new LR2SongDBExtended.chart_info
        {
            md5 = md5,
            sha256 = new string('a', 64),
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            notes = notes
        };
    }

    private static Func<ChartFile, LR2SongDBExtended.chart_info> CreateChartInfoResolver(params LR2SongDBExtended.chart_info[] rows)
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

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        FieldInfo fieldInfo = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        fieldInfo.SetValue(target, value);
    }

    private static void InvokeDispatchOwnedChartHashChanges(
        BMSLibrary library,
        IEnumerable<LibraryChartHashChange> hashChanges,
        string reason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("DispatchOwnedChartHashChanges", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [hashChanges, reason, true]);
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

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetNotes(int? value)
        {
            karinotes = value;
        }

        public void SetTitle(string value)
        {
            Title = value;
        }

        public void SetArtist(string value)
        {
            Artist = value;
        }

        public void SetGenre(string value)
        {
            genre = value;
        }
    }
}
