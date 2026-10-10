using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class BmsLibraryInitializationLoadTests
{
    [TestMethod]
    public void LoadSongTable_FixesRelativePathsWithoutMaintenanceHydration()
    {

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

                ChartFile song = ChartTestValues.Empty() with
                {
                    Path = Path.Combine("Songs", "chart.bms"),
                    Folder = "folder"
                };
                song = song with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                songDb.InsertOrReplace(ChartTestValues.CreateBmsStorageRow(song, "parent"), typeof(LR2SongDB.song));
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
                songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = rootedChartPath, hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", encoding = "shift_jis" }, typeof(LR2SongDBExtended.maintenance));
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
            Assert.AreEqual(rootedChartPath, result.LoadedFiles[0].Path);
            Assert.IsNull(result.LoadedFiles[0].ResourceHealthMaintenanceSnapshot);
            Assert.AreEqual(1, result.RelativePathFixedCount);
            Assert.IsTrue(result.DbWriteRequired);
            CollectionAssert.Contains(result.DeletedSongPaths, Path.Combine("Songs", "chart.bms"));

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
                    songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = rootedChartPath, hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", encoding = "shift_jis", is_encoding_fixed = true, wav_files_existing = 10, wav_files_defined = 12, bga_files_existing = 3, bga_files_defined = 4, movie_files_existing = 1, movie_files_defined = 2, is_stagefile_existing = true, is_stagefile_defined = true, is_banner_existing = false, is_banner_defined = true, is_backbmp_existing = true, is_backbmp_defined = false, is_files_warning_ignored = true, lr2_warning_flags = 11, lr2_resource_max_relative_cp932_bytes = 55, lr2_resource_has_parent_traversal = true }, typeof(LR2SongDBExtended.maintenance));
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
                Assert.IsTrue(result.MaintenanceMap.TryGetValue(rootedChartPath, out ResourceHealthMaintenanceSnapshot? info));
                ResourceHealthMaintenanceSnapshot loadedInfo = info!;
                Assert.AreEqual("shift_jis", loadedInfo.Encoding);
                Assert.IsTrue(loadedInfo.EncodingFixed);
                Assert.AreEqual(10, loadedInfo.WavFilesExisting);
                Assert.AreEqual(12, loadedInfo.WavFilesDefined);
                Assert.AreEqual(3, loadedInfo.BgaFilesExisting);
                Assert.AreEqual(4, loadedInfo.BgaFilesDefined);
                Assert.AreEqual(1, loadedInfo.MovieFilesExisting);
                Assert.AreEqual(2, loadedInfo.MovieFilesDefined);
                Assert.AreEqual(true, loadedInfo.StagefileExisting);
                Assert.AreEqual(true, loadedInfo.StagefileDefined);
                Assert.AreEqual(false, loadedInfo.BannerExisting);
                Assert.AreEqual(true, loadedInfo.BannerDefined);
                Assert.AreEqual(true, loadedInfo.BackbmpExisting);
                Assert.AreEqual(false, loadedInfo.BackbmpDefined);
                Assert.IsTrue(loadedInfo.FilesWarningIgnored);
                Assert.AreEqual(11, loadedInfo.Lr2WarningFlags);
                Assert.AreEqual(55, loadedInfo.Lr2ResourceMaxRelativeCp932Bytes);
                Assert.AreEqual(true, loadedInfo.Lr2ResourceHasParentTraversal);
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
                    songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = rootedChartPath, hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", encoding = "utf-8", is_encoding_fixed = false, is_stagefile_existing = false, is_stagefile_defined = true }, typeof(LR2SongDBExtended.maintenance));
                }

                var service = new BmsLibraryInitializationService();
                MaintenanceTableHydrationResult result = service.LoadMaintenanceTable(
                    new BmsLibraryDbGateway(songDbPath),
                    new BmsLibraryOptionsSnapshot());

                Assert.AreEqual(1L, result.MaintenanceTableCount);
                Assert.AreEqual("sqlite_net", result.MaintenanceMaterializeMode);
                Assert.AreEqual(0, result.MaintenanceRawRows);
                Assert.IsTrue(result.MaintenanceMap.TryGetValue(rootedChartPath, out ResourceHealthMaintenanceSnapshot? info));
                ResourceHealthMaintenanceSnapshot loadedInfo = info!;
                Assert.AreEqual("utf-8", loadedInfo.Encoding);
                Assert.IsFalse(loadedInfo.EncodingFixed);
                Assert.AreEqual(false, loadedInfo.StagefileExisting);
                Assert.AreEqual(true, loadedInfo.StagefileDefined);
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

        string? previousMode = Environment.GetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_SONG_TABLE_LOAD_MODE", null);
        try
        {
            WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
            {
                string chartPath = Path.Combine(lr2RootPath, "Songs", "full-columns.bms");
                Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
                File.WriteAllText(chartPath, "#PLAYER 1");

                LR2SongDB.song expectedCrc = ChartSongStorageMapping.ToBmsRow(ChartTestValues.Empty() with { Path = chartPath });
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
                ChartFile loaded = result.LoadedFiles.Single();
                using var verifyStored = new LR2SongDBExtended(songDbPath);
                LR2SongDB.song loadedStorage = verifyStored.Table<LR2SongDB.song>().Single(row => row.path == chartPath);
                Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", loaded.Md5);
                Assert.AreEqual("Title", loaded.RawTitle);
                Assert.AreEqual("Subtitle", loaded.RawSubtitle);
                Assert.AreEqual("Artist", loaded.RawArtist);
                Assert.AreEqual("SubArtist", loaded.Subartist);
                Assert.AreEqual("Genre", loaded.Genre);
                Assert.AreEqual("Tag", loaded.Tag);
                Assert.AreEqual(chartPath, loaded.Path);
                Assert.AreEqual(1, loadedStorage.type);
                Assert.AreEqual(expectedCrc.folder, loadedStorage.folder);
                Assert.AreEqual(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(chartPath)), loaded.Folder);
                Assert.AreEqual("stage.png", loaded.Stagefile);
                Assert.AreEqual("banner.png", loaded.Banner);
                Assert.AreEqual("back.png", loaded.Backbmp);
                Assert.AreEqual(expectedCrc.parent, loadedStorage.parent);
                Assert.AreEqual(12, loaded.Level);
                Assert.AreEqual(4, loaded.Difficulty);
                Assert.AreEqual(180, loadedStorage.maxbpm);
                Assert.AreEqual(90, loadedStorage.minbpm);
                Assert.AreEqual(7, loaded.Mode);
                Assert.AreEqual(2, loaded.Judge);
                Assert.AreEqual(1, loadedStorage.longnote);
                Assert.AreEqual(1, loadedStorage.bga);
                Assert.AreEqual(0, loadedStorage.random);
                Assert.AreEqual(12345, loaded.Date);
                Assert.AreEqual(1, loaded.Favorite);
                Assert.AreEqual(0, loaded.Txt);
                Assert.AreEqual(678, loadedStorage.karinotes);
                Assert.AreEqual(23456, loaded.AddDate);
                Assert.AreEqual(9, loadedStorage.exlevel);
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

                ChartFile song = ChartTestValues.Empty() with
                {
                    Path = rootedChartPath,
                    Folder = "folder"
                };
                song = song with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                songDb.InsertOrReplace(ChartTestValues.CreateBmsStorageRow(song, "parent"), typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = rootedChartPath, hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", encoding = "shift_jis" }, typeof(LR2SongDBExtended.maintenance));
            }

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message);
            ChartFile captured = result.LoadedFiles[0];
            ChartFile changed = ChartFileProjection.WithMaintenance(captured,
                new ResourceHealthMaintenanceSnapshot
                {
                    Path = captured.Path,
                    Hash = captured.Md5,
                    Encoding = "utf-8",
                    Origin = MaintenanceInfoOrigin.Calculated
                });
            Assert.AreSame(captured, result.LoadedFiles[0]);
            Assert.IsNull(captured.ResourceHealthMaintenanceSnapshot);
            Assert.AreEqual("utf-8", changed.ResourceHealthMaintenanceSnapshot.Encoding);
        });
    }

    [TestMethod]
    public void LoadSongTable_AppliesChartDigestMapToLoadedFiles()
    {

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

                ChartFile song = ChartTestValues.Empty() with
                {
                    Path = rootedChartPath,
                    Folder = "folder"
                };
                song = song with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                songDb.InsertOrReplace(ChartTestValues.CreateBmsStorageRow(song, "parent"), typeof(LR2SongDB.song));
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
            Assert.AreEqual(new string('b', 64), result.LoadedFiles[0].Sha256);
            Assert.AreEqual(1, result.ChartDigestMap.Count);
        });
    }

    [TestMethod]
    public void LoadSongTable_PreservesShiftJisUnsupportedExistingSongAndWarns()
    {

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
                ChartFile song = ChartTestValues.Empty() with
                {
                    Path = chartPath,
                    Folder = "folder"
                };
                song = song with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                songDb.InsertOrReplace(ChartTestValues.CreateBmsStorageRow(song, "parent"), typeof(LR2SongDB.song));
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
            Assert.AreEqual(chartPath, result.LoadedFiles[0].Path);
            Assert.AreEqual(Path.GetFileName(Path.GetDirectoryName(chartPath)), result.LoadedFiles[0].Folder);
            Assert.IsTrue(string.IsNullOrWhiteSpace(ChartSongStorageMapping.ToBmsRow(result.LoadedFiles[0]).parent));
            Assert.IsTrue(result.LoadedFiles[0].Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", chartPath)));
        });
    }

    [TestMethod]
    public void LoadSongTable_StandalonePreservesShiftJisUnsupportedExistingSongAndWarns()
    {

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
                ChartFile song = ChartTestValues.Empty() with
                {
                    Path = chartPath,
                    Folder = "folder"
                };
                song = song with { Md5 = "abababababababababababababababab" };
                songDb.InsertOrReplace(ChartTestValues.CreateBmsStorageRow(song, "parent"), typeof(LR2SongDB.song));
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
            Assert.AreEqual(chartPath, result.LoadedFiles[0].Path);
            Assert.AreEqual(Path.GetFileName(Path.GetDirectoryName(chartPath)), result.LoadedFiles[0].Folder);
            Assert.IsTrue(string.IsNullOrWhiteSpace(ChartSongStorageMapping.ToBmsRow(result.LoadedFiles[0]).parent));
            Assert.IsTrue(result.LoadedFiles[0].Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", chartPath)));
        });
    }

    [TestMethod]
    public void LoadSongTable_DoesNotPartiallyFixRelativeShiftJisUnsupportedPath()
    {

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
                ChartFile song = ChartTestValues.Empty() with
                {
                    Path = relativePath,
                    Folder = "folder"
                };
                song = song with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
                songDb.InsertOrReplace(ChartTestValues.CreateBmsStorageRow(song, "parent"), typeof(LR2SongDB.song));
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
            Assert.AreEqual(relativePath, result.LoadedFiles[0].Path);
            Assert.IsTrue(string.IsNullOrWhiteSpace(ChartSongStorageMapping.ToBmsRow(result.LoadedFiles[0]).parent));
            Assert.IsTrue(result.LoadedFiles[0].Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", relativePath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
        });
    }

    [TestMethod]
    public void LoadSongTable_LoadsBmsonSongsFromCatalogTable()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string bmsonPath = Path.Combine(lr2RootPath, "Songs", "chart.bmson");
            Directory.CreateDirectory(Path.GetDirectoryName(bmsonPath)!);
            File.WriteAllText(bmsonPath, CreateBmsonJson("Title", "Sub", "Chart", "Artist", "Genre", 12, "beat-7k"));

            ChartFile row = ChartTestValues.ReadBmson(bmsonPath);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(row), typeof(LR2SongDBExtended.bmson_song));
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
            Assert.AreEqual(bmsonPath, result.LoadedBmsonSongs[0].Path);
            Assert.AreEqual("Title", result.LoadedBmsonSongs[0].RawTitle);
            Assert.AreEqual("Sub [Chart]", result.LoadedBmsonSongs[0].Subtitle);
            Assert.IsTrue(string.IsNullOrEmpty(result.LoadedBmsonSongs[0].ChartName));
        });
    }

    [TestMethod]
    public void LoadSongTable_DoesNotHydrateChartInfoOnCriticalPath()
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string chartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n");
            string md5 = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath)).Md5;
            string sha256 = new('1', 64);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                ChartFile song = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                song = song with { Md5 = md5 };
                songDb.InsertOrReplace(ChartTestValues.CreateBmsStorageRow(song, "parent"), typeof(LR2SongDB.song));
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
            Assert.AreEqual(sha256, result.LoadedFiles[0].Sha256);
        });
    }

    [DataTestMethod]
    [DataRow(2023, 2, 28, 12, 0, 0)]
    [DataRow(2024, 3, 2, 0, 0, 0)]
    public void LoadSongTable_DoesNotTreatNonTargetTimestampAsLeapYearBug(int year, int month, int day, int hour, int minute, int second)
    {

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

            IReadOnlyList<LeapYearFolderRepairCandidate> candidates = service.CaptureLeapYearFolderRepairCandidates(new BmsLibraryDbGateway(songDbPath), out _);
            Assert.AreEqual(0, candidates.Count);
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
