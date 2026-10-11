using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbWriterTests
{
    /// <summary>
    /// 同じ生成行の再反映を変更なしとして一行だけ保存し、重複挿入や無変更更新を検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSong_SkipsUnchangedGeneratedColumns()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");

            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            Assert.IsFalse(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>
    /// 生成タイトルを更新してもお気に入り・追加時刻・タグを保持し、利用者設定の上書きを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSong_UpdatesChangedGeneratedColumnsAndPreservesUserColumns()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Old");
            file.SetUserColumns(favoriteValue: 7, addDateValue: 12345, tagValue: "keep");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            TestableBmsFile updated = CreateSong(file.path, file.hash, "New");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, updated));

            LR2SongDB.song row = songDb.Table<LR2SongDB.song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.AreEqual(7, row.favorite);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("keep", row.tag);
        });
    }

    /// <summary>
    /// 既存更新と新規追加のバッチを反映し、生成タイトルと件数を更新しながら既存利用者設定を保持する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongs_UpdatesChunkWithSingleExistingLookupAndPreservesUserColumns()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile existing = CreateSong(@"D:\BMS\Pack\existing.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Old");
            existing.SetUserColumns(favoriteValue: 7, addDateValue: 12345, tagValue: "keep");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, existing));
            TestableBmsFile updated = CreateSong(existing.path, existing.hash, "New");
            TestableBmsFile added = CreateSong(@"D:\BMS\Pack\added.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Added");

            int changed = Lr2SongDbWriter.UpsertGeneratedSongs(songDb, [updated, added]);

            Assert.AreEqual(2, changed);
            Assert.AreEqual(2, songDb.Table<LR2SongDB.song>().Count());
            LR2SongDB.song updatedRow = songDb.Table<LR2SongDB.song>().Single(row => row.path == existing.path);
            Assert.AreEqual("New", updatedRow.title);
            Assert.AreEqual(7, updatedRow.favorite);
            Assert.AreEqual(12345, updatedRow.adddate);
            Assert.AreEqual("keep", updatedRow.tag);
            Assert.AreEqual("Added", songDb.Table<LR2SongDB.song>().Single(row => row.path == added.path).title);
        });
    }

    /// <summary>
    /// パス完全一致とMD5大文字小文字同一の既存行だけを解析結果で更新し、欠落行を追加せず他列と利用者設定を保持する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpdateChartInfoSongProjections_UpdatesOnlyMatchedExistingRowsAndNeverInserts()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            TestableBmsFile first = CreateSong(@"D:\BMS\Pack\first.bms", md5, "First");
            TestableBmsFile second = CreateSong(@"D:\BMS\Pack\second.bms", md5, "Second");
            Assert.AreEqual(2, Lr2SongDbWriter.UpsertGeneratedSongs(songDb, [first, second]));
            songDb.Execute("UPDATE song SET hash = upper(hash) WHERE path = ?;", second.path);
            songDb.Execute(
                "UPDATE song SET title = ?, artist = ?, folder = ?, parent = ?, mode = ?, judge = ?, date = ?, txt = ?, "
                + "stagefile = ?, favorite = ?, tag = ?, adddate = ?, level = ?, difficulty = ?, maxbpm = ?, minbpm = ?, "
                + "bga = ?, exlevel = ?, longnote = ?, random = ?, karinotes = ? WHERE path = ?;",
                "keep-title",
                "keep-artist",
                "keep-folder",
                "keep-parent",
                11,
                7,
                123456,
                1,
                "keep-stagefile",
                9,
                "keep-tag",
                654321,
                1,
                1,
                1,
                1,
                9,
                9,
                9,
                9,
                9,
                first.path);
            var chartInfo = new BeMusicSeeker.Models.ChartDetails
            {
                md5 = md5,
                level = 12,
                difficulty = -3,
                maxbpm = 180.9,
                minbpm = 90.1,
                mode = 14,
                judge = 99,
                bga = 1,
                exlevel = null,
                feature = 4 | 8,
                notes = 4321
            };
            var firstProjection =
                Lr2ChartInfoSongProjection.Create(first.path, first.hash, chartInfo);
            var secondProjection =
                Lr2ChartInfoSongProjection.Create(second.path, second.hash.ToUpperInvariant(), chartInfo);
            var missingProjection =
                Lr2ChartInfoSongProjection.Create(@"D:\BMS\Pack\missing.bms", md5, chartInfo);
            var caseOnlyPathMismatchProjection =
                Lr2ChartInfoSongProjection.Create(first.path.ToUpperInvariant(), md5, chartInfo);

            Lr2ChartInfoSongProjectionWriteResult result = Lr2SongDbWriter.UpdateChartInfoSongProjections(
                songDb,
                [firstProjection, secondProjection, missingProjection, caseOnlyPathMismatchProjection]);

            Assert.AreEqual(4, result.RequestedCount);
            Assert.AreEqual(2, result.MatchedCount);
            Assert.AreEqual(2, result.ChangedCount);
            Assert.AreEqual(2, result.MissingCount);
            CollectionAssert.AreEquivalent(
                new[] { missingProjection.Identity, caseOnlyPathMismatchProjection.Identity },
                result.MissingProjections.Select(projection => projection.Identity).ToArray());
            Assert.AreEqual(2, songDb.Table<LR2SongDB.song>().Count());
            foreach (LR2SongDB.song row in songDb.Table<LR2SongDB.song>())
            {
                Assert.AreEqual(12, row.level);
                Assert.AreEqual(2, row.difficulty);
                Assert.AreEqual(180, row.maxbpm);
                Assert.AreEqual(90, row.minbpm);
                Assert.AreEqual(1, row.bga);
                Assert.AreEqual(0, row.exlevel);
                Assert.AreEqual(1, row.longnote);
                Assert.AreEqual(1, row.random);
                Assert.AreEqual(4321, row.karinotes);
            }
            LR2SongDB.song preserved = songDb.Table<LR2SongDB.song>().Single(row => row.path == first.path);
            Assert.AreEqual("keep-title", preserved.title);
            Assert.AreEqual("keep-artist", preserved.artist);
            Assert.AreEqual("keep-folder", preserved.folder);
            Assert.AreEqual("keep-parent", preserved.parent);
            Assert.AreEqual(11, preserved.mode);
            Assert.AreEqual(7, preserved.judge);
            Assert.AreEqual(123456, preserved.date);
            Assert.AreEqual(1, preserved.txt);
            Assert.AreEqual("keep-stagefile", preserved.stagefile);
            Assert.AreEqual(9, preserved.favorite);
            Assert.AreEqual("keep-tag", preserved.tag);
            Assert.AreEqual(654321, preserved.adddate);

            Lr2ChartInfoSongProjectionWriteResult unchanged =
                Lr2SongDbWriter.UpdateChartInfoSongProjections(
                    songDb,
                    [firstProjection, secondProjection, missingProjection, caseOnlyPathMismatchProjection]);
            Assert.AreEqual(2, unchanged.MatchedCount);
            Assert.AreEqual(0, unchanged.ChangedCount);
            Assert.AreEqual(2, unchanged.MissingCount);
            Assert.AreEqual(2, songDb.Table<LR2SongDB.song>().Count());
        });
    }

    /// <summary>
    /// songテーブル不在は未照合として報告してテーブルを作らず、解析補完が保存領域を新設する誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpdateChartInfoSongProjections_MissingSongTableReportsMissingWithoutCreatingTable()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var projection = Lr2ChartInfoSongProjection.Create(
                @"D:\BMS\Pack\missing.bms",
                md5,
                new BeMusicSeeker.Models.ChartDetails { md5 = md5, level = 12 });

            Lr2ChartInfoSongProjectionWriteResult result =
                Lr2SongDbWriter.UpdateChartInfoSongProjections(songDb, [projection]);

            Assert.AreEqual(1, result.RequestedCount);
            Assert.AreEqual(0, result.MatchedCount);
            Assert.AreEqual(0, result.ChangedCount);
            Assert.AreEqual(1, result.MissingCount);
            Assert.AreEqual(
                0,
                songDb.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'song';"));
        });
    }

    /// <summary>
    /// 大文字小文字だけ異なるパスを別行として保存し、既存利用者設定とメンテナンス行を新規行へ流用しない。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongs_TreatsCaseOnlyPathAsDistinct()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            songDb.CreateTable<LR2SongDBExtended.maintenance>();
            TestableBmsFile existing = CreateSong(@"D:\BMS\Pack\CHART.BMS", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            existing.SetUserColumns(favoriteValue: 7, addDateValue: 12345, tagValue: "keep");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, existing));
            songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = existing.path, hash = existing.hash, encoding = "shift_jis" }, typeof(LR2SongDBExtended.maintenance));
            TestableBmsFile updated = CreateSong(@"D:\BMS\Pack\chart.bms", existing.hash, "Title");

            int changed = Lr2SongDbWriter.UpsertGeneratedSongs(songDb, [updated]);

            Assert.AreEqual(1, changed);
            Assert.AreEqual(2, songDb.Table<LR2SongDB.song>().Count());
            LR2SongDB.song existingRow = songDb.Find<LR2SongDB.song>(existing.path);
            Assert.IsNotNull(existingRow);
            Assert.AreEqual(7, existingRow.favorite);
            Assert.AreEqual(12345, existingRow.adddate);
            Assert.AreEqual("keep", existingRow.tag);
            LR2SongDB.song updatedRow = songDb.Find<LR2SongDB.song>(updated.path);
            Assert.IsNotNull(updatedRow);
            Assert.AreEqual(updated.path, updatedRow.path);
            Assert.IsFalse(updatedRow.favorite.HasValue);
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", existing.path));
            Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", updated.path));
        });
    }

    /// <summary>
    /// 譜面MD5変更で新しいSHA対応を保存し参照のない旧対応だけを消して、孤立したダイジェストの残留を検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongs_UpsertsDigestRowsAndRemovesOrphanedPreviousHash()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            string oldHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string newHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string oldSha = Sha('1');
            string newSha = Sha('2');
            TestableBmsFile existing = CreateSong(@"D:\BMS\Pack\existing.bms", oldHash, "Old");
            existing.SetSha256(oldSha);
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, existing));

            TestableBmsFile updated = CreateSong(existing.path, newHash, "New");
            updated.SetSha256(newSha);
            int changed = Lr2SongDbWriter.UpsertGeneratedSongs(songDb, [updated]);

            Assert.AreEqual(1, changed);
            Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ?;", oldHash));
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", newHash, newSha));
        });
    }

    /// <summary>
    /// BMSのMD5変更後もbmsonが参照する旧SHA対応を残し、別形式の共用ダイジェストを失う誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongs_KeepsPreviousDigestWhenBmsonStillReferencesHash()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            string oldHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string newHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string oldSha = Sha('1');
            string newSha = Sha('2');
            TestableBmsFile existing = CreateSong(@"D:\BMS\Pack\existing.bms", oldHash, "Old");
            existing.SetSha256(oldSha);
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, existing));
            songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = @"D:\BMS\Pack\chart.bmson",
                Folder = @"D:\BMS\Pack",
                RawTitle = "Bmson",
                Md5 = oldHash,
                Sha256 = oldSha
            }), typeof(LR2SongDBExtended.bmson_song));

            TestableBmsFile updated = CreateSong(existing.path, newHash, "New");
            updated.SetSha256(newSha);
            int changed = Lr2SongDbWriter.UpsertGeneratedSongs(songDb, [updated]);

            Assert.AreEqual(1, changed);
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", oldHash, oldSha));
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", newHash, newSha));
        });
    }

    /// <summary>
    /// LR2同期の既存更新・新規追加でタイトルを反映し、既存利用者設定を保持して新規追加時刻を付ける。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_PreservesUserColumnsWithoutExistingRowRead()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile existing = CreateSong(@"D:\BMS\Pack\existing.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Old");
            existing.SetUserColumns(favoriteValue: 7, addDateValue: 12345, tagValue: "keep");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, existing));
            TestableBmsFile updated = CreateSong(existing.path, existing.hash, "New");
            TestableBmsFile added = CreateSong(@"D:\BMS\Pack\added.bms", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Added");

            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [updated, added]);

            Assert.AreEqual(2, written);
            Assert.AreEqual(2, songDb.Table<LR2SongDB.song>().Count());
            LR2SongDB.song updatedRow = songDb.Table<LR2SongDB.song>().Single(row => row.path == existing.path);
            Assert.AreEqual("New", updatedRow.title);
            Assert.AreEqual(7, updatedRow.favorite);
            Assert.AreEqual(12345, updatedRow.adddate);
            Assert.AreEqual("keep", updatedRow.tag);
            LR2SongDB.song addedRow = songDb.Table<LR2SongDB.song>().Single(row => row.path == added.path);
            Assert.AreEqual("Added", addedRow.title);
            Assert.IsTrue(addedRow.adddate.HasValue && addedRow.adddate > 0);
        });
    }

    /// <summary>
    /// LR2同期で大文字小文字だけ異なるパスを別行として保存し、既存設定とメンテナンスの同一性を保持する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_TreatsCaseOnlyPathAsDistinct()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            songDb.CreateTable<LR2SongDBExtended.maintenance>();
            TestableBmsFile existing = CreateSong(@"D:\BMS\Pack\CHART.BMS", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            existing.SetUserColumns(favoriteValue: 7, addDateValue: 12345, tagValue: "keep");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, existing));
            songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = existing.path, hash = existing.hash, encoding = "shift_jis" }, typeof(LR2SongDBExtended.maintenance));
            TestableBmsFile updated = CreateSong(@"D:\BMS\Pack\chart.bms", existing.hash, "Title");

            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [updated]);

            Assert.AreEqual(1, written);
            Assert.AreEqual(2, songDb.Table<LR2SongDB.song>().Count());
            LR2SongDB.song existingRow = songDb.Find<LR2SongDB.song>(existing.path);
            Assert.IsNotNull(existingRow);
            Assert.AreEqual(7, existingRow.favorite);
            Assert.AreEqual(12345, existingRow.adddate);
            Assert.AreEqual("keep", existingRow.tag);
            LR2SongDB.song updatedRow = songDb.Find<LR2SongDB.song>(updated.path);
            Assert.IsNotNull(updatedRow);
            Assert.AreEqual(updated.path, updatedRow.path);
            Assert.AreEqual("Title", updatedRow.title);
            Assert.IsFalse(updatedRow.favorite.HasValue);
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", existing.path));
            Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", updated.path));
        });
    }

    /// <summary>
    /// LR2同期で大文字小文字が違う二行をそれぞれのタイトルへ更新し、実キーの混同や制約違反を検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_UpdatesCaseOnlyVariantsWithoutConstraint()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile lower = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Old Lower");
            TestableBmsFile upper = CreateSong(@"D:\BMS\Pack\CHART.BMS", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Old Upper");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, lower));
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, upper));
            TestableBmsFile lowerUpdated = CreateSong(lower.path, lower.hash, "New Lower");
            TestableBmsFile upperUpdated = CreateSong(upper.path, upper.hash, "New Upper");

            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(
                songDb,
                [lowerUpdated, upperUpdated]);

            Assert.AreEqual(2, written);
            Assert.AreEqual(2, songDb.Table<LR2SongDB.song>().Count());
            Assert.AreEqual("New Lower", songDb.Find<LR2SongDB.song>(lower.path).title);
            Assert.AreEqual("New Upper", songDb.Find<LR2SongDB.song>(upper.path).title);
        });
    }

    /// <summary>
    /// LR2同期の同じ生成列は更新なしとして返し、songとSHA対応を保持して不要な更新を検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_SkipsUnchangedGeneratedColumns()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\unchanged.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            file.SetSha256(Sha('1'));
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            TestableBmsFile unchanged = CreateSong(file.path, file.hash, "Title");
            unchanged.CopyGeneratedHashesFrom(file);
            unchanged.SetSha256(file.sha256);
            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [unchanged]);

            Assert.AreEqual(0, written);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", file.hash, file.sha256));
        });
    }

    /// <summary>
    /// LR2同期で同じSHA対応を再反映しても監査トリガーへ書込みを発生させず、無変更時の不要更新を検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_SkipsUnchangedDigestMapRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\unchanged-digest-write.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            file.SetSha256(Sha('1'));
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            songDb.Execute("CREATE TABLE digest_write_audit (op TEXT);");
            songDb.Execute("CREATE TRIGGER digest_write_audit_insert AFTER INSERT ON chart_digest_map BEGIN INSERT INTO digest_write_audit (op) VALUES ('insert'); END;");
            songDb.Execute("CREATE TRIGGER digest_write_audit_update AFTER UPDATE ON chart_digest_map BEGIN INSERT INTO digest_write_audit (op) VALUES ('update'); END;");

            TestableBmsFile unchanged = CreateSong(file.path, file.hash, "Title");
            unchanged.CopyGeneratedHashesFrom(file);
            unchanged.SetSha256(file.sha256);
            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [unchanged]);

            Assert.AreEqual(0, written);
            Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM digest_write_audit;"));
        });
    }

    /// <summary>
    /// songに変更がなくても欠落したSHA対応を補修し、生成列比較で必要なダイジェスト修復を省く誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_RepairsDigestForUnchangedGeneratedColumns()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\unchanged-digest.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            file.SetSha256(Sha('1'));
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            songDb.Execute("DELETE FROM chart_digest_map WHERE md5 = ?;", file.hash);

            TestableBmsFile unchanged = CreateSong(file.path, file.hash, "Title");
            unchanged.CopyGeneratedHashesFrom(file);
            unchanged.SetSha256(file.sha256);
            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [unchanged]);

            Assert.AreEqual(0, written);
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", file.hash, file.sha256));
        });
    }

    /// <summary>
    /// song無変更で古いSHA対応だけを一度更新し、不要な挿入とダイジェスト修復漏れを検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_UpdatesChangedDigestWithoutSongWrite()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\changed-digest-only.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            file.SetSha256(Sha('1'));
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            string staleSha = Sha('2');
            songDb.Execute("UPDATE chart_digest_map SET sha256 = ? WHERE md5 = ?;", staleSha, file.hash);
            songDb.Execute("CREATE TABLE digest_write_audit (op TEXT);");
            songDb.Execute("CREATE TRIGGER digest_write_audit_insert AFTER INSERT ON chart_digest_map BEGIN INSERT INTO digest_write_audit (op) VALUES ('insert'); END;");
            songDb.Execute("CREATE TRIGGER digest_write_audit_update AFTER UPDATE ON chart_digest_map BEGIN INSERT INTO digest_write_audit (op) VALUES ('update'); END;");

            TestableBmsFile unchanged = CreateSong(file.path, file.hash, "Title");
            unchanged.CopyGeneratedHashesFrom(file);
            unchanged.SetSha256(file.sha256);
            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [unchanged]);

            Assert.AreEqual(0, written);
            Assert.AreEqual(file.sha256, songDb.ExecuteScalar<string>("SELECT sha256 FROM chart_digest_map WHERE md5 = ?;", file.hash));
            Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM digest_write_audit WHERE op = 'insert';"));
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM digest_write_audit WHERE op = 'update';"));
        });
    }

    /// <summary>
    /// LR2同期で保存済み大文字MD5を小文字へ更新し、既存行のハッシュ正規化漏れを検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_NormalizesExistingUppercaseHash()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\uppercase.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            songDb.Execute(
                "UPDATE song SET hash = ? WHERE path = ?;",
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                file.path);

            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [file]);

            Assert.AreEqual(1, written);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", songDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", file.path));
        });
    }

    /// <summary>
    /// LR2同期のMD5変更で新しいSHA対応を保存し、参照のない旧対応だけを削除する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_UpsertsDigestRowsAndRemovesOrphanedPreviousHash()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            string oldHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string newHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string oldSha = Sha('1');
            string newSha = Sha('2');
            TestableBmsFile existing = CreateSong(@"D:\BMS\Pack\existing.bms", oldHash, "Old");
            existing.SetSha256(oldSha);
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, existing));

            TestableBmsFile updated = CreateSong(existing.path, newHash, "New");
            updated.SetSha256(newSha);
            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [updated]);

            Assert.AreEqual(1, written);
            Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ?;", oldHash));
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", newHash, newSha));
        });
    }

    /// <summary>
    /// LR2同期のMD5変更でもbmsonが参照する旧SHA対応を残し、別形式の共用保存行を削除する誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_KeepsPreviousDigestWhenBmsonStillReferencesHash()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            string oldHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string newHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string oldSha = Sha('1');
            string newSha = Sha('2');
            TestableBmsFile existing = CreateSong(@"D:\BMS\Pack\existing.bms", oldHash, "Old");
            existing.SetSha256(oldSha);
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, existing));
            songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsonRow(ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = @"D:\BMS\Pack\chart.bmson",
                Folder = @"D:\BMS\Pack",
                RawTitle = "Bmson",
                Md5 = oldHash,
                Sha256 = oldSha
            }), typeof(LR2SongDBExtended.bmson_song));

            TestableBmsFile updated = CreateSong(existing.path, newHash, "New");
            updated.SetSha256(newSha);
            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [updated]);

            Assert.AreEqual(1, written);
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", oldHash, oldSha));
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", newHash, newSha));
        });
    }

    /// <summary>
    /// LR2同期の未取得txtは既存の値を保持して変更なしと扱い、情報欠落を保存値消去へ読み替える誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_TreatsNullTextFlagAsUnchanged()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            file.SetTextFlagForTest(1);
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            TestableBmsFile withoutTextFlag = CreateSong(file.path, file.hash, "Title");
            withoutTextFlag.CopyGeneratedHashesFrom(file);
            withoutTextFlag.SetTextFlagForTest(null);
            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [withoutTextFlag]);

            Assert.AreEqual(0, written);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.song>().Single().txt);
        });
    }

    /// <summary>
    /// LR2同期の更新入力に追加時刻がなくても既存時刻と新タイトルを保存し、更新時の履歴消失を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSongsForLr2SongDbSync_PreservesExistingAddDate()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            PrepareLr2SongDbSyncSongWriterSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Old");
            file.adddate = 98765;
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            TestableBmsFile updated = CreateSong(file.path, file.hash, "New");
            updated.adddate = null;
            int written = Lr2SongDbWriter.UpsertGeneratedSongsForLr2SongDbSync(songDb, [updated]);

            Assert.AreEqual(1, written);
            LR2SongDB.song row = songDb.Table<LR2SongDB.song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.AreEqual(98765, row.adddate);
        });
    }

    /// <summary>
    /// 単行反映の未取得txtは既存の値を保持して変更なしと扱い、未取得入力で保存値を消す誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSong_TreatsNullTextFlagAsUnchanged()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            file.SetTextFlagForTest(1);
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            TestableBmsFile withoutTextFlag = CreateSong(file.path, file.hash, "Title");
            withoutTextFlag.CopyGeneratedHashesFrom(file);
            withoutTextFlag.SetTextFlagForTest(null);
            Assert.IsFalse(Lr2SongDbWriter.UpsertGeneratedSong(songDb, withoutTextFlag));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.song>().Single().txt);
        });
    }

    /// <summary>
    /// 単行反映で保存済み大文字MD5を小文字へ更新し、保存キーの正規化漏れを検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSong_NormalizesExistingUppercaseHash()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Pack\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            songDb.Execute(
                "UPDATE song SET hash = ? WHERE path = ?;",
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                file.path);

            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", songDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", file.path));
        });
    }

    /// <summary>
    /// 実ファイルのない新規行でも未取得更新時刻を保持し追加時刻だけを付けて、勝手なファイル時刻取得を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSong_DoesNotStatMissingDateAndFillsNewAddDate()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Missing\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Title");
            file.date = null;
            file.adddate = null;

            int before = Lr2SongRowEnricher.ToLr2UnixSeconds(DateTime.UtcNow.AddSeconds(-1));
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
            int after = Lr2SongRowEnricher.ToLr2UnixSeconds(DateTime.UtcNow.AddSeconds(1));

            LR2SongDB.song row = songDb.Table<LR2SongDB.song>().Single();
            Assert.IsFalse(row.date.HasValue);
            Assert.IsTrue(row.adddate >= before && row.adddate <= after, "新規行には現在の追加時刻が保存される。");
        });
    }

    /// <summary>
    /// 実ファイルのない既存行を更新し、未取得更新時刻と元追加時刻を保持して履歴の上書きを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存行を反映前後で観測し、SQL更新・削除と利用者データ保持の接続を確認する。</remarks>
    [TestMethod]
    public void UpsertGeneratedSong_DoesNotStatMissingDateButPreservesExistingAddDate()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
            TestableBmsFile file = CreateSong(@"D:\BMS\Missing\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Old");
            file.adddate = 98765;
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));

            TestableBmsFile updated = CreateSong(file.path, file.hash, "New");
            updated.date = null;
            updated.adddate = null;
            Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, updated));

            LR2SongDB.song row = songDb.Table<LR2SongDB.song>().Single();
            Assert.AreEqual("New", row.title);
            Assert.IsFalse(row.date.HasValue);
            Assert.AreEqual(98765, row.adddate);
        });
    }

    private static TestableBmsFile CreateSong(string path, string hash, string title)
    {
        var file = new TestableBmsFile
        {
            path = path,
            type = 0,
            level = 12,
            difficulty = 3,
            date = 123456,
            exlevel = 0
        };
        file.SetHash(hash);
        file.title = title;
        file.SetArtistForTest("Artist");
        file.SetTextFlagForTest(0);
        return file;
    }

    private static string Sha(char value)
    {
        return new string(value, 64);
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2SongDbWriterTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static void PrepareLr2SongDbSyncSongWriterSchema(LR2SongDBExtended songDb)
    {
        BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
        BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
    }

    private sealed class TestableBmsFile : LR2SongDB.song
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            sha256 = value;
        }

        public void SetArtistForTest(string value)
        {
            artist = value;
        }

        public void SetTextFlagForTest(int? value)
        {
            txt = value;
        }

        public void SetUserColumns(int? favoriteValue, int? addDateValue, string tagValue)
        {
            favorite = favoriteValue;
            adddate = addDateValue;
            tag = tagValue;
        }

        public void CopyGeneratedHashesFrom(LR2SongDB.song source)
        {
            folder = source.folder;
            parent = source.parent;
        }
    }
}
