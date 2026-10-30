using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <summary>情報源別の履歴から基準日のスコアを復元し、欠損と不正を隠さないことを検証する。</summary>
/// <remarks>境界日時・旧値は復元器へ直接渡す。読取り契約のケースは専用SQLiteを使い、実トリガー・索引の状態とDBバイト列の不変を観測する。表示と要求世代はセッション・ビューアへ分担する。</remarks>
[TestClass]
public sealed class PlaylistLampHistoricalScoreSnapshotTests
{
    /// <summary>復元consumerへ渡す取得済み入力を、通常成績と同じ実モデル取得で準備します。UI接続の代替ではありません。</summary>
    private static PlaylistLampHistoricalScoreSourceContext AcquireLr2HistoryContext(string scoreDbPath)
    {
        ScoreTableLoadResult acquired = new BmsLibraryInitializationService().LoadScoreTable(
            new BmsLibraryDbGateway(Path.Combine(Path.GetDirectoryName(scoreDbPath) ?? string.Empty, "song.db"), scoreDbPath), new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true });
        return PlaylistLampHistoricalScoreSourceContext.FromSnapshot(new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = acquired.ActiveScoreSource,
            SourceGeneration = 1,
            PlayHistorySnapshot = acquired.PlayHistorySnapshot
        });
    }

    private static readonly DateTime TestToday = new(2026, 8, 29);

    /// <summary>基準日翌日の午前0時を含めて巻き戻し、同時刻は行ID降順で旧値を復元する。境界行の除外や同時刻の逆処理で別スコアへ戻る誤りを検出する。</summary>
    [TestMethod]
    public void Cutoff_includes_local_midnight_and_same_timestamp_uses_source_id_descending()
    {
        const string hash = "chart-a";
        DateTime selectedDate = new(2026, 8, 28);
        DateTime cutoff = selectedDate.AddDays(1);
        PlaylistLampScoreSnapshot current = CurrentLr2(hash, ClearType.EASY, 160, 100);
        PlaylistLampHistoricalScoreChange[] changes = new[]
        {
            Change(ActiveScoreSource.Lr2, hash, 10, selectedDate.AddHours(12), 4, 8, 100, 100, timeZone: TimeZoneInfo.Utc),
            Change(ActiveScoreSource.Lr2, hash, 11, cutoff, 3, 0, 120, 100, timeZone: TimeZoneInfo.Utc),
            Change(ActiveScoreSource.Lr2, hash, 12, cutoff, 1, 0, 0, 100, timeZone: TimeZoneInfo.Utc),
            Change(ActiveScoreSource.Lr2, hash, 13, cutoff.AddHours(1), 4, 8, 140, 100, timeZone: TimeZoneInfo.Utc)
        };

        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            current,
            ActiveScoreSource.Lr2,
            selectedDate,
            changes,
            TestToday,
            TimeZoneInfo.Utc);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, result.Status);
        Assert.IsTrue(result.ScoreSnapshot.ScoresByHash.TryGetValue(hash, out PlaylistLampScore? score));
        Assert.AreEqual(ClearType.CLEAR, score!.Clear);
        Assert.AreEqual(120, score!.ExScore);
    }

    /// <summary>丸日欠落の前日と当日は同じ翌日境界で実LR2履歴を巻き戻し、境界行・同時刻ID順・指定時刻帯の日付分類を保持する。</summary>
    /// <remarks>UTC固定値の境界前後を実DBへ保存し、120という復元値で境界除外・順序逆転・現在値/空/利用不可への代用を区別する。読取り前後のDBバイト列も比較する。</remarks>
    [TestMethod]
    public void Lr2_reader_skipped_day_shares_cutoff_with_previous_day_without_writing_database()
    {
        string directory = CreateTemporaryDirectory();
        string scoreDbPath = Path.Combine(directory, "score.db");
        try
        {
            CreateLr2ScoreDb(scoreDbPath);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed,
                new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true).Status);
            const string hash = "skipped-day-chart";
            long boundary = DateTimeOffset.Parse("2024-06-01T12:00:00Z", CultureInfo.InvariantCulture).ToUnixTimeSeconds();
            using (SQLiteConnection db = new(scoreDbPath))
            {
                db.RunInTransaction(() =>
                {
                    InsertLr2History(db, 1, hash, boundary - 1, 1, 3, 0, 100, 100);
                    InsertLr2History(db, 2, hash, boundary, 1, 3, 0, 120, 100);
                    InsertLr2History(db, 3, hash, boundary, 1, 3, 0, 140, 100);
                    InsertLr2History(db, 4, hash, boundary + 1, 1, 3, 0, 160, 100);
                });
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);
            TimeZoneInfo zone = LocalDateBoundaryTestTimeZones.Create(4);
            var reader = new PlaylistLampHistoricalScoreSnapshotReader();
            PlaylistLampScoreSnapshot current = CurrentLr2(hash, ClearType.CLEAR, 180, 100);
            PlaylistLampHistoricalScoreSourceContext context = AcquireLr2HistoryContext(scoreDbPath);

            // 5/31の翌日6/1は丸日欠落し、6/1の翌日6/2と同じ境界になる。
            foreach (DateTime selected in new[] { new DateTime(2024, 5, 31), new DateTime(2024, 6, 1) })
            {
                PlaylistLampHistoricalScoreSnapshotResult result = reader.Read(context, current, selected, timeZone: zone);
                Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, result.Status);
                Assert.AreEqual(selected, result.SelectedLocalDate);
                Assert.AreEqual(new DateTime(2024, 5, 31), result.DateRange.EarliestLocalDate);
                Assert.AreEqual(1, result.ScoreSnapshot.ScoresByHash.Count);
                Assert.AreEqual(120, result.ScoreSnapshot.ScoresByHash[hash].ExScore);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
            }
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    /// <summary>基準日より後の履歴がなければ現在スコアをそのまま維持する。履歴行の存在だけで巻き戻したり未プレイへ置換する誤りを検出する。</summary>
    [TestMethod]
    public void No_future_history_keeps_exact_current_score()
    {
        const string hash = "chart-b";
        DateTime selectedDate = new(2026, 8, 28);
        var currentScore = PlaylistLampScore.FromExScore(
            hash,
            null,
            ClearType.HARD,
            170,
            100);
        PlaylistLampScoreSnapshot current = new(
            ActiveScoreSource.Lr2,
            ScoreTableLoadStatus.Loaded,
            3,
            4,
            null,
            new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
            {
                [hash] = currentScore
            });
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            current,
            ActiveScoreSource.Lr2,
            selectedDate,
            [Change(ActiveScoreSource.Lr2, hash, 2, selectedDate.AddHours(8), 4, 8, 100, 100)],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, result.Status);
        Assert.AreSame(currentScore, result.ScoreSnapshot.ScoresByHash[hash]);
    }

    /// <summary>LR2の旧プレイ回数は存在するが旧値4項目がすべて欠損した履歴を利用不可にする。欠損を現在値や未プレイで補う誤りを検出する。</summary>
    [TestMethod]
    public void Lr2_nonnull_old_playcount_with_all_null_tuple_degrades_the_historical_snapshot()
    {
        const string hash = "chart-c";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampScoreSnapshot current = CurrentLr2(hash, ClearType.CLEAR, 180, 100);
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            current,
            ActiveScoreSource.Lr2,
            selectedDate,
            [
                Change(ActiveScoreSource.Lr2, hash, 1, selectedDate, 3, 0, 100, 100),
                Change(ActiveScoreSource.Lr2, hash, 2, selectedDate.AddDays(1), null, null, null, null)
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, result.Status);
        Assert.IsFalse(result.ScoreSnapshot.IsScoreDataAvailable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    /// <summary>LR2の旧プレイ回数NULLは他の旧値が存在しても初回プレイ前と解釈し、旧スコアを除く。NULLと旧値欠損を混同する誤りを検出する。</summary>
    [TestMethod]
    public void Lr2_null_old_playcount_is_the_explicit_first_play_sentinel()
    {
        const string hash = "chart-sentinel";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            CurrentLr2(hash, ClearType.CLEAR, 180, 100),
            ActiveScoreSource.Lr2,
            selectedDate,
            [
                Change(ActiveScoreSource.Lr2, hash, 0, selectedDate, 3, 0, 100, 100),
                Change(
                    ActiveScoreSource.Lr2,
                    hash,
                    1,
                    selectedDate.AddDays(1),
                    oldClear: 3,
                    oldOperationHistory: 0,
                    oldExScore: 100,
                    oldTotalNotes: 100,
                    oldPlayCount: null)
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, result.Status);
        Assert.IsFalse(result.ScoreSnapshot.ScoresByHash.ContainsKey(hash));
    }

    /// <summary>beatorajaの旧クリア・旧EXスコアを現在ノーツ数に合わせて復元し、DJランクを算出する。現在EX値の残存や情報源別の旧値対応漏れを検出する。</summary>
    [TestMethod]
    public void Beatoraja_rollback_uses_oldclear_and_oldscore_with_current_notes()
    {
        const string sha256 = "chart-d";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampScoreSnapshot current = new(
            ActiveScoreSource.Beatoraja,
            ScoreTableLoadStatus.Loaded,
            1,
            1,
            null,
            scoresBySha256: new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
            {
                [sha256] = PlaylistLampScore.FromExScore(null, sha256, ClearType.HARD, 190, 100)
            });
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            current,
            ActiveScoreSource.Beatoraja,
            selectedDate,
            [
                Change(ActiveScoreSource.Beatoraja, sha256, 1, selectedDate, 4, null, 100, 100),
                Change(ActiveScoreSource.Beatoraja, sha256, 2, selectedDate.AddDays(1), 4, null, 150, 100)
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, result.Status);
        PlaylistLampScore score = result.ScoreSnapshot.ScoresBySha256[sha256];
        Assert.AreEqual(150, score.ExScore);
        Assert.AreEqual(RankType.A, score.Rank);
    }

    /// <summary>保存値上限のMAXと満点EXスコアを有効な過去成績として復元する。上限の誤拒否やMAXを別ランプへ変換する誤りを検出する。</summary>
    [TestMethod]
    public void Beatoraja_oldclear_max_restores_the_valid_upper_boundary()
    {
        const string sha256 = "chart-max-clear";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampScoreSnapshot current = new(
            ActiveScoreSource.Beatoraja,
            ScoreTableLoadStatus.Loaded,
            1,
            1,
            null,
            scoresBySha256: new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
            {
                [sha256] = PlaylistLampScore.FromExScore(null, sha256, ClearType.HARD, 190, 100)
            });
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            current,
            ActiveScoreSource.Beatoraja,
            selectedDate,
            [
                Change(ActiveScoreSource.Beatoraja, sha256, 1, selectedDate, 4, null, 100, 100),
                Change(ActiveScoreSource.Beatoraja, sha256, 2, selectedDate.AddDays(1), (int)ClearType.MAX, null, 200, 100)
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, result.Status);
        PlaylistLampScore score = result.ScoreSnapshot.ScoresBySha256[sha256];
        Assert.AreEqual(ClearType.MAX, score.Clear);
        Assert.AreEqual(200, score.ExScore);
        Assert.AreEqual(RankType.MAX, score.Rank);
    }

    /// <summary>旧クリアNO_PLAY・EXスコア0を有効な履歴スコアとして復元する。未プレイという保存値を不正履歴や現在成績へ置換する誤りを検出する。</summary>
    [TestMethod]
    public void Beatoraja_oldclear_no_play_projects_to_np()
    {
        const string sha256 = "chart-no-play";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampScoreSnapshot current = new(
            ActiveScoreSource.Beatoraja,
            ScoreTableLoadStatus.Loaded,
            1,
            1,
            null,
            scoresBySha256: new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
            {
                [sha256] = PlaylistLampScore.FromExScore(null, sha256, ClearType.HARD, 190, 100)
            });
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            current,
            ActiveScoreSource.Beatoraja,
            selectedDate,
            [
                Change(ActiveScoreSource.Beatoraja, sha256, 0, selectedDate, 4, null, 100, 100),
                Change(
                    ActiveScoreSource.Beatoraja,
                    sha256,
                    1,
                    selectedDate.AddDays(1),
                    (int)ClearType.NO_PLAY,
                    null,
                    0,
                    100)
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, result.Status);
        Assert.IsTrue(result.ScoreSnapshot.ScoresBySha256.TryGetValue(sha256, out PlaylistLampScore? score));
        Assert.AreEqual(ClearType.NO_PLAY, score!.Clear);
        Assert.AreEqual(RankType.F, score!.Rank);
    }

    /// <summary>保存範囲の直前・直後の旧クリア値は診断付きで履歴を利用不可にする。不正整数のカテゴリへの転用を検出する。</summary>
    [DataTestMethod]
    // 保存範囲0〜10の下側と上側を個別に確認する。
    [DataRow(-1)]
    [DataRow(11)]
    public void Beatoraja_invalid_oldclear_outside_storage_range_degrades_the_historical_snapshot(int invalidOldClear)
    {
        const string sha256 = "chart-invalid-clear";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampScoreSnapshot current = new(
            ActiveScoreSource.Beatoraja,
            ScoreTableLoadStatus.Loaded,
            1,
            1,
            null,
            scoresBySha256: new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
            {
                [sha256] = PlaylistLampScore.FromExScore(null, sha256, ClearType.HARD, 190, 100)
            });
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            current,
            ActiveScoreSource.Beatoraja,
            selectedDate,
            [
                Change(ActiveScoreSource.Beatoraja, sha256, 1, selectedDate, 4, null, 100, 100),
                Change(ActiveScoreSource.Beatoraja, sha256, 2, selectedDate.AddDays(1), invalidOldClear, null, 150, 100)
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, result.Status);
        Assert.IsFalse(result.ScoreSnapshot.IsScoreDataAvailable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    /// <summary>全件読取りで確定・未確定両方のLR2履歴を新しい順に取り込み、旧プレイ回数NULLとDB内容を維持する。未確定の脱落や参照時の書込みを検出する。</summary>
    [TestMethod]
    public void Lr2_reader_includes_unfinalized_rows_without_writing_the_database()
    {
        string directory = CreateTemporaryDirectory();
        string scoreDbPath = Path.Combine(directory, "score.db");
        try
        {
            CreateLr2ScoreDb(scoreDbPath);
            Assert.AreEqual(
                Lr2PlayHistorySchemaStatus.Installed,
                new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true).Status);
            using (SQLiteConnection db = new(scoreDbPath))
            {
                db.Execute(
                    "INSERT INTO bms_lr2_play_history (hash, played_at, finalized, score_write_type, new_playcount, playcount_delta) VALUES (?, ?, ?, ?, ?, ?);",
                    "lr2-finalized",
                    1_700_000_000L,
                    1,
                    "update",
                    1,
                    1);
                db.Execute(
                    "INSERT INTO bms_lr2_play_history (hash, played_at, finalized, score_write_type, new_playcount, playcount_delta) VALUES (?, ?, ?, ?, ?, ?);",
                    "lr2-unfinalized",
                    1_700_000_001L,
                    0,
                    "update",
                    1,
                    1);
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                FinalizationFilter = Lr2PlayHistoryFinalizationFilter.All,
                DisableLimit = true
            });

            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual(2, result.Rows.Count);
            Assert.AreEqual(0, result.Rows[0].finalized);
            Assert.AreEqual(1, result.Rows[1].finalized);
            Assert.IsNull(result.Rows[0].old_playcount);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    /// <summary>実モデル取得で履歴欠落・正常0件・playerだけ利用不可を区別し、ランプの最新/過去でも取得時診断を維持します。正常履歴はmode0だけを使い元DBを書き換えません。</summary>
    [DataTestMethod]
    [DataRow("missing")]
    [DataRow("empty")]
    [DataRow("rows-without-player")]
    public void Beatoraja_acquired_history_distinguishes_missing_empty_and_player_only_failure_without_writing(string history)
    {
        string directory = CreateTemporaryDirectory();
        string scoreDbPath = Path.Combine(directory, "score.db");
        string scoreLogDbPath = Path.Combine(directory, "scorelog.db");
        string late = new('a', 64), early = new('b', 64), course = new('c', 64);
        try
        {
            // 通常scoreは正常です。playerを用意しない条件は、scorelogの成否から独立します。
            using (SQLiteConnection db = new(scoreDbPath))
            {
                db.RunInTransaction(() =>
                {
                    db.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
                    foreach (string hash in new[] { late, early })
                    {
                        db.Execute("INSERT INTO score VALUES (?, 0, 6, 50, 0, 0, 0, 100, 10, 1, 1, 1)", hash);
                    }
                });
            }
            if (history != "missing") { CreateBeatorajaScoreLogDb(scoreLogDbPath); }
            if (history == "rows-without-player")
            {
                using SQLiteConnection db = new(scoreLogDbPath);
                db.RunInTransaction(() =>
                {
                    InsertBeatorajaScoreLog(db, late, 0, 1_700_000_002L);
                    InsertBeatorajaScoreLog(db, course, 1, 1_700_000_003L);
                    InsertBeatorajaScoreLog(db, early, 0, 1_700_000_001L);
                });
            }
            byte[] scoreBefore = File.ReadAllBytes(scoreDbPath);
            byte[]? scoreLogBefore = File.Exists(scoreLogDbPath) ? File.ReadAllBytes(scoreLogDbPath) : null;
            ScoreTableLoadResult acquired = new BmsLibraryInitializationService().LoadScoreTable(
                new BmsLibraryDbGateway(Path.Combine(directory, "song.db"), scoreDbPath),
                new BmsLibraryOptionsSnapshot { UseBeatorajaScoreDb = true, BeatorajaScoreDbPath = scoreDbPath });
            Assert.AreEqual(ScoreTableLoadStatus.Loaded, acquired.Status);
            Assert.AreEqual(2, acquired.BeatorajaScoresBySha256.Count);
            BeatorajaPlayHistoryReadResult raw = acquired.PlayHistorySnapshot.Beatoraja;
            Assert.IsFalse(raw.HasErrors);
            Assert.IsFalse(raw.PlayerSnapshotsAvailable);
            Assert.IsTrue(raw.Diagnostics.Any(diagnostic => diagnostic.Code == "play_history_beatoraja_player_aggregate_unreadable"));
            Assert.AreEqual(history == "missing" ? Lr2PlayHistorySchemaStatus.Unreadable : Lr2PlayHistorySchemaStatus.Installed, raw.SchemaStatus);
            Assert.AreEqual(history == "rows-without-player" ? 2 : 0, raw.Rows.Count);
            if (history == "rows-without-player")
            {
                Assert.IsTrue(raw.Rows.All(row => row.mode == 0));
                CollectionAssert.AreEqual(new[] { late, early }, raw.Rows.Select(row => row.sha256).ToArray());
            }
            var batch = new BMSLibrary.ScoreSnapshot
            {
                ActiveScoreSource = acquired.ActiveScoreSource,
                LoadStatus = acquired.Status,
                SourceGeneration = 1,
                ScoresBySha256 = acquired.BeatorajaScoresBySha256,
                PlayHistorySnapshot = acquired.PlayHistorySnapshot
            };
            var context = PlaylistLampHistoricalScoreSourceContext.FromSnapshot(batch);
            var current = PlaylistLampScoreSnapshot.FromBmsLibrarySnapshot(batch);
            var reader = new PlaylistLampHistoricalScoreSnapshotReader();
            PlaylistLampHistoricalScoreSnapshotResult latest = reader.Read(context, current, null, timeZone: TimeZoneInfo.Utc);
            PlaylistLampHistoricalScoreSnapshotResult past = reader.Read(context, current, new DateTime(2023, 11, 14), timeZone: TimeZoneInfo.Utc);
            Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Latest, latest.Status);
            Assert.IsTrue(latest.ScoreSnapshot.IsScoreDataAvailable);
            Assert.AreEqual(100, latest.ScoreSnapshot.ScoresBySha256[late].ExScore);
            if (history == "missing")
            {
                string missingDiagnostic = raw.Diagnostics.Single(diagnostic => diagnostic.Code == "play_history_beatoraja_scorelog_missing").Message;
                Assert.AreEqual(missingDiagnostic, latest.FailureMessage);
                Assert.AreEqual(missingDiagnostic, past.FailureMessage);
                Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, past.Status);
                Assert.IsFalse(past.ScoreSnapshot.IsScoreDataAvailable);
                Assert.IsFalse(latest.DateRange.HasHistoricalDates);
                Assert.IsFalse(File.Exists(scoreLogDbPath));
            }
            else
            {
                Assert.AreEqual(string.Empty, latest.FailureMessage);
                Assert.AreEqual(history == "rows-without-player", latest.DateRange.HasHistoricalDates);
                Assert.AreEqual(history == "rows-without-player" ? PlaylistLampHistoricalSnapshotStatus.Available : PlaylistLampHistoricalSnapshotStatus.Unavailable, past.Status);
                if (history == "rows-without-player")
                {
                    Assert.IsTrue(past.ScoreSnapshot.IsScoreDataAvailable);
                    Assert.AreEqual(string.Empty, past.FailureMessage);
                    Assert.AreEqual(100, past.ScoreSnapshot.ScoresBySha256[late].ExScore);
                }
                Assert.IsNotNull(scoreLogBefore);
                CollectionAssert.AreEqual(scoreLogBefore, File.ReadAllBytes(scoreLogDbPath));
            }
            CollectionAssert.AreEqual(scoreBefore, File.ReadAllBytes(scoreDbPath));
        }
        finally { DeleteTemporaryDirectory(directory); }
    }

    /// <summary>LR2の旧ランプ操作履歴だけが欠けた場合は復元不能として診断を返す。部分的旧値の暗黙補完を検出する。</summary>
    [TestMethod]
    public void Malformed_old_values_degrade_the_historical_snapshot()
    {
        const string hash = "chart-e";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            CurrentLr2(hash, ClearType.CLEAR, 180, 100),
            ActiveScoreSource.Lr2,
            selectedDate,
            [
                Change(ActiveScoreSource.Lr2, hash, 1, selectedDate, 3, 0, 100, 100),
                Change(ActiveScoreSource.Lr2, hash, 2, selectedDate.AddDays(1), 3, null, 100, 100)
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, result.Status);
        Assert.IsFalse(result.ScoreSnapshot.IsScoreDataAvailable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    /// <summary>負の旧プレイ回数は利用不可と診断を返す。初回プレイを示すNULLと不正な回数を混同する誤りを検出する。</summary>
    [TestMethod]
    public void Negative_lr2_old_playcount_degrades_the_historical_snapshot()
    {
        const string hash = "chart-negative-playcount";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            CurrentLr2(hash, ClearType.CLEAR, 180, 100),
            ActiveScoreSource.Lr2,
            selectedDate,
            [
                Change(ActiveScoreSource.Lr2, hash, 0, selectedDate, 3, 0, 100, 100),
                Change(ActiveScoreSource.Lr2, hash, 1, selectedDate.AddDays(1), 3, 0, 100, 100, oldPlayCount: -1)
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, result.Status);
        Assert.IsFalse(result.ScoreSnapshot.IsScoreDataAvailable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    /// <summary>1ノーツに対するint最大EXスコアの履歴は診断付きで利用不可とし、ノーツ数から許容できない成績を受理する誤りを検出する。</summary>
    [TestMethod]
    public void Old_exscore_above_double_total_notes_degrades_the_historical_snapshot()
    {
        const string hash = "chart-overflow-safe-exscore";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            CurrentLr2(hash, ClearType.CLEAR, 180, 100),
            ActiveScoreSource.Lr2,
            selectedDate,
            [
                Change(ActiveScoreSource.Lr2, hash, 0, selectedDate, 3, 0, 100, 100),
                Change(ActiveScoreSource.Lr2, hash, 1, selectedDate.AddDays(1), 3, 0, int.MaxValue, 1)
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, result.Status);
        Assert.IsFalse(result.ScoreSnapshot.IsScoreDataAvailable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    /// <summary>変換不能なUnix時刻を持つ履歴は診断付きで利用不可とする。行を黙って除いて現在スコアを正しい過去値として返す誤りを検出する。</summary>
    [TestMethod]
    public void Invalid_history_unix_timestamp_degrades_instead_of_being_ignored()
    {
        const string hash = "chart-invalid-timestamp";
        DateTime selectedDate = new(2026, 8, 28);
        var invalidTimestamp = new PlaylistLampHistoricalScoreChange(
            ActiveScoreSource.Lr2,
            hash,
            1,
            long.MaxValue,
            1,
            3,
            0,
            100,
            100);
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            CurrentLr2(hash, ClearType.CLEAR, 180, 100),
            ActiveScoreSource.Lr2,
            selectedDate,
            [
                Change(ActiveScoreSource.Lr2, hash, 0, selectedDate, 3, 0, 100, 100),
                invalidTimestamp
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, result.Status);
        Assert.IsFalse(result.ScoreSnapshot.IsScoreDataAvailable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.FailureMessage));
    }

    /// <summary>今日より未来の有効時刻は選択可能な最古日を広げず、基準日後の巻戻し対象には含める。日付範囲の検査で必要な履歴まで捨てる誤りを検出する。</summary>
    [TestMethod]
    public void Valid_timestamp_after_today_is_excluded_from_range_but_remains_a_rollback_candidate()
    {
        const string hash = "chart-future-timestamp";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            CurrentLr2(hash, ClearType.HARD, 180, 100),
            ActiveScoreSource.Lr2,
            selectedDate,
            [
                Change(ActiveScoreSource.Lr2, hash, 0, selectedDate, 3, 0, 100, 100),
                Change(ActiveScoreSource.Lr2, hash, 1, TestToday.AddDays(1), 3, 0, 120, 100)
            ],
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, result.Status);
        Assert.AreEqual(selectedDate, result.DateRange.EarliestLocalDate);
        Assert.AreEqual(120, result.ScoreSnapshot.ScoresByHash[hash].ExScore);
    }

    /// <summary>LR2履歴トリガーの欠損と定義不一致は履歴を利用不可にし、読取りでDBを修復しない。信頼できない履歴の受理や書込みを検出する。</summary>
    [DataTestMethod]
    // 欠損と、同じ名前で定義が異なるトリガーの両方は履歴の信頼性を失わせる。
    [DataRow("missing")]
    [DataRow("mismatched")]
    public void Lr2_trigger_schema_defect_degrades_without_repairing_the_database(string defect)
    {
        string directory = CreateTemporaryDirectory();
        string scoreDbPath = Path.Combine(directory, "score.db");
        try
        {
            CreateLr2ScoreDb(scoreDbPath);
            Assert.AreEqual(
                Lr2PlayHistorySchemaStatus.Installed,
                new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true).Status);
            DateTime selectedDate = new(2026, 8, 28);
            long playedAt = Change(
                ActiveScoreSource.Lr2,
                "schema-chart",
                1,
                selectedDate,
                3,
                0,
                100,
                100).PlayedAtUnixSeconds;
            using (SQLiteConnection db = new(scoreDbPath))
            {
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
                if (string.Equals(defect, "mismatched", StringComparison.Ordinal))
                {
                    db.Execute(
                        "CREATE TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName
                        + " AFTER INSERT ON score BEGIN SELECT 1; END;");
                }
                InsertLr2History(db, 1, "schema-chart", playedAt, oldPlayCount: 1, oldClear: 3, oldOperationHistory: 0, oldExScore: 100, oldTotalNotes: 100);
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            PlaylistLampHistoricalScoreSourceContext captured = AcquireLr2HistoryContext(scoreDbPath);
            Lr2PlayHistoryReadResult ordinary = Lr2PlayHistoryReader.ForPurpose(captured.PlayHistorySnapshot.Lr2, false, false);
            Assert.IsFalse(ordinary.HasErrors, "通常履歴はトリガー不備を警告として保持します。");
            Assert.AreEqual(1, ordinary.Rows.Count);
            PlaylistLampHistoricalScoreSnapshotResult result = new PlaylistLampHistoricalScoreSnapshotReader().Read(
                captured,
                CurrentLr2("schema-chart", ClearType.CLEAR, 180, 100),
                selectedDate);

            Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, result.Status);
            Assert.IsFalse(result.ScoreSnapshot.IsScoreDataAvailable);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.FailureMessage));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    /// <summary>LR2の索引だけが欠損したDBは過去スコアを復元でき、DBを書き換えない。修復可能な索引欠損による過剰拒否や参照時修復を検出する。</summary>
    [TestMethod]
    public void Lr2_index_only_schema_defect_keeps_historical_snapshot_available_without_repairing_the_database()
    {
        string directory = CreateTemporaryDirectory();
        string scoreDbPath = Path.Combine(directory, "score.db");
        try
        {
            CreateLr2ScoreDb(scoreDbPath);
            Assert.AreEqual(
                Lr2PlayHistorySchemaStatus.Installed,
                new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true).Status);
            DateTime selectedDate = new(2026, 8, 28);
            long firstPlayedAt = Change(
                ActiveScoreSource.Lr2,
                "index-only-chart",
                1,
                selectedDate,
                3,
                0,
                100,
                100).PlayedAtUnixSeconds;
            long secondPlayedAt = Change(
                ActiveScoreSource.Lr2,
                "index-only-chart",
                2,
                selectedDate.AddDays(1),
                3,
                0,
                150,
                100).PlayedAtUnixSeconds;
            using (SQLiteConnection db = new(scoreDbPath))
            {
                InsertLr2History(db, 1, "index-only-chart", firstPlayedAt, oldPlayCount: 1, oldClear: 3, oldOperationHistory: 0, oldExScore: 100, oldTotalNotes: 100);
                InsertLr2History(db, 2, "index-only-chart", secondPlayedAt, oldPlayCount: 1, oldClear: 3, oldOperationHistory: 0, oldExScore: 150, oldTotalNotes: 100);
                db.Execute("DROP INDEX " + Lr2PlayHistorySchemaService.TimeIndexName + ";");
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            PlaylistLampHistoricalScoreSourceContext captured = AcquireLr2HistoryContext(scoreDbPath);
            Lr2PlayHistoryReadResult ordinary = Lr2PlayHistoryReader.ForPurpose(captured.PlayHistorySnapshot.Lr2, false, false);
            Assert.IsTrue(ordinary.HasErrors, "通常履歴は索引不備で停止し、ランプだけが同じrawを利用します。");
            Assert.AreEqual(0, ordinary.Rows.Count);
            PlaylistLampHistoricalScoreSnapshotResult result = new PlaylistLampHistoricalScoreSnapshotReader().Read(
                captured,
                CurrentLr2("index-only-chart", ClearType.CLEAR, 180, 100),
                selectedDate);

            Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, result.Status);
            Assert.AreEqual(150, result.ScoreSnapshot.ScoresByHash["index-only-chart"].ExScore);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    /// <summary>プレイリスト外の履歴からも情報源全体の最古日を求め、今日までを選択可能にする。所属譜面だけへ日付範囲を狭める誤りを検出する。</summary>
    [TestMethod]
    public void Provider_wide_range_uses_history_not_playlist_membership()
    {
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            CurrentLr2("playlist-chart", ClearType.CLEAR, 180, 100),
            ActiveScoreSource.Lr2,
            null,
            [
                Change(ActiveScoreSource.Lr2, "not-in-playlist", 1, selectedDate, 3, 0, 100, 100)
            ],
            TestToday);

        Assert.AreEqual(selectedDate, result.DateRange.EarliestLocalDate);
        Assert.AreEqual(TestToday, result.DateRange.LatestLocalDate);
        Assert.IsTrue(result.DateRange.Contains(selectedDate));
    }

    private static PlaylistLampScoreSnapshot CurrentLr2(
        string hash,
        ClearType clear,
        int exScore,
        int totalNotes)
    {
        return new PlaylistLampScoreSnapshot(
            ActiveScoreSource.Lr2,
            ScoreTableLoadStatus.Loaded,
            1,
            1,
            null,
            new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
            {
                [hash] = PlaylistLampScore.FromExScore(hash, null, clear, exScore, totalNotes)
            });
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlaylistLampHistorical_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void CreateLr2ScoreDb(string path)
    {
        using SQLiteConnection db = new(path);
        db.CreateTable<LR2ScoreDB.score>();
        db.CreateTable<LR2ScoreDB.player>();
    }

    private static void InsertLr2History(
        SQLiteConnection db,
        long historyId,
        string hash,
        long playedAt,
        int? oldPlayCount,
        int? oldClear,
        int? oldOperationHistory,
        int? oldExScore,
        int? oldTotalNotes)
    {
        db.Execute(
            "INSERT INTO bms_lr2_play_history (history_id, hash, played_at, finalized, score_write_type, old_playcount, new_playcount, playcount_delta, old_clear, old_op_history, old_exscore, old_totalnotes) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
            historyId,
            hash,
            playedAt,
            1,
            "update",
            oldPlayCount,
            1,
            1,
            oldClear,
            oldOperationHistory,
            oldExScore,
            oldTotalNotes);
    }

    private static void CreateBeatorajaScoreLogDb(string path)
    {
        using SQLiteConnection db = new(path);
        db.Execute(
            "CREATE TABLE scorelog (sha256 TEXT NOT NULL, mode INTEGER, clear INTEGER, oldclear INTEGER, score INTEGER, oldscore INTEGER, combo INTEGER, oldcombo INTEGER, minbp INTEGER, oldminbp INTEGER, date INTEGER);");
    }

    private static void InsertBeatorajaScoreLog(SQLiteConnection db, string sha256, int mode, long date)
    {
        db.Execute(
            "INSERT INTO scorelog (sha256, mode, clear, oldclear, score, oldscore, combo, oldcombo, minbp, oldminbp, date) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
            sha256,
            mode,
            (int)ClearType.HARD,
            (int)ClearType.NO_PLAY,
            100,
            0,
            0,
            0,
            0,
            0,
            date);
    }

    private static PlaylistLampHistoricalScoreChange Change(
        ActiveScoreSource source,
        string key,
        long sourceId,
        DateTime localTime,
        int? oldClear,
        int? oldOperationHistory,
        int? oldExScore,
        int? oldTotalNotes,
        long? oldPlayCount = 1,
        TimeZoneInfo? timeZone = null)
    {
        var unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone ?? TimeZoneInfo.Local);
        long unixSeconds = new DateTimeOffset(utc).ToUnixTimeSeconds();
        return new PlaylistLampHistoricalScoreChange(
            source,
            key,
            sourceId,
            unixSeconds,
            oldPlayCount,
            oldClear,
            oldOperationHistory,
            oldExScore,
            oldTotalNotes);
    }
}
