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
    /// <summary>
    /// LR2の相対譜面の三列と親hashを保ち、folderの新規targetだけ通常adddateで作る。共存target自身の保持・削除をDBと返却面で揃え、maintenanceを混ぜない。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void LoadSongTable_FixesRelativePathsWithoutMaintenanceHydration(bool targetExists, bool targetUnreadable)
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string rootedChartPath = Path.Combine(lr2RootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(rootedChartPath)!);
            File.WriteAllText(rootedChartPath, "#PLAYER 1");

            DateTime generatedAt = new(2026, 6, 20, 1, 2, 3, DateTimeKind.Utc);
            var target = new LR2SongDB.folder { path = ToFolderPath(Path.Combine(lr2RootPath, "Songs")), title = "Exact target", parent = "target-parent", type = 1, date = targetUnreadable ? -1 : 77, adddate = 678 };
            string untouched = ToFolderPath(Path.Combine(lr2RootPath, "Untouched"));
            string fixedDate = ToFolderPath(Path.Combine(lr2RootPath, "DateCorrection"));
            var unreadable = new LR2SongDB.folder { path = ToFolderPath(Path.Combine(lr2RootPath, "Unreadable")), title = "Unreadable", type = 1, date = -1, adddate = 123 };
            var unreadableAlias = new LR2SongDB.folder { path = unreadable.path.ToUpperInvariant(), title = unreadable.title, type = unreadable.type, date = unreadable.date, adddate = unreadable.adddate };
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.RunInTransaction(() =>
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDB.folder>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();

                    ChartFile song = ChartTestValues.Empty() with
                    {
                        Path = Path.Combine("Songs", "chart.bms"),
                        Folder = "folder",
                        Favorite = 5,
                        Tag = "relative-user",
                        AddDate = 5432
                    };
                    song = song with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
                    songDb.InsertOrReplace(ChartTestValues.CreateBmsStorageRow(song, "parent"), typeof(LR2SongDB.song));
                    songDb.InsertOrReplace(new LR2SongDB.folder
                    {
                        path = "Songs\\",
                        title = "Songs",
                        adddate = 333,
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
                    if (targetExists) { songDb.Insert(target); }
                    songDb.InsertOrReplace(new LR2SongDB.folder { path = untouched, title = "Keep unchanged", parent = "keep-parent", type = 1, date = 7, adddate = 456 }, typeof(LR2SongDB.folder));
                    songDb.InsertOrReplace(new LR2SongDB.folder { path = fixedDate, title = "Fix date", type = 1, date = 9, adddate = -1 }, typeof(LR2SongDB.folder));
                    songDb.InsertOrReplace(unreadable, typeof(LR2SongDB.folder));
                    if (targetUnreadable) { songDb.Insert(unreadableAlias); }
                    songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = rootedChartPath, hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", encoding = "shift_jis" }, typeof(LR2SongDBExtended.maintenance));
                });
            }

            var service = new BmsLibraryInitializationService();
            SongTableLoadResult result = service.LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true },
                null,
                new TestFileMutationService(),
                null,
                ex => ex.Message,
                leapYearRepairApproval: new(songDbPath, unreadableFolders:
                    new[] { new LeapYearFolderRepairCandidate(unreadable.path, unreadable.path, DateTime.MinValue, unreadable) }
                    .Concat(targetUnreadable ? new[] { new LeapYearFolderRepairCandidate(target.path, target.path, DateTime.MinValue, target) } : []).ToArray()),
                generatedAtUtc: generatedAt);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(rootedChartPath, result.LoadedFiles[0].Path);
            Assert.IsNull(result.LoadedFiles[0].ResourceHealthMaintenanceSnapshot);
            Assert.AreEqual(((int?)5, "relative-user", (int?)5432), (result.LoadedFiles[0].Favorite, result.LoadedFiles[0].Tag, result.LoadedFiles[0].AddDate));
            Assert.AreEqual(1, result.RelativePathFixedCount);
            Assert.IsTrue(result.DbWriteRequired);
            CollectionAssert.Contains(result.DeletedSongPaths, Path.Combine("Songs", "chart.bms"));

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            string rootedNestedFolderPath = Path.Combine(lr2RootPath, "Songs", "Nested") + "\\";
            LR2SongDB.folder nested = verify.Table<LR2SongDB.folder>().Single(folder => folder.path == rootedNestedFolderPath);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.Combine(lr2RootPath, "Songs")),
                nested.parent);
            Assert.IsNotNull(result.FolderTableSnapshot);
            IReadOnlyDictionary<string, LR2SongDB.folder> reused = result.FolderTableSnapshot.ExistingRowsByPath;
            CollectionAssert.AreEquivalent(verify.Table<LR2SongDB.folder>().Select(row => row.path).ToArray(), reused.Values.Select(row => row.path).ToArray());
            Assert.AreEqual(nested.path, reused[rootedNestedFolderPath].path);
            Assert.AreEqual(nested.parent, reused[rootedNestedFolderPath].parent);
            if (targetUnreadable)
            {
                Assert.IsTrue(reused.ContainsKey(unreadableAlias.path), "片方の読取不能承認を同列の別raw keyへ適用しません。");
                Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(unreadableAlias), System.Text.Json.JsonSerializer.Serialize(reused[unreadableAlias.path]));
            }
            Assert.AreEqual(!targetUnreadable, reused.ContainsKey(target.path), "既存target自身の削除結果をrelative sourceから復活させません。");
            Assert.IsFalse(reused.ContainsKey("Songs\\"));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM folder WHERE path = ?;", "Songs\\"));
            if (!targetUnreadable)
            {
                LR2SongDB.folder savedTarget = verify.Find<LR2SongDB.folder>(target.path);
                Assert.AreEqual(targetExists ? 678 : ToUnixSeconds(generatedAt), savedTarget.adddate);
                Assert.AreEqual(targetExists ? "Exact target" : "Songs", savedTarget.title);
                Assert.AreEqual(savedTarget.adddate, reused[target.path].adddate);
                Assert.AreEqual(savedTarget.title, reused[target.path].title);
            }
            else { Assert.IsNull(verify.Find<LR2SongDB.folder>(target.path)); }
            LR2SongDB.song savedSong = verify.Find<LR2SongDB.song>(rootedChartPath);
            Assert.AreEqual(((int?)5, "relative-user", (int?)5432), (savedSong.favorite, savedSong.tag, savedSong.adddate));
            Assert.IsFalse(reused.ContainsKey(unreadable.path));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM folder WHERE path = ?;", unreadable.path));
            Assert.AreEqual("Keep unchanged", reused[untouched].title); Assert.AreEqual(7, reused[untouched].date); Assert.AreEqual(456, reused[untouched].adddate);
            Assert.IsNull(reused[fixedDate].date); Assert.IsNull(reused[fixedDate].adddate);
            Assert.IsNull(verify.Table<LR2SongDB.folder>().Single(row => row.path == fixedDate).date);

        });
    }

    /// <summary>
    /// 保存されたmaintenanceの文字コード・各リソース状態・LR2警告を読取り専用で復元し、列の欠落や取り違えを検出する。
    /// </summary>
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
                    songDb.RunInTransaction(() =>
                    {
                        songDb.CreateTable<LR2SongDB.song>();
                        songDb.CreateTable<LR2SongDBExtended.maintenance>();
                        songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = Path.Combine(lr2RootPath, "zero.bms"), hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", wav_files_defined = 0, bga_files_defined = 0, movie_files_defined = 0, is_stagefile_defined = false, is_banner_defined = false, is_backbmp_defined = false }, typeof(LR2SongDBExtended.maintenance));
                        songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = rootedChartPath, hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", encoding = "shift_jis", is_encoding_fixed = true, wav_files_existing = 10, wav_files_defined = 12, bga_files_existing = 3, bga_files_defined = 4, movie_files_existing = 1, movie_files_defined = 2, is_stagefile_existing = true, is_stagefile_defined = true, is_banner_existing = false, is_banner_defined = true, is_backbmp_existing = true, is_backbmp_defined = false, is_files_warning_ignored = true, lr2_warning_flags = 11, lr2_resource_max_relative_cp932_bytes = 55, lr2_resource_has_parent_traversal = true }, typeof(LR2SongDBExtended.maintenance));
                    });
                }

                var service = new BmsLibraryInitializationService();
                MaintenanceTableHydrationResult result = service.LoadMaintenanceTable(
                    new BmsLibraryDbGateway(songDbPath),
                    new BmsLibraryOptionsSnapshot());

                Assert.AreEqual(2L, result.MaintenanceTableCount);
                Assert.AreEqual("raw_string", result.MaintenanceMaterializeMode);
                Assert.AreEqual(2, result.MaintenanceRawRows);
                Assert.AreEqual(2, result.MaintenanceMap.Count);
                ResourceHealthMaintenanceSnapshot zero = result.MaintenanceMap[Path.Combine(lr2RootPath, "zero.bms")];
                Assert.IsTrue(zero.IsInformationChecked);
                Assert.IsNull(zero.GetWavHealth());
                Assert.IsNull(zero.WavFilesExisting);
                Assert.AreEqual(0, zero.WavFilesDefined);
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

    /// <summary>旧版の保存済みnull/null件数と補助完了から検査済みを導き、起動・非force保守では再読取りも再保存も行いません。明示forceだけが対象外定義を新基準の0/0へ更新します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void LoadMaintenanceTable_UnresolvedCompletedCountsSkipAutomaticMaintenanceAndAllowForce()
    {
        string? previousMode = Environment.GetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE", null);
        try
        {
            WithTemporaryLr2SongDb((root, songDbPath) =>
            {
                string path = Path.Combine(root, "unresolved.bms");
                File.WriteAllText(path, "#TITLE Unresolved\n#WAV01 .wav\n#BMP01 invalid\0path\n#00111:01\n#00104:01\n", Encoding.ASCII);
                ChartFile parsed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
                // 旧版で保存済みの未判定値を固定します。現行の計数で旧DB状態を合成しません。
                var evaluated = new ResourceHealthMaintenanceSnapshot
                {
                    Path = path,
                    Hash = parsed.Md5,
                    Encoding = "shift_jis",
                    WavFilesDefined = null,
                    WavFilesExisting = null,
                    BgaFilesDefined = null,
                    BgaFilesExisting = null,
                    MovieFilesDefined = null,
                    MovieFilesExisting = null,
                    StagefileDefined = false,
                    BannerDefined = false,
                    BackbmpDefined = false
                };
                ExecuteSongDbFixtureTransaction(songDbPath, db =>
                {
                    db.CreateTable<LR2SongDB.song>();
                    db.CreateTable<LR2SongDBExtended.maintenance>();
                    db.InsertOrReplace(MaintenanceStorageMapping.ToStorage(evaluated), typeof(LR2SongDBExtended.maintenance));
                });
                var gateway = new BmsLibraryDbGateway(songDbPath);
                ResourceHealthMaintenanceSnapshot restored = new BmsLibraryInitializationService()
                    .LoadMaintenanceTable(gateway, new BmsLibraryOptionsSnapshot()).MaintenanceMap[path];
                Assert.AreEqual(MaintenanceInfoOrigin.DbHydrated, restored.Origin);
                Assert.IsTrue(restored.IsInformationChecked);
                Assert.IsNull(restored.WavFilesDefined);
                Assert.IsNull(restored.WavFilesExisting);
                Assert.IsNull(restored.BgaFilesDefined);
                Assert.IsNull(restored.BgaFilesExisting);
                Assert.IsNull(restored.MovieFilesDefined);
                Assert.IsNull(restored.MovieFilesExisting);
                ChartFile loaded = ChartFileProjection.WithMaintenance(parsed with { Resources = null }, restored);
                var owner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), gateway);
                int writes = 0;
                CatalogMaintenanceWriteReceipt Write(CatalogMaintenanceWriteRequest request)
                {
                    writes++;
                    return owner.ApplyMaintenanceWrite(request);
                }
                var maintenance = new BmsLibraryMaintenanceService();
                MaintenanceWorkflowResult automatic = maintenance.UpdateMaintenanceInfo([loaded], false, Write, null);
                Assert.AreEqual(0, automatic.HealthTargetCount);
                Assert.AreEqual(0, automatic.CheckedFileCount);
                Assert.AreEqual(0, writes);
                MaintenanceWorkflowResult forced = maintenance.UpdateMaintenanceInfo([loaded], true, Write, null);
                Assert.AreEqual(1, forced.HealthTargetCount);
                Assert.AreEqual(1, forced.CheckedFileCount);
                Assert.AreEqual(0, forced.MaintenanceInfoUnchangedCount);
                Assert.AreEqual(1, forced.MaintenanceInfoUpsertCount);
                Assert.AreEqual(1, writes);
                using LR2SongDBExtended saved = gateway.OpenSongDbReadOnly();
                LR2SongDBExtended.maintenance updated = saved.Table<LR2SongDBExtended.maintenance>().Single();
                Assert.AreEqual(0, updated.wav_files_defined);
                Assert.AreEqual(0, updated.wav_files_existing);
                Assert.AreEqual(0, updated.bga_files_defined);
                Assert.AreEqual(0, updated.bga_files_existing);
                Assert.AreEqual(0, updated.movie_files_defined);
                Assert.AreEqual(0, updated.movie_files_existing);
                Assert.IsNull(restored.WavFilesDefined, "旧捕捉値は明示再評価後も書き換えません。");
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMS_MAINTENANCE_TABLE_LOAD_MODE", previousMode);
        }
    }

    /// <summary>
    /// sqlite_netを指定したmaintenance読込みでも保存された文字コードと画像状態を復元し、読込み方式で値を変えない。
    /// </summary>
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
                    songDb.RunInTransaction(() =>
                    {
                        songDb.CreateTable<LR2SongDBExtended.maintenance>();
                        songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = rootedChartPath, hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", encoding = "utf-8", is_encoding_fixed = false, is_stagefile_existing = false, is_stagefile_defined = true }, typeof(LR2SongDBExtended.maintenance));
                    });
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

    /// <summary>
    /// raw読込みでLR2 songの文字列・数値列と大文字MD5正規化を復元し、永続化された譜面情報の欠落を検出する。
    /// </summary>
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
                    songDb.RunInTransaction(() =>
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
                    });
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
                using LR2SongDBExtended verifyStored = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
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

    /// <summary>
    /// songとdigest mapを接続して読込み譜面へ保存済みSHA256を付与し、MD5だけの譜面を返さない。
    /// </summary>
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
                songDb.RunInTransaction(() =>
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
                });
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

    /// <summary>
    /// LR2でCP932不能な既存絶対パスを保持して警告し、DB行の削除や不正な親hashの保存を防ぐ。
    /// </summary>
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
                songDb.RunInTransaction(() =>
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
                });
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", chartPath)));
        });
    }

    /// <summary>
    /// 独立DBでもCP932不能な既存パスを保持して警告し、動作modeによるDB行の削除を防ぐ。
    /// </summary>
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
                songDb.RunInTransaction(() =>
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
                });
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
            Assert.IsTrue(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", chartPath)));
        });
    }

    /// <summary>
    /// CP932不能な相対パスを部分的に絶対化せず警告し、旧行を失うことなくDBの元パスを維持する。
    /// </summary>
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
                songDb.RunInTransaction(() =>
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
                });
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

            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", relativePath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", chartPath));
        });
    }

    /// <summary>
    /// 保存済みbmsonのtitleと統合subtitleを起動結果へ復元し、BMS専用読込みによるbmsonの脱落を検出する。
    /// </summary>
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
                songDb.RunInTransaction(() =>
                {
                    songDb.CreateTable<LR2SongDB.song>();
                    songDb.CreateTable<LR2SongDB.folder>();
                    songDb.CreateTable<LR2SongDBExtended.maintenance>();
                    BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(row), typeof(LR2SongDBExtended.bmson_song));
                });
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

    /// <summary>
    /// chart_infoも保存済みの譜面を通常起動のsong読込みで取得し、digest mapのSHA256が失われないことを確認する。
    /// </summary>
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
                songDb.RunInTransaction(() =>
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
                });
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

    /// <summary>現在ディレクトリ依存で実在の閏日フォルダへ解決できるdrive-relative行も修復候補へ採用せず、通常ロードの返却raw行・保存全列・物理日時を維持します。</summary>
    [TestMethod]
    public void LeapYearCandidateCapture_PreservesDriveRelativeRowWithoutPhysicalRepair()
    {
        WithTemporaryLr2SongDb((lr2RootPath, songDbPath) =>
        {
            string directory = Path.Combine(lr2RootPath, "Songs", "DriveRelativeCandidate");
            Directory.CreateDirectory(directory);
            DateTime timestamp = new(2024, 2, 29, 12, 0, 0);
            Directory.SetLastWriteTime(directory, timestamp);
            string drive = Path.GetPathRoot(directory)?.TrimEnd('\\') ?? throw new AssertFailedException();
            string driveCurrentBase = Path.GetFullPath(drive);
            string rawPath = drive + Path.GetRelativePath(driveCurrentBase, directory) + "\\";
            Assert.IsTrue(Path.IsPathRooted(rawPath));
            Assert.IsFalse(Path.IsPathFullyQualified(rawPath));
            Assert.AreEqual(directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath)), "旧CWD依存の解決なら所有tempフォルダを指す入力です。");
            var row = new LR2SongDB.folder
            {
                path = rawPath,
                title = "Keep raw relative",
                subtitle = "Keep subtitle",
                parent = "keep-parent",
                type = 1,
                date = -1,
                adddate = 123,
                max = 17
            };
            ExecuteSongDbFixtureTransaction(songDbPath, db =>
            {
                Assert.AreEqual(lr2RootPath, db.LR2RootPath, "標準LR2 DBの候補判定まで到達させます。");
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDB.folder>();
                db.CreateTable<LR2SongDBExtended.maintenance>();
                db.Insert(row);
            });
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new BmsLibraryInitializationService();
            IReadOnlyList<LeapYearFolderRepairCandidate> candidates = service.CaptureLeapYearFolderRepairCandidates(gateway, out IReadOnlyList<LeapYearFolderRepairCandidate> unreadable);
            Assert.AreEqual(0, candidates.Count);
            Assert.AreEqual(0, unreadable.Count);
            var mutation = new RecordingFileMutationService();
            LeapYearFolderRepairResult repair = service.RepairLeapYearFolderTimestamps(gateway, candidates, mutation, null);
            Assert.AreEqual(0, repair.Failures.Count);
            SongTableLoadResult loaded = service.LoadSongTable(gateway,
                new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true, LR2RootPath = lr2RootPath },
                null, mutation, null, failure => failure.Message,
                leapYearRepairApproval: new(songDbPath, approvedCandidates: candidates, unreadableFolders: unreadable));

            Assert.AreEqual(0, mutation.TimestampCalls.Count);
            Assert.AreEqual(timestamp, Directory.GetLastWriteTime(directory));
            Assert.AreEqual(0, loaded.DeletedFolderPaths.Count);
            Assert.AreEqual(0, loaded.UpdatedFolders.Count);
            Assert.IsFalse(loaded.DbWriteRequired);
            Assert.IsNotNull(loaded.FolderTableSnapshot);
            Assert.AreEqual(rawPath, loaded.FolderTableSnapshot.ExistingRowsByPath.Single().Key);
            string expected = System.Text.Json.JsonSerializer.Serialize(row);
            Assert.AreEqual(expected, System.Text.Json.JsonSerializer.Serialize(loaded.FolderTableSnapshot.Rows.Single()));
            using LR2SongDBExtended verify = gateway.OpenSongDbReadOnly();
            Assert.AreEqual(expected, System.Text.Json.JsonSerializer.Serialize(verify.Table<LR2SongDB.folder>().Single()));
        });
    }

    /// <summary>
    /// 同所a/Aの承認をraw keyごとに照合し、両承認の保存行を物理日時変更一回で修復します。
    /// 片方だけの承認・承認後の行変更や削除・物理変更失敗では対象を拡大せず、保存値を偽成功へ更新しません。
    /// </summary>
    [DataTestMethod]
    [DataRow("both-approved")]
    [DataRow("primary-only")]
    [DataRow("alias-changed")]
    [DataRow("alias-removed")]
    [DataRow("timestamp-failed")]
    public void LeapYearFolderRepair_PreservesEachApprovedRawKeyWithOnePhysicalMutation(string condition)
    {
        WithTemporaryLr2SongDb((lr2RootPath, songDbPath) =>
        {
            string directory = Path.Combine(lr2RootPath, "Songs", "a");
            Directory.CreateDirectory(directory);
            DateTime originalTimestamp = new(2024, 2, 29, 12, 0, 0);
            Directory.SetLastWriteTime(directory, originalTimestamp);
            var primary = new LR2SongDB.folder
            {
                path = directory + "\\",
                title = "Primary",
                subtitle = "keep primary",
                type = 1,
                parent = "primary-parent",
                date = -1,
                adddate = 123
            };
            var alias = new LR2SongDB.folder
            {
                path = Path.Combine(lr2RootPath, "Songs", "A") + "\\",
                title = "Alias",
                subtitle = "keep alias",
                type = 1,
                parent = "alias-parent",
                date = -2,
                adddate = 456
            };
            ExecuteSongDbFixtureTransaction(songDbPath, db =>
            {
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(primary);
                db.Insert(alias);
            });

            var service = new BmsLibraryInitializationService();
            var gateway = new BmsLibraryDbGateway(songDbPath);
            IReadOnlyList<LeapYearFolderRepairCandidate> captured = service.CaptureLeapYearFolderRepairCandidates(gateway, out _);
            CollectionAssert.AreEquivalent(new[] { primary.path, alias.path }, captured.Select(candidate => candidate.OriginalFolderPath).ToArray());
            IEnumerable<LeapYearFolderRepairCandidate> approved = condition == "primary-only"
                ? captured.Where(candidate => string.Equals(candidate.OriginalFolderPath, primary.path, StringComparison.Ordinal))
                : captured;
            if (condition == "alias-changed")
            {
                alias.title = "Changed after approval";
                ExecuteSongDbFixtureTransaction(songDbPath, db => db.InsertOrReplace(alias));
            }
            else if (condition == "alias-removed")
            {
                ExecuteSongDbFixtureTransaction(songDbPath, db => db.Execute("DELETE FROM folder WHERE path = ?;", alias.path));
            }

            var mutationFailure = new IOException("timestamp mutation failed");
            var mutation = new RecordingFileMutationService
            {
                OnSetTimestamps = call =>
                {
                    if (condition == "timestamp-failed") { throw mutationFailure; }
                    Directory.SetLastWriteTime(call.Path, call.LastWriteTime.GetValueOrDefault());
                }
            };
            LeapYearFolderRepairResult result = service.RepairLeapYearFolderTimestamps(gateway, approved, mutation, null, failure => failure.Message);

            Assert.AreEqual(1, mutation.TimestampCalls.Count, "同じ物理フォルダへの変更は一回に共有します。");
            Assert.AreEqual(condition == "timestamp-failed" ? 1 : 0, result.Failures.Count);
            if (condition == "timestamp-failed")
            {
                Assert.AreSame(mutationFailure, result.Failures.Single().Exception.InnerException);
                Assert.AreEqual(originalTimestamp, Directory.GetLastWriteTime(directory));
            }
            else
            {
                Assert.AreEqual(mutation.TimestampCalls.Single().LastWriteTime, Directory.GetLastWriteTime(directory));
                Assert.AreNotEqual(originalTimestamp, Directory.GetLastWriteTime(directory));
                primary.date = null;
                primary.adddate = null;
                if (condition == "both-approved") { alias.date = null; alias.adddate = null; }
            }
            using LR2SongDBExtended verify = gateway.OpenSongDbReadOnly();
            var rows = verify.Table<LR2SongDB.folder>().ToDictionary(row => row.path, StringComparer.Ordinal);
            Assert.AreEqual(condition == "alias-removed" ? 1 : 2, rows.Count);
            Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(primary), System.Text.Json.JsonSerializer.Serialize(rows[primary.path]));
            if (condition == "alias-removed") { Assert.IsFalse(rows.ContainsKey(alias.path), "削除された承認行を復活させません。"); }
            else { Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(alias), System.Text.Json.JsonSerializer.Serialize(rows[alias.path])); }
        });
    }

    /// <summary>
    /// 閏日不具合の対象外である平年2月末と閏年3月2日のfolderには修復確認・日時更新・保存変更を行わない。
    /// </summary>
    [DataTestMethod]
    [DataRow(2023, 2, 28, 12, 0, 0)] // 平年の2月末
    [DataRow(2024, 3, 2, 0, 0, 0)] // 閏年でも対象日より後
    public void LoadSongTable_DoesNotTreatNonTargetTimestampAsLeapYearBug(int year, int month, int day, int hour, int minute, int second)
    {

        WithTemporaryLr2SongDb(delegate (string lr2RootPath, string songDbPath)
        {
            string folderPath = Path.Combine(lr2RootPath, "Songs", "NormalFolder");
            Directory.CreateDirectory(folderPath);
            Directory.SetLastWriteTime(folderPath, new DateTime(year, month, day, hour, minute, second));

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.RunInTransaction(() =>
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
                });
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
