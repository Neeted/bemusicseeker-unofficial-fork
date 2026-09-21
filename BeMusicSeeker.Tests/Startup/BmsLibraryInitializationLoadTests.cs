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
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class BmsLibraryInitializationLoadTests
{
    [TestMethod]
    public void LoadSongTable_FixesRelativePathsWithoutMaintenanceHydration()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath)!);
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
        string? previousMode = Environment.GetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE", null);
        try
        {
            WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
            {
                string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
                Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath)!);
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
                Assert.IsTrue(result.MaintenanceMap.TryGetValue(rootedChartPath, out BMSFileMaintenanceInfo? info));
                BMSFileMaintenanceInfo loadedInfo = info!;
                Assert.AreEqual("shift_jis", loadedInfo.encoding);
                Assert.IsTrue(loadedInfo.is_encoding_fixed);
                Assert.AreEqual(10, loadedInfo.wav_files_existing);
                Assert.AreEqual(12, loadedInfo.wav_files_defined);
                Assert.AreEqual(3, loadedInfo.bga_files_existing);
                Assert.AreEqual(4, loadedInfo.bga_files_defined);
                Assert.AreEqual(1, loadedInfo.movie_files_existing);
                Assert.AreEqual(2, loadedInfo.movie_files_defined);
                Assert.AreEqual(true, loadedInfo.is_stagefile_existing);
                Assert.AreEqual(true, loadedInfo.is_stagefile_defined);
                Assert.AreEqual(false, loadedInfo.is_banner_existing);
                Assert.AreEqual(true, loadedInfo.is_banner_defined);
                Assert.AreEqual(true, loadedInfo.is_backbmp_existing);
                Assert.AreEqual(false, loadedInfo.is_backbmp_defined);
                Assert.IsTrue(loadedInfo.is_files_warning_ignored);
                Assert.AreEqual(11, loadedInfo.lr2_warning_flags);
                Assert.AreEqual(55, loadedInfo.lr2_resource_max_relative_cp932_bytes);
                Assert.AreEqual(true, loadedInfo.lr2_resource_has_parent_traversal);
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
        string? previousMode = Environment.GetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE", "sqlite_net");
        try
        {
            WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
            {
                string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
                Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath)!);
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
                Assert.IsTrue(result.MaintenanceMap.TryGetValue(rootedChartPath, out BMSFileMaintenanceInfo? info));
                BMSFileMaintenanceInfo loadedInfo = info!;
                Assert.AreEqual("utf-8", loadedInfo.encoding);
                Assert.IsFalse(loadedInfo.is_encoding_fixed);
                Assert.AreEqual(false, loadedInfo.is_stagefile_existing);
                Assert.AreEqual(true, loadedInfo.is_stagefile_defined);
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
        string? previousMode = Environment.GetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE", null);
        try
        {
            WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
            {
                string chartPath = Path.Combine(lr2RootPath, "Songs", "full-columns.bms");
                Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
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
            Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath)!);
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
            result.LoadedFiles[0].PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                propertyNames.Add(e.PropertyName!);
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
            Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath)!);
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
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
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
    public void LoadSongTable_LoadsBmsonSongsFromCatalogTable()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Songs", "chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath)!);
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
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
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

}
