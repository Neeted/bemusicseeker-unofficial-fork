using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <summary>
/// PLV-HIST-20260829 provider-neutral rollback contract tests.
/// </summary>
[TestClass]
public sealed class PlaylistLampHistoricalScoreSnapshotTests
{
    private static readonly DateTime TestToday = new(2026, 8, 29);

    [TestMethod]
    public void Cutoff_includes_local_midnight_and_same_timestamp_uses_source_id_descending()
    {
        const string hash = "chart-a";
        DateTime selectedDate = new(2026, 8, 28);
        DateTime cutoff = selectedDate.AddDays(1);
        PlaylistLampScoreSnapshot current = CurrentLr2(hash, ClearType.EASY, 160, 100);
        var changes = new[]
        {
            Change(ActiveScoreSource.Lr2, hash, 10, selectedDate.AddHours(12), 4, 8, 100, 100),
            Change(ActiveScoreSource.Lr2, hash, 11, cutoff, 3, 0, 120, 100),
            Change(ActiveScoreSource.Lr2, hash, 12, cutoff, 1, 0, 0, 100),
            Change(ActiveScoreSource.Lr2, hash, 13, cutoff.AddHours(1), 4, 8, 140, 100)
        };

        PlaylistLampHistoricalScoreSnapshotResult result = PlaylistLampHistoricalScoreSnapshotBuilder.Build(
            current,
            ActiveScoreSource.Lr2,
            selectedDate,
            changes,
            TestToday);

        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, result.Status);
        Assert.IsTrue(result.ScoreSnapshot.ScoresByHash.TryGetValue(hash, out PlaylistLampScore score));
        Assert.AreEqual(ClearType.CLEAR, score.Clear);
        Assert.AreEqual(120, score.ExScore);
    }

    [TestMethod]
    public void No_future_history_keeps_exact_current_score()
    {
        const string hash = "chart-b";
        DateTime selectedDate = new(2026, 8, 28);
        PlaylistLampScore currentScore = PlaylistLampScore.FromExScore(
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
        Assert.IsTrue(result.ScoreSnapshot.ScoresBySha256.TryGetValue(sha256, out PlaylistLampScore score));
        Assert.AreEqual(ClearType.NO_PLAY, score.Clear);
        Assert.AreEqual(RankType.F, score.Rank);
    }

    [DataTestMethod]
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

    [TestMethod]
    public void Beatoraja_reader_reads_only_mode_zero_without_writing_score_databases()
    {
        string directory = CreateTemporaryDirectory();
        string scoreDbPath = Path.Combine(directory, "score.db");
        string scoreLogDbPath = Path.Combine(directory, "scorelog.db");
        try
        {
            CreateEmptySqliteDatabase(scoreDbPath);
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            using (SQLiteConnection db = new(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, "mode-zero-late", 0, 1_700_000_002L);
                InsertBeatorajaScoreLog(db, "mode-one", 1, 1_700_000_003L);
                InsertBeatorajaScoreLog(db, "mode-zero-early", 0, 1_700_000_001L);
            }
            byte[] scoreBefore = File.ReadAllBytes(scoreDbPath);
            byte[] scoreLogBefore = File.ReadAllBytes(scoreLogDbPath);

            BeatorajaPlayHistoryReadResult result = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                ScoreLogDbPath = scoreLogDbPath,
                ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase),
                FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly,
                DisableLimit = true
            });

            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual(2, result.Rows.Count);
            Assert.IsTrue(result.Rows.All(row => row.mode == 0));
            Assert.AreEqual("mode-zero-late", result.Rows[0].sha256);
            Assert.AreEqual("mode-zero-early", result.Rows[1].sha256);
            CollectionAssert.AreEqual(scoreBefore, File.ReadAllBytes(scoreDbPath));
            CollectionAssert.AreEqual(scoreLogBefore, File.ReadAllBytes(scoreLogDbPath));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

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

    [DataTestMethod]
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

            PlaylistLampHistoricalScoreSnapshotResult result = new PlaylistLampHistoricalScoreSnapshotReader().Read(
                PlaylistLampHistoricalScoreSourceContext.Lr2(scoreDbPath),
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

            PlaylistLampHistoricalScoreSnapshotResult result = new PlaylistLampHistoricalScoreSnapshotReader().Read(
                PlaylistLampHistoricalScoreSourceContext.Lr2(scoreDbPath),
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

    private static void CreateEmptySqliteDatabase(string path)
    {
        using SQLiteConnection db = new(path);
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
        long? oldPlayCount = 1)
    {
        DateTime unspecified = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        DateTime utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZoneInfo.Local);
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
