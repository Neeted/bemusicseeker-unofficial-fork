using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BmsLibraryInitializationServiceTests
{
    [TestMethod]
    public void LoadSongTable_FixesRelativePathsWithoutMaintenanceHydration()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath));
            File.WriteAllText(rootedChartPath, "#PLAYER 1");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();

                var song = new TestableBmsFile
                {
                    path = Path.Combine("Songs", "chart.bms"),
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = "Songs\\",
                    title = "Songs",
                    parent = "e2977170",
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = Path.Combine("Songs", "Nested") + "\\",
                    title = "Nested",
                    parent = "stale-parent",
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = rootedChartPath,
                    hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    encoding = "shift_jis"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(rootedChartPath, result.LoadedFiles[0].path);
            Assert.IsNull(result.LoadedFiles[0].maintenanceInfo.encoding);
            Assert.AreEqual(1, result.RelativePathFixedCount);
            Assert.IsTrue(result.DbWriteRequired);
            CollectionAssert.Contains(result.DeletedSongPaths, Path.Combine("Songs", "chart.bms"));
            Assert.IsTrue(result.UpdatedSongs.Any(file => file.path == rootedChartPath));

            using var verify = new LR2SongDBExtended(songDbPath);
            string rootedNestedFolderPath = Path.Combine(lr2RootPath, "Songs", "Nested") + "\\";
            LR2SongDB.folder nested = verify.Table<LR2SongDB.folder>().Single(folder => folder.path == rootedNestedFolderPath);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.Combine(lr2RootPath, "Songs")),
                nested.parent);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void LoadMaintenanceTable_LoadsMaintenanceMapAfterCatalogLoad()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string previousMode = Environment.GetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE", null);
        try
        {
            WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
            {
                string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
                Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath));
                File.WriteAllText(rootedChartPath, "#PLAYER 1");

                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                    {
                        path = rootedChartPath,
                        hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        encoding = "shift_jis",
                        is_encoding_fixed = true,
                        wav_files_existing = 10,
                        wav_files_defined = 12,
                        bga_files_existing = 3,
                        bga_files_defined = 4,
                        movie_files_existing = 1,
                        movie_files_defined = 2,
                        is_stagefile_existing = true,
                        is_stagefile_defined = true,
                        is_banner_existing = false,
                        is_banner_defined = true,
                        is_backbmp_existing = true,
                        is_backbmp_defined = false,
                        is_files_warning_ignored = true,
                        lr2_warning_flags = 11,
                        lr2_resource_max_relative_cp932_bytes = 55,
                        lr2_resource_has_parent_traversal = true
                    }, typeof(LR2SongDBExtended.maintenance));
                }

                var service = new BmsLibraryInitializationService();
                MaintenanceTableHydrationResult result = service.LoadMaintenanceTable(
                    new BmsLibraryDbGateway(songDbPath),
                    new BmsLibraryOptionsSnapshot());

                Assert.AreEqual(1L, result.MaintenanceTableCount);
                Assert.AreEqual("raw_string", result.MaintenanceMaterializeMode);
                Assert.AreEqual(1, result.MaintenanceRawRows);
                Assert.AreEqual(1, result.MaintenanceMap.Count);
                Assert.IsTrue(result.ReadOnly);
                Assert.AreEqual(0L, result.DbLockWaitMs);
                Assert.IsTrue(result.MaintenanceMap.TryGetValue(rootedChartPath, out BMSFileMaintenanceInfo info));
                Assert.AreEqual("shift_jis", info.encoding);
                Assert.IsTrue(info.is_encoding_fixed);
                Assert.AreEqual(10, info.wav_files_existing);
                Assert.AreEqual(12, info.wav_files_defined);
                Assert.AreEqual(3, info.bga_files_existing);
                Assert.AreEqual(4, info.bga_files_defined);
                Assert.AreEqual(1, info.movie_files_existing);
                Assert.AreEqual(2, info.movie_files_defined);
                Assert.AreEqual(true, info.is_stagefile_existing);
                Assert.AreEqual(true, info.is_stagefile_defined);
                Assert.AreEqual(false, info.is_banner_existing);
                Assert.AreEqual(true, info.is_banner_defined);
                Assert.AreEqual(true, info.is_backbmp_existing);
                Assert.AreEqual(false, info.is_backbmp_defined);
                Assert.IsTrue(info.is_files_warning_ignored);
                Assert.AreEqual(11, info.lr2_warning_flags);
                Assert.AreEqual(55, info.lr2_resource_max_relative_cp932_bytes);
                Assert.AreEqual(true, info.lr2_resource_has_parent_traversal);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE", previousMode);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void LoadMaintenanceTable_CanUseSqliteNetFallback()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string previousMode = Environment.GetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE", "sqlite_net");
        try
        {
            WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
            {
                string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
                Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath));
                File.WriteAllText(rootedChartPath, "#PLAYER 1");

                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                    {
                        path = rootedChartPath,
                        hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        encoding = "utf-8",
                        is_encoding_fixed = false,
                        is_stagefile_existing = false,
                        is_stagefile_defined = true
                    }, typeof(LR2SongDBExtended.maintenance));
                }

                var service = new BmsLibraryInitializationService();
                MaintenanceTableHydrationResult result = service.LoadMaintenanceTable(
                    new BmsLibraryDbGateway(songDbPath),
                    new BmsLibraryOptionsSnapshot());

                Assert.AreEqual(1L, result.MaintenanceTableCount);
                Assert.AreEqual("sqlite_net", result.MaintenanceMaterializeMode);
                Assert.AreEqual(0, result.MaintenanceRawRows);
                Assert.IsTrue(result.MaintenanceMap.TryGetValue(rootedChartPath, out BMSFileMaintenanceInfo info));
                Assert.AreEqual("utf-8", info.encoding);
                Assert.IsFalse(info.is_encoding_fixed);
                Assert.AreEqual(false, info.is_stagefile_existing);
                Assert.AreEqual(true, info.is_stagefile_defined);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE", previousMode);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void LoadSongTable_RawCatalogLoaderPreservesSongColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        string previousMode = Environment.GetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE", null);
        try
        {
            WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
            {
                string chartPath = Path.Combine(lr2RootPath, "Songs", "full-columns.bms");
                Directory.CreateDirectory(Path.GetDirectoryName(chartPath));
                File.WriteAllText(chartPath, "#PLAYER 1");

                var expectedCrc = new TestableBmsFile
                {
                    path = chartPath
                };
                Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(expectedCrc);

                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDB.folder>();
                    songDb.Execute(
                        "INSERT INTO song (hash, title, subtitle, artist, subartist, genre, tag, path, type, folder, stagefile, banner, backbmp, parent, level, difficulty, maxbpm, minbpm, mode, judge, longnote, bga, random, date, favorite, txt, karinotes, adddate, exlevel) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                        "Title",
                        "Subtitle",
                        "Artist",
                        "SubArtist",
                        "Genre",
                        "Tag",
                        chartPath,
                        1,
                        expectedCrc.folder,
                        "stage.png",
                        "banner.png",
                        "back.png",
                        expectedCrc.parent,
                        12,
                        4,
                        180,
                        90,
                        7,
                        2,
                        1,
                        1,
                        0,
                        12345,
                        1,
                        0,
                        678,
                        23456,
                        9);
                }

                var service = new BmsLibraryInitializationService();
                SongTableLoadResult result = service.LoadSongTable(
                    new BmsLibraryDbGateway(songDbPath),
                    new BmsLibraryOptionsSnapshot(),
                    null,
                    new TestFileMutationService(),
                    null,
                    ex => ex.Message);

                Assert.AreEqual("raw_string", result.SongMaterializeMode);
                Assert.AreEqual(1, result.SongRawRows);
                BMSFile loaded = result.LoadedFiles.Single();
                Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", loaded.hash);
                Assert.AreEqual("Title", loaded.title);
                Assert.AreEqual("Subtitle", loaded.subtitle);
                Assert.AreEqual("Artist", loaded.artist);
                Assert.AreEqual("SubArtist", loaded.subartist);
                Assert.AreEqual("Genre", loaded.genre);
                Assert.AreEqual("Tag", loaded.tag);
                Assert.AreEqual(chartPath, loaded.path);
                Assert.AreEqual(1, loaded.type);
                Assert.AreEqual(expectedCrc.folder, loaded.folder);
                Assert.AreEqual("stage.png", loaded.stagefile);
                Assert.AreEqual("banner.png", loaded.banner);
                Assert.AreEqual("back.png", loaded.backbmp);
                Assert.AreEqual(expectedCrc.parent, loaded.parent);
                Assert.AreEqual(12, loaded.level);
                Assert.AreEqual(4, loaded.difficulty);
                Assert.AreEqual(180, loaded.maxbpm);
                Assert.AreEqual(90, loaded.minbpm);
                Assert.AreEqual(7, loaded.mode);
                Assert.AreEqual(2, loaded.judge);
                Assert.AreEqual(1, loaded.longnote);
                Assert.AreEqual(1, loaded.bga);
                Assert.AreEqual(0, loaded.random);
                Assert.AreEqual(12345, loaded.date);
                Assert.AreEqual(1, loaded.favorite);
                Assert.AreEqual(0, loaded.txt);
                Assert.AreEqual(678, loaded.karinotes);
                Assert.AreEqual(23456, loaded.adddate);
                Assert.AreEqual(9, loaded.exlevel);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE", previousMode);
        }
    }

    [TestMethod]
    public void LoadSongTable_DoesNotRegisterMaintenanceEncodingPropertyChangedHandler()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath));
            File.WriteAllText(rootedChartPath, "#PLAYER 1");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();

                var song = new TestableBmsFile
                {
                    path = rootedChartPath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = rootedChartPath,
                    hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    encoding = "shift_jis"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);
            List<string> propertyNames = [];
            result.LoadedFiles[0].PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                propertyNames.Add(e.PropertyName);
            };

            result.LoadedFiles[0].maintenanceInfo.encoding = "utf-8";

            CollectionAssert.DoesNotContain(propertyNames, nameof(BMSFile.maintenanceInfo));
        });
    }

    [TestMethod]
    public void LoadSongTable_AppliesChartDigestMapToLoadedFiles()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath));
            File.WriteAllText(rootedChartPath, "#PLAYER 1");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);

                var song = new TestableBmsFile
                {
                    path = rootedChartPath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    sha256 = new string('b', 64)
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(new string('b', 64), result.LoadedFiles[0].sha256);
            Assert.AreEqual(1, result.ChartDigestMap.Count);
        });
    }

    [TestMethod]
    public void LoadSongTable_PreservesShiftJisUnsupportedExistingSongAndWarns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Songs😀");
            Directory.CreateDirectory(chartDirectoryPath);
            string chartPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, CreateValidBmsText("Emoji Path"), Encoding.ASCII);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                var song = new TestableBmsFile
                {
                    path = chartPath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(0, result.DeletedSongPaths.Count);
            Assert.AreEqual(chartPath, result.LoadedFiles[0].path);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.LoadedFiles[0].folder));
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.LoadedFiles[0].parent));
            Assert.IsTrue(result.LoadedFiles[0].Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", chartPath)));
        });
    }

    [TestMethod]
    public void LoadSongTable_StandalonePreservesShiftJisUnsupportedExistingSongAndWarns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryStandaloneSongDb(delegate (string rootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(rootPath, "Songs😀");
            Directory.CreateDirectory(chartDirectoryPath);
            string chartPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(chartPath, CreateValidBmsText("Standalone Emoji Path"), Encoding.ASCII);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                var song = new TestableBmsFile
                {
                    path = chartPath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("abababababababababababababababab");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false },
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(0, result.DeletedSongPaths.Count);
            Assert.AreEqual(chartPath, result.LoadedFiles[0].path);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.LoadedFiles[0].folder));
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.LoadedFiles[0].parent));
            Assert.IsTrue(result.LoadedFiles[0].Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", chartPath)));
        });
    }

    [TestMethod]
    public void LoadSongTable_DoesNotPartiallyFixRelativeShiftJisUnsupportedPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string relativePath = Path.Combine("Songs😀", "chart.bms");
            string chartPath = Path.Combine(lr2RootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath));
            File.WriteAllText(chartPath, CreateValidBmsText("Relative Emoji Path"), Encoding.ASCII);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                var song = new TestableBmsFile
                {
                    path = relativePath,
                    folder = "folder",
                    parent = "parent"
                };
                song.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(0, result.DeletedSongPaths.Count);
            Assert.AreEqual(relativePath, result.LoadedFiles[0].path);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.LoadedFiles[0].parent));
            Assert.IsTrue(result.LoadedFiles[0].Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", relativePath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesPrefetchedScanAndClearsStaleInstallDestination()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string newDirectoryPath = Path.Combine(lr2RootPath, "New");
            string staleDirectoryPath = Path.Combine(lr2RootPath, "Stale");
            Directory.CreateDirectory(keepDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            File.WriteAllText(Path.Combine(keepDirectoryPath, "keep.bms"), "#PLAYER 1\r\n#TITLE Keep\r\n");
            File.WriteAllText(Path.Combine(newDirectoryPath, "added.bms"), "#PLAYER 1\r\n#TITLE Added\r\n");

            var keepFile = new TestableBmsFile
            {
                path = Path.Combine(keepDirectoryPath, "keep.bms")
            };
            keepFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            keepFile.date = ToUnixSeconds(File.GetLastWriteTimeUtc(keepFile.path));
            var deletedFile = new TestableBmsFile
            {
                path = Path.Combine(lr2RootPath, "Deleted", "deleted.bms")
            };
            deletedFile.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            int executeScanCount = 0;
            int catalogProjectionAppliedCount = 0;
            List<ChartFile> cleanupCharts = [ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsFile(keepFile, includeWarningSnapshot: false),
                staleDirectoryPath,
                string.Empty,
                string.Empty,
                [])];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [keepFile, deletedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    NativeBridgeUsed = true,
                    NativeBridgeMs = 234L,
                    NativeBridgeReason = "everything_bridge_fixed_scan",
                    ManagedDecodeMs = 12L,
                    ManagedMaterializeMs = 7L,
                    BridgeRawBufferBytes = 4096UL,
                    Result = CreateScanResult(
                        [keepFile.path, Path.Combine(newDirectoryPath, "added.bms")],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { keepDirectoryPath, Array.Empty<string>() },
                            { newDirectoryPath, Array.Empty<string>() }
                        })
                },
                123L,
                delegate
                {
                    Interlocked.Increment(ref executeScanCount);
                    return null;
                },
                null,
                catalogProjectionApplied: _ => catalogProjectionAppliedCount++);
            ProjectCatalogState(result, [keepFile, deletedFile], [], cleanupCharts);

            Assert.AreEqual(0, executeScanCount);
            Assert.IsTrue(result.PrefetchedScanUsed);
            Assert.IsTrue(result.HasDbDiff);
            CollectionAssert.Contains(result.DeletedPaths, deletedFile.path);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(2, result.NextFiles.Count);
            Assert.AreEqual(234L, result.NativeBridgeMs);
            Assert.AreEqual("everything_bridge_fixed_scan", result.NativeBridgeReason);
            Assert.AreEqual(12L, result.ManagedDecodeMs);
            Assert.AreEqual(7L, result.ManagedMaterializeMs);
            Assert.AreEqual(4096UL, result.BridgeRawBufferBytes);
            Assert.AreEqual(1, catalogProjectionAppliedCount);
            LibraryInstallDestinationChange installDestinationChange = result.MutationDelta.UpdatedInstallDestinations.Single();
            Assert.AreSame(keepFile, installDestinationChange.Chart.GetBmsStorageOwner());
            Assert.IsNull(installDestinationChange.NewInstallDestination);
            Assert.IsTrue(installDestinationChange.ClearInstallDestinationState);
            CollectionAssert.Contains(result.NextDirectoryResourceLookupCache.Keys.ToList(), keepDirectoryPath);
            CollectionAssert.Contains(result.NextDirectoryResourceLookupCache.Keys.ToList(), newDirectoryPath);

            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            List<BMSFile> dbFiles = [.. songDb.Table<BMSFile>()];
            Assert.AreEqual(1, dbFiles.Count);
            Assert.AreEqual(Path.Combine(newDirectoryPath, "added.bms"), dbFiles[0].path);
            List<LR2SongDBExtended.chart_digest_map> digestRows = [.. songDb.Table<LR2SongDBExtended.chart_digest_map>()];
            Assert.AreEqual(1, digestRows.Count);
            Assert.AreEqual(dbFiles[0].hash, digestRows[0].md5);
            Assert.AreEqual(result.AddedFiles[0].sha256, digestRows[0].sha256);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ReportsEverythingFallbackMetadata()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            const string fallbackReason = "empty_results_with_roots";
            var dialogService = new RecordingDialogService();
            List<string> logs = [];
            var service = new BmsLibraryInitializationService();

            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    FallbackUsed = true,
                    FallbackReason = fallbackReason,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                dialogService,
                logInstallPerformance: logs.Add,
                logEverythingScan: logs.Add,
                currentBmsonSongs: []);

            Assert.IsTrue(result.ScanFallbackUsed);
            Assert.AreEqual(fallbackReason, result.ScanFallbackReason);
            Assert.AreEqual(0, dialogService.Calls.Count);
            Assert.IsTrue(logs.Any(message => message.Contains("fallback_used=true") || message.Contains("fallback=true")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_IncompleteScanSkipsDiffAndKeepsExistingDb()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string existingDirectoryPath = Path.Combine(lr2RootPath, "Existing");
            string existingPath = Path.Combine(existingDirectoryPath, "chart.bms");
            string addedDirectoryPath = Path.Combine(lr2RootPath, "Added");
            string addedPath = Path.Combine(addedDirectoryPath, "added.bms");
            Directory.CreateDirectory(existingDirectoryPath);
            Directory.CreateDirectory(addedDirectoryPath);
            File.WriteAllText(existingPath, "#PLAYER 1\r\n#TITLE Existing\r\n");
            File.WriteAllText(addedPath, "#PLAYER 1\r\n#TITLE Added\r\n");

            var existingFile = new TestableBmsFile
            {
                path = existingPath
            };
            existingFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            existingFile.date = ToUnixSeconds(File.GetLastWriteTimeUtc(existingPath));
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            bool scanCompleted = false;
            bool fileDiffStarted = false;
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = false,
                    IsComplete = false,
                    ErrorReason = "directory_enumeration_failed:" + existingDirectoryPath,
                    IncompleteReason = "directory_enumeration_failed:" + existingDirectoryPath,
                    Result = CreateScanResult(
                        [addedPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { addedDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => throw new InvalidOperationException("executeScan should not run"),
                null,
                currentBmsonSongs: [],
                scanCompleted: () => scanCompleted = true,
                fileDiffStarted: () => fileDiffStarted = true);

            Assert.IsTrue(scanCompleted);
            Assert.IsFalse(fileDiffStarted);
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual(0, result.DeletedPaths.Count);
            Assert.AreEqual(0, result.DeletedBmsonPaths.Count);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual("directory_enumeration_failed:" + existingDirectoryPath, result.ScanFallbackReason);

            using var songDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", existingPath));
            Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", addedPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_EmptyFallbackScanWithExistingDbSkipsDiffAndKeepsExistingDb()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string existingDirectoryPath = Path.Combine(lr2RootPath, "Existing");
            string existingPath = Path.Combine(existingDirectoryPath, "chart.bms");
            Directory.CreateDirectory(existingDirectoryPath);
            File.WriteAllText(existingPath, "#PLAYER 1\r\n#TITLE Existing\r\n");

            var existingFile = new TestableBmsFile
            {
                path = existingPath
            };
            existingFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            existingFile.date = ToUnixSeconds(File.GetLastWriteTimeUtc(existingPath));
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            bool scanCompleted = false;
            bool fileDiffStarted = false;
            List<string> logs = [];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    ScanSource = ChartScanSource.Fallback,
                    Success = true,
                    IsComplete = true,
                    FallbackUsed = true,
                    FallbackReason = "everything_not_running",
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => throw new InvalidOperationException("executeScan should not run"),
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                scanCompleted: () => scanCompleted = true,
                fileDiffStarted: () => fileDiffStarted = true);

            Assert.IsTrue(scanCompleted);
            Assert.IsFalse(fileDiffStarted);
            Assert.IsTrue(result.EmptyScanWithExistingDbSkipped);
            StringAssert.Contains(result.EmptyScanWithExistingDbSkipReason, "empty_scan_with_existing_db");
            StringAssert.Contains(result.EmptyScanWithExistingDbSkipReason, "scanSource=fallback");
            StringAssert.Contains(result.EmptyScanWithExistingDbSkipReason, "fallbackReason=everything_not_running");
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual(0, result.DeletedPaths.Count);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.IsTrue(logs.Any(message => message.Contains("reason=empty_scan_with_existing_db")));

            using var songDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", existingPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_EmptyEverythingScanWithExistingDbSkipsDiffAndKeepsExistingDb()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string existingDirectoryPath = Path.Combine(lr2RootPath, "Existing");
            string existingPath = Path.Combine(existingDirectoryPath, "chart.bms");
            Directory.CreateDirectory(existingDirectoryPath);
            File.WriteAllText(existingPath, "#PLAYER 1\r\n#TITLE Existing\r\n");

            var existingFile = new TestableBmsFile
            {
                path = existingPath
            };
            existingFile.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            existingFile.date = ToUnixSeconds(File.GetLastWriteTimeUtc(existingPath));
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            bool fileDiffStarted = false;
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    ScanSource = ChartScanSource.Everything,
                    Success = true,
                    IsComplete = true,
                    NativeBridgeUsed = true,
                    NativeBridgeReason = EverythingNative.FixedScanNativeBridgeReason,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => throw new InvalidOperationException("executeScan should not run"),
                null,
                currentBmsonSongs: [],
                fileDiffStarted: () => fileDiffStarted = true);

            Assert.IsFalse(fileDiffStarted);
            Assert.IsTrue(result.EmptyScanWithExistingDbSkipped);
            StringAssert.Contains(result.EmptyScanWithExistingDbSkipReason, "scanSource=everything");
            Assert.IsFalse(result.EmptyScanWithExistingDbSkipReason.Contains("fallbackReason="));
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual(0, result.DeletedPaths.Count);

            using var songDb = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", existingPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_EmptyFallbackScanWithoutExistingDbAllowsEmptyResult()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            bool scanCompleted = false;
            bool fileDiffStarted = false;
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    ScanSource = ChartScanSource.Fallback,
                    Success = true,
                    IsComplete = true,
                    FallbackUsed = true,
                    FallbackReason = "empty_results_with_roots",
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => throw new InvalidOperationException("executeScan should not run"),
                null,
                currentBmsonSongs: [],
                scanCompleted: () => scanCompleted = true,
                fileDiffStarted: () => fileDiffStarted = true);

            Assert.IsTrue(scanCompleted);
            Assert.IsTrue(fileDiffStarted);
            Assert.IsFalse(result.EmptyScanWithExistingDbSkipped);
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual(0, result.DeletedPaths.Count);
            Assert.AreEqual(0, result.AddedFiles.Count);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_IncompleteBmsonScanSkipsDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonDirectoryPath = Path.Combine(lr2RootPath, "Bmson");
            string bmsonPath = Path.Combine(bmsonDirectoryPath, "chart.bmson");
            Directory.CreateDirectory(bmsonDirectoryPath);
            File.WriteAllText(bmsonPath, "{\"version\":\"1.0.0\"}");
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            bool scanCompleted = false;
            bool fileDiffStarted = false;
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => throw new InvalidOperationException("executeScan should not run"),
                null,
                currentBmsonSongs: [],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = false,
                    IsComplete = false,
                    ErrorReason = "bmson_directory_enumeration_failed:" + bmsonDirectoryPath,
                    IncompleteReason = "bmson_directory_enumeration_failed:" + bmsonDirectoryPath,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { bmsonDirectoryPath, Array.Empty<string>() }
                        })
                },
                scanCompleted: () => scanCompleted = true,
                fileDiffStarted: () => fileDiffStarted = true);

            Assert.IsTrue(scanCompleted);
            Assert.IsFalse(fileDiffStarted);
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual(0, result.DeletedPaths.Count);
            Assert.AreEqual(0, result.DeletedBmsonPaths.Count);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual("bmson_directory_enumeration_failed:" + bmsonDirectoryPath, result.ScanFallbackReason);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ReadFailureIsAggregatedWithoutInitializationDialog()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "ReadFailure");
            Directory.CreateDirectory(chartDirectoryPath);
            string missingPath = Path.Combine(chartDirectoryPath, "missing.bms");

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var dialogService = new RecordingDialogService();
            List<string> scanLogs = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [missingPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService,
                logEverythingScan: scanLogs.Add,
                currentBmsonSongs: []);

            Assert.AreEqual(0, dialogService.Calls.Count);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(1, result.FileScanFailures.Count);
            Assert.AreEqual(missingPath, result.FileScanFailures[0].Path);
            Assert.AreEqual("bms", result.FileScanFailures[0].ChartKind);
            Assert.AreEqual("read", result.FileScanFailures[0].Stage);
            Assert.IsTrue(scanLogs.Any(message => message.Contains("bms_scan_failed") && message.Contains("stage=read")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_LongPathBmsIsRegisteredAndLr2CompatibilityWarns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = CreateLongPathDirectory(lr2RootPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "kabukin_______________________________________________________________________________________________________________________________________________________________________________________________________________________________.bms");
            Directory.CreateDirectory(LongPathFileSystem.ToExtendedPath(chartDirectoryPath));
            File.WriteAllText(LongPathFileSystem.ToExtendedPath(bmsPath), CreateValidBmsText("Long Path"), Encoding.ASCII);
            Assert.IsTrue(bmsPath.Length > 260);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var dialogService = new RecordingDialogService();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService,
                currentBmsonSongs: []);

            Assert.AreEqual(0, dialogService.Calls.Count);
            Assert.AreEqual(0, result.FileScanFailures.Count);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(bmsPath, result.AddedFiles[0].path);
            Assert.IsTrue(result.AddedFiles[0].Warnings.Contains(ChartWarningKind.Lr2PathTooLong));
            Assert.IsFalse(string.IsNullOrWhiteSpace(BMSFile.DetectEncodingOfBMSFile(bmsPath)));
            BMSFile.ReloadBMSFileWithEncoding(result.AddedFiles[0], "shift_jis");
            Assert.AreEqual("Long Path", result.AddedFiles[0].title);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_LongPathBmsonIsRegistered()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = CreateLongPathDirectory(lr2RootPath);
            string bmsonPath = Path.Combine(chartDirectoryPath, "long_bmson____________________________________________________________________________________________________________________________________________________________________________________________________________________________.bmson");
            Directory.CreateDirectory(LongPathFileSystem.ToExtendedPath(chartDirectoryPath));
            File.WriteAllText(LongPathFileSystem.ToExtendedPath(bmsonPath), CreateBmsonJson("Long Bmson", "", "", "Artist", "Genre", 5, "beat-7k"), Encoding.UTF8);
            Assert.IsTrue(bmsonPath.Length > 260);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            }

            var dialogService = new RecordingDialogService();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService,
                currentBmsonSongs: []);

            Assert.AreEqual(0, dialogService.Calls.Count);
            Assert.AreEqual(0, result.FileScanFailures.Count);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.AreEqual(bmsonPath, result.AddedBmsonSongs[0].path);
            Assert.AreEqual("Long Bmson", result.AddedBmsonSongs[0].title);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song WHERE path = ?;", bmsonPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_BmsonReadFailureIsAggregatedWithoutInitializationDialog()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "BmsonReadFailure");
            Directory.CreateDirectory(chartDirectoryPath);
            string missingPath = Path.Combine(chartDirectoryPath, "missing.bmson");

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            }

            var dialogService = new RecordingDialogService();
            List<string> scanLogs = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [missingPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService,
                logEverythingScan: scanLogs.Add,
                currentBmsonSongs: []);

            Assert.AreEqual(0, dialogService.Calls.Count);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.FileScanFailures.Count);
            Assert.AreEqual(missingPath, result.FileScanFailures[0].Path);
            Assert.AreEqual("bmson", result.FileScanFailures[0].ChartKind);
            Assert.AreEqual("read", result.FileScanFailures[0].Stage);
            Assert.IsTrue(scanLogs.Any(message => message.Contains("bmson_scan_failed") && message.Contains("stage=read")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ClearsStaleBmsonInstallDestination()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepBmsonPath = Path.Combine(lr2RootPath, "Keep", "keep.bmson");
            string staleDirectoryPath = Path.Combine(lr2RootPath, "Stale");
            Directory.CreateDirectory(Path.GetDirectoryName(keepBmsonPath));
            File.WriteAllText(keepBmsonPath, CreateBmsonJson("Keep", "", "", "Artist", "Genre", 5, "beat-5k"));

            LR2SongDBExtended.bmson_song keepSong = BmsonSongParser.Parse(keepBmsonPath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepSong, typeof(LR2SongDBExtended.bmson_song));
            }

            List<ChartFile> cleanupCharts = [ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(keepSong, includeWarningSnapshot: false),
                staleDirectoryPath,
                string.Empty,
                string.Empty,
                [])];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [keepSong],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepBmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(keepBmsonPath), Array.Empty<string>() }
                        })
                });
            ProjectCatalogState(result, [], [keepSong], cleanupCharts);

            LibraryInstallDestinationChange installDestinationChange = result.MutationDelta.UpdatedInstallDestinations.Single();
            Assert.AreSame(keepSong, installDestinationChange.Chart.GetBmsonStorageOwner());
            Assert.IsNull(installDestinationChange.NewInstallDestination);
            Assert.IsTrue(installDestinationChange.ClearInstallDestinationState);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CatalogProjectionCallbackRunsBeforeBreakdownLog()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
            }

            List<string> events = [];
            var service = new BmsLibraryInitializationService();
            service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                logInstallPerformance: message => events.Add(message),
                catalogProjectionApplied: projection =>
                {
                    projection.ApplyMs = 17;
                    projection.InstlDstCleanupMs = 23;
                    events.Add("catalog_projection");
                });

            int projectionIndex = events.IndexOf("catalog_projection");
            int breakdownIndex = events.FindIndex(message => message.StartsWith("song_tbl_file_check_breakdown", StringComparison.Ordinal));
            Assert.IsTrue(projectionIndex >= 0);
            Assert.IsTrue(breakdownIndex > projectionIndex);
            StringAssert.Contains(events[breakdownIndex], "apply_ms=17");
            StringAssert.Contains(events[breakdownIndex], "instl_dst_cleanup_ms=23");
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ReportsCombinedBmsAndBmsonParseProgress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Added");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            string bmsonPath = Path.Combine(chartDirectoryPath, "added.bmson");
            File.WriteAllText(bmsPath, CreateValidBmsText("Added"), Encoding.ASCII);
            File.WriteAllText(Path.Combine(chartDirectoryPath, "sound.wav"), string.Empty, Encoding.ASCII);
            File.WriteAllText(bmsonPath, CreateBmsonJson("Added", "", "", "Artist", "Genre", 7, "beat-7k"));

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            }

            int scanCompletedCount = 0;
            int fileDiffStartedCount = 0;
            object progressLock = new();
            List<(int Total, int Processed, string Path)> progress = [];
            List<string> logs = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath, bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, ["sound.wav"] }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: message => logs.Add(message),
                currentBmsonSongs: [],
                scanCompleted: () => scanCompletedCount++,
                fileDiffStarted: () => fileDiffStartedCount++,
                reportParseProgress: (total, processed, path) =>
                {
                    lock (progressLock)
                    {
                        progress.Add((total, processed, path));
                    }
                });

            Assert.AreEqual(1, scanCompletedCount);
            Assert.AreEqual(1, fileDiffStartedCount);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(1, result.FileDiffParserDegree);
            Assert.AreEqual(result.NewFileParseMs, result.BmsParseMs);
            Assert.IsTrue(result.BmsonParseMs >= 0);
            long expectedReadBytesEstimate = new FileInfo(bmsPath).Length + new FileInfo(bmsonPath).Length;
            Assert.AreEqual(expectedReadBytesEstimate, result.ParseReadBytesEstimate);
            Assert.AreEqual(1, result.DbCommitChunks);
            Assert.AreEqual(10000, result.DbCommitChunkSize);
            Assert.IsTrue(result.DbCommitMaxChunkMs >= 0);
            Assert.IsTrue(result.DbCommitApplyMs >= 0);
            Assert.IsTrue(result.DbCommitBmsUpsertMs >= 0);
            Assert.IsTrue(result.DbCommitMaintenanceUpsertMs >= 0);
            Assert.IsTrue(result.DbCommitChartInfoMs >= 0);
            Assert.IsTrue(result.DbCommitSqliteCommitMs >= 0);
            Assert.AreEqual(1, result.DbCommitBmsChangedCount);
            Assert.IsTrue(result.FileDiffReadMs >= 0);
            Assert.IsTrue(result.FileDiffParseMs >= 0);
            Assert.IsTrue(result.SnapshotQueueHighWatermark > 0);
            Assert.AreEqual(2048, result.InlineChartInfoBatchSize);
            Assert.AreEqual(ChartFileReadPipelinePolicy.ResolveReaderDegree(Environment.ProcessorCount, 2), result.FileDiffReaderDegree);
            Assert.AreEqual(ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(result.FileDiffParserDegree, result.FileDiffReaderDegree), result.ReadQueueCapacity);
            Assert.AreEqual(FileScanParseCommitOwner.ResolveFileDiffParsedQueueCapacity(result.FileDiffParserDegree), result.ParsedQueueCapacity);
            Assert.AreEqual(FileScanParseCommitOwner.ResolveFileDiffPostParseQueueCapacity(result.FileDiffPostParseWorkerDegree), result.PostParseQueueCapacity);
            Assert.AreEqual(1, result.CommitQueueCapacity);
            Assert.AreEqual(2, result.CommitWriterQueueCapacity);
            Assert.IsTrue(result.CommitStreamingEnabled);
            Assert.AreEqual("none", result.CommitStreamingBarrierReason);
            Assert.AreEqual(1, result.FileDiffPostParseWorkerDegree);
            Assert.IsTrue(result.PostParseOutputWaitMs >= 0);
            Assert.IsTrue(result.CommitWriterQueueWaitMs >= 0);
            Assert.AreEqual(2, result.PostParseWorkItemCount);
            Assert.AreEqual(1, result.InlineMaintenanceDegree);
            Assert.IsTrue(result.PostParseWallMs >= 0);
            Assert.IsTrue(result.InlineChartInfoWallMs >= 0);
            Assert.IsTrue(result.InlineMaintenanceWallMs >= 0);
            Assert.IsTrue(result.InlineMaintenanceResourceIndexHitCount > 0);
            Assert.IsTrue(progress.Any(item => item.Total == 2 && item.Processed == 0));
            Assert.IsTrue(progress.Any(item => item.Total == 2 && item.Processed == 2));
            Assert.IsTrue(progress.All(item => item.Total == 2));
            Assert.IsTrue(logs.Any(message => message.Contains("bms_added_target_count=1")
                && message.Contains("bmson_upsert_target_count=1")
                && message.Contains("file_diff_reader_degree=" + result.FileDiffReaderDegree)
                && message.Contains("file_diff_parser_degree=1")
                && message.Contains("file_diff_post_parse_worker_degree=1")
                && message.Contains("read_queue_capacity=" + result.ReadQueueCapacity)
                && message.Contains("parsed_queue_capacity=" + result.ParsedQueueCapacity)
                && message.Contains("post_parse_queue_capacity=" + result.PostParseQueueCapacity)
                && message.Contains("commit_queue_capacity=1")
                && message.Contains("commit_writer_queue_capacity=2")
                && message.Contains("commit_streaming_enabled=true")
                && message.Contains("commit_streaming_barrier=none")
                && message.Contains("post_parse_output_wait_ms=")
                && message.Contains("commit_writer_queue_wait_ms=")
                && message.Contains("post_parse_work_item_count=2")
                && message.Contains("inline_chart_info_target_count=2")
                && message.Contains("inline_chart_info_batch_size=2048")
                && message.Contains("inline_maintenance_degree=1")
                && message.Contains("inline_maintenance_resource_index_hit=")
                && message.Contains("inline_maintenance_resource_set_cache_hit=")
                && message.Contains("inline_maintenance_resource_set_cache_entries=")
                && message.Contains("parse_read_bytes_estimate=" + expectedReadBytesEstimate)
                && message.Contains("db_commit_apply_ms=")
                && message.Contains("db_commit_bms_upsert_ms=")
                && message.Contains("db_commit_maintenance_upsert_ms=")
                && message.Contains("db_commit_sqlite_commit_ms=")
                && message.Contains("db_commit_chunks=1")
                && message.Contains("db_commit_chunk_size=10000")));
            Assert.IsTrue(logs.Any(message => message.Contains("song_tbl_file_check db_commit_chunk_done chunk=1")
                && message.Contains("applyMs=")
                && message.Contains("bmsUpsertMs=")
                && message.Contains("bmsChanged=1")
                && message.Contains("maintenanceUpsertMs=")
                && message.Contains("chartInfoMs=")
                && message.Contains("sqliteCommitMs=")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CommitsChunksThroughPostParseWriter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "ManyAdded");
            Directory.CreateDirectory(chartDirectoryPath);
            List<string> paths = [];
            for (int i = 0; i < 120; i++)
            {
                string path = Path.Combine(chartDirectoryPath, "added-" + i.ToString("D4") + ".bms");
                File.WriteAllText(path, CreateValidBmsText("Added " + i.ToString("D4")), Encoding.ASCII);
                paths.Add(path);
            }

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            List<string> events = [];
            object eventLock = new();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, inlineChartInfoBatchSizeOverride: 32, fileDiffCommitChunkSizeOverride: 50);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        paths,
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: delegate (string message)
                {
                    lock (eventLock)
                    {
                        if (message.Contains("song_tbl_file_check db_commit_chunk_done"))
                        {
                            events.Add(message);
                        }
                    }
                },
                reportParseProgress: delegate (int total, int processed, string path)
                {
                    lock (eventLock)
                    {
                        events.Add("progress " + processed + "/" + total);
                    }
                });

            Assert.AreEqual(120, result.AddedFiles.Count);
            Assert.AreEqual(120, result.DbCommitBmsChangedCount);
            Assert.AreEqual((int)Math.Ceiling(paths.Count / 50.0), result.DbCommitChunks);
            Assert.AreEqual(50, result.DbCommitChunkSize);
            Assert.AreEqual(32, result.InlineChartInfoBatchSize);
            Assert.AreEqual(120, result.PostParseWorkItemCount);
            Assert.IsTrue(result.PostParseWallMs >= 0);
            Assert.IsTrue(result.CommitQueueWaitMs >= 0);
            int firstCommitIndex = events.FindIndex(item => item.Contains("db_commit_chunk_done chunk=1"));
            int finalProgressIndex = events.FindIndex(item => item == "progress 120/120");
            int firstCommitChunkProgressIndex = events.FindIndex(item => item == "progress 50/120");
            List<string> progressEvents = [.. events.Where(item => item.StartsWith("progress ", StringComparison.Ordinal))];
            Assert.IsTrue(firstCommitIndex >= 0, "first commit chunk log was not recorded.");
            Assert.AreEqual(121, progressEvents.Count, "file diff progress should be reported for the initial state and each prepared chart.");
            Assert.IsTrue(progressEvents.Contains("progress 1/120"), "first prepared chart progress was not recorded.");
            Assert.IsTrue(progressEvents.Contains("progress 119/120"), "near-final per-chart progress was not recorded.");
            Assert.IsTrue(finalProgressIndex >= 0, "final parse progress was not recorded.");
            Assert.IsTrue(firstCommitChunkProgressIndex >= 0, "first commit chunk prepared progress was not recorded.");
            Assert.IsTrue(firstCommitChunkProgressIndex < firstCommitIndex, "file diff progress should report DB-ready rows before their commit completion.");
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(120L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(120L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(120L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance;"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AggregatesInlineRowsAcrossPostParseWorkers()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "ParallelPostParse");
            Directory.CreateDirectory(chartDirectoryPath);
            const int count = 300;
            List<string> paths = [];
            for (int i = 0; i < count; i++)
            {
                string path = Path.Combine(chartDirectoryPath, "added-" + i.ToString("D4") + ".bms");
                File.WriteAllText(path, CreateValidBmsText("Parallel PostParse " + i.ToString("D4")), Encoding.ASCII);
                paths.Add(path);
            }

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(paths[0]);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertChartInfos([CreateMinimalChartInfoRow(firstSnapshot.Sha256, firstSnapshot.Md5)]);
            List<string> logs = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 2, inlineChartInfoBatchSizeOverride: 512, fileDiffCommitChunkSizeOverride: 50);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                gateway,
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        paths,
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add);

            Assert.AreEqual(count, result.AddedFiles.Count);
            Assert.AreEqual(2, result.FileDiffParserDegree);
            Assert.AreEqual(2, result.FileDiffPostParseWorkerDegree);
            Assert.AreEqual(count, result.PostParseWorkItemCount);
            Assert.AreEqual((int)Math.Ceiling(count / 50.0), result.DbCommitChunks, "unexpected DB commit chunk count: " + result.DbCommitChunks);
            Assert.AreEqual(50, result.DbCommitChunkSize);
            Assert.AreEqual(count, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoCurrentSkippedCount);
            Assert.AreEqual(count, result.InlineMaintenanceSuccessCount);
            Assert.AreEqual(1, result.InlineMaintenanceResourceSetCacheEntries);
            Assert.IsTrue(result.PostParseOutputWaitMs >= 0);
            Assert.IsTrue(logs.Any(message => message.Contains("file_diff_chart_info_snapshot")
                && message.Contains("status=loaded")));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(count, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(count, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(count, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM maintenance;"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_StreamingWriterFailureDoesNotBlockPipeline()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "WriterFailure");
            Directory.CreateDirectory(chartDirectoryPath);
            List<string> paths = [];
            for (int i = 0; i < 160; i++)
            {
                string path = Path.Combine(chartDirectoryPath, "added-" + i.ToString("D4") + ".bms");
                File.WriteAllText(path, CreateValidBmsText("Writer Failure " + i.ToString("D4")), Encoding.ASCII);
                paths.Add(path);
            }

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 2, inlineChartInfoBatchSizeOverride: 8, fileDiffCommitChunkSizeOverride: 8);
            var injectedException = new InvalidOperationException("injected streaming writer failure");
            Task<SongTableFileCheckResult> task = Task.Run(() => service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        paths,
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: delegate (string message)
                {
                    if (message.Contains("song_tbl_file_check db_commit_chunk_start chunk=1"))
                    {
                        throw injectedException;
                    }
                }));

            bool completed;
            try
            {
                completed = task.Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException)
            {
                completed = true;
            }
            Assert.IsTrue(completed, "file diff pipeline did not complete after streaming writer failure.");
            Assert.IsTrue(task.IsFaulted, "streaming writer failure should fault the pipeline.");
            StringAssert.Contains(task.Exception.ToString(), injectedException.Message);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AddsBmsWithLr2FolderAndParentHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Lr2Crc");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("CRC Added"), Encoding.ASCII);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);
            ProjectCatalogState(result, []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            BMSFile added = result.AddedFiles[0];
            Assert.IsFalse(string.IsNullOrWhiteSpace(added.folder));
            Assert.IsFalse(string.IsNullOrWhiteSpace(added.parent));
            Assert.IsTrue(added.parent.Length <= 8);
            Assert.IsFalse(added.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            Assert.AreEqual(0, result.NextFiles.Count(file => string.IsNullOrWhiteSpace(file.parent)));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(added.folder, verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(added.parent, verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_PreservesShiftJisUnsupportedPathWithoutParentAndWarns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Emoji😀");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Emoji Added"), Encoding.ASCII);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);
            ProjectCatalogState(result, []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            BMSFile added = result.AddedFiles[0];
            Assert.IsTrue(string.IsNullOrWhiteSpace(added.folder));
            Assert.IsTrue(string.IsNullOrWhiteSpace(added.parent));
            Assert.IsTrue(added.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            StringAssert.Contains(added.Warnings.BuildTooltipText(), "Shift_JIS");
            Assert.AreEqual(1, result.NextFiles.Count(file => string.IsNullOrWhiteSpace(file.parent)));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath)));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersAfterFullScanWhenEnabled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Sync"), Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Synced Folder", Encoding.GetEncoding(932));

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            scanResult.FolderInfoFilePaths.Add(folderInfoPath);

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.IsFalse(result.Lr2NormalFolderSyncFailed);
            Assert.AreEqual(2, result.Lr2NormalFolderGeneratedCount);
            Assert.AreEqual(2, result.Lr2NormalFolderUpsertedCount);
            Assert.AreEqual(0, result.Lr2NormalFolderDeletedCount);
            Assert.AreEqual(2, result.Lr2NormalFolderMetadataRequestedDirectoryCount);
            Assert.AreEqual(2, result.Lr2NormalFolderMetadataResolvedDirectoryCount);
            Assert.AreEqual(1, result.Lr2NormalFolderInfoCandidateCount);
            Assert.AreEqual(1, result.Lr2NormalFolderInfoAppliedCount);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder root = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(lr2RootPath));
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual(1, root.type);
            Assert.AreEqual(1, pack.type);
            Assert.AreEqual("Synced Folder", pack.title);
            Assert.IsTrue(pack.date.HasValue);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersForChangedDirectoryMtimeWithoutSongDbDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Updated Folder", Encoding.GetEncoding(932));
            DateTime previousDirectoryTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime currentDirectoryTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, previousDirectoryTimestamp);
            File.SetLastWriteTimeUtc(folderInfoPath, currentDirectoryTimestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, previousDirectoryTimestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, currentDirectoryTimestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
            };

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Old Folder",
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
                }, typeof(LR2SongDB.folder));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            scanResult.FolderInfoFilePaths.Add(folderInfoPath);
            scanResult.FolderInfoFileEntriesByPath[folderInfoPath] =
                new RootFileEnumerationEntry(folderInfoPath, File.GetLastWriteTimeUtc(folderInfoPath), new FileInfo(folderInfoPath).Length);

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(1, result.Lr2NormalFolderInfoCandidateCount);
            Assert.AreEqual(1, result.Lr2NormalFolderInfoAppliedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Updated Folder", pack.title);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersForUnchangedDirectoryMtimeWithoutSongDbDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Updated Folder", Encoding.GetEncoding(932));
            DateTime timestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime folderInfoTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, timestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile.SetTextGroupFlagForTest(0);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Old Folder",
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            RootFileEnumerationEntry folderInfoEntry =
                new(folderInfoPath, File.GetLastWriteTimeUtc(folderInfoPath), new FileInfo(folderInfoPath).Length);
            scanResult.FolderInfoFilePaths.Add(folderInfoPath);
            scanResult.FolderInfoFileEntriesByPath[folderInfoPath] = folderInfoEntry;

            var logs = new List<string>();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync skipped reason=no_db_diff")));
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Old Folder", pack.title);
        });
    }

    [TestMethod]
    public void LoadNormalFolderMtimeSnapshot_LoadsOnlyNormalFolderRowsUnderRoots()
    {
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            string appManagedLr2FolderPath = Path.Combine(lr2RootPath, "LR2files", "CustomFolder", "Table.lr2folder");
            string outsideFolderPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Outside_" + Guid.NewGuid().ToString("N"));
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Pack",
                    type = 1,
                    date = 2
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = appManagedLr2FolderPath,
                    title = "Table",
                    type = 2,
                    date = 3
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(outsideFolderPath),
                    title = "Outside",
                    type = 1,
                    date = 4
                }, typeof(LR2SongDB.folder));
            }

            var logs = new List<string>();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            Lr2NormalFolderMtimeSnapshot snapshot = service.LoadNormalFolderMtimeSnapshot(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [lr2RootPath],
                logs.Add);

            Assert.IsNotNull(snapshot);
            Assert.IsTrue(snapshot.ExistingRowsByPath.ContainsKey(ToFolderPath(lr2RootPath)));
            Assert.IsTrue(snapshot.ExistingRowsByPath.ContainsKey(ToFolderPath(packDirectoryPath)));
            Assert.IsFalse(snapshot.ExistingRowsByPath.ContainsKey(appManagedLr2FolderPath));
            Assert.IsFalse(snapshot.ExistingRowsByPath.ContainsKey(ToFolderPath(outsideFolderPath)));
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_mtime_snapshot_prefetch")
                && message.Contains("existingRows=2")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesPrefetchedNormalFolderMtimeSnapshot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Updated Folder", Encoding.GetEncoding(932));
            DateTime timestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime folderInfoTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, timestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile.SetTextGroupFlagForTest(0);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Old Folder",
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            RootFileEnumerationEntry folderInfoEntry =
                new(folderInfoPath, File.GetLastWriteTimeUtc(folderInfoPath), new FileInfo(folderInfoPath).Length);
            scanResult.FolderInfoFilePaths.Add(folderInfoPath);
            scanResult.FolderInfoFileEntriesByPath[folderInfoPath] = folderInfoEntry;

            var logs = new List<string>();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            Lr2NormalFolderMtimeSnapshot snapshot = service.LoadNormalFolderMtimeSnapshot(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [lr2RootPath]);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath],
                normalFolderMtimeSnapshot: snapshot);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_mtime_diff")
                && message.Contains("prefetched=true")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersWhenOnlyFolderInfoMetadataDiffers()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            File.WriteAllText(folderInfoPath, "#TITLE Updated Folder", Encoding.GetEncoding(932));
            DateTime timestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime folderInfoTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, timestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile.SetTextGroupFlagForTest(0);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Old Folder",
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            scanResult.FolderInfoFilePaths.Add(folderInfoPath);
            scanResult.FolderInfoFileEntriesByPath[folderInfoPath] =
                new RootFileEnumerationEntry(folderInfoPath, folderInfoTimestamp, new FileInfo(folderInfoPath).Length);

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Old Folder", pack.title);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersForChangedDirectoryMtimeAndDeletedFolderInfoWithoutSongDbDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            string folderInfoPath = Path.Combine(packDirectoryPath, "folderinfo.txt");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            DateTime previousDirectoryTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime currentDirectoryTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, previousDirectoryTimestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, previousDirectoryTimestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, currentDirectoryTimestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
            };
            currentFile.SetTextGroupFlagForTest(0);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    title = Path.GetFileName(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    title = "Old FolderInfo",
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(previousDirectoryTimestamp)
                }, typeof(LR2SongDB.folder));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(0, result.Lr2NormalFolderInfoCandidateCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual("Pack", pack.title);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesDirectoryMtimeFromScanSurfaceForLr2NormalFolders()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Surface Date"), Encoding.ASCII);
            DateTime rootSurfaceTimestamp = new(2026, 6, 7, 1, 0, 0, DateTimeKind.Utc);
            DateTime packSurfaceTimestamp = new(2026, 6, 7, 2, 0, 0, DateTimeKind.Utc);
            DateTime liveTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { packDirectoryPath, Array.Empty<string>() }
                });
            scanResult.DirectoryEntriesByPath[Lr2FolderPath.NormalizeDirectoryPath(lr2RootPath)] =
                new RootFileEnumerationEntry(lr2RootPath, rootSurfaceTimestamp);
            scanResult.DirectoryEntriesByPath[Lr2FolderPath.NormalizeDirectoryPath(packDirectoryPath)] =
                new RootFileEnumerationEntry(packDirectoryPath, packSurfaceTimestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, liveTimestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, liveTimestamp);

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder root = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(lr2RootPath));
            LR2SongDB.folder pack = verify.Table<LR2SongDB.folder>().Single(row => row.path == ToFolderPath(packDirectoryPath));
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(rootSurfaceTimestamp), root.date);
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(packSurfaceTimestamp), pack.date);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_PopulatesLr2FolderSurfaceFromProducerDiscoveryRoots()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsRootPath = Path.Combine(lr2RootPath, "Songs");
            string normalOutputBasePath = Path.Combine(lr2RootPath, "CustomFolderOutput");
            string rootOutputBasePath = Path.Combine(lr2RootPath, "RootCustomFolderOutput");
            string builtinCustomFolderPath = Path.Combine(lr2RootPath, "LR2files", "CustomFolder");
            Directory.CreateDirectory(bmsRootPath);
            Directory.CreateDirectory(normalOutputBasePath);
            Directory.CreateDirectory(rootOutputBasePath);
            Directory.CreateDirectory(builtinCustomFolderPath);

            string rootLr2FolderPath = Path.Combine(bmsRootPath, "external.lr2folder");
            string outputLr2FolderPath = Path.Combine(normalOutputBasePath, "0000.lr2folder");
            string rootOutputLr2FolderPath = Path.Combine(rootOutputBasePath, "root.lr2folder");
            string builtinFavoritePath = Path.Combine(builtinCustomFolderPath, "favorite.lr2folder");
            string builtinNewsongPath = Path.Combine(builtinCustomFolderPath, "newsong.lr2folder");
            File.WriteAllText(rootLr2FolderPath, "#TITLE External", Encoding.GetEncoding(932));
            File.WriteAllText(outputLr2FolderPath, "#TITLE Output", Encoding.GetEncoding(932));
            File.WriteAllText(rootOutputLr2FolderPath, "#TITLE Root Output", Encoding.GetEncoding(932));
            File.WriteAllText(builtinFavoritePath, "#TITLE Favorite", Encoding.GetEncoding(932));
            File.WriteAllText(builtinNewsongPath, "#TITLE Newsong", Encoding.GetEncoding(932));

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
            }

            var events = new List<string>();
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                    LR2RootPath = lr2RootPath,
                    LR2CustomFolderOutputBaseDir = normalOutputBasePath,
                    LR2CustomFolderOutputBaseDirRootType = rootOutputBasePath
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                logEverythingScan: message =>
                {
                    if (message.StartsWith("lr2folder_scan", StringComparison.OrdinalIgnoreCase))
                    {
                        events.Add(message);
                    }
                },
                currentBmsonSongs: [],
                scanCompleted: () => events.Add("scanCompleted"),
                fileDiffStarted: () => events.Add("fileDiffStarted"),
                lr2NormalFolderSyncRootDirectories: [bmsRootPath],
                lr2FolderDiscoveryRootDirectories: [bmsRootPath],
                lr2BuiltinCustomFolderSettings: new Lr2BuiltinCustomFolderSettings(0x2, 24, includeNewSongFolder: false));

            Assert.IsTrue(result.Lr2ScanSurfaceAvailable);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderDiscoveryDirectories.ToList(), bmsRootPath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderDiscoveryDirectories.ToList(), normalOutputBasePath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderDiscoveryDirectories.ToList(), rootOutputBasePath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderDiscoveryDirectories.ToList(), builtinCustomFolderPath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderFilePaths.ToList(), rootLr2FolderPath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderFilePaths.ToList(), outputLr2FolderPath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderFilePaths.ToList(), rootOutputLr2FolderPath);
            CollectionAssert.Contains(result.Lr2ScanLr2FolderFilePaths.ToList(), builtinFavoritePath);
            CollectionAssert.DoesNotContain(result.Lr2ScanLr2FolderFilePaths.ToList(), builtinNewsongPath);
            Assert.IsTrue(result.Lr2ScanLr2FolderFileEntries.TryGetValue(builtinFavoritePath, out RootFileEnumerationEntry builtinEntry));
            Assert.IsTrue(builtinEntry.LastWriteTimeUtc.HasValue);
            Assert.IsTrue(result.Lr2ScanLr2FolderFileDiscoveryComplete);
            int lr2FolderScanIndex = events.FindIndex(message => message.StartsWith("lr2folder_scan success", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(lr2FolderScanIndex >= 0);
            Assert.IsTrue(lr2FolderScanIndex < events.IndexOf("scanCompleted"));
            Assert.IsTrue(events.IndexOf("scanCompleted") < events.IndexOf("fileDiffStarted"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_SyncsLr2NormalFoldersOnlyForAffectedDeletedBmsScope()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string removedDirectoryPath = Path.Combine(lr2RootPath, "Removed");
            string staleOtherDirectoryPath = Path.Combine(lr2RootPath, "OtherStale");
            Directory.CreateDirectory(keepDirectoryPath);
            Directory.CreateDirectory(removedDirectoryPath);
            string keepPath = Path.Combine(keepDirectoryPath, "keep.bms");
            string removedPath = Path.Combine(removedDirectoryPath, "removed.bms");
            File.WriteAllText(keepPath, CreateValidBmsText("Keep"), Encoding.ASCII);
            File.WriteAllText(removedPath, CreateValidBmsText("Removed"), Encoding.ASCII);
            var keepTimestamp = new DateTime(2026, 6, 6, 1, 0, 0, DateTimeKind.Utc);
            var removedTimestamp = new DateTime(2026, 6, 6, 2, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(keepPath, keepTimestamp);
            File.SetLastWriteTimeUtc(removedPath, removedTimestamp);
            var keepFile = new TestableBmsFile
            {
                path = keepPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(keepTimestamp)
            };
            keepFile.SetTextGroupFlagForTest(0);
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(keepPath).hash);
            var removedFile = new TestableBmsFile
            {
                path = removedPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(removedTimestamp)
            };
            removedFile.SetHash(BMSFile.CreateBMSFileFromFile(removedPath).hash);
            string removedFolderPath = ToFolderPath(removedDirectoryPath);
            string staleOtherFolderPath = ToFolderPath(staleOtherDirectoryPath);
            var logs = new List<string>();

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = removedFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = staleOtherFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [keepFile, removedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { keepDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(1, result.Lr2NormalFolderDeletedCount);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync done")
                && message.Contains("paths=0")
                && message.Contains("pruneScopes=1")));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleOtherFolderPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DeletedNestedBmsDoesNotExpandLr2NormalFolderScopeToRootChild()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepDirectoryPath = Path.Combine(lr2RootPath, "Big", "Keep");
            string removedDirectoryPath = Path.Combine(lr2RootPath, "Big", "Removed");
            string staleSiblingDirectoryPath = Path.Combine(lr2RootPath, "Big", "SiblingStale");
            Directory.CreateDirectory(keepDirectoryPath);
            Directory.CreateDirectory(removedDirectoryPath);
            string keepPath = Path.Combine(keepDirectoryPath, "keep.bms");
            string removedPath = Path.Combine(removedDirectoryPath, "removed.bms");
            File.WriteAllText(keepPath, CreateValidBmsText("Keep Nested"), Encoding.ASCII);
            File.WriteAllText(removedPath, CreateValidBmsText("Removed Nested"), Encoding.ASCII);
            var keepFile = new TestableBmsFile
            {
                path = keepPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(keepPath))
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(keepPath).hash);
            var removedFile = new TestableBmsFile
            {
                path = removedPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(removedPath))
            };
            removedFile.SetHash(BMSFile.CreateBMSFileFromFile(removedPath).hash);
            string bigFolderPath = ToFolderPath(Path.Combine(lr2RootPath, "Big"));
            string removedFolderPath = ToFolderPath(removedDirectoryPath);
            string staleSiblingFolderPath = ToFolderPath(staleSiblingDirectoryPath);
            var logs = new List<string>();

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(lr2RootPath))
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = bigFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = removedFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = staleSiblingFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [keepFile, removedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { keepDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(1, result.Lr2NormalFolderDeletedCount);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync done")
                && message.Contains("paths=0")
                && message.Contains("pruneScopes=1")));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", bigFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleSiblingFolderPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DeletedLastNestedBmsPrunesEmptyAncestorFolder()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "EmptyPack");
            string removedDirectoryPath = Path.Combine(packDirectoryPath, "Removed");
            Directory.CreateDirectory(removedDirectoryPath);
            string removedPath = Path.Combine(removedDirectoryPath, "removed.bms");
            File.WriteAllText(removedPath, CreateValidBmsText("Removed Last Nested"), Encoding.ASCII);
            var removedFile = new TestableBmsFile
            {
                path = removedPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(removedPath))
            };
            removedFile.SetHash(BMSFile.CreateBMSFileFromFile(removedPath).hash);
            string packFolderPath = ToFolderPath(packDirectoryPath);
            string removedFolderPath = ToFolderPath(removedDirectoryPath);

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(lr2RootPath))
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = packFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = removedFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [removedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase),
                        [lr2RootPath])
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsTrue(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(2, result.Lr2NormalFolderDeletedCount);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", removedFolderPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", packFolderPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", ToFolderPath(lr2RootPath)));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DeletedRootLevelBmsDoesNotExpandLr2NormalFolderScopeToRoot()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string staleDirectoryPath = Path.Combine(lr2RootPath, "StaleSibling");
            Directory.CreateDirectory(keepDirectoryPath);
            string keepPath = Path.Combine(keepDirectoryPath, "keep.bms");
            string removedPath = Path.Combine(lr2RootPath, "removed-root.bms");
            File.WriteAllText(keepPath, CreateValidBmsText("Keep Root Sibling"), Encoding.ASCII);
            File.WriteAllText(removedPath, CreateValidBmsText("Removed Root Level"), Encoding.ASCII);
            var keepFile = new TestableBmsFile
            {
                path = keepPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(keepPath))
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(keepPath).hash);
            var removedFile = new TestableBmsFile
            {
                path = removedPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(removedPath))
            };
            removedFile.SetHash(BMSFile.CreateBMSFileFromFile(removedPath).hash);
            string staleFolderPath = ToFolderPath(staleDirectoryPath);
            var logs = new List<string>();

            File.Delete(removedPath);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(removedFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(lr2RootPath))
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(keepDirectoryPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(keepDirectoryPath))
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = staleFolderPath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [keepFile, removedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { keepDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.HasDbDiff);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.AreEqual(0, result.Lr2NormalFolderDeletedCount);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync skipped reason=no_normal_folder_mtime_diff")));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", staleFolderPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncStaleLr2NormalFoldersOutsideLr2Mode()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Sync Disabled"), Encoding.ASCII);
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { packDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersOutsideLr2Mode()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Sync Disabled"), Encoding.ASCII);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { packDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersWhenScanIsIncomplete()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Folder Sync Incomplete"), Encoding.ASCII);
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));
            var logs = new List<string>();

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = false,
                    IsComplete = false,
                    IncompleteReason = "directory_enumeration_failed:" + lr2RootPath,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { packDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.IsFalse(result.HasDbDiff);
            Assert.AreEqual("directory_enumeration_failed:" + lr2RootPath, result.ScanFallbackReason);
            Assert.IsFalse(logs.Any(message => message.Contains("lr2_normal_folder_sync")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersWhenThereIsNoDbDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string packDirectoryPath = Path.Combine(lr2RootPath, "Pack");
            Directory.CreateDirectory(packDirectoryPath);
            string bmsPath = Path.Combine(packDirectoryPath, "current.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Current"), Encoding.ASCII);
            DateTime timestamp = new DateTime(2026, 6, 6, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            Directory.SetLastWriteTimeUtc(packDirectoryPath, timestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile.SetTextGroupFlagForTest(0);
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));
            var logs = new List<string>();

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(packDirectoryPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { packDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsFalse(result.HasDbDiff);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync skipped reason=no_db_diff")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(3L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotSyncLr2NormalFoldersForBmsTextOnlyDiff()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "TextOnly");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "current.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Text Only"), Encoding.ASCII);
            DateTime timestamp = new DateTime(2026, 6, 6, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            Directory.SetLastWriteTimeUtc(chartDirectoryPath, timestamp);
            Directory.SetLastWriteTimeUtc(lr2RootPath, timestamp);
            BMSFile parsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
            };
            currentFile.SetHash(parsed.hash);
            currentFile.SetTextGroupFlagForTest(0);
            string stalePath = ToFolderPath(Path.Combine(lr2RootPath, "Stale"));
            var logs = new List<string>();

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDB.folder>();
                songDbConnection.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(lr2RootPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = ToFolderPath(chartDirectoryPath),
                    type = 1,
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
                }, typeof(LR2SongDB.folder));
                songDbConnection.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 1
                }, typeof(LR2SongDB.folder));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, ["readme.txt"] }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add,
                currentBmsonSongs: [],
                lr2NormalFolderSyncRootDirectories: [lr2RootPath]);

            Assert.IsTrue(result.HasDbDiff);
            Assert.AreEqual(1, result.BmsTextOnlyUpdateCount);
            Assert.IsFalse(result.Lr2NormalFolderSyncExecuted);
            Assert.IsTrue(logs.Any(message => message.Contains("lr2_normal_folder_sync skipped reason=no_normal_folder_mtime_diff")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        });
    }

    [TestMethod]
    public void UpsertSongs_FillsLr2FolderAndParentForInstalledBmsRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Installed");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Installed"), Encoding.ASCII);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var file = new TestableBmsFile
            {
                path = bmsPath,
                folder = null,
                parent = null
            };
            file.SetHash("cccccccccccccccccccccccccccccccc");

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([file]);

            Assert.IsFalse(string.IsNullOrWhiteSpace(file.folder));
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.parent));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(file.folder, verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(file.parent, verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void UpsertSongs_PreservesShiftJisUnsupportedInstalledBmsRowsWithWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Installed😀");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Installed Emoji"), Encoding.ASCII);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var file = new TestableBmsFile
            {
                path = bmsPath,
                folder = null,
                parent = null
            };
            file.SetHash("dddddddddddddddddddddddddddddddd");

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([file]);

            Assert.IsTrue(string.IsNullOrWhiteSpace(file.parent));
            Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", bmsPath)));
        });
    }

    [TestMethod]
    public void UpsertSongs_UpdatesGeneratedColumnsWithoutOverwritingUserSongColumns()
    {
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "MergeSong");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "chart.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Updated Title"), Encoding.ASCII);
            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                var existing = new TestableBmsFile
                {
                    path = bmsPath,
                    date = 100,
                    adddate = 12345,
                    tag = "user-tag"
                };
                existing.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                existing.SetFavorite(7);
                existing.SetTextGroupFlagForTest(1);
                songDbConnection.InsertOrReplace(existing, typeof(LR2SongDB.song));
            }

            BMSFile updated = BMSFile.CreateBMSFileFromFile(bmsPath);
            updated.date = 200;

            new BmsLibraryDbGateway(songDbPath).UpsertSongs([updated]);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(bmsPath, row.path);
            Assert.AreEqual("Updated Title", row.title);
            Assert.AreEqual(updated.hash, row.hash);
            Assert.AreEqual(200, row.date);
            Assert.AreEqual(7, row.favorite);
            Assert.AreEqual(1, row.txt);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("user-tag", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AddsBmsAndPersistsInlineChartInfo()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineInfo");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Inline Added"), Encoding.ASCII);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(0, result.InlineChartInfoParseFailedCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", result.AddedFiles[0].sha256, result.AddedFiles[0].hash));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
            LR2SongDBExtended.chart_info chartInfoRow = verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ? AND md5 = ?;", result.AddedFiles[0].sha256, result.AddedFiles[0].hash).Single();
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, chartInfoRow.parser_version);
            LR2SongDB.song songRow = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(120, songRow.maxbpm);
            Assert.AreEqual(120, songRow.minbpm);
            Assert.AreEqual(5, songRow.mode);
            Assert.AreEqual(1, songRow.karinotes);
            Assert.AreEqual(0, songRow.longnote);
            Assert.AreEqual(0, songRow.random);
            Assert.AreEqual(2, songRow.judge);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AddsBmsPersistsInlineMaintenanceAndClearsResourceRefs()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineMaintenance");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            string wavPath = Path.Combine(chartDirectoryPath, "sound.wav");
            File.WriteAllText(bmsPath, CreateValidBmsText("Inline Maintenance"), Encoding.ASCII);
            File.WriteAllBytes(wavPath, [1, 2, 3]);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { "sound.wav" } }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            BMSFile added = result.AddedFiles.Single();
            Assert.AreEqual(1, result.InlineMaintenanceTargetCount);
            Assert.AreEqual(1, result.InlineMaintenanceSuccessCount);
            Assert.AreEqual(0, result.InlineMaintenanceFailedCount);
            Assert.AreEqual(1, result.InlineMaintenanceBmsCount);
            Assert.IsTrue(result.InlineBmsMaintenanceWallMs >= 0);
            Assert.IsNull(added.WAVfiles);
            Assert.IsNull(added.BGAfiles);
            Assert.AreEqual(1, added.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(1, added.maintenanceInfo.wav_files_existing);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(result.InlineMaintenanceSuccessCount, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM maintenance;"));
            LR2SongDBExtended.maintenance maintenance = verify.Query<LR2SongDBExtended.maintenance>("SELECT * FROM maintenance WHERE path = ?;", bmsPath).Single();
            Assert.AreEqual(added.hash, maintenance.hash);
            Assert.AreEqual(1, maintenance.wav_files_defined);
            Assert.AreEqual(1, maintenance.wav_files_existing);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ReloadsDetectedEncodingDuringInlineMaintenance()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineEncoding");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            string title = "\uacaf";
            string subtitle = "[Pattern]";
            string artist = "KoreanArtist";
            string subartist = "obj:Tester";
            File.WriteAllText(
                bmsPath,
                "#PLAYER 1\r\n#TITLE " + title + "\r\n#SUBTITLE " + subtitle + "\r\n#ARTIST " + artist + "\r\n#SUBARTIST " + subartist + "\r\n#WAV01 sound.wav\r\n#00111:01\r\n",
                Encoding.GetEncoding("ks_c_5601-1987"));

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { "sound.wav" } }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            BMSFile added = result.AddedFiles.Single();
            Assert.AreEqual(title, added.title);
            Assert.AreEqual(subtitle, added.subtitle);
            Assert.AreEqual(title + " " + subtitle, added.Title);
            Assert.AreEqual(artist, added.artist);
            Assert.AreEqual(subartist, added.subartist);
            Assert.AreEqual(artist + " " + subartist, added.Artist);
            Assert.AreEqual("ks_c_5601-1987", added.maintenanceInfo.encoding);
            Assert.IsTrue(added.maintenanceInfo.is_encoding_fixed);
            Assert.AreEqual(1, result.InlineEncodingReloadCount);
            Assert.IsTrue(result.InlineEncodingReloadWallMs >= 0);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(title, verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(subtitle, verify.ExecuteScalar<string>("SELECT subtitle FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(artist, verify.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(subartist, verify.ExecuteScalar<string>("SELECT subartist FROM song WHERE path = ?;", bmsPath));
            LR2SongDBExtended.maintenance maintenance = verify.Query<LR2SongDBExtended.maintenance>("SELECT * FROM maintenance WHERE path = ?;", bmsPath).Single();
            Assert.AreEqual("ks_c_5601-1987", maintenance.encoding);
            Assert.AreEqual(1, maintenance.wav_files_defined);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_TracksInlineEncodingOutcomesForMixedBmsBatch()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineEncodingMixed");
            Directory.CreateDirectory(chartDirectoryPath);
            string asciiPath = Path.Combine(chartDirectoryPath, "ascii.bms");
            string koreanPath = Path.Combine(chartDirectoryPath, "korean.bms");
            const string koreanTitle = "\uacaf";
            File.WriteAllText(asciiPath, CreateValidBmsText("ASCII"), Encoding.ASCII);
            File.WriteAllText(
                koreanPath,
                "#PLAYER 1\r\n#TITLE " + koreanTitle + "\r\n#ARTIST KoreanArtist\r\n#BPM 120\r\n#WAV01 sound.wav\r\n#00111:01\r\n",
                Encoding.GetEncoding("ks_c_5601-1987"));
            File.WriteAllBytes(Path.Combine(chartDirectoryPath, "sound.wav"), [1, 2, 3]);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [asciiPath, koreanPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { "sound.wav" } }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(2, result.AddedFiles.Count);
            Assert.AreEqual(2, result.InlineEncodingDetectCount);
            Assert.AreEqual(1, result.InlineEncodingFastAsciiCount);
            Assert.AreEqual(1, result.InlineEncodingShiftJisCount);
            Assert.AreEqual(1, result.InlineEncodingKoreanCount);
            Assert.AreEqual(0, result.InlineEncodingShiftJisQuestionCount);
            Assert.AreEqual(0, result.InlineEncodingKoreanQuestionCount);
            Assert.AreEqual(0, result.InlineEncodingUtf8Count);
            Assert.AreEqual(0, result.InlineEncodingUnknownCount);
            Assert.AreEqual(0, result.InlineEncodingOtherCount);
            Assert.AreEqual(1, result.InlineEncodingReloadCount);
            Assert.IsTrue(result.InlineEncodingMaxItemMs >= 0);

            BMSFile ascii = result.AddedFiles.Single(file => file.path == asciiPath);
            BMSFile korean = result.AddedFiles.Single(file => file.path == koreanPath);
            Assert.AreEqual("shift_jis", ascii.maintenanceInfo.encoding);
            Assert.IsFalse(ascii.maintenanceInfo.is_encoding_fixed);
            Assert.AreEqual("ks_c_5601-1987", korean.maintenanceInfo.encoding);
            Assert.IsTrue(korean.maintenanceInfo.is_encoding_fixed);
            Assert.AreEqual(koreanTitle, korean.Title);
            Assert.IsNull(ascii.WAVfiles);
            Assert.IsNull(korean.WAVfiles);
            Assert.AreEqual(1, ascii.maintenanceInfo.wav_files_existing);
            Assert.AreEqual(1, korean.maintenanceInfo.wav_files_existing);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(koreanTitle, verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", koreanPath));
            Assert.AreEqual("ks_c_5601-1987", verify.ExecuteScalar<string>("SELECT encoding FROM maintenance WHERE path = ?;", koreanPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CachesFolderParentHashesForChartsInSameDirectory()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "FolderParentCache");
            Directory.CreateDirectory(chartDirectoryPath);
            string firstPath = Path.Combine(chartDirectoryPath, "first.bms");
            string secondPath = Path.Combine(chartDirectoryPath, "second.bms");
            string wavPath = Path.Combine(chartDirectoryPath, "sound.wav");
            File.WriteAllText(firstPath, CreateValidBmsText("First"), Encoding.ASCII);
            File.WriteAllText(secondPath, CreateValidBmsText("Second"), Encoding.ASCII);
            File.WriteAllBytes(wavPath, [1, 2, 3]);

            var expected = BMSFile.CreateBMSFileFromFile(firstPath);
            Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(expected);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 2);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [firstPath, secondPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { "sound.wav" } }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(2, result.AddedFiles.Count);
            Assert.IsTrue(result.InlineHealthWallMs >= 0);
            Assert.IsTrue(result.InlineEncodingWallMs >= 0);
            Assert.IsTrue(result.InlineBmsMaintenanceWallMs >= 0);
            foreach (BMSFile added in result.AddedFiles)
            {
                Assert.AreEqual(expected.folder, added.folder);
                Assert.AreEqual(expected.parent, added.parent);
                Assert.IsFalse(added.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
                Assert.AreEqual(1, added.maintenanceInfo.wav_files_defined);
                Assert.AreEqual(1, added.maintenanceInfo.wav_files_existing);
            }
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DeletesBmsAndMaintenanceInSameChunk()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "DeleteMaintenance");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "deleted.bms");

            var existing = new TestableBmsFile
            {
                path = bmsPath
            };
            existing.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDBExtended.maintenance>();
                songDbConnection.InsertOrReplace(existing, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = bmsPath,
                    hash = existing.hash,
                    encoding = "shift_jis"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existing],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.DeletedPaths.Count);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_BulkDeleteKeepsExactPathKeys()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = " " + Path.Combine(lr2RootPath, "Whitespace", "deleted.bms");

            var existing = new TestableBmsFile
            {
                path = bmsPath
            };
            existing.SetHash("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                songDbConnection.CreateTable<LR2SongDBExtended.maintenance>();
                songDbConnection.InsertOrReplace(existing, typeof(LR2SongDB.song));
                songDbConnection.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = bmsPath,
                    hash = existing.hash,
                    encoding = "shift_jis"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existing],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.DeletedPaths.Count);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", bmsPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_BulkDeleteHandlesMultipleCommitChunks()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            TestableBmsFile[] bmsFiles = [.. Enumerable.Range(0, 3)
                .Select(delegate (int index)
                {
                    var file = new TestableBmsFile
                    {
                        path = Path.Combine(lr2RootPath, "BulkDelete", "deleted" + index + ".bms")
                    };
                    file.SetHash(new string((char)('a' + index), 32));
                    return file;
                })];
            LR2SongDBExtended.bmson_song[] bmsonSongs = [.. Enumerable.Range(0, 2)
                .Select(index => new LR2SongDBExtended.bmson_song
                {
                    path = Path.Combine(lr2RootPath, "BulkDelete", "deleted" + index + ".bmson"),
                    folder = Path.Combine(lr2RootPath, "BulkDelete"),
                    title = "Deleted " + index,
                    md5 = new string((char)('d' + index), 32),
                    sha256 = new string(index == 0 ? '1' : '2', 64),
                    updated_at = DateTime.UtcNow.AddDays(-1)
                })];

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
                songDbConnection.CreateTable<LR2SongDBExtended.maintenance>();
                foreach (TestableBmsFile file in bmsFiles)
                {
                    songDbConnection.InsertOrReplace(file, typeof(LR2SongDB.song));
                    songDbConnection.InsertOrReplace(new BMSFileMaintenanceInfo
                    {
                        path = file.path,
                        hash = file.hash,
                        encoding = "shift_jis"
                    }, typeof(LR2SongDBExtended.maintenance));
                }
                foreach (LR2SongDBExtended.bmson_song song in bmsonSongs)
                {
                    songDbConnection.InsertOrReplace(song, typeof(LR2SongDBExtended.bmson_song));
                    songDbConnection.InsertOrReplace(new BMSFileMaintenanceInfo
                    {
                        path = song.path,
                        hash = song.md5,
                        encoding = "utf-8"
                    }, typeof(LR2SongDBExtended.maintenance));
                }
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, fileDiffCommitChunkSizeOverride: 2);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                bmsFiles,
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: bmsonSongs,
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                });

            Assert.AreEqual(3, result.DeletedPaths.Count);
            Assert.AreEqual(2, result.DeletedBmsonPaths.Count);
            Assert.AreEqual(3, result.DbCommitChunks);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance;"));
        });
    }

    [TestMethod]
    public void EnsureSongLookupIndexes_CreatesLr2CompatibleLookupIndexes()
    {
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
            }

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'hashidx';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'parentidx';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'song_idx_folder';"));
            Assert.AreEqual(
                1L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = ?;",
                    BmsLibraryDbGateway.SongPathNocaseIndexName));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_AddsBmsPublishesGeneratedInlineChartInfoToCallback()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineInfoCallback");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "added.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Inline Added Callback"), Encoding.ASCII);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            List<LR2SongDBExtended.chart_info> callbackRows = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                inlineChartInfoRowsCommitted: rows => callbackRows.AddRange(rows));

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(1, result.InlineChartInfoIndexPublishedCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);
            Assert.AreEqual(1, callbackRows.Count);
            Assert.AreEqual(result.AddedFiles[0].sha256, callbackRows[0].sha256);
            Assert.AreEqual(result.AddedFiles[0].hash, callbackRows[0].md5);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, callbackRows[0].parser_version);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", result.AddedFiles[0].sha256, result.AddedFiles[0].hash));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ChartInfoParseFailureDoesNotBlockSongRegistration()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineFailure");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "bad.bms");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Bad\r\n#00111:01\r\n", Encoding.ASCII);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoParseFailedCount);
            Assert.AreEqual(1, result.InlineChartInfoFailurePersistedCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", result.AddedFiles[0].hash).Single();
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, failure.parser_version);
            Assert.IsTrue(failure.message.Length > 0);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CurrentParseFailureSkipsInlineChartInfoParse()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineFailureSkip");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "skip.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Inline Failure Skip"), Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(bmsPath);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDbConnection);
                songDbConnection.InsertOrReplace(
                    CreateChartInfoParseFailureRow(snapshot.Md5, snapshot.Sha256, bmsPath),
                    typeof(LR2SongDBExtended.chart_info_parse_failure));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoFailureSkippedCount);
            Assert.AreEqual(0, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", snapshot.Md5));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CurrentInlineChartInfoRowSkipsParseAndReturnsAppliedRow()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineCurrent");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsPath = Path.Combine(chartDirectoryPath, "current.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Inline Current"), Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(bmsPath);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertChartInfos([CreateMinimalChartInfoRow(snapshot.Sha256, snapshot.Md5)]);

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                gateway,
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: []);

            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.InlineChartInfoTargetCount);
            Assert.AreEqual(1, result.InlineChartInfoCurrentSkippedCount);
            Assert.AreEqual(0, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", result.AddedFiles[0].sha256, result.AddedFiles[0].hash));
            LR2SongDB.song songRow = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(7, songRow.level);
            Assert.AreEqual(4, songRow.difficulty);
            Assert.AreEqual(1, songRow.karinotes);
            Assert.AreEqual(5, songRow.mode);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CurrentInlineChartInfoRowsDoNotPublishToCallback()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InlineCurrentCallback");
            Directory.CreateDirectory(chartDirectoryPath);
            string firstPath = Path.Combine(chartDirectoryPath, "current1.bms");
            string secondPath = Path.Combine(chartDirectoryPath, "current2.bms");
            File.WriteAllText(firstPath, CreateValidBmsText("Inline Current 1"), Encoding.ASCII);
            File.WriteAllText(secondPath, CreateValidBmsText("Inline Current 2"), Encoding.ASCII);
            ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
            ChartFileSnapshot secondSnapshot = ChartFileContentReader.ReadSnapshot(secondPath);

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertChartInfos(
            [
                CreateMinimalChartInfoRow(firstSnapshot.Sha256, firstSnapshot.Md5),
                CreateMinimalChartInfoRow(secondSnapshot.Sha256, secondSnapshot.Md5)
            ]);

            List<LR2SongDBExtended.chart_info> callbackRows = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                gateway,
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [firstPath, secondPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [],
                inlineChartInfoRowsCommitted: rows => callbackRows.AddRange(rows));

            Assert.AreEqual(2, result.AddedFiles.Count);
            Assert.AreEqual(2, result.InlineChartInfoTargetCount);
            Assert.AreEqual(2, result.InlineChartInfoCurrentSkippedCount);
            Assert.AreEqual(0, result.InlineChartInfoSuccessCount);
            Assert.AreEqual(0, result.InlineChartInfoIndexPublishedCount);
            Assert.AreEqual(0, callbackRows.Count);
            Assert.AreEqual(0, result.InlineChartInfoRows.Count);
            Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_FileDiffParserDegree_UsesDefaultAndNormalizesOverrides()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            int expectedDefault = FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(Environment.ProcessorCount);
            Assert.AreEqual(expectedDefault, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree());
            Assert.AreEqual(1, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(1));
            Assert.AreEqual(1, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(2));
            Assert.AreEqual(2, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(4));
            Assert.AreEqual(4, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(8));
            Assert.AreEqual(8, FileScanParseCommitOwner.ResolveDefaultFileDiffParserDegree(16));
            Assert.AreEqual(1, FileScanParseCommitOwner.ResolveDefaultFileDiffPostParseWorkerDegree(1, 1));
            Assert.AreEqual(3, FileScanParseCommitOwner.ResolveDefaultFileDiffPostParseWorkerDegree(4, 2));
            Assert.AreEqual(6, FileScanParseCommitOwner.ResolveDefaultFileDiffPostParseWorkerDegree(8, 4));
            Assert.AreEqual(12, FileScanParseCommitOwner.ResolveDefaultFileDiffPostParseWorkerDegree(16, 8));
            Assert.AreEqual(12, FileScanParseCommitOwner.ResolveDefaultFileDiffPostParseWorkerDegree(8, 12));

            Assert.AreEqual(expectedDefault, RunWithParserDegreeOverride(null, songDbPath).FileDiffParserDegree);
            Assert.AreEqual(1, RunWithParserDegreeOverride(0, songDbPath).FileDiffParserDegree);
            Assert.AreEqual(1, RunWithParserDegreeOverride(-10, songDbPath).FileDiffParserDegree);
            Assert.AreEqual(2, RunWithParserDegreeOverride(2, songDbPath).FileDiffParserDegree);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_FileDiffCommitChunkSize_UsesDefaultAndNormalizesOverrides()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            Assert.AreEqual(10000, FileScanParseCommitOwner.ResolveDefaultFileDiffCommitChunkSize());

            Assert.AreEqual(10000, RunWithCommitChunkSizeOverride(null, songDbPath).DbCommitChunkSize);
            Assert.AreEqual(1, RunWithCommitChunkSizeOverride(0, songDbPath).DbCommitChunkSize);
            Assert.AreEqual(1, RunWithCommitChunkSizeOverride(-10, songDbPath).DbCommitChunkSize);
            Assert.AreEqual(2, RunWithCommitChunkSizeOverride(2, songDbPath).DbCommitChunkSize);
            Assert.AreEqual(1000, RunWithCommitChunkSizeOverride(1000, songDbPath).DbCommitChunkSize);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_InlineChartInfoBatchSize_UsesDefaultAndNormalizesOverrides()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            Assert.AreEqual(2048, FileScanParseCommitOwner.ResolveDefaultInlineChartInfoBatchSize());

            Assert.AreEqual(2048, RunWithInlineChartInfoBatchSizeOverride(null, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(1, RunWithInlineChartInfoBatchSizeOverride(0, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(1, RunWithInlineChartInfoBatchSizeOverride(-10, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(2, RunWithInlineChartInfoBatchSizeOverride(2, songDbPath).InlineChartInfoBatchSize);
            Assert.AreEqual(512, RunWithInlineChartInfoBatchSizeOverride(512, songDbPath).InlineChartInfoBatchSize);
        });
    }

    [TestMethod]
    public void SQLiteConnectionEx_ReadOptimizedPragmas_AppliesAndReportsState()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), "bemusicseeker-pragmas-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var disabled = new SQLiteConnectionEx(dbPath))
            {
                List<string> disabledLogs = disabled.TryApplyReadOptimizedPragmas(enabled: false);
                CollectionAssert.AreEqual(new[] { "enabled=false" }, disabledLogs);
            }

            using var enabled = new SQLiteConnectionEx(dbPath);
            List<string> enabledLogs = enabled.TryApplyReadOptimizedPragmas(enabled: true);
            Assert.AreEqual(3, enabledLogs.Count);
            Assert.AreEqual("temp_store=MEMORY:ok", enabledLogs[0]);
            Assert.AreEqual("cache_size=-262144:ok", enabledLogs[1]);
            StringAssert.StartsWith(enabledLogs[2], "mmap_size=2147483648:ok(");
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [TestMethod]
    public void SongTableFileCheckResult_ReleasePostApplyTransientBuffers_ClearsTransientListsOnly()
    {
        var result = new SongTableFileCheckResult();
        var added = new BMSFile();
        var addedBmson = new LR2SongDBExtended.bmson_song();
        var next = new BMSFile();
        var nextBmson = new LR2SongDBExtended.bmson_song();
        result.Pragmas.Add("pragma");
        result.AddedFiles.Add(added);
        result.AddedBmsonSongs.Add(addedBmson);
        result.InlineChartInfoRows.Add(new LR2SongDBExtended.chart_info());
        result.InlineChartInfoAppliedRows.Add(new LR2SongDBExtended.chart_info());
        result.InlineChartInfoParseFailureRows.Add(new LR2SongDBExtended.chart_info_parse_failure());
        result.InlineChartInfoParseFailureDeleteMd5s.Add("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        result.FileScanFailures.Add(new ChartFileScanFailure("failed.bms", "bms", "read", "IOException", "failed"));
        result.DeletedPaths.Add("deleted.bms");
        result.DeletedBmsonPaths.Add("deleted.bmson");
        result.MutationDelta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange());
        result.MutationDelta.NotifyStorageRowPathChanges = true;
        result.MutationDelta.InvalidateInstalledDirectoryIndex = true;
        result.NextFiles.Add(next);
        result.NextBmsonSongs.Add(nextBmson);
        result.NextDirectoryResourceLookupCache = new DirectoryResourceLookupCache();

        result.ReleasePostApplyTransientBuffers();

        Assert.AreEqual(0, result.Pragmas.Count);
        Assert.AreEqual(0, result.AddedFiles.Count);
        Assert.AreEqual(0, result.AddedBmsonSongs.Count);
        Assert.AreEqual(0, result.InlineChartInfoRows.Count);
        Assert.AreEqual(0, result.InlineChartInfoAppliedRows.Count);
        Assert.AreEqual(0, result.InlineChartInfoParseFailureRows.Count);
        Assert.AreEqual(0, result.InlineChartInfoParseFailureDeleteMd5s.Count);
        Assert.AreEqual(0, result.FileScanFailures.Count);
        Assert.AreEqual(0, result.DeletedPaths.Count);
        Assert.AreEqual(0, result.DeletedBmsonPaths.Count);
        Assert.AreEqual(0, result.MutationDelta.UpdatedInstallDestinations.Count);
        Assert.IsFalse(result.MutationDelta.NotifyStorageRowPathChanges);
        Assert.IsFalse(result.MutationDelta.InvalidateInstalledDirectoryIndex);
        Assert.AreEqual(1, result.NextFiles.Count);
        Assert.AreSame(next, result.NextFiles[0]);
        Assert.AreEqual(1, result.NextBmsonSongs.Count);
        Assert.AreSame(nextBmson, result.NextBmsonSongs[0]);
        Assert.IsNotNull(result.NextDirectoryResourceLookupCache);
    }

    [TestMethod]
    public void ApplyFileScanDiff_InvalidBmsonLogsAndReportsProgress()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "InvalidBmson");
            Directory.CreateDirectory(chartDirectoryPath);
            string bmsonPath = Path.Combine(chartDirectoryPath, "invalid.bmson");
            File.WriteAllText(bmsonPath, "{\"version\":\"1.0.0\",\"info\":");

            using (var songDbConnection = new LR2SongDBExtended(songDbPath))
            {
                songDbConnection.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDbConnection);
            }

            object progressLock = new();
            List<(int Total, int Processed, string Path)> progress = [];
            List<string> logs = [];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logEverythingScan: message => logs.Add(message),
                currentBmsonSongs: [],
                reportParseProgress: (total, processed, path) =>
                {
                    lock (progressLock)
                    {
                        progress.Add((total, processed, path));
                    }
                });

            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(1, result.FileScanFailures.Count);
            Assert.AreEqual("parse", result.FileScanFailures[0].Stage);
            Assert.AreEqual(new FileInfo(bmsonPath).Length, result.ParseReadBytesEstimate);
            Assert.IsTrue(progress.Any(item => item.Total == 1 && item.Processed == 0));
            Assert.IsTrue(progress.Any(item => item.Total == 1 && item.Processed == 1 && string.Equals(item.Path, bmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(logs.Any(message => message.Contains("bmson_scan_failed") && message.Contains("stage=parse") && message.Contains("invalid.bmson")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_BuildsCachesDirectlyFromScanHashes()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string chartPath = Path.Combine(chartDirectoryPath, "keep.bms");
            Directory.CreateDirectory(chartDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Keep\r\n");

            var keepFile = new TestableBmsFile
            {
                path = chartPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(chartPath).hash);

            uint audioRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\sound");
            uint imageRelativeHash = ChartResourceKeyHash.GetLookupHash("bg");

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [keepFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = new ChartScanResult
                    {
                        ChartFilePaths = new HashSet<string>(StringComparer.Ordinal) { chartPath },
                        ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDirectoryPath },
                        AudioRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { audioRelativeHash } }
                        },
                        ImageRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { imageRelativeHash } }
                        },
                        MovieRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<uint>() }
                        },
                        SelfOwnedAudioRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { audioRelativeHash } }
                        },
                        SelfOwnedImageRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, new[] { imageRelativeHash } }
                        },
                        SelfOwnedMovieRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectoryPath, Array.Empty<uint>() }
                        }
                    }
                },
                0L,
                () => null,
                null);

            DirectoryResourceLookupCache.Entry entry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(chartDirectoryPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(1, entry.AudioFileNameHashCount);
            Assert.AreEqual(1, entry.ImageFileNameHashCount);
            Assert.AreEqual(0, entry.MovieFileNameHashCount);
            Assert.IsTrue(entry.AudioRelativePathHashes.Contains(audioRelativeHash));
            Assert.IsTrue(entry.ImageRelativePathHashes.Contains(imageRelativeHash));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesNativeResourceIndexWithoutMaterializingScanHashMaps()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectoryPath = Path.Combine(lr2RootPath, "Keep");
            string chartPath = Path.Combine(chartDirectoryPath, "keep.bms");
            Directory.CreateDirectory(chartDirectoryPath);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Keep\r\n");

            var keepFile = new TestableBmsFile
            {
                path = chartPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(chartPath).hash);

            uint audioRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\sound");
            uint movieRelativeHash = ChartResourceKeyHash.GetLookupHash("movie");
            var nativeIndex = LibraryResourceIndex.CreateFromNativeCanonicalArrays(
                [chartDirectoryPath],
                [[audioRelativeHash]],
                [[]],
                [[movieRelativeHash]],
                [[audioRelativeHash]],
                [[]],
                [[movieRelativeHash]],
                new Dictionary<uint, string[]> { { audioRelativeHash, new[] { chartDirectoryPath } } },
                [],
                new Dictionary<uint, string[]> { { movieRelativeHash, new[] { chartDirectoryPath } } });

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [keepFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    NativeBridgeReason = EverythingNative.FixedScanNativeBridgeReason,
                    AudioResourceKeyHashCount = 1,
                    MovieResourceKeyHashCount = 1,
                    ResourceIndex = nativeIndex,
                    Result = new ChartScanResult
                    {
                        ChartFilePaths = new HashSet<string>(StringComparer.Ordinal) { chartPath },
                        ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDirectoryPath }
                    }
                },
                0L,
                () => null,
                null);

            DirectoryResourceLookupCache.Entry entry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(chartDirectoryPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(1, entry.AudioFileNameHashCount);
            Assert.AreEqual(0, entry.ImageFileNameHashCount);
            Assert.AreEqual(1, entry.MovieFileNameHashCount);
            Assert.IsTrue(entry.AudioRelativePathHashes.Contains(audioRelativeHash));
            Assert.IsTrue(entry.MovieRelativePathHashes.Contains(movieRelativeHash));
            Assert.AreEqual(1ul, result.AudioResourceKeyHashEntryCount);
            Assert.AreEqual(0ul, result.ImageResourceKeyHashEntryCount);
            Assert.AreEqual(1ul, result.MovieResourceKeyHashEntryCount);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_RemovesOrphanChartDigestRowsForDeletedSongs()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string deletedChartPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(deletedChartPath));
            File.WriteAllText(deletedChartPath, "#PLAYER 1\r\n#TITLE Deleted\r\n");

            var deletedFile = new TestableBmsFile
            {
                path = deletedChartPath
            };
            var source = BMSFile.CreateBMSFileFromFile(deletedChartPath);
            deletedFile.SetHash(source.hash);
            deletedFile.SetSha256(source.sha256);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(deletedFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = deletedFile.hash,
                    sha256 = deletedFile.sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [deletedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null);

            CollectionAssert.Contains(result.DeletedPaths, deletedChartPath);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + deletedFile.hash + "';"));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_KeepsSharedChartDigestRowsWhenAnotherSongStillUsesSameMd5()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepChartPath = Path.Combine(lr2RootPath, "Keep", "keep.bms");
            string deletedChartPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(keepChartPath));
            Directory.CreateDirectory(Path.GetDirectoryName(deletedChartPath));
            File.WriteAllText(keepChartPath, "#PLAYER 1\r\n#TITLE Same\r\n");
            File.Copy(keepChartPath, deletedChartPath, overwrite: true);

            var sourceKeep = BMSFile.CreateBMSFileFromFile(keepChartPath);
            var sourceDeleted = BMSFile.CreateBMSFileFromFile(deletedChartPath);
            var keepFile = new TestableBmsFile
            {
                path = keepChartPath
            };
            keepFile.SetHash(sourceKeep.hash);
            keepFile.SetSha256(sourceKeep.sha256);
            var deletedFile = new TestableBmsFile
            {
                path = deletedChartPath
            };
            deletedFile.SetHash(sourceDeleted.hash);
            deletedFile.SetSha256(sourceDeleted.sha256);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(deletedFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = keepFile.hash,
                    sha256 = keepFile.sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [keepFile, deletedFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepChartPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(keepChartPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);

            CollectionAssert.Contains(result.DeletedPaths, deletedChartPath);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + keepFile.hash + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartDigests_ComputesOnlyMissingHashesAndPersistsThem()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartAPath = Path.Combine(lr2RootPath, "Songs", "a.bms");
            string chartBPath = Path.Combine(lr2RootPath, "Songs", "b.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartAPath));
            File.WriteAllText(chartAPath, "#PLAYER 1\r\n#TITLE A\r\n");
            File.WriteAllText(chartBPath, "#PLAYER 1\r\n#TITLE B\r\n");

            var chartA = new TestableBmsFile
            {
                path = chartAPath
            };
            chartA.SetHash(BMSFile.CreateBMSFileFromFile(chartAPath).hash);
            var chartB = new TestableBmsFile
            {
                path = chartBPath
            };
            chartB.SetHash(BMSFile.CreateBMSFileFromFile(chartBPath).hash);
            chartB.SetSha256(new string('c', 64));

            var service = new BmsLibraryInitializationService();
            List<(int Total, int Processed, string Path)> progress = [];
            ChartDigestBackfillResult result = service.BackfillChartDigests(
                new BmsLibraryDbGateway(songDbPath),
                [chartA, chartB],
                (total, processed, path) => progress.Add((total, processed, path)));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(chartA.sha256));
            Assert.AreEqual(new string('c', 64), chartB.sha256);
            Assert.IsTrue(progress.Any((item) => item.Total == 1 && item.Processed == 1));

            using var songDb = new LR2SongDBExtended(songDbPath);
            List<LR2SongDBExtended.chart_digest_map> rows = [.. songDb.Table<LR2SongDBExtended.chart_digest_map>()];
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(chartA.hash, rows[0].md5);
            Assert.AreEqual(chartA.sha256, rows[0].sha256);
        });
    }

    [TestMethod]
    public void LoadSongTable_LoadsBmsonSongsFromCatalogTable()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Songs", "chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Title", "Sub", "Chart", "Artist", "Genre", 12, "beat-7k"));

            LR2SongDBExtended.bmson_song row = BmsonSongParser.Parse(bmsonPath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.bmson_song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedBmsonSongs.Count);
            Assert.AreEqual(bmsonPath, result.LoadedBmsonSongs[0].path);
            Assert.AreEqual("Title", result.LoadedBmsonSongs[0].title);
            Assert.AreEqual("Sub [Chart]", result.LoadedBmsonSongs[0].subtitle);
        });
    }

    [TestMethod]
    public void LoadSongTable_DoesNotHydrateChartInfoOnCriticalPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath));
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n");
            string md5 = BMSFile.CreateBMSFileFromFile(chartPath).hash;
            string sha256 = new('1', 64);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                var song = new TestableBmsFile
                {
                    path = chartPath
                };
                song.SetHash(md5);
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
                songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = md5,
                    sha256 = sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertChartInfos([CreateMinimalChartInfoRow(sha256, md5)]);

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                gateway,
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(sha256, result.LoadedFiles[0].sha256);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_TracksBmsonAddsDeletesAndUpdatesDatabase()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string keepBmsonPath = Path.Combine(lr2RootPath, "Keep", "keep.bmson");
            string addedBmsonPath = Path.Combine(lr2RootPath, "Added", "added.bmson");
            string deletedBmsonPath = Path.Combine(lr2RootPath, "Deleted", "deleted.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(keepBmsonPath));
            Directory.CreateDirectory(Path.GetDirectoryName(addedBmsonPath));
            File.WriteAllText(keepBmsonPath, CreateBmsonJson("Keep", "", "", "Artist", "Genre", 5, "beat-5k"));
            File.WriteAllText(addedBmsonPath, CreateBmsonJson("Added", "", "", "Artist", "Genre", 7, "beat-7k"));

            LR2SongDBExtended.bmson_song keepSong = BmsonSongParser.Parse(keepBmsonPath);
            var deletedSong = new LR2SongDBExtended.bmson_song
            {
                path = deletedBmsonPath,
                folder = Path.GetDirectoryName(deletedBmsonPath),
                title = "Deleted",
                md5 = new string('a', 32),
                sha256 = new string('b', 64),
                updated_at = DateTime.UtcNow.AddDays(-1)
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(deletedSong, typeof(LR2SongDBExtended.bmson_song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [keepSong, deletedSong],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [keepBmsonPath, addedBmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(keepBmsonPath), Array.Empty<string>() },
                            { Path.GetDirectoryName(addedBmsonPath), Array.Empty<string>() }
                        })
                });
            ProjectCatalogState(result, [], [keepSong, deletedSong]);

            CollectionAssert.Contains(result.DeletedBmsonPaths, deletedBmsonPath);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.IsTrue(result.AddedBmsonSongs[0].HasFreshResourceReferences);
            Assert.AreEqual(2, result.NextBmsonSongs.Count);
            Assert.IsTrue(result.NextBmsonSongs.Any(song => string.Equals(song.path, keepBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(result.NextBmsonSongs.Any(song => string.Equals(song.path, addedBmsonPath, StringComparison.OrdinalIgnoreCase)));

            using var verify = new LR2SongDBExtended(songDbPath);
            List<LR2SongDBExtended.bmson_song> rows = [.. verify.Table<LR2SongDBExtended.bmson_song>()];
            Assert.AreEqual(2, rows.Count);
            Assert.IsTrue(rows.Any(song => string.Equals(song.path, keepBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(rows.Any(song => string.Equals(song.path, addedBmsonPath, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(rows.Any(song => string.Equals(song.path, deletedBmsonPath, StringComparison.OrdinalIgnoreCase)));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UpdatedBmsDateMismatchWithSameMd5UpdatesDateOnly()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = Path.Combine(lr2RootPath, "Updated", "same-md5.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsPath));
            File.WriteAllText(bmsPath, CreateValidBmsText("Same Md5"), Encoding.ASCII);
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, oldTimestamp);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(oldTimestamp),
                adddate = 12345,
                tag = "keep"
            };
            existingFile.SetHash(BMSFile.CreateBMSFileFromFile(bmsPath).hash);
            existingFile.SetFavorite(1);
            File.SetLastWriteTimeUtc(bmsPath, newTimestamp);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(1, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(1, result.NextFiles.Count);
            Assert.AreSame(existingFile, result.NextFiles[0]);
            Assert.AreEqual(ToUnixSeconds(newTimestamp), existingFile.date);
            Assert.AreEqual(12345, existingFile.adddate);
            Assert.AreEqual("keep", existingFile.tag);
            Assert.IsTrue(result.HasDbDiff);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(ToUnixSeconds(newTimestamp), row.date);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("keep", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CaseOnlyBmsPathMismatchReplacesExactPathWithoutMigratingUserColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = Path.Combine(lr2RootPath, "CaseOnly", "chart.bms");
            string oldCasePath = Path.Combine(lr2RootPath, "caseonly", "CHART.BMS");
            string staleMaintenancePath = Path.Combine(lr2RootPath, "CASEONLY", "Chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsPath));
            File.WriteAllText(bmsPath, CreateValidBmsText("Case Only"), Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            BMSFile parsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var existingFile = new TestableBmsFile
            {
                path = oldCasePath,
                date = ToUnixSeconds(timestamp.AddDays(-1)),
                adddate = 12345,
                tag = "keep"
            };
            existingFile.SetHash(parsed.hash);
            existingFile.SetFavorite(1);
            existingFile.SetTextGroupFlagForTest(0);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = staleMaintenancePath,
                    hash = parsed.hash,
                    encoding = "shift_jis"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, fileDiffCommitChunkSizeOverride: 1);
            ChartScanExecutionResult scanResult = new()
            {
                Success = true,
                Result = CreateScanResult(
                    [bmsPath],
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        { Path.GetDirectoryName(bmsPath), ["readme.txt"] }
                    })
            };

            SongTableFileCheckResult first = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [existingFile],
                scanResult,
                0L,
                () => null,
                null);
            ProjectCatalogState(first, [existingFile]);

            Assert.AreEqual(1, first.BmsAddedTargetCount);
            Assert.AreEqual(1, first.BmsDeletedTargetCount);
            Assert.AreEqual(0, first.BmsDateOnlyUpdateCount);
            Assert.AreEqual(0, first.BmsTextOnlyUpdateCount);
            Assert.AreEqual(0, first.BmsMovedHashRelinkCount);
            Assert.AreEqual(1, first.AddedFiles.Count);
            Assert.IsTrue(first.HasDbDiff);
            Assert.AreEqual(oldCasePath, existingFile.path);
            Assert.AreEqual(ToUnixSeconds(timestamp.AddDays(-1)), existingFile.date);
            Assert.AreEqual(0, existingFile.txt);
            BMSFile addedFile = first.AddedFiles.Single();
            Assert.AreEqual(bmsPath, addedFile.path);
            Assert.AreEqual(ToUnixSeconds(timestamp), addedFile.date);
            Assert.AreEqual(1, addedFile.txt);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(bmsPath)),
                addedFile.folder);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(lr2RootPath),
                addedFile.parent);
            Assert.AreEqual(12345, existingFile.adddate);
            Assert.AreEqual("keep", existingFile.tag);
            Assert.AreEqual(1, first.NextFiles.Count);
            Assert.AreEqual(bmsPath, first.NextFiles.Single().path);

            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", oldCasePath));
                LR2SongDB.song row = verify.Find<LR2SongDB.song>(bmsPath);
                Assert.IsNotNull(row);
                Assert.AreEqual(bmsPath, row.path);
                Assert.AreEqual(
                    Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(bmsPath)),
                    row.folder);
                Assert.AreEqual(
                    Lr2SongFolderParentNormalizer.ComputeDirectoryHash(lr2RootPath),
                    row.parent);
                Assert.AreEqual(ToUnixSeconds(timestamp), row.date);
                Assert.AreEqual(1, row.txt);
                Assert.AreNotEqual(1, row.favorite);
                Assert.AreNotEqual("keep", row.tag);
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", oldCasePath));
                Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", staleMaintenancePath));
                Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", bmsPath));
            }

            SongTableFileCheckResult second = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                first.NextFiles,
                scanResult,
                0L,
                () => null,
                null);

            Assert.AreEqual(0, second.BmsAddedTargetCount);
            Assert.IsFalse(second.HasDbDiff);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ProtectedLegacyMigrationSkipsExistingBmsMetadataRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = Path.Combine(lr2RootPath, "Updated", "legacy-date.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsPath));
            File.WriteAllText(bmsPath, CreateValidBmsText("Legacy Date"), Encoding.ASCII);
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, oldTimestamp);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(oldTimestamp),
                adddate = 12345,
                tag = "keep"
            };
            existingFile.SetHash(BMSFile.CreateBMSFileFromFile(bmsPath).hash);
            existingFile.SetFavorite(1);
            File.SetLastWriteTimeUtc(bmsPath, newTimestamp);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                protectExistingBmsRowsFromLr2SongDbSyncMigration: true);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsLegacyExistingProtectedCount);
            Assert.AreEqual(0, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreEqual(1, result.NextFiles.Count);
            Assert.AreSame(existingFile, result.NextFiles[0]);
            Assert.AreEqual(ToUnixSeconds(oldTimestamp), existingFile.date);
            Assert.AreEqual(12345, existingFile.adddate);
            Assert.AreEqual("keep", existingFile.tag);
            Assert.IsFalse(result.HasDbDiff);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(ToUnixSeconds(oldTimestamp), row.date);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("keep", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ProtectedLegacyMigrationSkipsExistingBmsTextRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectory = Path.Combine(lr2RootPath, "LegacyMigrationText");
            Directory.CreateDirectory(chartDirectory);
            string bmsPath = Path.Combine(chartDirectory, "legacy-date-text.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Legacy Date Text"), Encoding.ASCII);
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, oldTimestamp);
            BMSFile parsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(oldTimestamp),
                adddate = 12345,
                tag = "keep"
            };
            existingFile.SetHash(parsed.hash);
            existingFile.SetFavorite(1);
            existingFile.SetTextGroupFlagForTest(0);
            File.SetLastWriteTimeUtc(bmsPath, newTimestamp);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectory, ["readme.txt"] }
                        })
                },
                0L,
                () => null,
                null,
                protectExistingBmsRowsFromLr2SongDbSyncMigration: true);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(1, result.BmsLegacyExistingProtectedCount);
            Assert.AreEqual(0, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(0, result.BmsTextOnlyUpdateCount);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreSame(existingFile, result.NextFiles.Single());
            Assert.AreEqual(ToUnixSeconds(oldTimestamp), existingFile.date);
            Assert.AreEqual(0, existingFile.txt);
            Assert.AreEqual(12345, existingFile.adddate);
            Assert.AreEqual("keep", existingFile.tag);
            Assert.IsFalse(result.HasDbDiff);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(ToUnixSeconds(oldTimestamp), row.date);
            Assert.AreEqual(0, row.txt);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("keep", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UpdatedBmsDateMismatchWithChangedMd5ReplacesCatalog()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = Path.Combine(lr2RootPath, "Updated", "changed-md5.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsPath));
            File.WriteAllText(bmsPath, CreateValidBmsText("Old"), Encoding.ASCII);
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, oldTimestamp);
            BMSFile oldParsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(oldTimestamp),
                adddate = 23456,
                tag = "preserve"
            };
            existingFile.SetHash(oldParsed.hash);
            existingFile.SetFavorite(1);

            File.WriteAllText(bmsPath, CreateValidBmsText("New"), Encoding.ASCII);
            File.SetLastWriteTimeUtc(bmsPath, newTimestamp);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(1, result.BmsAddedTargetCount);
            Assert.AreEqual(0, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.NextFiles.Count);
            Assert.AreEqual("New", result.NextFiles[0].title);
            Assert.AreEqual(ToUnixSeconds(newTimestamp), result.NextFiles[0].date);
            Assert.AreEqual(23456, result.NextFiles[0].adddate);
            Assert.AreEqual("preserve", result.NextFiles[0].tag);
            Assert.AreNotEqual(oldParsed.hash, result.NextFiles[0].hash);
            Assert.IsTrue(result.HasDbDiff);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.AreEqual(ToUnixSeconds(newTimestamp), row.date);
            Assert.AreEqual(23456, row.adddate);
            Assert.AreEqual("preserve", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_MovedBmsWithSameMd5PreservesUserSongColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string oldPath = Path.Combine(lr2RootPath, "Old", "moved.bms");
            string newPath = Path.Combine(lr2RootPath, "New", "moved.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(oldPath));
            Directory.CreateDirectory(Path.GetDirectoryName(newPath));
            string bmsText = CreateValidBmsText("Moved Same Md5");
            File.WriteAllText(oldPath, bmsText, Encoding.ASCII);
            File.WriteAllText(newPath, bmsText, Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 3, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(newPath, timestamp);
            BMSFile oldParsed = BMSFile.CreateBMSFileFromFile(oldPath);
            var existingFile = new TestableBmsFile
            {
                path = oldPath,
                date = ToUnixSeconds(timestamp.AddDays(-1)),
                adddate = 34567,
                tag = "moved-tag"
            };
            existingFile.SetHash(oldParsed.hash);
            existingFile.SetFavorite(3);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            List<string> logs = [];
            var service = new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, fileDiffCommitChunkSizeOverride: 1);
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [newPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(newPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                logInstallPerformance: logs.Add);
            ProjectCatalogState(result, [existingFile]);

            CollectionAssert.Contains(result.DeletedPaths, oldPath);
            Assert.AreEqual(1, result.BmsMovedHashRelinkCount);
            Assert.AreEqual(0, result.BmsMovedHashRelinkAmbiguousCount);
            Assert.AreEqual(1, result.AddedFiles.Count);
            Assert.AreEqual(1, result.NextFiles.Count);
            BMSFile moved = result.NextFiles[0];
            Assert.AreEqual(newPath, moved.path);
            Assert.AreEqual(oldParsed.hash, moved.hash);
            Assert.AreEqual(3, moved.favorite);
            Assert.AreEqual(34567, moved.adddate);
            Assert.AreEqual("moved-tag", moved.tag);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", oldPath));
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(newPath, row.path);
            Assert.AreEqual(oldParsed.hash, row.hash);
            Assert.AreEqual(3, row.favorite);
            Assert.AreEqual(34567, row.adddate);
            Assert.AreEqual("moved-tag", row.tag);
            Assert.IsTrue(logs.Any(message => message.Contains("user_column_restore_done")
                && message.Contains("rows=1")));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_MovedBmsWithAmbiguousSourceMd5DoesNotPreserveUserSongColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string oldPath1 = Path.Combine(lr2RootPath, "OldA", "duplicate.bms");
            string oldPath2 = Path.Combine(lr2RootPath, "OldB", "duplicate.bms");
            string newPath = Path.Combine(lr2RootPath, "New", "duplicate.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(newPath));
            string bmsText = CreateValidBmsText("Ambiguous Source");
            File.WriteAllText(newPath, bmsText, Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 3, 2, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(newPath, timestamp);
            BMSFile parsed = BMSFile.CreateBMSFileFromFile(newPath);
            var existingFile1 = new TestableBmsFile
            {
                path = oldPath1,
                date = ToUnixSeconds(timestamp.AddDays(-1)),
                adddate = 34567,
                tag = "source-a"
            };
            existingFile1.SetHash(parsed.hash);
            existingFile1.SetFavorite(3);
            var existingFile2 = new TestableBmsFile
            {
                path = oldPath2,
                date = ToUnixSeconds(timestamp.AddDays(-1)),
                adddate = 45678,
                tag = "source-b"
            };
            existingFile2.SetHash(parsed.hash);
            existingFile2.SetFavorite(4);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile1, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(existingFile2, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existingFile1, existingFile2],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [newPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(newPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(result, [existingFile1, existingFile2]);

            Assert.AreEqual(0, result.BmsMovedHashRelinkCount);
            Assert.AreEqual(1, result.BmsMovedHashRelinkAmbiguousCount);
            BMSFile moved = result.NextFiles.Single();
            Assert.AreEqual(newPath, moved.path);
            Assert.IsNull(moved.favorite);
            Assert.AreNotEqual("source-a", moved.tag);
            Assert.AreNotEqual("source-b", moved.tag);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(newPath, row.path);
            Assert.IsNull(row.favorite);
            Assert.AreNotEqual("source-a", row.tag);
            Assert.AreNotEqual("source-b", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_MovedBmsWithAmbiguousDestinationMd5DoesNotPreserveUserSongColumns()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string oldPath = Path.Combine(lr2RootPath, "Old", "duplicate.bms");
            string newPath1 = Path.Combine(lr2RootPath, "NewA", "duplicate.bms");
            string newPath2 = Path.Combine(lr2RootPath, "NewB", "duplicate.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(newPath1));
            Directory.CreateDirectory(Path.GetDirectoryName(newPath2));
            string bmsText = CreateValidBmsText("Ambiguous Destination");
            File.WriteAllText(newPath1, bmsText, Encoding.ASCII);
            File.WriteAllText(newPath2, bmsText, Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 3, 3, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(newPath1, timestamp);
            File.SetLastWriteTimeUtc(newPath2, timestamp);
            BMSFile parsed = BMSFile.CreateBMSFileFromFile(newPath1);
            var existingFile = new TestableBmsFile
            {
                path = oldPath,
                date = ToUnixSeconds(timestamp.AddDays(-1)),
                adddate = 34567,
                tag = "moved-tag"
            };
            existingFile.SetHash(parsed.hash);
            existingFile.SetFavorite(3);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [newPath1, newPath2],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(newPath1), Array.Empty<string>() },
                            { Path.GetDirectoryName(newPath2), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(0, result.BmsMovedHashRelinkCount);
            Assert.AreEqual(2, result.BmsMovedHashRelinkAmbiguousCount);
            Assert.AreEqual(2, result.NextFiles.Count);
            foreach (BMSFile moved in result.NextFiles)
            {
                Assert.IsNull(moved.favorite);
                Assert.AreNotEqual("moved-tag", moved.tag);
            }

            using var verify = new LR2SongDBExtended(songDbPath);
            List<LR2SongDB.song> rows = [.. verify.Table<LR2SongDB.song>()];
            Assert.AreEqual(2, rows.Count);
            foreach (LR2SongDB.song row in rows)
            {
                Assert.IsNull(row.favorite);
                Assert.AreNotEqual("moved-tag", row.tag);
            }
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_NewBmsSetsTxtFromDirectTextGroupOnlyWhenLr2SongDbSyncEnabled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directDirectory = Path.Combine(lr2RootPath, "DirectText");
            string nestedDirectory = Path.Combine(lr2RootPath, "NestedText");
            Directory.CreateDirectory(directDirectory);
            Directory.CreateDirectory(nestedDirectory);
            string directBmsPath = Path.Combine(directDirectory, "direct.bms");
            string nestedBmsPath = Path.Combine(nestedDirectory, "nested.bms");
            File.WriteAllText(directBmsPath, CreateValidBmsText("Direct Text"), Encoding.ASCII);
            File.WriteAllText(nestedBmsPath, CreateValidBmsText("Nested Text"), Encoding.ASCII);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [directBmsPath, nestedBmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { directDirectory, ["readme.txt"] },
                            { nestedDirectory, [Path.Combine("docs", "readme.txt")] }
                        })
                },
                0L,
                () => null,
                null);

            Assert.AreEqual(2, result.AddedFiles.Count);
            Assert.AreEqual(1, result.AddedFiles.Single(file => file.path == directBmsPath).txt);
            Assert.AreEqual(0, result.AddedFiles.Single(file => file.path == nestedBmsPath).txt);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", directBmsPath));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", nestedBmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_ExistingBmsTextGroupChangeUpdatesTxtOnlyWhenLr2SongDbSyncEnabled()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectory = Path.Combine(lr2RootPath, "TextOnly");
            Directory.CreateDirectory(chartDirectory);
            string bmsPath = Path.Combine(chartDirectory, "text-only.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Text Only"), Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 4, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            BMSFile parsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(timestamp),
                adddate = 45678,
                tag = "text-tag"
            };
            existingFile.SetHash(parsed.hash);
            existingFile.SetFavorite(4);
            existingFile.SetTextGroupFlagForTest(0);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = true,
                },
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { chartDirectory, ["readme.txt"] }
                        })
                },
                0L,
                () => null,
                null);
            ProjectCatalogState(result, [existingFile]);

            Assert.AreEqual(1, result.BmsAddedTargetCount);
            Assert.AreEqual(0, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(1, result.BmsTextOnlyUpdateCount);
            Assert.AreEqual(0, result.AddedFiles.Count);
            Assert.AreSame(existingFile, result.NextFiles.Single());
            Assert.AreEqual(1, existingFile.txt);
            Assert.AreEqual(45678, existingFile.adddate);
            Assert.AreEqual("text-tag", existingFile.tag);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual(1, row.txt);
            Assert.AreEqual(ToUnixSeconds(timestamp), row.date);
            Assert.AreEqual(4, row.favorite);
            Assert.AreEqual(45678, row.adddate);
            Assert.AreEqual("text-tag", row.tag);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_DoesNotResetTxtOutsideLr2Mode()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartDirectory = Path.Combine(lr2RootPath, "TextDisabled");
            Directory.CreateDirectory(chartDirectory);
            string bmsPath = Path.Combine(chartDirectory, "text-disabled.bms");
            File.WriteAllText(bmsPath, CreateValidBmsText("Text Disabled"), Encoding.ASCII);
            var timestamp = new DateTime(2026, 5, 4, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, timestamp);
            var existingFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(timestamp)
            };
            existingFile.SetHash(BMSFile.CreateBMSFileFromFile(bmsPath).hash);
            existingFile.SetTextGroupFlagForTest(1);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingFile, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                },
                [existingFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
                },
                0L,
                () => null,
                null);

            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(0, result.BmsTextOnlyUpdateCount);
            Assert.AreEqual(1, existingFile.txt);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", bmsPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UpdatedBmsonUsesSnapshotTimestamp()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Updated", "chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Old", "", "", "Artist", "Genre", 5, "beat-5k"));
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, oldTimestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);

            File.WriteAllText(bmsonPath, CreateBmsonJson("New", "", "", "Artist", "Genre", 7, "beat-7k"));
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, newTimestamp);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
            }

            List<ChartFile> cleanupCharts = [ChartFileProjection.WithPackageState(
                ChartFileProjection.FromBmsonSong(existingSong, includeWarningSnapshot: false),
                Path.Combine(lr2RootPath, "Stale"),
                string.Empty,
                string.Empty,
                [])];
            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [existingSong]);
            ProjectCatalogState(result, [], [existingSong], cleanupCharts);

            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.AreEqual("New", result.AddedBmsonSongs[0].title);
            Assert.AreEqual(newTimestamp, result.AddedBmsonSongs[0].updated_at);
            Assert.IsTrue(result.AddedBmsonSongs[0].HasFreshResourceReferences);
            Assert.AreEqual(1, result.NextBmsonSongs.Count);
            Assert.AreEqual("New", result.NextBmsonSongs[0].title);
            LibraryInstallDestinationChange installDestinationChange = result.MutationDelta.UpdatedInstallDestinations.Single();
            Assert.AreSame(existingSong, installDestinationChange.Chart.GetBmsonStorageOwner());
            Assert.IsTrue(installDestinationChange.ClearInstallDestinationState);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.bmson_song row = verify.Table<LR2SongDBExtended.bmson_song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.AreEqual(newTimestamp, row.updated_at);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CaseOnlyBmsonPathMismatchAddsExactPathWithoutMigratingMaintenance()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "BmsonCase", "chart.bmson");
            string oldCasePath = Path.Combine(lr2RootPath, "BmsonCase", "CHART.BMSON");
            string staleMaintenancePath = Path.Combine(lr2RootPath, "BMSONCASE", "Chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Case", "", "", "Artist", "Genre", 5, "beat-5k"));
            var timestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, timestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);
            existingSong.path = oldCasePath;
            existingSong.folder = Path.GetDirectoryName(oldCasePath);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = staleMaintenancePath,
                    hash = existingSong.md5,
                    encoding = "utf-8"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [existingSong]);

            Assert.AreEqual(1, result.BmsonDeletedTargetCount);
            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(1, result.AddedBmsonSongs.Count);
            Assert.IsTrue(result.HasDbDiff);
            Assert.AreEqual(oldCasePath, existingSong.path);
            Assert.AreEqual(Path.GetDirectoryName(oldCasePath), existingSong.folder);
            Assert.AreEqual(bmsonPath, result.AddedBmsonSongs.Single().path);
            Assert.AreEqual(Path.GetDirectoryName(bmsonPath), result.AddedBmsonSongs.Single().folder);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM bmson_song WHERE path = ?;", oldCasePath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM bmson_song WHERE path = ?;", bmsonPath));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", oldCasePath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", staleMaintenancePath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", bmsonPath));
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_CaseOnlyBmsonPathMismatchReplacesExactPathAndConverges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "BmsonMtimeCase", "chart.bmson");
            string oldCasePath = Path.Combine(lr2RootPath, "bmsonmtimecase", "CHART.BMSON");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Case Mtime", "", "", "Artist", "Genre", 5, "beat-5k"));
            var timestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, timestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);
            existingSong.path = oldCasePath;
            existingSong.folder = Path.GetDirectoryName(oldCasePath);
            existingSong.updated_at = timestamp.AddDays(-1);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo
                {
                    path = oldCasePath,
                    hash = existingSong.md5,
                    encoding = "utf-8"
                }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService();
            ChartScanExecutionResult scanResult = new()
            {
                Success = true,
                Result = CreateScanResult(
                    [bmsonPath],
                    new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        { Path.GetDirectoryName(bmsonPath), Array.Empty<string>() }
                    })
            };

            SongTableFileCheckResult first = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                scanResult,
                0L,
                () => null,
                null,
                currentBmsonSongs: [existingSong]);
            ProjectCatalogState(first, [], [existingSong]);

            Assert.AreEqual(1, first.BmsonDeletedTargetCount);
            Assert.AreEqual(1, first.BmsonUpsertTargetCount);
            Assert.AreEqual(1, first.AddedBmsonSongs.Count);
            Assert.IsTrue(first.HasDbDiff);
            Assert.AreEqual(bmsonPath, first.AddedBmsonSongs.Single().path);

            using (var verify = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM bmson_song WHERE path = ?;", oldCasePath));
                Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM bmson_song WHERE path = ?;", bmsonPath));
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", oldCasePath));
                Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", bmsonPath));
            }

            SongTableFileCheckResult second = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                scanResult,
                0L,
                () => null,
                null,
                currentBmsonSongs: first.NextBmsonSongs);

            Assert.AreEqual(0, second.BmsonUpsertTargetCount);
            Assert.IsFalse(second.HasDbDiff);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesScanMtimeForUnchangedBms()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsPath = Path.Combine(lr2RootPath, "Keep", "keep.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsPath));
            File.WriteAllText(bmsPath, CreateValidBmsText("Keep"));
            var scanTimestamp = new DateTime(2026, 5, 3, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsPath, scanTimestamp.AddDays(1));
            BMSFile parsed = BMSFile.CreateBMSFileFromFile(bmsPath);
            var currentFile = new TestableBmsFile
            {
                path = bmsPath,
                date = ToUnixSeconds(scanTimestamp)
            };
            currentFile.SetHash(parsed.hash);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.InsertOrReplace(currentFile, typeof(LR2SongDB.song));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { Path.GetDirectoryName(bmsPath), Array.Empty<string>() }
                });
            scanResult.ChartFileEntriesByPath[bmsPath] = new RootFileEnumerationEntry(bmsPath, scanTimestamp);

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [currentFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null);

            Assert.AreEqual(0, result.BmsAddedTargetCount);
            Assert.AreEqual(0, result.BmsDateOnlyUpdateCount);
            Assert.AreEqual(0, result.BmsMtimeFallbackCount);
            Assert.IsFalse(result.HasDbDiff);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UnchangedBmsonDoesNotParseOrMarkFresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Keep", "keep.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Keep", "", "", "Artist", "Genre", 5, "beat-5k"));
            var timestamp = new DateTime(2026, 5, 3, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, timestamp);
            var currentSong = new LR2SongDBExtended.bmson_song
            {
                path = bmsonPath,
                folder = Path.GetDirectoryName(bmsonPath),
                title = "Keep",
                md5 = new string('a', 32),
                sha256 = new string('b', 64),
                updated_at = timestamp
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(currentSong, typeof(LR2SongDBExtended.bmson_song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [currentSong]);
            ProjectCatalogState(result, [], [currentSong]);

            Assert.AreEqual(0, result.BmsonUpsertTargetCount);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.NextBmsonSongs.Count);
            Assert.IsFalse(result.NextBmsonSongs[0].HasFreshResourceReferences);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_UsesScanMtimeForUnchangedBmson()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Keep", "keep.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Keep", "", "", "Artist", "Genre", 5, "beat-5k"));
            var scanTimestamp = new DateTime(2026, 5, 3, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, scanTimestamp.AddDays(1));
            var currentSong = new LR2SongDBExtended.bmson_song
            {
                path = bmsonPath,
                folder = Path.GetDirectoryName(bmsonPath),
                title = "Keep",
                md5 = new string('a', 32),
                sha256 = new string('b', 64),
                updated_at = scanTimestamp
            };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(currentSong, typeof(LR2SongDBExtended.bmson_song));
            }

            ChartScanResult scanResult = CreateScanResult(
                [bmsonPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    { Path.GetDirectoryName(bmsonPath), Array.Empty<string>() }
                });
            scanResult.ChartFileEntriesByPath[bmsonPath] = new RootFileEnumerationEntry(bmsonPath, scanTimestamp);

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = scanResult
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [currentSong]);

            Assert.AreEqual(0, result.BmsonUpsertTargetCount);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(0, result.BmsonMtimeFallbackCount);
            Assert.IsFalse(result.HasDbDiff);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_InvalidUpdatedBmsonKeepsExistingCatalogWithoutFreshRefs()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Updated", "chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath));
            File.WriteAllText(bmsonPath, CreateBmsonJson("Old", "", "", "Artist", "Genre", 5, "beat-5k"));
            var oldTimestamp = new DateTime(2026, 5, 1, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, oldTimestamp);
            LR2SongDBExtended.bmson_song existingSong = BmsonSongParser.Parse(bmsonPath);
            existingSong.HasFreshResourceReferences = false;

            File.WriteAllText(bmsonPath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            var newTimestamp = new DateTime(2026, 5, 2, 1, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(bmsonPath, newTimestamp);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(existingSong, typeof(LR2SongDBExtended.bmson_song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { Path.GetDirectoryName(bmsonPath), Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                null,
                currentBmsonSongs: [existingSong]);
            ProjectCatalogState(result, [], [existingSong]);

            Assert.AreEqual(1, result.BmsonUpsertTargetCount);
            Assert.AreEqual(0, result.AddedBmsonSongs.Count);
            Assert.AreEqual(1, result.NextBmsonSongs.Count);
            Assert.AreEqual("Old", result.NextBmsonSongs[0].title);
            Assert.IsFalse(result.NextBmsonSongs[0].HasFreshResourceReferences);
        });
    }

    [TestMethod]
    public void ApplyFileScanDiff_MergesBmsonOnlyDirectoriesIntoInstallCandidateCache()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsDir = Path.Combine(lr2RootPath, "BmsKeep");
            string bmsonDir = Path.Combine(lr2RootPath, "BmsonOnly");
            Directory.CreateDirectory(bmsDir);
            Directory.CreateDirectory(bmsonDir);
            string bmsPath = Path.Combine(bmsDir, "keep.bms");
            string bmsonPath = Path.Combine(bmsonDir, "chart.bmson");
            File.WriteAllText(bmsPath, "#PLAYER 1\r\n#TITLE Keep\r\n");
            File.WriteAllText(bmsonPath, CreateBmsonJson("Title", "Sub", "Chart", "Artist", "Genre", 12, "beat-7k"));

            var keepFile = new TestableBmsFile
            {
                path = bmsPath
            };
            keepFile.SetHash(BMSFile.CreateBMSFileFromFile(bmsPath).hash);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
            }

            var service = new BmsLibraryInitializationService();
            SongTableFileCheckResult result = service.ApplyFileScanDiff(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                [keepFile],
                new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { bmsDir, Array.Empty<string>() }
                        })
                },
                0L,
                () => null,
                dialogService: null,
                logInstallPerformance: null,
                logEverythingScan: null,
                currentBmsonSongs: [],
                executeBmsonScan: () => new ChartScanExecutionResult
                {
                    Success = true,
                    Result = CreateScanResult(
                        [bmsonPath],
                        new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            { bmsonDir, new[] { Path.Combine("sound", "song.ogg") } }
                        })
                });

            Assert.IsNotNull(result.NextDirectoryResourceLookupCache);
            CollectionAssert.Contains(result.NextDirectoryResourceLookupCache.Keys.ToList(), bmsDir);
            Assert.IsTrue(result.NextDirectoryResourceLookupCache.Keys.Contains(bmsonDir, StringComparer.OrdinalIgnoreCase));
            DirectoryResourceLookupCache.Entry bmsonEntry = result.NextDirectoryResourceLookupCache.GetEntryOrNull(bmsonDir);
            Assert.IsNotNull(bmsonEntry);
            Assert.AreEqual(1, bmsonEntry.AudioFileNameHashCount);
            Assert.AreEqual(1, bmsonEntry.AudioRelativePathHashArray.Length);
        });
    }

    [TestMethod]
    public void RunInitialize_InvokesAllPhasesAndWaitsForContinuations()
    {
        var service = new BmsLibraryInitializationService();
        int phase1Count = 0;
        int phase2Count = 0;
        int phase3Count = 0;
        int continuationCount = 0;

        InitializationExecutionResult result = service.RunInitialize(
            [
                delegate
                {
                    Interlocked.Increment(ref continuationCount);
                }
            ],
            new SemaphoreSlim(2, 2),
            delegate
            {
                Interlocked.Increment(ref phase1Count);
            },
            delegate
            {
                Interlocked.Increment(ref phase2Count);
            },
            delegate
            {
                Interlocked.Increment(ref phase3Count);
            });

        Assert.AreEqual(1, phase1Count);
        Assert.AreEqual(1, phase2Count);
        Assert.AreEqual(1, phase3Count);
        Assert.AreEqual(1, continuationCount);
        Assert.IsTrue(result.Phase1MinLoadMs >= 0);
        Assert.IsTrue(result.Phase2ScanMaintMs >= 0);
        Assert.IsTrue(result.Phase3InstallMaintenanceMs >= 0);
        Assert.IsTrue(result.WaitContinuationMs >= 0);
        Assert.IsTrue(result.WaitBeforeContinuationStartMs >= 0);
        Assert.IsTrue(result.WaitForContinuationSignalMs >= 0);
        Assert.IsTrue(result.WaitForContinuationTasksMs >= 0);
        Assert.AreEqual(
            result.WaitBeforeContinuationStartMs + result.WaitForContinuationSignalMs + result.WaitForContinuationTasksMs,
            result.WaitContinuationMs);
        Assert.IsTrue(result.TotalMs >= 0);
    }

    [TestMethod]
    public void LoadInstallTable_InitializesPendingWarningsAndCounts()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingDir");
            Directory.CreateDirectory(directoryPackagePath);
            string directoryChartPath = Path.Combine(directoryPackagePath, "dir_chart.bms");
            File.WriteAllText(directoryChartPath, "#PLAYER 1\r\n#TITLE Dir\r\n");

            string singleFileDirectoryPath = Path.Combine(lr2RootPath, "Single");
            Directory.CreateDirectory(singleFileDirectoryPath);
            string singleFileChartPath = Path.Combine(singleFileDirectoryPath, "single_chart.bms");
            File.WriteAllText(singleFileChartPath, "#PLAYER 1\r\n#TITLE Single\r\n");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = singleFileChartPath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = Path.Combine(lr2RootPath, "MissingPkg"),
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            string installedHash = BMSFile.CreateBMSFileFromFile(directoryChartPath).hash;
            var service = new BmsLibraryInitializationService();

            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                chart => string.Equals(chart?.Md5, installedHash, StringComparison.OrdinalIgnoreCase));

            Assert.AreEqual(2, result.PendingPackages.Count);
            Assert.AreEqual(1, result.StaleInstallPaths.Count);
            Assert.AreEqual(1, result.InstalledWarningCount);
            Assert.AreEqual(1, result.SingleFileWarningCount);
            Assert.AreEqual(0, result.StrictWarningCount);
            Assert.IsTrue(result.LoadMs >= 0);
            Assert.IsTrue(result.WarningInitMs >= 0);
            Assert.IsTrue(result.TotalMs >= 0);
            ChartPackage installedWarningPackage = result.PendingPackages.Single(pkg => pkg.path.Equals(directoryPackagePath, StringComparison.OrdinalIgnoreCase));
            ChartPackage singleFileWarningPackage = result.PendingPackages.Single(pkg => pkg.path.Equals(singleFileChartPath, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(installedWarningPackage.ChartEntries[0].Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(singleFileWarningPackage.ChartEntries[0].Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SingleBmsFile));
        });
    }

    [TestMethod]
    public void LoadInstallTable_DatabaseFailureIsPropagated()
    {
        string missingSongDbPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MissingInitTests_" + Guid.NewGuid().ToString("N"), "song.db");
        var service = new BmsLibraryInitializationService();

        Assert.ThrowsException<SQLite.SQLiteException>(() => service.LoadInstallTable(new BmsLibraryDbGateway(missingSongDbPath)));
    }

    [TestMethod]
    public void LoadInstallTable_ChecksInstalledChartsWithoutMaterializingUnmatchedBmsonEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingBmsonDir");
            Directory.CreateDirectory(directoryPackagePath);
            string installedBmsonPath = CreateBmsonFile(directoryPackagePath, "installed.bmson", "Installed", "Artist");
            string unmatchedBmsonPath = Path.Combine(directoryPackagePath, "unmatched.bmson");
            File.WriteAllText(unmatchedBmsonPath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Unmatched\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[],\"lines\":[{\"y\":0}]}");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();

            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                chart => string.Equals(chart?.Path, installedBmsonPath, StringComparison.OrdinalIgnoreCase));

            ChartPackage pendingPackage = result.PendingPackages.Single();
            PackageChartEntry installedEntry = pendingPackage.ChartEntries.Single(entry => string.Equals(entry.Chart.Path, installedBmsonPath, StringComparison.OrdinalIgnoreCase));
            PackageChartEntry unmatchedEntry = pendingPackage.ChartEntries.Single(entry => string.Equals(entry.Chart.Path, unmatchedBmsonPath, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(1, result.InstalledWarningCount);
            Assert.IsNull(installedEntry.GetBmsOwnerForTest());
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsNull(unmatchedEntry.GetBmsOwnerForTest());
        });
    }

    [TestMethod]
    public void LoadInstallTable_AddsBmsonResourceWarningWithoutMaterializingCompatibilityAdapter()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingBmsonResource");
            Directory.CreateDirectory(directoryPackagePath);
            string bmsonPath = Path.Combine(directoryPackagePath, "chart.bmson");
            File.WriteAllText(bmsonPath, CreateBmsonJsonWithSound("missing.wav"));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();

            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                _ => false);

            ChartPackage pendingPackage = result.PendingPackages.Single();
            PackageChartEntry entry = pendingPackage.ChartEntries.Single();
            Assert.AreEqual(1, result.StrictWarningCount);
            Assert.IsNull(entry.GetBmsOwnerForTest());
            Assert.AreEqual(ChartFileKind.Bmson, entry.Chart.Kind);
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void LoadInstallTable_ProjectsResourceHealthForAllPackageEntries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingBmsonResources");
            Directory.CreateDirectory(directoryPackagePath);
            string missingBmsonPath = Path.Combine(directoryPackagePath, "missing.bmson");
            string healthyBmsonPath = Path.Combine(directoryPackagePath, "healthy.bmson");
            File.WriteAllText(missingBmsonPath, CreateBmsonJsonWithSound("missing.wav"));
            File.WriteAllText(healthyBmsonPath, CreateBmsonJsonWithSound("sound.wav"));
            File.WriteAllBytes(Path.Combine(directoryPackagePath, "sound.wav"), new byte[] { 1 });

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();
            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                _ => false);

            ChartPackage pendingPackage = result.PendingPackages.Single();
            PackageChartEntry missingEntry = pendingPackage.ChartEntries.Single(entry => string.Equals(entry.Chart.Path, missingBmsonPath, StringComparison.OrdinalIgnoreCase));
            PackageChartEntry healthyEntry = pendingPackage.ChartEntries.Single(entry => string.Equals(entry.Chart.Path, healthyBmsonPath, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(1, result.StrictWarningCount);
            Assert.IsTrue(missingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(missingEntry.Chart.WAVHealth.HasValue);
            Assert.AreEqual(100, healthyEntry.Chart.WAVHealth);
            Assert.IsFalse(healthyEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    [TestMethod]
    public void LoadInstallTable_RestoresNestedChartsAndPrioritizesNestedWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string directoryPackagePath = Path.Combine(lr2RootPath, "PendingDir");
            string nestedDirectoryPath = Path.Combine(directoryPackagePath, "sub");
            Directory.CreateDirectory(nestedDirectoryPath);
            File.WriteAllText(Path.Combine(directoryPackagePath, "root.bms"), "#PLAYER 1\r\n#TITLE Root\r\n");
            File.WriteAllText(Path.Combine(nestedDirectoryPath, "another.bms"), "#PLAYER 1\r\n#TITLE Nested\r\n#WAVAA missing.wav\r\n#00111:AA\r\n");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = directoryPackagePath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();
            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                file => false);

            Assert.AreEqual(1, result.PendingPackages.Count);
            Assert.AreEqual(2, result.PendingPackages[0].GetBmsOwnersForTest().Count);
            PackageChartEntry nestedEntry = result.PendingPackages[0].ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals("another.bms", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.NestedChartFileInPackage));
            Assert.IsTrue(nestedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual("[2] " + BeMusicSeeker.Properties.Resources.WarningDigest_NestedChart + ", " + BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing, ChartWarningTestHelpers.BuildDigestText(nestedEntry));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(nestedEntry), BeMusicSeeker.Properties.Resources.Warning_NestedChartFileInPackage);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(nestedEntry), "WAV");
        });
    }

    [TestMethod]
    public void LoadSongTable_DetectsLeapYearFolderTimestampInAnyLeapYear()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "LeapYearFolder");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(2024, 2, 29, 12, 0, 0));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = folderPath + Path.DirectorySeparatorChar,
                    title = "LeapYearFolder",
                    parent = "e2977170",
                    type = 1,
                    date = null,
                    adddate = 0
                }, typeof(LR2SongDB.folder));
            }

            var dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.Yes
            };
            var fileMutationService = new RecordingFileMutationService();
            var service = new BmsLibraryInitializationService();

            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                dialogService,
                fileMutationService,
                null,
                ex => ex.Message);

            Assert.IsTrue(result.LeapYearDetected);
            Assert.AreEqual(1, fileMutationService.TimestampCalls.Count);
            Assert.AreEqual(folderPath, fileMutationService.TimestampCalls[0].Path);
            Assert.IsTrue(result.UpdatedFolders.Any(folder => string.Equals(folder.path, folderPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
            Assert.IsTrue(dialogService.Calls.Any(call => call.Button == MessageBoxButton.YesNo));
        });
    }

    [TestMethod]
    public void LoadInstallTable_AssignsBmsonSingleFileWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string singleFileDirectoryPath = Path.Combine(lr2RootPath, "Pending");
            Directory.CreateDirectory(singleFileDirectoryPath);
            string singleFileChartPath = Path.Combine(singleFileDirectoryPath, "single_chart.bmson");
            File.WriteAllText(singleFileChartPath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Single\",\"artist\":\"Artist\"},\"lines\":[{\"y\":0}]}");

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.install>();
                songDb.InsertOrReplace(new ChartPackage
                {
                    path = singleFileChartPath,
                    delete_parent = false
                }, typeof(LR2SongDBExtended.install));
            }

            var service = new BmsLibraryInitializationService();
            InstallTableLoadResult result = service.LoadInstallTable(
                new BmsLibraryDbGateway(songDbPath),
                file => false);

            Assert.AreEqual(1, result.PendingPackages.Count);
            Assert.AreEqual(1, result.SingleFileWarningCount);
            Assert.IsTrue(result.PendingPackages[0].ChartEntries[0].Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SingleBmsonFile));
        });
    }

    [DataTestMethod]
    [DataRow(2023, 2, 28, 12, 0, 0)]
    [DataRow(2024, 3, 2, 0, 0, 0)]
    public void LoadSongTable_DoesNotTreatNonTargetTimestampAsLeapYearBug(int year, int month, int day, int hour, int minute, int second)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "NormalFolder");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(year, month, day, hour, minute, second));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = folderPath + Path.DirectorySeparatorChar,
                    title = "NormalFolder",
                    parent = "e2977170",
                    type = 1,
                    date = null,
                    adddate = 0
                }, typeof(LR2SongDB.folder));
            }

            var dialogService = new RecordingDialogService
            {
                ResultToReturn = MessageBoxResult.Yes
            };
            var fileMutationService = new RecordingFileMutationService();
            var service = new BmsLibraryInitializationService();

            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                dialogService,
                fileMutationService,
                null,
                ex => ex.Message);

            Assert.IsFalse(result.LeapYearDetected);
            Assert.AreEqual(0, fileMutationService.TimestampCalls.Count);
            Assert.AreEqual(0, result.UpdatedFolders.Count);
            Assert.IsFalse(dialogService.Calls.Any(call => call.Button == MessageBoxButton.YesNo));
        });
    }

    private static void WithTemporaryLr2SongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InitTests_" + Guid.NewGuid().ToString("N"));
        string lr2FilesPath = Path.Combine(tempRootPath, "LR2files");
        string databaseDirectoryPath = Path.Combine(lr2FilesPath, "Database");
        string songDbPath = Path.Combine(databaseDirectoryPath, "song.db");
        Directory.CreateDirectory(databaseDirectoryPath);
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(tempRootPath, songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(LongPathFileSystem.ToExtendedPath(tempRootPath), recursive: true);
            }
        }
    }

    private static void WithTemporaryStandaloneSongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StandaloneInitTests_" + Guid.NewGuid().ToString("N"));
        string databaseDirectoryPath = Path.Combine(tempRootPath, "data");
        string songDbPath = Path.Combine(databaseDirectoryPath, "song.db");
        Directory.CreateDirectory(databaseDirectoryPath);
        File.WriteAllBytes(songDbPath, []);
        try
        {
            testAction(tempRootPath, songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(LongPathFileSystem.ToExtendedPath(tempRootPath), recursive: true);
            }
        }
    }

    private static string CreateBmsonJson(string title, string subtitle, string chartName, string artist, string genre, int level, string modeHint)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{"
            + "\"title\":\"" + title + "\","
            + "\"subtitle\":\"" + subtitle + "\","
            + "\"chart_name\":\"" + chartName + "\","
            + "\"artist\":\"" + artist + "\","
            + "\"genre\":\"" + genre + "\","
            + "\"level\":" + level + ","
            + "\"mode_hint\":\"" + modeHint + "\","
            + "\"banner_image\":\"banner.png\","
            + "\"back_image\":\"back.png\","
            + "\"eyecatch_image\":\"stage.png\","
            + "\"preview_music\":\"preview.ogg\","
            + "\"subartists\":[\"SubA\",\"SubB\"]"
            + "},"
            + "\"sound_channels\":[],"
            + "\"bpm_events\":[],"
            + "\"lines\":[{\"y\":0}]"
            + "}";
    }

    private static string CreateBmsonJsonWithSound(string soundFileName)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"Resource\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[{\"name\":\"" + soundFileName + "\",\"notes\":[]}],"
            + "\"lines\":[{\"y\":0}]"
            + "}";
    }

    private static string CreateBmsonFile(string directoryPath, string fileName, string title, string artist)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, CreateBmsonJson(title, string.Empty, string.Empty, artist, string.Empty, 1, "beat-7k"));
        return filePath;
    }

    private static string CreateValidBmsText(string title)
    {
        return "#PLAYER 1\r\n"
            + "#TITLE " + title + "\r\n"
            + "#ARTIST Artist\r\n"
            + "#BPM 120\r\n"
            + "#WAV01 sound.wav\r\n"
            + "#00111:01\r\n";
    }

    private static string CreateLongPathDirectory(string rootPath)
    {
        return Path.Combine(
            rootPath,
            "LongPath",
            new string('a', 70),
            new string('b', 70));
    }

    private static int ToUnixSeconds(DateTime utcTime)
    {
        return (int)new DateTimeOffset(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }

    private static void ProjectCatalogState(
        SongTableFileCheckResult result,
        IEnumerable<BMSFile> currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> currentBmsonSongs = null!,
        IEnumerable<ChartFile> currentInstallDestinationCharts = null!)
    {
        LibraryFileScanPipelineOwner.ApplyCatalogProjection(
            result,
            currentFiles,
            currentBmsonSongs,
            currentInstallDestinationCharts);
    }

    private static string ToFolderPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath);
        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalized = !string.IsNullOrEmpty(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                ? root.TrimEnd(Path.AltDirectorySeparatorChar)
                : trimmed;
        return normalized.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || normalized.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalized
                : normalized + Path.DirectorySeparatorChar;
    }

    private static SongTableFileCheckResult RunWithParserDegreeOverride(int? overrideValue, string songDbPath)
    {
        BmsLibraryInitializationService service = overrideValue.HasValue
            ? new BmsLibraryInitializationService(overrideValue.Value)
            : new BmsLibraryInitializationService();
        return service.ApplyFileScanDiff(
            new BmsLibraryDbGateway(songDbPath),
            new BmsLibraryOptionsSnapshot(),
            [],
            new ChartScanExecutionResult
            {
                Success = true,
                Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
            },
            0L,
            () => null,
            null,
            currentBmsonSongs: []);
    }

    private static SongTableFileCheckResult RunWithCommitChunkSizeOverride(int? overrideValue, string songDbPath)
    {
        BmsLibraryInitializationService service = overrideValue.HasValue
            ? new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, fileDiffCommitChunkSizeOverride: overrideValue.Value)
            : new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
        return service.ApplyFileScanDiff(
            new BmsLibraryDbGateway(songDbPath),
            new BmsLibraryOptionsSnapshot(),
            [],
            new ChartScanExecutionResult
            {
                Success = true,
                Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
            },
            0L,
            () => null,
            null,
            currentBmsonSongs: []);
    }

    private static SongTableFileCheckResult RunWithInlineChartInfoBatchSizeOverride(int? overrideValue, string songDbPath)
    {
        BmsLibraryInitializationService service = overrideValue.HasValue
            ? new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1, inlineChartInfoBatchSizeOverride: overrideValue.Value)
            : new BmsLibraryInitializationService(fileDiffParserDegreeOverride: 1);
        return service.ApplyFileScanDiff(
            new BmsLibraryDbGateway(songDbPath),
            new BmsLibraryOptionsSnapshot(),
            [],
            new ChartScanExecutionResult
            {
                Success = true,
                Result = CreateScanResult([], new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase))
            },
            0L,
            () => null,
            null,
            currentBmsonSongs: []);
    }

    private static LR2SongDBExtended.chart_info CreateMinimalChartInfoRow(string sha256, string md5)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            charthash = sha256,
            level = 7,
            difficulty = 4,
            difficulty_defined = true,
            mainbpm = 120,
            maxbpm = 120,
            minbpm = 120,
            length = 1000,
            mode = 7,
            judge = 100,
            feature = 0,
            notes = 1,
            n = 1,
            ln = 0,
            s = 0,
            ls = 0,
            total = 100,
            total_defined = true,
            density = 1,
            peakdensity = 1,
            enddensity = 1,
            distribution = string.Empty,
            speedchange = string.Empty,
            speedchange_count = 0,
            lanenotes = string.Empty,
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            updated_at = DateTime.UtcNow
        };
    }

    private static LR2SongDBExtended.chart_info_parse_failure CreateChartInfoParseFailureRow(string md5, string sha256, string path)
    {
        return new LR2SongDBExtended.chart_info_parse_failure
        {
            md5 = md5,
            sha256 = sha256,
            path = path,
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            failure_kind = "parse_failed",
            exception_type = "InvalidDataException",
            message = "bad bpm",
            parse_timeout_ms = null,
            updated_at = DateTime.UtcNow
        };
    }

    private static ChartScanResult CreateScanResult(
        IEnumerable<string> chartPaths,
        IDictionary<string, IEnumerable<string>> resourcesByDirectory,
        IEnumerable<string>? directorySurfaceRoots = null)
    {
        var chartPathSet = new HashSet<string>(chartPaths ?? [], StringComparer.Ordinal);
        var chartDirectories = new HashSet<string>(
            chartPathSet.Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in resourcesByDirectory?.Keys ?? Enumerable.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                chartDirectories.Add(directoryPath);
            }
        }

        var result = new ChartScanResult
        {
            ChartFilePaths = chartPathSet,
            ChartDirectories = chartDirectories
        };
        foreach (string directory in EnumerateExistingDirectorySurface(chartDirectories, directorySurfaceRoots))
        {
            RootFileEnumerationEntry entry = RootFileEnumerationEntry.FromDirectoryInfo(directory);
            if (entry != null)
            {
                result.DirectoryEntriesByPath[Lr2FolderPath.NormalizeDirectoryPath(directory)] = entry;
            }
        }
        var cache = new DirectoryResourceLookupCache();
        foreach (string chartDirectory in chartDirectories)
        {
            IEnumerable<string>? resourceFiles = null;
            resourcesByDirectory?.TryGetValue(chartDirectory, out resourceFiles);
            cache.AddDir(chartDirectory, resourceFiles ?? []);
            DirectoryResourceLookupCache.Entry entry = cache.GetEntryOrNull(chartDirectory) ?? new DirectoryResourceLookupCache.Entry();
            result.AudioRelativePathHashesByChartDirectory[chartDirectory] = entry.AudioRelativePathHashArray;
            result.ImageRelativePathHashesByChartDirectory[chartDirectory] = entry.ImageRelativePathHashArray;
            result.MovieRelativePathHashesByChartDirectory[chartDirectory] = entry.MovieRelativePathHashArray;
            result.SelfOwnedAudioRelativePathHashesByChartDirectory[chartDirectory] = entry.SelfOwnedAudioRelativePathHashArray;
            result.SelfOwnedImageRelativePathHashesByChartDirectory[chartDirectory] = entry.SelfOwnedImageRelativePathHashArray;
            result.SelfOwnedMovieRelativePathHashesByChartDirectory[chartDirectory] = entry.SelfOwnedMovieRelativePathHashArray;
            if ((resourceFiles ?? []).Any(IsDirectTextFile))
            {
                result.ChartDirectoriesWithTextFiles.Add(chartDirectory);
            }
        }
        return result;
    }

    private static IEnumerable<string> EnumerateExistingDirectorySurface(
        IEnumerable<string> directories,
        IEnumerable<string>? roots)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots ?? [])
        {
            string normalizedRoot = Lr2FolderPath.NormalizeDirectoryPath(root);
            if (string.IsNullOrWhiteSpace(normalizedRoot) || !Directory.Exists(normalizedRoot))
            {
                continue;
            }

            result.Add(normalizedRoot);
            foreach (string directory in Directory.EnumerateDirectories(normalizedRoot, "*", SearchOption.AllDirectories))
            {
                string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    result.Add(normalized);
                }
            }
        }

        foreach (string directory in directories ?? [])
        {
            string current = Lr2FolderPath.NormalizeDirectoryPath(directory);
            while (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current) && result.Add(current))
            {
                string parent = Lr2FolderPath.NormalizeDirectoryPath(Path.GetDirectoryName(current));
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = parent;
            }
        }
        return result;
    }

    private static bool IsDirectTextFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path ?? string.Empty), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        string directory = Path.GetDirectoryName(path);
        return string.IsNullOrWhiteSpace(directory);
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            ApplySha256(value);
        }

        public void SetFavorite(int? value)
        {
            favorite = value;
        }

        public void SetTextGroupFlagForTest(int value)
        {
            SetTextGroupFlag(value);
        }
    }

    private sealed class TestFileMutationService : IFileMutationService
    {
        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public void DeleteFileShell(string filePath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteFileDirect(filePath, options);
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive);
            }
        }

        public void DeleteDirectoryShell(string directoryPath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            DeleteDirectoryDirect(directoryPath, recursive: true, options);
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
        }
    }

    private sealed class RecordingFileMutationService : IFileMutationService
    {
        public List<TimestampCall> TimestampCalls { get; } = [];

        public void EnsureDirectory(string directoryPath, FileMutationOptions options = null!)
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void MoveDirectory(string sourcePath, string destinationPath, bool overwrite, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileDirect(string filePath, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteFileShell(string filePath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteDirectoryDirect(string directoryPath, bool recursive, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void DeleteDirectoryShell(string directoryPath, Microsoft.VisualBasic.FileIO.UIOption uiOption, Microsoft.VisualBasic.FileIO.RecycleOption recycleOption, FileMutationOptions options = null!)
        {
            throw new NotSupportedException();
        }

        public void SetTimestamps(string path, bool isDirectory, DateTime? creationTime, DateTime? lastWriteTime, FileMutationOptions options = null!)
        {
            TimestampCalls.Add(new TimestampCall
            {
                Path = path,
                IsDirectory = isDirectory,
                CreationTime = creationTime,
                LastWriteTime = lastWriteTime
            });
        }
    }

    private sealed class TimestampCall
    {
        public string Path { get; set; } = string.Empty;

        public bool IsDirectory { get; set; }

        public DateTime? CreationTime { get; set; }

        public DateTime? LastWriteTime { get; set; }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public List<DialogCall> Calls { get; } = [];

        public MessageBoxResult ResultToReturn { get; set; } = MessageBoxResult.OK;

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            Calls.Add(new DialogCall
            {
                Message = messageBoxText,
                Caption = caption,
                Button = button,
                Icon = icon,
                DefaultResult = defaultResult
            });
            return ResultToReturn;
        }
    }

    private sealed class DialogCall
    {
        public string Message { get; set; } = string.Empty;

        public string Caption { get; set; } = string.Empty;

        public MessageBoxButton Button { get; set; }

        public MessageBoxImage Icon { get; set; }

        public MessageBoxResult DefaultResult { get; set; }
    }
}
