using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlayHistoryReadModelTests
{
    /// <summary>
    /// 検査結果の保存後に元の状態・パス・文言を変えても捕捉値を維持し、可変結果の共有による表示の書換えを防ぐ。
    /// </summary>
    [TestMethod]
    public void Lr2PlayHistorySchemaStatusSnapshot_DetachesMutableServiceResult()
    {
        var result = new Lr2PlayHistorySchemaCheckResult
        {
            Status = Lr2PlayHistorySchemaStatus.Installed,
            ScoreDbPath = "score.db",
            Message = "installed"
        };

        var snapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(result);
        result.Status = Lr2PlayHistorySchemaStatus.Repairable;
        result.ScoreDbPath = "other.db";
        result.Message = "changed";

        Assert.IsFalse(snapshot.IsReset);
        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, snapshot.Status);
        Assert.AreEqual("score.db", snapshot.ScoreDbPath);
        Assert.AreEqual("installed", snapshot.Message);
    }

    /// <summary>
    /// 全期間は時刻制限を付けず確定履歴だけを要求し、当日の範囲や未確定行を混ぜない。
    /// </summary>
    [TestMethod]
    public void PlayHistoryPeriodRequest_AllHasNoEpochBounds()
    {
        var request = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.All,
            new DateTimeOffset(2026, 6, 19, 15, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.IsNull(request.PlayedAtFromInclusive);
        Assert.IsNull(request.PlayedAtToExclusive);
        Assert.IsFalse(request.IncludeUnfinalized);
        Assert.AreEqual(Lr2PlayHistoryFinalizationFilter.FinalizedOnly, request.FinalizationFilter);
    }

    /// <summary>
    /// 今日・昨日・最近7日/30日は開始日を含む午前0時から翌境界を除く区間とし、日数と境界のずれを検出する。
    /// </summary>
    [TestMethod]
    public void PlayHistoryPeriodRequest_DayRangesUseLocalMidnightInclusiveAndNextMidnightExclusive()
    {
        AssertPeriodRange(PlayHistoryPeriodKind.Today, UtcEpoch(2026, 6, 19), UtcEpoch(2026, 6, 20));
        AssertPeriodRange(PlayHistoryPeriodKind.Yesterday, UtcEpoch(2026, 6, 18), UtcEpoch(2026, 6, 19));
        AssertPeriodRange(PlayHistoryPeriodKind.Recent7Days, UtcEpoch(2026, 6, 13), UtcEpoch(2026, 6, 20));
        AssertPeriodRange(PlayHistoryPeriodKind.Recent30Days, UtcEpoch(2026, 5, 21), UtcEpoch(2026, 6, 20));
    }

    /// <summary>
    /// UTC+9の日境界をUTC秒へ換算し、UTC日付をそのままローカル日付として扱わない。
    /// </summary>
    [TestMethod]
    public void PlayHistoryPeriodRequest_UsesProvidedLocalTimeZoneForDayBoundary()
    {
        var utcPlusNine = TimeZoneInfo.CreateCustomTimeZone("UTC+09", TimeSpan.FromHours(9), "UTC+09", "UTC+09");
        var request = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.Today,
            new DateTimeOffset(2026, 6, 19, 15, 30, 0, TimeSpan.Zero),
            utcPlusNine);

        Assert.AreEqual(UtcEpoch(2026, 6, 19, 15), request.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 6, 20, 15), request.PlayedAtToExclusive);
    }

    /// <summary>
    /// 診断期間は日付制限なしで未確定だけをLR2/beatoraja要求へ渡し、通常の確定履歴を混ぜない。
    /// </summary>
    [TestMethod]
    public void PlayHistoryPeriodRequest_DiagnosticsIncludesUnfinalizedWithoutDateBounds()
    {
        var request = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.Diagnostics,
            new DateTimeOffset(2026, 6, 19, 15, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.IsNull(request.PlayedAtFromInclusive);
        Assert.IsNull(request.PlayedAtToExclusive);
        Assert.IsTrue(request.IncludeUnfinalized);
        Assert.AreEqual(Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly, request.FinalizationFilter);
        Assert.AreEqual(
            Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly,
            request.ToLr2ReadRequest("score.db", isLr2LinkedProfile: true).FinalizationFilter);
        Assert.AreEqual(
            Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly,
            request.ToBeatorajaReadRequest("score.db").FinalizationFilter);
    }

    /// <summary>
    /// 年・12月・個別日の範囲と表示名をUTC+9の境界から求め、年/月末の繰上げを誤らない。
    /// </summary>
    [TestMethod]
    public void PlayHistoryPeriodRequest_ArchiveRangesUseLocalBoundaries()
    {
        var utcPlusNine = TimeZoneInfo.CreateCustomTimeZone("UTC+09", TimeSpan.FromHours(9), "UTC+09", "UTC+09");

        var year = PlayHistoryPeriodRequest.CreateYear(2026, utcPlusNine);
        var month = PlayHistoryPeriodRequest.CreateMonth(2026, 12, utcPlusNine);
        var day = PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, utcPlusNine);

        Assert.AreEqual(PlayHistoryPeriodKind.Year, year.Kind);
        Assert.AreEqual("2026", year.Label);
        Assert.AreEqual(UtcEpoch(2025, 12, 31, 15), year.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 12, 31, 15), year.PlayedAtToExclusive);
        Assert.AreEqual(PlayHistoryPeriodKind.Month, month.Kind);
        Assert.AreEqual("2026/12", month.Label);
        Assert.AreEqual(UtcEpoch(2026, 11, 30, 15), month.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 12, 31, 15), month.PlayedAtToExclusive);
        Assert.AreEqual(PlayHistoryPeriodKind.Day, day.Kind);
        Assert.AreEqual("2026/01/02", day.Label);
        Assert.AreEqual(UtcEpoch(2026, 1, 1, 15), day.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 1, 2, 15), day.PlayedAtToExclusive);
    }

    /// <summary>
    /// 複数年/日に跨る時刻を年→月→日の降順にまとめ、同日の重複を統合して日範囲を保持する。
    /// </summary>
    [TestMethod]
    public void PlayHistoryPeriodTreeItem_BuildArchiveTreeGroupsDescending()
    {
        IReadOnlyList<PlayHistoryPeriodTreeItem> tree = PlayHistoryPeriodTreeItem.BuildArchiveTree(
            [
                UtcEpoch(2025, 12, 31, 23),
                UtcEpoch(2026, 1, 1, 1),
                UtcEpoch(2026, 1, 2, 1),
                UtcEpoch(2026, 1, 2, 23)
            ],
            TimeZoneInfo.Utc);

        CollectionAssert.AreEqual(new[] { "2026", "2025" }, tree.Select(node => node.Label).ToArray());
        PlayHistoryPeriodTreeItem year2026 = tree[0];
        Assert.AreEqual("2026", year2026.ToString());
        Assert.AreEqual(PlayHistoryPeriodKind.Year, year2026.Request.Kind);
        Assert.AreEqual(UtcEpoch(2026, 1, 1), year2026.Request.PlayedAtFromInclusive);
        CollectionAssert.AreEqual(new[] { "2026/01" }, year2026.Children.Select(node => node.Label).ToArray());
        CollectionAssert.AreEqual(new[] { "2026/01/02", "2026/01/01" }, year2026.Children[0].Children.Select(node => node.Label).ToArray());
        Assert.AreEqual(PlayHistoryPeriodKind.Day, year2026.Children[0].Children[0].Request.Kind);
        Assert.AreEqual(UtcEpoch(2026, 1, 2), year2026.Children[0].Children[0].Request.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 1, 3), year2026.Children[0].Children[0].Request.PlayedAtToExclusive);
    }

    /// <summary>
    /// UTC+9の午前0時前後は別の日へ分類し、UTC日付だけでarchiveをまとめない。
    /// </summary>
    [TestMethod]
    public void PlayHistoryPeriodTreeItem_BuildArchiveTreeUsesProvidedTimeZone()
    {
        var utcPlusNine = TimeZoneInfo.CreateCustomTimeZone("UTC+09", TimeSpan.FromHours(9), "UTC+09", "UTC+09");
        IReadOnlyList<PlayHistoryPeriodTreeItem> tree = PlayHistoryPeriodTreeItem.BuildArchiveTree(
            [
                UtcEpoch(2026, 1, 1, 14),
                UtcEpoch(2026, 1, 1, 15),
                UtcEpoch(2026, 1, 2, 14)
            ],
            utcPlusNine);

        PlayHistoryPeriodTreeItem january = tree.Single().Children.Single();

        CollectionAssert.AreEqual(new[] { "2026/01/02", "2026/01/01" }, january.Children.Select(node => node.Label).ToArray());
        Assert.AreEqual(UtcEpoch(2026, 1, 1, 15), january.Children[0].Request.PlayedAtFromInclusive);
        Assert.AreEqual(UtcEpoch(2026, 1, 2, 15), january.Children[0].Request.PlayedAtToExclusive);
    }

    /// <summary>
    /// 実履歴の期間・確定条件と上限をSQL読取りへ適用し、未確定を含める要求では時刻降順の先頭を返す。
    /// </summary>
    /// <remarks>実SQLiteの履歴行を読み、期間・確定条件・上限で選ばれるIDと降順を確認する。</remarks>
    [TestMethod]
    public void Lr2Reader_ReadsFinalizedRowsByRangeDescendingAndExcludesUnfinalized()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 4, hash: HashA, playedAt: 999, finalized: true, newExscore: 180);
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                InsertHistory(db, historyId: 2, hash: HashB, playedAt: 2000, finalized: true, newExscore: 250);
                InsertHistory(db, historyId: 3, hash: HashC, playedAt: 2500, finalized: false, newExscore: 300);
                InsertHistory(db, historyId: 5, hash: HashB, playedAt: 3000, finalized: true, newExscore: 400);
            }

            var reader = new Lr2PlayHistoryReader();
            Lr2PlayHistoryReadResult result = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                PlayedAtFromInclusive = 1000,
                PlayedAtToExclusive = 3000
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, result.SchemaStatus);
            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual(2, result.Rows.Count);
            Assert.AreEqual(2L, result.Rows[0].history_id);
            Assert.AreEqual(1L, result.Rows[1].history_id);

            Lr2PlayHistoryReadResult withUnfinalized = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                IncludeUnfinalized = true,
                PlayedAtFromInclusive = 1000,
                PlayedAtToExclusive = 3000,
                Limit = 2
            });

            Assert.AreEqual(2, withUnfinalized.Rows.Count);
            Assert.AreEqual(3L, withUnfinalized.Rows[0].history_id);
            Assert.AreEqual(2L, withUnfinalized.Rows[1].history_id);
        });
    }

    /// <summary>
    /// 未確定専用要求は実DBの診断行だけを降順に返し、確定済み行を混入させない。
    /// </summary>
    /// <remarks>実SQLiteの確定・未確定行を読み、返る履歴IDと未確定状態を確認する。</remarks>
    [TestMethod]
    public void Lr2Reader_UnfinalizedOnlyFilterReadsOnlyDiagnosticRows()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                InsertHistory(db, historyId: 2, hash: HashB, playedAt: 2000, finalized: false, newExscore: 250);
                InsertHistory(db, historyId: 3, hash: HashC, playedAt: 3000, finalized: false, newExscore: 300);
            }

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                FinalizationFilter = Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly,
                Limit = 10
            });

            CollectionAssert.AreEqual(new long[] { 3L, 2L }, result.Rows.Select(row => row.history_id).ToArray());
            Assert.IsTrue(result.Rows.All(row => row.finalized == 0));
        });
    }

    /// <summary>
    /// 開始直前/開始/終了直前/終了の4行から区間内2行だけを返し、SQLの両境界を取り違えない。
    /// </summary>
    /// <remarks>実SQLiteに境界前後の履歴を保存し、readerが返すIDから区間条件を確認する。</remarks>
    [TestMethod]
    public void Lr2Reader_RangeBoundariesAreInclusiveFromExclusiveTo()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 999, finalized: true, newExscore: 100);
                InsertHistory(db, historyId: 2, hash: HashA, playedAt: 1000, finalized: true, newExscore: 110);
                InsertHistory(db, historyId: 3, hash: HashA, playedAt: 1999, finalized: true, newExscore: 120);
                InsertHistory(db, historyId: 4, hash: HashA, playedAt: 2000, finalized: true, newExscore: 130);
            }

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                PlayedAtFromInclusive = 1000,
                PlayedAtToExclusive = 2000,
                Limit = 10
            });

            CollectionAssert.AreEqual(new long[] { 3L, 2L }, result.Rows.Select(row => row.history_id).ToArray());
        });
    }

    /// <summary>
    /// 履歴未導入DBはエラー診断を返し、読取りに伴って履歴objectを作成しない。
    /// </summary>
    /// <remarks>履歴未導入の実SQLiteを読み、診断とsqlite_master上の履歴object不在を確認する。</remarks>
    [TestMethod]
    public void Lr2Reader_NotInstalledReturnsDiagnosticWithoutCreatingObjects()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, result.SchemaStatus);
            Assert.IsTrue(result.HasErrors);
            AssertSingleDiagnostic(result, PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_schema_not_installed", scoreDbPath);
            using var verify = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM sqlite_master WHERE name LIKE 'bms_lr2_%';"));
        });
    }

    /// <summary>
    /// 不足索引を検出すると行を返さず対象DBの診断を出し、修復が必要な読取りを成功扱いしない。
    /// </summary>
    /// <remarks>実SQLiteの索引を削除し、修復が必要なschema状態とエラー・履歴0件を確認する。</remarks>
    [TestMethod]
    public void Lr2Reader_IndexRepairRequiredReturnsDiagnosticWithoutReading()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                db.Execute("DROP INDEX " + Lr2PlayHistorySchemaService.TimeIndexName + ";");
            }

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.SchemaStatus);
            Assert.IsTrue(result.HasErrors);
            Assert.AreEqual(0, result.Rows.Count);
            AssertSingleDiagnostic(result, PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_schema_index_repair_required", scoreDbPath);
        });
    }

    /// <summary>
    /// 必須triggerがmissing/mismatchedなら厳密要求は履歴0件とエラーを返し、DBバイトを変更しない。
    /// </summary>
    /// <remarks>実SQLiteのtrigger欠落・不一致を作り、厳密要求の診断・履歴0件と読取り前後のDBバイト一致を確認する。</remarks>
    [DataTestMethod]
    [DataRow("missing")]
    [DataRow("mismatched")]
    public void Lr2Reader_StrictHistoryTriggerDefectReturnsErrorWithoutReadingRows(string defect)
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
                if (string.Equals(defect, "mismatched", StringComparison.Ordinal))
                {
                    db.Execute(
                        "CREATE TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName
                        + " AFTER INSERT ON score BEGIN SELECT 1; END;");
                }
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                AllowRepairableIndexRead = true,
                RequireCompleteHistoryTriggers = true,
                DisableLimit = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.SchemaStatus);
            Assert.IsTrue(result.HasErrors);
            Assert.AreEqual(0, result.Rows.Count);
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual(PlayHistoryDiagnosticSeverity.Error, result.Diagnostics[0].Severity);
            Assert.AreEqual(scoreDbPath, result.Diagnostics[0].SourcePath);
            Assert.IsNotNull(result.SchemaCheckResult);
            Assert.IsTrue(
                result.SchemaCheckResult.MissingTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName)
                || result.SchemaCheckResult.MismatchedTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
        });
    }

    /// <summary>
    /// missing/mismatched triggerは厳密要求なら停止し、非厳密なら警告付きで読む。索引修復許可をtrigger要求の代用にしない。
    /// </summary>
    /// <remarks>実SQLiteのtriggerを欠落・不一致にし、要求条件別の診断・履歴件数と読取り前後のDBバイト一致を確認する。</remarks>
    [DataTestMethod]
    [DataRow("missing", true, false)]
    [DataRow("mismatched", true, false)]
    [DataRow("missing", false, true)]
    [DataRow("mismatched", false, true)]
    public void Lr2Reader_TriggerRequirementAndIndexRepairPoliciesRemainIndependent(
        string defect,
        bool requireCompleteHistoryTriggers,
        bool allowRepairableIndexRead)
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
                if (string.Equals(defect, "mismatched", StringComparison.Ordinal))
                {
                    db.Execute(
                        "CREATE TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName
                        + " AFTER INSERT ON score BEGIN SELECT 1; END;");
                }
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                AllowRepairableIndexRead = allowRepairableIndexRead,
                RequireCompleteHistoryTriggers = requireCompleteHistoryTriggers,
                DisableLimit = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.SchemaStatus);
            Assert.IsNotNull(result.SchemaCheckResult);
            if (string.Equals(defect, "missing", StringComparison.Ordinal))
            {
                Assert.IsTrue(result.SchemaCheckResult.MissingTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
                Assert.AreEqual(0, result.SchemaCheckResult.MismatchedTriggers.Count);
            }
            else
            {
                Assert.AreEqual(0, result.SchemaCheckResult.MissingTriggers.Count);
                Assert.IsTrue(result.SchemaCheckResult.MismatchedTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
            }

            if (requireCompleteHistoryTriggers)
            {
                Assert.IsTrue(result.HasErrors);
                Assert.AreEqual(0, result.Rows.Count);
                AssertSingleDiagnostic(
                    result,
                    PlayHistoryDiagnosticSeverity.Error,
                    "play_history_lr2_schema_trigger_repair_required",
                    scoreDbPath);
            }
            else
            {
                Assert.IsFalse(result.HasErrors);
                Assert.AreEqual(1, result.Rows.Count);
                AssertSingleDiagnostic(
                    result,
                    PlayHistoryDiagnosticSeverity.Warning,
                    "play_history_lr2_schema_repairable",
                    scoreDbPath);
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
        });
    }

    /// <summary>
    /// missing/mismatched triggerの厳密検査は、照会すれば失敗する履歴表を問い合わせずschema診断で停止する。
    /// </summary>
    /// <remarks>実SQLiteの履歴表定義とtriggerを壊し、行照会より前のschema診断とDBバイトの非変更を確認する。</remarks>
    [DataTestMethod]
    [DataRow("missing")]
    [DataRow("mismatched")]
    public void Lr2Reader_StrictHistoryTriggerPolicyStopsBeforeMalformedRowQuery(string defect)
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
                if (string.Equals(defect, "mismatched", StringComparison.Ordinal))
                {
                    db.Execute(
                        "CREATE TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName
                        + " AFTER INSERT ON score BEGIN SELECT 1; END;");
                }
                db.Execute(
                    "INSERT INTO bms_lr2_play_history (history_id, hash, played_at, finalized, score_write_type, new_playcount, playcount_delta, new_exscore, new_totalnotes) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);",
                    1,
                    HashA,
                    "not-a-unix-timestamp",
                    1,
                    "update",
                    1,
                    1,
                    200,
                    100);
                // Keep PRAGMA table_info compatible while making SELECT ... ORDER BY fail
                // on the reader's fresh connection if the strict policy is bypassed.
                db.Execute("PRAGMA writable_schema = ON;");
                db.Execute(
                    "UPDATE sqlite_master SET sql = replace(sql, 'played_at INTEGER NOT NULL', 'played_at INTEGER COLLATE missing_history_collation NOT NULL') WHERE type = 'table' AND name = 'bms_lr2_play_history';");
                db.Execute("PRAGMA writable_schema = OFF;");
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                AllowRepairableIndexRead = true,
                RequireCompleteHistoryTriggers = true,
                DisableLimit = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.SchemaStatus);
            Assert.IsTrue(result.HasErrors);
            Assert.AreEqual(0, result.Rows.Count);
            AssertSingleDiagnostic(
                result,
                PlayHistoryDiagnosticSeverity.Error,
                "play_history_lr2_schema_trigger_repair_required",
                scoreDbPath);
            Assert.IsNotNull(result.SchemaCheckResult);
            if (string.Equals(defect, "missing", StringComparison.Ordinal))
            {
                Assert.IsTrue(result.SchemaCheckResult.MissingTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
            }
            else
            {
                Assert.IsTrue(result.SchemaCheckResult.MismatchedTriggers.Contains(Lr2PlayHistorySchemaService.ScoreInsertTriggerName));
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
        });
    }

    /// <summary>
    /// triggerが揃って索引だけ不足するDBは明示許可時に警告付きで読み、厳密trigger要求で一律拒否しない。
    /// </summary>
    /// <remarks>実SQLiteの索引だけを削除し、明示許可時の履歴・警告と読取り前後のDBバイト一致を確認する。</remarks>
    [TestMethod]
    public void Lr2Reader_StrictHistoryTriggerRequirementAllowsIndexOnlyRepairableReadWhenOptedIn()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                db.Execute("DROP INDEX " + Lr2PlayHistorySchemaService.TimeIndexName + ";");
            }
            byte[] before = File.ReadAllBytes(scoreDbPath);

            Lr2PlayHistoryReadResult result = new Lr2PlayHistoryReader().Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                AllowRepairableIndexRead = true,
                RequireCompleteHistoryTriggers = true,
                DisableLimit = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.SchemaStatus);
            Assert.IsFalse(result.HasErrors);
            Assert.AreEqual(1, result.Rows.Count);
            Assert.AreEqual(1L, result.Rows[0].history_id);
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual(PlayHistoryDiagnosticSeverity.Warning, result.Diagnostics[0].Severity);
            Assert.IsNotNull(result.SchemaCheckResult);
            Assert.IsTrue(result.SchemaCheckResult.MissingIndexes.Contains(Lr2PlayHistorySchemaService.TimeIndexName));
            Assert.AreEqual(0, result.SchemaCheckResult.MissingTriggers.Count);
            Assert.AreEqual(0, result.SchemaCheckResult.MismatchedTriggers.Count);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(scoreDbPath));
        });
    }

    /// <summary>
    /// 非連携・パスなし・元表不足・trigger不足をそれぞれ情報/エラー/警告へ分類し、許可されたtrigger不備だけは行を読む。
    /// </summary>
    /// <remarks>非連携・未設定入力と実SQLiteのschema不備をreaderへ渡し、各診断と履歴件数を確認する。</remarks>
    [TestMethod]
    public void Lr2Reader_MapsSchemaStatusesToDiagnostics()
    {
        var reader = new Lr2PlayHistoryReader();

        Lr2PlayHistoryReadResult skipped = reader.Read(new Lr2PlayHistoryReadRequest
        {
            ScoreDbPath = "missing.db",
            IsLr2LinkedProfile = false
        });

        Assert.AreEqual(Lr2PlayHistorySchemaStatus.SkippedProfile, skipped.SchemaStatus);
        Assert.IsFalse(skipped.HasErrors);
        Assert.AreEqual(0, skipped.Rows.Count);
        AssertSingleDiagnostic(skipped, PlayHistoryDiagnosticSeverity.Info, "play_history_lr2_skipped_profile", "missing.db");

        Lr2PlayHistoryReadResult unreadable = reader.Read(new Lr2PlayHistoryReadRequest
        {
            ScoreDbPath = null,
            IsLr2LinkedProfile = true
        });

        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, unreadable.SchemaStatus);
        Assert.IsTrue(unreadable.HasErrors);
        Assert.AreEqual(0, unreadable.Rows.Count);
        AssertSingleDiagnostic(unreadable, PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_schema_unreadable", string.Empty);

        WithScoreDb(delegate (string scoreDbPath)
        {
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("CREATE TABLE unrelated (id INTEGER);");
            }

            Lr2PlayHistoryReadResult manualRepair = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.ManualRepairRequired, manualRepair.SchemaStatus);
            Assert.IsTrue(manualRepair.HasErrors);
            Assert.AreEqual(0, manualRepair.Rows.Count);
            AssertSingleDiagnostic(manualRepair, PlayHistoryDiagnosticSeverity.Error, "play_history_lr2_schema_manual_repair_required", scoreDbPath);
        });

        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: 1000, finalized: true, newExscore: 200);
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
            }

            Lr2PlayHistoryReadResult repairableTrigger = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, repairableTrigger.SchemaStatus);
            Assert.IsFalse(repairableTrigger.HasErrors);
            Assert.AreEqual(1, repairableTrigger.Rows.Count);
            AssertSingleDiagnostic(repairableTrigger, PlayHistoryDiagnosticSeverity.Warning, "play_history_lr2_schema_repairable", scoreDbPath);
        });
    }

    /// <summary>
    /// 上限未指定・0・負値は有限の既定上限を実履歴へ適用し、最新行を保持して全件読取りへ広げない。
    /// </summary>
    /// <remarks>実SQLiteに既定上限を超える履歴を保存し、未指定・非正上限で返る件数と最新IDを確認する。</remarks>
    [TestMethod]
    public void Lr2Reader_DefaultAndNonPositiveLimitUseDefaultLimit()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.BeginTransaction();
                try
                {
                    for (int index = 1; index <= Lr2PlayHistoryReader.DefaultReadLimit + 1; index++)
                    {
                        InsertHistory(db, historyId: index, hash: HashA, playedAt: index, finalized: true, newExscore: 200);
                    }
                    db.Commit();
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            }

            var reader = new Lr2PlayHistoryReader();
            Lr2PlayHistoryReadResult defaultResult = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true
            });
            Lr2PlayHistoryReadResult zeroResult = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                Limit = 0
            });
            Lr2PlayHistoryReadResult negativeResult = reader.Read(new Lr2PlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                IsLr2LinkedProfile = true,
                Limit = -1
            });

            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit, defaultResult.Rows.Count);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit + 1L, defaultResult.Rows[0].history_id);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit, zeroResult.Rows.Count);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit + 1L, zeroResult.Rows[0].history_id);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit, negativeResult.Rows.Count);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit + 1L, negativeResult.Rows[0].history_id);
        });
    }

    /// <summary>
    /// 期間索引は一覧上限を超える確定履歴も読み、未確定の時刻を日付候補へ混ぜない。
    /// </summary>
    /// <remarks>実SQLiteに一覧上限を超える履歴を保存し、期間索引に含まれる確定時刻の件数と降順を確認する。</remarks>
    [TestMethod]
    public void Lr2Reader_ReadPeriodIndexReadsFinalizedRowsWithoutDefaultLimit()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.BeginTransaction();
                try
                {
                    long baseEpoch = UtcEpoch(2020, 1, 1);
                    for (int index = 1; index <= Lr2PlayHistoryReader.DefaultReadLimit + 1; index++)
                    {
                        InsertHistory(db, historyId: index, hash: HashA, playedAt: baseEpoch + (index * 86400L), finalized: true, newExscore: 200);
                    }
                    InsertHistory(db, historyId: 99999, hash: HashB, playedAt: 99999, finalized: false, newExscore: 999);
                    db.Commit();
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            }

            Lr2PlayHistoryPeriodIndexResult result = new Lr2PlayHistoryReader().ReadPeriodIndex(
                new Lr2PlayHistoryPeriodIndexRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true
                },
                CancellationToken.None);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, result.SchemaStatus);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit + 1, result.PlayedAtUnixSeconds.Count);
            Assert.IsTrue(result.PlayedAtUnixSeconds[0] > result.PlayedAtUnixSeconds[result.PlayedAtUnixSeconds.Count - 1]);
        });
    }

    /// <summary>
    /// 最初に捕捉した全履歴はDB削除後も期間/未確定/日索引へ再利用し、失効後は実不在を診断する。
    /// </summary>
    /// <remarks>実SQLiteを初回読取り後に削除し、cacheからの期間・未確定・日索引取得と失効後の不在診断を確認する。</remarks>
    [TestMethod]
    public void PlayHistoryReadCache_Lr2LoadsAllRowsOnceAndFiltersFromMemory()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            long day1 = UtcEpoch(2026, 1, 1);
            long day2 = UtcEpoch(2026, 1, 2);
            long day3 = UtcEpoch(2026, 1, 3);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: day1, finalized: true, newExscore: 100);
                InsertHistory(db, historyId: 2, hash: HashA, playedAt: day2, finalized: true, newExscore: 200);
                InsertHistory(db, historyId: 3, hash: HashB, playedAt: day3, finalized: false, newExscore: 300);
            }

            var cache = new PlayHistoryReadCache();
            Lr2PlayHistoryReadResult first = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    PlayedAtFromInclusive = day1 - 100,
                    PlayedAtToExclusive = day1 + 100,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly
                },
                CancellationToken.None,
                out bool firstCacheHit);

            Assert.IsFalse(firstCacheHit);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, first.SchemaStatus);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, first.SchemaCheckResult.Status);
            CollectionAssert.AreEqual(new long[] { 1L }, first.Rows.Select(row => row.history_id).ToArray());

            File.Delete(scoreDbPath);

            Lr2PlayHistoryReadResult second = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    PlayedAtFromInclusive = day2 - 100,
                    PlayedAtToExclusive = day2 + 100,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly
                },
                CancellationToken.None,
                out bool secondCacheHit);

            Assert.IsTrue(secondCacheHit);
            CollectionAssert.AreEqual(new long[] { 2L }, second.Rows.Select(row => row.history_id).ToArray());

            Lr2PlayHistoryReadResult unfinalized = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly
                },
                CancellationToken.None,
                out bool unfinalizedCacheHit);

            Assert.IsTrue(unfinalizedCacheHit);
            CollectionAssert.AreEqual(new long[] { 3L }, unfinalized.Rows.Select(row => row.history_id).ToArray());

            Lr2PlayHistoryPeriodIndexResult periodIndex = cache.ReadLr2PeriodIndex(
                new Lr2PlayHistoryPeriodIndexRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true
                },
                CancellationToken.None,
                out bool periodIndexCacheHit);

            Assert.IsTrue(periodIndexCacheHit);
            CollectionAssert.AreEqual(new[] { day2, day1 }, periodIndex.PlayedAtUnixSeconds.ToArray());

            cache.Invalidate();
            Lr2PlayHistoryReadResult afterInvalidate = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly
                },
                CancellationToken.None,
                out bool afterInvalidateCacheHit);

            Assert.IsFalse(afterInvalidateCacheHit);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, afterInvalidate.SchemaStatus);
        });
    }

    /// <summary>
    /// 同じ全件cacheでも通常一覧は上限を保ち、明示DisableLimitだけ全件を返す。暖まった読取りで条件を失わない。
    /// </summary>
    /// <remarks>実SQLiteから捕捉した全件cacheへ異なる上限条件を適用し、cache利用状態と返却件数を確認する。</remarks>
    [TestMethod]
    public void PlayHistoryReadCache_Lr2KeepsDefaultLimitUnlessDisabled()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.BeginTransaction();
                try
                {
                    for (int index = 1; index <= Lr2PlayHistoryReader.DefaultReadLimit + 1; index++)
                    {
                        InsertHistory(db, historyId: index, hash: HashA, playedAt: index, finalized: true, newExscore: 200);
                    }
                    db.Commit();
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            }

            var cache = new PlayHistoryReadCache();
            Lr2PlayHistoryReadResult defaultLimited = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly
                },
                CancellationToken.None,
                out bool firstCacheHit);

            Lr2PlayHistoryReadResult unlimited = cache.ReadLr2(
                new Lr2PlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    IsLr2LinkedProfile = true,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.FinalizedOnly,
                    DisableLimit = true
                },
                CancellationToken.None,
                out bool secondCacheHit);

            Assert.IsFalse(firstCacheHit);
            Assert.IsTrue(secondCacheHit);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit, defaultLimited.Rows.Count);
            Assert.AreEqual(Lr2PlayHistoryReader.DefaultReadLimit + 1, unlimited.Rows.Count);
        });
    }

    /// <summary>
    /// beatorajaの履歴とplayer面を一度捕捉し、DB削除後も期間/日索引を返す。未確定診断は空、失効後は不在診断へ戻す。
    /// </summary>
    /// <remarks>実beatoraja DBを初回読取り後に削除し、cacheの履歴・player面・日索引と失効後の不在診断を確認する。</remarks>
    [TestMethod]
    public void PlayHistoryReadCache_BeatorajaLoadsAllRowsOnceAndFiltersFromMemory()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            long day1 = UtcEpoch(2026, 1, 1);
            long day2 = UtcEpoch(2026, 1, 2);
            long day3 = UtcEpoch(2026, 1, 3);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: day1, oldClear: 4, clear: 5, oldScore: 80, score: 120, oldCombo: 10, combo: 12, oldMinBp: 4, minBp: 3);
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: day2, oldClear: 5, clear: 6, oldScore: 100, score: 180, oldCombo: 12, combo: 24, oldMinBp: 3, minBp: 2);
                InsertBeatorajaScoreLog(db, ShaA, mode: 10000, date: day3, oldClear: 5, clear: 9, oldScore: 10, score: 99, oldCombo: 1, combo: 2, oldMinBp: 1, minBp: 0);
            }
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertBeatorajaPlayerAggregate(db, day1 - 1, playCount: 1, judgeCount: 100, playtime: 100);
                InsertBeatorajaPlayerAggregate(db, day2 - 1, playCount: 2, judgeCount: 160, playtime: 160);
                InsertBeatorajaPlayerAggregate(db, day3 - 1, playCount: 3, judgeCount: 210, playtime: 210);
            }

            var cache = new PlayHistoryReadCache();
            BeatorajaPlayHistoryReadResult first = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                    ScoreSnapshotVersion = 1,
                    PlayedAtFromInclusive = day1 - 100,
                    PlayedAtToExclusive = day1 + 100
                },
                CancellationToken.None,
                out bool firstCacheHit);

            Assert.IsFalse(firstCacheHit);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, first.SchemaStatus);
            CollectionAssert.AreEqual(new[] { day1 }, first.Rows.Select(row => row.played_at).ToArray());
            Assert.AreEqual(100, first.Rows[0].notes);
            Assert.IsTrue(first.PlayerSnapshotsAvailable);
            Assert.AreEqual(3, first.PlayerSnapshots.Count);

            File.Delete(scoreLogDbPath);

            BeatorajaPlayHistoryReadResult second = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoreLogDbPath = scoreLogDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                    ScoreSnapshotVersion = 1,
                    PlayedAtFromInclusive = day2 - 100,
                    PlayedAtToExclusive = day2 + 100
                },
                CancellationToken.None,
                out bool secondCacheHit);

            Assert.IsTrue(secondCacheHit);
            Assert.AreEqual(1, second.Rows.Count);
            Assert.AreEqual(day2, second.Rows[0].played_at);
            Assert.AreEqual(100, second.Rows[0].old_exscore);
            Assert.AreEqual(180, second.Rows[0].new_exscore);
            Assert.AreEqual(100, second.Rows[0].notes);
            Assert.IsTrue(second.PlayerSnapshotsAvailable);
            Assert.AreEqual(3, second.PlayerSnapshots.Count);

            BeatorajaPlayHistoryReadResult unfinalized = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                    ScoreSnapshotVersion = 1,
                    FinalizationFilter = Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly
                },
                CancellationToken.None,
                out bool unfinalizedCacheHit);

            Assert.IsTrue(unfinalizedCacheHit);
            Assert.AreEqual(0, unfinalized.Rows.Count);

            BeatorajaPlayHistoryPeriodIndexResult periodIndex = cache.ReadBeatorajaPeriodIndex(
                new BeatorajaPlayHistoryPeriodIndexRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                    ScoreSnapshotVersion = 1
                },
                CancellationToken.None,
                out bool periodIndexCacheHit);

            Assert.IsTrue(periodIndexCacheHit);
            CollectionAssert.AreEqual(new[] { day2, day1 }, periodIndex.PlayedAtUnixSeconds.ToArray());

            cache.Invalidate();
            BeatorajaPlayHistoryReadResult afterInvalidate = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath
                },
                CancellationToken.None,
                out bool afterInvalidateCacheHit);

            Assert.IsFalse(afterInvalidateCacheHit);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, afterInvalidate.SchemaStatus);
            Assert.AreEqual(0, afterInvalidate.Rows.Count);
            Assert.AreEqual("play_history_beatoraja_scorelog_missing", afterInvalidate.Diagnostics.Single().Code);
        });
    }

    /// <summary>
    /// スコア面の版とノーツ数が変わるとcacheを失効し、古いノーツ数を履歴へ残さない。
    /// </summary>
    /// <remarks>実scorelogの履歴と指定スコア面を使い、スコア版変更時のcache失効と返却ノーツ数を確認する。</remarks>
    [TestMethod]
    public void PlayHistoryReadCache_BeatorajaScoreSnapshotVersionInvalidatesCachedNotes()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: 1000, oldClear: 4, clear: 5, oldScore: 100, score: 133, oldCombo: 70, combo: 80, oldMinBp: 20, minBp: 10);
            }

            var cache = new PlayHistoryReadCache();
            BeatorajaPlayHistoryReadResult first = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                    ScoreSnapshotVersion = 1
                },
                CancellationToken.None,
                out bool firstCacheHit);
            BeatorajaPlayHistoryReadResult second = cache.ReadBeatoraja(
                new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 200)),
                    ScoreSnapshotVersion = 2
                },
                CancellationToken.None,
                out bool secondCacheHit);

            Assert.IsFalse(firstCacheHit);
            Assert.IsFalse(secondCacheHit);
            Assert.AreEqual(100, first.Rows.Single().notes);
            Assert.AreEqual(200, second.Rows.Single().notes);
        });
    }

    /// <summary>
    /// 実DBの読取り→期間索引→表示変換を順に接続し、同時刻ID降順・暖まった期間検索・失効後の追加/再読込み・指定昇順・観測取消を維持する。
    /// </summary>
    /// <remarks>実SQLite読取りから期間索引・表示変換へ接続し、返却表示行とcache利用・失効・取消を観測する。</remarks>
    [TestMethod]
    public void BuildReadView_Lr2OwnsReadPeriodProjectionPresentationAndCacheLifecycle()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateInstalledScoreDb(scoreDbPath);
            long playedAt = UtcEpoch(2026, 1, 2);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: playedAt, finalized: true, newExscore: 200);
                InsertHistory(db, historyId: 2, hash: HashA, playedAt: playedAt + 86400, finalized: true, newExscore: 250);
                InsertHistory(db, historyId: 3, hash: HashA, playedAt: playedAt + 86400, finalized: true, newExscore: 300);
            }

            string songDbPath = Path.Combine(Path.GetDirectoryName(scoreDbPath)!, "song.db");
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
            }
            var library = new TestBmsLibrary(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath);
            var owner = new PlayHistoryWorkflowOwner();
            var progressStages = new List<PlayHistoryReadWorkflowProgressStage>();
            PlayHistoryViewRequest firstRequest = owner.BeginRequest(
                PlayHistoryPeriodRequest.All(),
                string.Empty,
                PlayHistoryDisplayTargetItem.All.Identity,
                owner.DisplayTargetRevision);

            PlayHistoryReadWorkflowResult first = owner.BuildReadView(
                new PlayHistoryReadWorkflowRequest(
                    firstRequest,
                    PlayHistoryReadSourceContext.Lr2(scoreDbPath, isLr2LinkedProfile: true),
                    string.Empty,
                    PlayHistoryDisplayTargetItem.All,
                    summaryFilterTexts: null,
                    reportProgress: progress => progressStages.Add(progress.Stage)),
                library,
                playlist);

            Assert.IsTrue(first.Built);
            Assert.IsTrue(first.Read.Completed);
            Assert.IsFalse(first.Read.CacheHit);
            Assert.AreEqual(PlayHistoryProvider.Lr2, first.Read.Provider);
            Assert.AreEqual(3, first.Read.RowCount);
            Assert.AreEqual(PlayHistoryPeriodIndexStageStatus.Completed, first.PeriodIndex.Status);
            Assert.AreEqual(2, first.PeriodIndex.DayCount);
            Assert.AreEqual(PlayHistoryProjectionStageStatus.Completed, first.Projection.Status,
                string.Join("\n", first.Presentation.State.Diagnostics.Select(diagnostic => diagnostic.Message)));
            Assert.AreEqual(3, first.Projection.RawCount);
            Assert.AreEqual(3, first.Projection.ProjectedCount);
            Assert.AreEqual(3, first.Presentation.SortedRows.Count);
            Assert.AreEqual(3, first.Presentation.State.SourceCount);
            CollectionAssert.AreEqual(new long[] { 3, 2, 1 }, first.Presentation.SortedRows.Select(row => row.HistoryId).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    PlayHistoryReadWorkflowProgressStage.ReadCompleted,
                    PlayHistoryReadWorkflowProgressStage.PeriodIndexCompleted,
                    PlayHistoryReadWorkflowProgressStage.ProjectionCompleted
                },
                progressStages);

            File.Delete(scoreDbPath);
            PlayHistoryViewRequest secondRequest = owner.BeginRequest(
                PlayHistoryPeriodRequest.CreateDay(2026, 1, 3, TimeZoneInfo.Utc),
                string.Empty,
                PlayHistoryDisplayTargetItem.All.Identity,
                owner.DisplayTargetRevision);
            PlayHistoryReadWorkflowResult second = owner.BuildReadView(
                new PlayHistoryReadWorkflowRequest(
                    secondRequest,
                    PlayHistoryReadSourceContext.Lr2(scoreDbPath, isLr2LinkedProfile: true),
                    string.Empty,
                    PlayHistoryDisplayTargetItem.All,
                    summaryFilterTexts: null),
                library,
                playlist);

            Assert.IsTrue(second.Built);
            Assert.IsTrue(second.Read.CacheHit);
            Assert.IsTrue(second.PeriodIndex.CacheHit);
            Assert.AreEqual(2, second.Presentation.SortedRows.Count);
            CollectionAssert.AreEqual(new long[] { 3, 2 }, second.Presentation.SortedRows.Select(row => row.HistoryId).ToArray());

            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, historyId: 1, hash: HashA, playedAt: playedAt, finalized: true, newExscore: 200);
                InsertHistory(db, historyId: 2, hash: HashA, playedAt: playedAt + 86400, finalized: true, newExscore: 250);
                InsertHistory(db, historyId: 3, hash: HashA, playedAt: playedAt + 86400, finalized: true, newExscore: 300);
                InsertHistory(db, historyId: 4, hash: HashA, playedAt: playedAt + 172800, finalized: true, newExscore: 350);
            }
            owner.InvalidateReadCache("accepted_history_update");
            PlayHistoryViewRequest updatedRequest = owner.BeginRequest(PlayHistoryPeriodRequest.All(), string.Empty,
                PlayHistoryDisplayTargetItem.All.Identity, owner.DisplayTargetRevision);
            PlayHistoryReadWorkflowResult updated = owner.BuildReadView(new PlayHistoryReadWorkflowRequest(updatedRequest,
                PlayHistoryReadSourceContext.Lr2(scoreDbPath, isLr2LinkedProfile: true), string.Empty,
                PlayHistoryDisplayTargetItem.All, summaryFilterTexts: null), library, playlist);
            Assert.IsTrue(updated.Built);
            Assert.IsFalse(updated.Read.CacheHit);
            CollectionAssert.AreEqual(new long[] { 4, 3, 2, 1 }, updated.Presentation.SortedRows.Select(row => row.HistoryId).ToArray());
            owner.UpdateSortParameters(new ChartListSortParameters
            {
                ColumnsName = nameof(PlayHistoryRow.PlayedAt),
                Direction = ListSortDirection.Ascending
            });
            PlayHistoryViewRequest ascendingRequest = owner.BeginRequest(PlayHistoryPeriodRequest.All(), string.Empty,
                PlayHistoryDisplayTargetItem.All.Identity, owner.DisplayTargetRevision);
            PlayHistoryReadWorkflowResult ascending = owner.BuildReadView(new PlayHistoryReadWorkflowRequest(ascendingRequest,
                PlayHistoryReadSourceContext.Lr2(scoreDbPath, isLr2LinkedProfile: true), string.Empty,
                PlayHistoryDisplayTargetItem.All, summaryFilterTexts: null), library, playlist);
            Assert.IsTrue(ascending.Built);
            Assert.IsTrue(ascending.Read.CacheHit);
            CollectionAssert.AreEqual(new long[] { playedAt, playedAt + 86400, playedAt + 86400, playedAt + 172800 },
                ascending.Presentation.SortedRows.Select(row => row.PlayedAtUnix).ToArray());
            CollectionAssert.AreEquivalent(new long[] { 2, 3 }, ascending.Presentation.SortedRows.Skip(1).Take(2).Select(row => row.HistoryId).ToArray());

            PlayHistoryViewRequest thirdRequest = owner.BeginRequest(
                PlayHistoryPeriodRequest.All(),
                string.Empty,
                PlayHistoryDisplayTargetItem.All.Identity,
                owner.DisplayTargetRevision);
            Assert.ThrowsException<OperationCanceledException>(() => owner.BuildReadView(
                new PlayHistoryReadWorkflowRequest(
                    thirdRequest,
                    PlayHistoryReadSourceContext.Lr2(scoreDbPath, isLr2LinkedProfile: true),
                    string.Empty,
                    PlayHistoryDisplayTargetItem.All,
                    summaryFilterTexts: null,
                    reportProgress: progress =>
                    {
                        if (progress.Stage == PlayHistoryReadWorkflowProgressStage.PeriodIndexCompleted)
                        {
                            throw new OperationCanceledException("observer failure");
                        }
                    }),
                library,
                playlist));
        });
    }

    /// <summary>
    /// 読取不能は正常な空表示を構築せず、同じ取得元の次の通常要求で正常0件を取得できる。
    /// </summary>
    [TestMethod]
    public void BuildReadView_UnreadableIsNotBuiltAndNextNormalReadCanRecover()
    {
        WithScoreDb(scoreDbPath =>
        {
            var owner = new PlayHistoryWorkflowOwner();
            PlayHistoryReadWorkflowResult Read()
            {
                PlayHistoryViewRequest request = owner.BeginRequest(PlayHistoryPeriodRequest.All(), string.Empty,
                    PlayHistoryDisplayTargetItem.All.Identity, owner.DisplayTargetRevision);
                return owner.BuildReadView(new PlayHistoryReadWorkflowRequest(request,
                    PlayHistoryReadSourceContext.Lr2(scoreDbPath, isLr2LinkedProfile: true), string.Empty,
                    PlayHistoryDisplayTargetItem.All, summaryFilterTexts: null), library: null, playlist: null);
            }

            PlayHistoryReadWorkflowResult failed = Read();
            Assert.IsFalse(failed.Built, "読取不能を正常な0件表示へ採用しない。");
            Assert.IsNull(failed.Presentation);
            Assert.AreEqual(PlayHistoryPeriodIndexStageStatus.NotStarted, failed.PeriodIndex.Status);
            CreateInstalledScoreDb(scoreDbPath);
            PlayHistoryReadWorkflowResult recovered = Read();
            Assert.IsTrue(recovered.Built);
            Assert.IsFalse(recovered.Read.CacheHit);
            Assert.AreEqual(0, recovered.Presentation.SortedRows.Count);
        });
    }

    /// <summary>
    /// 古い要求は取得元や管理主体を使う前にread段階で中止し、索引・表示変換を開始しない。
    /// </summary>
    [TestMethod]
    public void BuildReadView_StaleRequestStopsAtReadBoundary()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest staleRequest = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            owner.DisplayTargetRevision);
        owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            owner.DisplayTargetRevision);

        PlayHistoryReadWorkflowResult result = owner.BuildReadView(
            new PlayHistoryReadWorkflowRequest(
                staleRequest,
                PlayHistoryReadSourceContext.Lr2("unused-score.db", isLr2LinkedProfile: true),
                string.Empty,
                PlayHistoryDisplayTargetItem.All,
                summaryFilterTexts: null),
            library: null,
            playlist: null);

        Assert.IsFalse(result.Built);
        Assert.AreEqual("read", result.CanceledStage);
        Assert.IsFalse(result.Read.Completed);
        Assert.AreEqual(PlayHistoryPeriodIndexStageStatus.NotStarted, result.PeriodIndex.Status);
        Assert.AreEqual(PlayHistoryProjectionStageStatus.NotStarted, result.Projection.Status);
    }

    /// <summary>
    /// 所持譜面/表を解決したLR2履歴はベスト前後と確定プレイの判定差分を分け、ランク・割合・オプション増分を表示する。
    /// </summary>
    [TestMethod]
    public void ProjectLr2Rows_ResolvesChartAndSeparatesBestDeltaFromActualResult()
    {
        var readResult = new Lr2PlayHistoryReadResult(
            PlayHistorySourceProfile.Lr2("score.db"),
            [
                CreateRawRecord(
                    historyId: 10,
                    hash: HashA,
                    playedAt: 1000,
                    finalized: true,
                    oldExscore: 200,
                    newExscore: 250,
                    newTotalNotes: 150,
                    oldClear: 0,
                    newClear: 3,
                    oldMinBp: null,
                    newMinBp: 20,
                    oldMaxCombo: 80,
                    newMaxCombo: 120,
                    perfectDelta: 100,
                    greatDelta: 40,
                    goodDelta: 10,
                    badDelta: 5,
                    poorDelta: 2,
                    playtimeDelta: 90,
                    judgeDelta: 157,
                    newOpBest: 23,
                    oldOpHistory: 1,
                    newOpHistory: 17)
            ],
            [],
            Lr2PlayHistorySchemaStatus.Installed);
        PlayHistoryProjectionIndex projectionIndex = CreateProjectionIndex();

        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(readResult, projectionIndex);

        Assert.AreEqual(1, projected.Rows.Count);
        PlayHistoryRow row = projected.Rows[0];
        Assert.AreEqual(PlayHistoryProvider.Lr2, row.Provider);
        Assert.AreEqual(PlayHistoryHashKind.Chart, row.HashKind);
        Assert.AreEqual("Resolved Title", row.Title);
        Assert.AreEqual("Resolved Artist", row.Artist);
        Assert.AreEqual(ShaA, row.Sha256);
        Assert.AreEqual("SAT", row.FolderLabels);
        Assert.AreEqual("NP -> NORMAL", row.BestClear);
        Assert.AreEqual("200 -> 250", row.BestExscore);
        Assert.AreEqual("20", row.BestBp);
        Assert.AreEqual("80 -> 120", row.BestCombo);
        Assert.AreEqual(RankType.AA, row.BestDjLevel);
        Assert.AreEqual("A -> AA", row.BestDjLevelText);
        Assert.IsTrue(row.BestRate.HasValue);
        Assert.AreEqual(250 / 300.0, row.BestRate.Value, 0.0001);
        Assert.AreEqual("66.67 -> 83.33", row.BestRateText);
        Assert.AreEqual(240, row.PlayExscore);
        Assert.AreEqual(250, row.NewBestExscore);
        Assert.AreEqual(90, row.PlaytimeSeconds);
        Assert.AreEqual(157, row.JudgeTotal);
        Assert.AreEqual("PG 100 / GR 40 / GD 10 / BD 5 / PR 2", row.Judges);
        Assert.AreEqual("EASY RANDOM", row.Option);
        Assert.AreEqual(16, row.OpHistoryNewBits);
        Assert.AreEqual("P.A", row.OpHistory);
        Assert.AreEqual("score bp clear combo", row.Kind);
    }

    /// <summary>
    /// 表単体を選ぶと一致するMD5行へその表のフォルダ表示を付け、元行の表示を変更しない。
    /// </summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_PlaylistTargetUsesPlaylistFolder()
    {
        PlayHistoryRow row = CreateProjectedRow(HashA);
        BMSTable table = CreateTargetTable(HashA, "Alpha");
        var index = PlayHistoryDisplayTargetIndex.Create(
            PlayHistoryDisplayTargetItem.FromPlaylist(table),
            [table],
            _ => { });

        bool matched = index.TryApply(row, out PlayHistoryRow displayRow);

        Assert.IsTrue(matched);
        Assert.AreEqual("Alpha", displayRow.FolderLabels);
        Assert.AreEqual(string.Empty, row.FolderLabels);
    }

    /// <summary>
    /// preset対象は一致行だけを通し、表記号とレベルを表示する。非対象行を通す誤りを検出する。
    /// </summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_TargetSetFiltersRowsAndUsesOrgSymbolLevel()
    {
        PlayHistoryRow matchingRow = CreateProjectedRow(HashA);
        PlayHistoryRow otherRow = CreateProjectedRow(HashB);
        BMSTable table = CreateTargetTable(HashA, "Alpha");
        var target = PlayHistoryDisplayTargetItem.FromTargetSet(new PlayHistoryDisplayTargetSet
        {
            Name = "SAT Alpha",
            Targets =
            [
                new PlayHistoryDisplayTargetReference
                {
                    PlaylistId = table.playlist_id
                }
            ]
        });
        var index = PlayHistoryDisplayTargetIndex.Create(target, [table], _ => { });

        bool matched = index.TryApply(matchingRow, out PlayHistoryRow displayRow);
        bool otherMatched = index.TryApply(otherRow, out _);

        Assert.IsTrue(matched);
        Assert.IsFalse(otherMatched);
        Assert.AreEqual("SATAlpha", displayRow.FolderLabels);
    }

    /// <summary>
    /// FOLDER専用presetは全行を残し、一致行だけ表記号/レベルを付けて非対象行の古い表示を消す。
    /// </summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_TargetSetProjectionOnlyKeepsRowsAndClearsUnmatchedFolder()
    {
        PlayHistoryRow matchingRow = CreateProjectedRow(HashA, initialFolderLabels: "Original");
        PlayHistoryRow otherRow = CreateProjectedRow(HashB, initialFolderLabels: "Original");
        BMSTable table = CreateTargetTable(HashA, "Alpha");
        var target = PlayHistoryDisplayTargetItem.FromTargetSetProjectionOnly(new PlayHistoryDisplayTargetSet
        {
            Name = "SAT Alpha",
            Targets =
            [
                new PlayHistoryDisplayTargetReference
                {
                    PlaylistId = table.playlist_id
                }
            ]
        });
        var index = PlayHistoryDisplayTargetIndex.Create(target, [table], _ => { });

        bool matched = index.TryApply(matchingRow, out PlayHistoryRow displayRow);
        bool otherMatched = index.TryApply(otherRow, out PlayHistoryRow otherDisplayRow);

        Assert.IsTrue(matched);
        Assert.IsTrue(otherMatched);
        Assert.IsFalse(target.IsFiltering);
        Assert.IsTrue(target.UsesProjection);
        Assert.AreEqual("SATAlpha", displayRow.FolderLabels);
        Assert.AreEqual(string.Empty, otherDisplayRow.FolderLabels);
    }

    /// <summary>
    /// 同名・同記号の別ID表はpreset参照とみなさず、指定IDだけを使って表示する。
    /// </summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_TargetSetUsesPlaylistIdOnly()
    {
        PlayHistoryRow idMatchRow = CreateProjectedRow(HashA);
        PlayHistoryRow nameOnlyRow = CreateProjectedRow(HashB);
        BMSTable idMatchedTable = CreateTargetTable(HashA, "Alpha");
        idMatchedTable.playlist_id = 101;
        BMSTable sameNameDifferentIdTable = CreateTargetTable(HashB, "Beta");
        sameNameDifferentIdTable.playlist_id = 202;
        sameNameDifferentIdTable.name = idMatchedTable.name;
        sameNameDifferentIdTable.symbol = idMatchedTable.symbol;
        sameNameDifferentIdTable.org_symbol = idMatchedTable.org_symbol;
        var target = PlayHistoryDisplayTargetItem.FromTargetSet(new PlayHistoryDisplayTargetSet
        {
            Name = "SAT Alpha",
            Targets =
            [
                new PlayHistoryDisplayTargetReference
                {
                    PlaylistId = idMatchedTable.playlist_id
                }
            ]
        });
        var index = PlayHistoryDisplayTargetIndex.Create(target, [idMatchedTable, sameNameDifferentIdTable], _ => { });

        bool idMatched = index.TryApply(idMatchRow, out PlayHistoryRow displayRow);
        bool sameNameMatched = index.TryApply(nameOnlyRow, out _);

        Assert.IsTrue(idMatched);
        Assert.IsFalse(sameNameMatched);
        Assert.AreEqual("SATAlpha", displayRow.FolderLabels);
    }

    /// <summary>
    /// 全件対象は表snapshotを要求せず既存行をそのまま使い、追加走査と表表示の消失を防ぐ。
    /// </summary>
    [TestMethod]
    public void ApplyPlayHistoryDisplayTargetRows_AllKeepsProjectedRowsFastPath()
    {
        var owner = new PlayHistoryWorkflowOwner();
        IReadOnlyList<PlayHistoryRow> rows = [CreateProjectedRow(HashA, initialFolderLabels: "SAT")];
        IReadOnlyList<PlayHistoryRow> result = owner.ApplyDisplayTarget(
            rows,
            PlayHistoryDisplayTargetItem.All,
            requestId: 1L,
            displayTargetRevision: 0L,
            CancellationToken.None,
            tableSnapshotFactory: () => throw new AssertFailedException("All must not snapshot display-target tables."),
            ensureEntriesLoaded: null);

        Assert.AreSame(rows, result);
        Assert.AreEqual("SAT", rows[0].FolderLabels);
    }

    /// <summary>
    /// 古い要求は表snapshot取得前に取消を返し、不要な遅延読込みを開始しない。
    /// </summary>
    [TestMethod]
    public void ApplyPlayHistoryDisplayTargetRows_StaleRequestStopsBeforeTableSnapshot()
    {
        var owner = new PlayHistoryWorkflowOwner();
        IReadOnlyList<PlayHistoryRow> rows = [CreateProjectedRow(HashA, initialFolderLabels: "SAT")];

        Assert.ThrowsException<OperationCanceledException>(() => owner.ApplyDisplayTarget(
            rows,
            PlayHistoryDisplayTargetItem.FromPlaylist(new BMSTable()),
            requestId: 1L,
            displayTargetRevision: owner.DisplayTargetRevision,
            CancellationToken.None,
            tableSnapshotFactory: () => throw new AssertFailedException("Stale requests must not snapshot display-target tables."),
            ensureEntriesLoaded: null));
    }

    /// <summary>
    /// 利用者の整列条件を切離して保持し、同値更新では版を変えず異なる条件へ変更すると旧snapshotを失効させる。
    /// </summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_OwnsSortParametersAndFreshnessSnapshot()
    {
        var owner = new PlayHistoryWorkflowOwner();
        Assert.IsNull(owner.CaptureSortParameters(out SortSnapshot initialSnapshot));
        Assert.IsTrue(owner.IsCurrentSortSnapshot(initialSnapshot));
        Assert.IsTrue(owner.UpdateSortParameters(new ChartListSortParameters()));
        Assert.IsNotNull(owner.CaptureSortParameters(out _));
        Assert.IsTrue(owner.UpdateSortParameters(null));
        var requested = new ChartListSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.PlayedAt),
            Direction = ListSortDirection.Descending
        };

        Assert.IsTrue(owner.UpdateSortParameters(requested));
        ChartListSortParameters captured = owner.CaptureSortParameters(out SortSnapshot snapshot);
        requested.ColumnsName = nameof(PlayHistoryRow.Title);

        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), captured.ColumnsName);
        Assert.IsTrue(owner.IsCurrentSortSnapshot(snapshot));
        Assert.IsFalse(owner.UpdateSortParameters(captured));
        Assert.IsTrue(owner.UpdateSortParameters(new ChartListSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.Title),
            Direction = ListSortDirection.Ascending
        }));
        Assert.IsFalse(owner.IsCurrentSortSnapshot(snapshot));
    }

    /// <summary>
    /// 整列・表示対象・キーワードの各変更を別のstale理由へ分類し、既に最新面があれば重複refreshを要求しない。
    /// </summary>
    [TestMethod]
    public void PlayHistoryWorkflowOwner_OwnsCurrentViewFreshnessAndRefreshCandidate()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest request = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: owner.DisplayTargetRevision);
        owner.UpdateSortParameters(new ChartListSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.PlayedAt),
            Direction = ListSortDirection.Descending
        });
        owner.CaptureSortParameters(out SortSnapshot sortSnapshot);
        PlayHistoryViewState state = CreatePlayHistoryViewState(
            request,
            sortSnapshot,
            keywordFilter: string.Empty,
            keywordRevision: owner.KeywordRevision,
            displayTarget: PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: owner.DisplayTargetRevision);
        owner.PresentationState.CurrentView = state;

        Assert.IsTrue(owner.TrySnapshotCurrentView(request, out PlayHistoryViewState currentView));
        Assert.AreSame(state, currentView);
        Assert.AreEqual(
            PlayHistoryPresentationFreshnessStatus.Fresh,
            owner.EvaluateTerminalPresentationFreshness(state, string.Empty, PlayHistoryDisplayTargetItem.All).Status);

        owner.UpdateSortParameters(new ChartListSortParameters
        {
            ColumnsName = nameof(PlayHistoryRow.Title),
            Direction = ListSortDirection.Ascending
        });
        Assert.AreEqual(
            PlayHistoryPresentationFreshnessStatus.SortStale,
            owner.EvaluateTerminalPresentationFreshness(state, string.Empty, PlayHistoryDisplayTargetItem.All).Status);

        owner.CaptureSortParameters(out SortSnapshot latestSortSnapshot);
        state = CreatePlayHistoryViewState(
            request,
            latestSortSnapshot,
            keywordFilter: string.Empty,
            keywordRevision: owner.KeywordRevision,
            displayTarget: PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: owner.DisplayTargetRevision);
        owner.PresentationState.CurrentView = state;

        var latestTarget = PlayHistoryDisplayTargetItem.FromPlaylist(CreateTargetTable(HashA, "Latest"));
        long latestDisplayRevision = owner.AdvanceDisplayTargetRevision(latestTarget.Identity);
        PlayHistoryPresentationFreshnessResult displayStale = owner.EvaluateTerminalPresentationFreshness(
            state,
            string.Empty,
            latestTarget);
        Assert.AreEqual(PlayHistoryPresentationFreshnessStatus.DisplayTargetStale, displayStale.Status);
        Assert.IsTrue(displayStale.QueueRefresh);
        Assert.AreSame(state, owner.PresentationState.CurrentView);

        PlayHistoryViewState latestDisplayState = CreatePlayHistoryViewState(
            request,
            latestSortSnapshot,
            keywordFilter: string.Empty,
            keywordRevision: owner.KeywordRevision,
            displayTarget: latestTarget,
            displayTargetRevision: latestDisplayRevision);
        owner.PresentationState.CurrentView = latestDisplayState;
        displayStale = owner.EvaluateTerminalPresentationFreshness(state, string.Empty, latestTarget);
        Assert.AreEqual(PlayHistoryPresentationFreshnessStatus.DisplayTargetStale, displayStale.Status);
        Assert.IsFalse(displayStale.QueueRefresh);
        Assert.AreSame(latestDisplayState, owner.PresentationState.CurrentView);

        long latestKeywordRevision = owner.UpdateKeywordIdentity("latest", advanceRevision: true);
        PlayHistoryPresentationFreshnessResult keywordStale = owner.EvaluateTerminalPresentationFreshness(
            latestDisplayState,
            "latest",
            latestTarget);
        Assert.AreEqual(PlayHistoryPresentationFreshnessStatus.KeywordStale, keywordStale.Status);
        Assert.IsTrue(keywordStale.QueueRefresh);

        PlayHistoryViewState latestKeywordState = CreatePlayHistoryViewState(
            request,
            latestSortSnapshot,
            keywordFilter: "latest",
            keywordRevision: latestKeywordRevision,
            displayTarget: latestTarget,
            displayTargetRevision: latestDisplayRevision);
        owner.PresentationState.CurrentView = latestKeywordState;
        keywordStale = owner.EvaluateTerminalPresentationFreshness(latestDisplayState, "latest", latestTarget);
        Assert.AreEqual(PlayHistoryPresentationFreshnessStatus.KeywordStale, keywordStale.Status);
        Assert.IsFalse(keywordStale.QueueRefresh);
        Assert.AreSame(latestKeywordState, owner.PresentationState.CurrentView);
    }

    /// <summary>
    /// MD5を持つ表entryは異なるMD5行とSHA256だけで一致させず、別譜面への誤参照を防ぐ。
    /// </summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_UsesPlaylistEntryMd5BeforeSha256()
    {
        PlayHistoryRow row = CreateProjectedRow(HashA, ShaA, initialFolderLabels: "SAT");
        BMSTable table = CreateTargetTable(HashB, "Alpha", ShaA);
        var index = PlayHistoryDisplayTargetIndex.Create(
            PlayHistoryDisplayTargetItem.FromPlaylist(table),
            [table],
            _ => { });

        bool matched = index.TryApply(row, out _);

        Assert.IsFalse(matched);
    }

    /// <summary>
    /// 一致する表entryのフォルダが空なら過去のフォルダ表示を消し、古いラベルを引き継がない。
    /// </summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetIndex_PlaylistTargetKeepsEmptyFolderEmpty()
    {
        PlayHistoryRow row = CreateProjectedRow(HashA, initialFolderLabels: "SAT");
        BMSTable table = CreateTargetTable(HashA, string.Empty);
        var index = PlayHistoryDisplayTargetIndex.Create(
            PlayHistoryDisplayTargetItem.FromPlaylist(table),
            [table],
            _ => { });

        bool matched = index.TryApply(row, out PlayHistoryRow displayRow);

        Assert.IsTrue(matched);
        Assert.AreEqual(string.Empty, displayRow.FolderLabels);
    }

    /// <summary>
    /// 解決できない未確定履歴も生ハッシュ付きで残し、架空のタイトルや実プレイスコアを補わない。
    /// </summary>
    [TestMethod]
    public void ProjectLr2Rows_UnresolvedUnfinalizedRowRemainsVisible()
    {
        var readResult = new Lr2PlayHistoryReadResult(
            PlayHistorySourceProfile.Lr2("score.db"),
            [CreateRawRecord(11, HashC, 1000, finalized: false, oldExscore: null, newExscore: 100, newTotalNotes: 100)],
            [],
            Lr2PlayHistorySchemaStatus.Installed);

        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(readResult, PlayHistoryProjectionIndex.Empty);

        Assert.AreEqual(1, projected.Rows.Count);
        PlayHistoryRow row = projected.Rows[0];
        Assert.AreEqual(HashC, row.RawHash);
        Assert.AreEqual(PlayHistoryHashKind.Unknown, row.HashKind);
        Assert.AreEqual(string.Empty, row.Title);
        Assert.IsFalse(row.Finalized);
        Assert.IsNull(row.PlayExscore);
    }

    /// <summary>
    /// ベスト値が変わらない確定プレイは更新列を空にし、実プレイEXSCOREだけを残す。毎プレイをベスト更新と誤認しない。
    /// </summary>
    [TestMethod]
    public void ProjectLr2Rows_BestScoreColumnsAreBlankWhenBestScoreDidNotChange()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        12,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 250,
                        newExscore: 250,
                        newTotalNotes: 150,
                        oldClear: 3,
                        newClear: 3,
                        oldMinBp: 20,
                        newMinBp: 20,
                        oldMaxCombo: 120,
                        newMaxCombo: 120,
                        perfectDelta: 90,
                        greatDelta: 30,
                        judgeDelta: 150)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        Assert.IsFalse(row.BestScoreUpdated);
        Assert.AreEqual(string.Empty, row.BestExscore);
        Assert.AreEqual(RankType.INVALID, row.BestDjLevel);
        Assert.IsNull(row.BestRate);
        Assert.AreEqual(string.Empty, row.BestClear);
        Assert.AreEqual(string.Empty, row.BestBp);
        Assert.AreEqual(string.Empty, row.BestCombo);
        Assert.AreEqual("play", row.Kind);
        Assert.AreEqual(210, row.PlayExscore);
    }

    /// <summary>
    /// 旧新clearがFCのままでも新PERFECTビットでPAへ昇格し、新PERFECT集計へ一件加える。
    /// </summary>
    [TestMethod]
    public void ProjectLr2Rows_OpHistoryPerfectBitPromotesFullComboToPerfect()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        13,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 250,
                        newExscore: 300,
                        newTotalNotes: 150,
                        oldClear: 5,
                        newClear: 5,
                        oldOpHistory: 0,
                        newOpHistory: 0x10)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        Assert.AreEqual(ClearType.FC, row.OldBestClear);
        Assert.AreEqual(ClearType.PA, row.NewBestClear);
        Assert.IsTrue(row.BestClearUpdated);
        Assert.AreEqual("FC -> PA", row.BestClear);

        var summary = PlayHistoryPeriodSummary.FromRows("all", projected.Rows);
        Assert.AreEqual(1, summary.NewPerfectCount);
    }

    /// <summary>
    /// 確定/未確定を行数で区別し、演奏時間・判定・更新集計には確定した解決済み行だけを加える。
    /// </summary>
    [TestMethod]
    public void SummaryCountsOnlyFinalizedRowsForPlaytimeAndJudges()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(1, HashA, 1000, finalized: true, oldExscore: 100, newExscore: 120, newTotalNotes: 100, oldClear: 0, newClear: 5, perfectDelta: 40, greatDelta: 20, playtimeDelta: 60, judgeDelta: 80),
                    CreateRawRecord(2, HashB, 2000, finalized: false, oldExscore: 100, newExscore: 140, newTotalNotes: 100, oldClear: 5, newClear: 5, playtimeDelta: 500, judgeDelta: 500)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        var summary = PlayHistoryPeriodSummary.FromRows("today", projected.Rows);

        Assert.AreEqual("today", summary.Label);
        Assert.AreEqual(2, summary.RowCount);
        Assert.AreEqual(1L, summary.FinalizedCount);
        Assert.AreEqual(1L, summary.UnfinalizedCount);
        Assert.AreEqual(1, summary.SummaryEligibleCount);
        Assert.AreEqual(60L, summary.PlaytimeSeconds);
        Assert.AreEqual(80L, summary.JudgeCount);
        Assert.AreEqual(1, summary.ScoreUpdateCount);
        Assert.AreEqual(1, summary.NewClearCount);
    }

    /// <summary>
    /// 履歴の集約テキストへ診断code・詳細・対象DBを含め、利用者が原因と対象を識別できる。
    /// </summary>
    [TestMethod]
    public void SummaryTextIncludesDiagnosticDetailWhenPresent()
    {
        var request = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.All,
            new DateTimeOffset(2026, 6, 19, 15, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        var summary = PlayHistoryPeriodSummary.FromRows("all", []);

        string text = PlayHistoryPresentationState.FormatGridSummaryText(
            request,
            summary,
            [
                new PlayHistoryDiagnostic
                {
                    Provider = PlayHistoryProvider.Lr2,
                    Stage = "read",
                    Severity = PlayHistoryDiagnosticSeverity.Error,
                    Code = "play_history_lr2_schema_unreadable",
                    Message = "Score DB file does not exist.",
                    SourcePath = "C:\\LR2\\Score\\player.db"
                }
            ]);

        StringAssert.Contains(text, "play_history_lr2_schema_unreadable");
        StringAssert.Contains(text, "Score DB file does not exist.");
        StringAssert.Contains(text, "C:\\LR2\\Score\\player.db");
    }

    /// <summary>
    /// 履歴の判定数・プレイ数・時間・各更新内訳をカードへ渡し、更新カードの検索条件を対応付ける。LR2へEXHを混ぜない。
    /// </summary>
    [TestMethod]
    public void SummaryCardsExposeDedicatedPlayHistoryMetrics()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(1, HashA, 1000, finalized: true, oldExscore: 100, newExscore: 120, newTotalNotes: 100, oldClear: 0, newClear: 2, newOpHistory: ClearTypeStorageConverter.OptionHistoryEasy, oldMinBp: null, newMinBp: 10, oldMaxCombo: 50, newMaxCombo: 80, playtimeDelta: 70, judgeDelta: 100),
                    CreateRawRecord(2, HashA, 2000, finalized: true, oldExscore: 100, newExscore: 100, newTotalNotes: 100, oldClear: 3, newClear: 5, playtimeDelta: 50, judgeDelta: 60)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());
        var summary = PlayHistoryPeriodSummary.FromRows("today", projected.Rows);

        IReadOnlyList<PlayHistorySummaryCard> cards = PlayHistoryPresentationState.CreateSummaryCards(summary, PlayHistoryProvider.Lr2);

        Assert.AreEqual(Resources.Play_history_summary_judge_count, cards.Single(card => card.Label == Resources.Play_history_summary_judge_count).Label);
        Assert.AreEqual("160", cards.Single(card => card.Label == Resources.Play_history_summary_judge_count).Value);
        Assert.AreEqual(Resources.Play_history_summary_play_count, cards.Single(card => card.Label == Resources.Play_history_summary_play_count).Label);
        Assert.AreEqual("2", cards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
        Assert.AreEqual(Resources.Play_history_summary_playtime, cards.Single(card => card.Label == Resources.Play_history_summary_playtime).Label);
        Assert.AreEqual("2:00", cards.Single(card => card.Label == Resources.Play_history_summary_playtime).Value);
        Assert.AreEqual(Resources.Play_history_summary_score_update, cards.Single(card => card.Label == Resources.Play_history_summary_score_update).Label);
        Assert.AreEqual("1", cards.Single(card => card.Label == Resources.Play_history_summary_score_update).Value);
        Assert.AreEqual("score", cards.Single(card => card.Label == Resources.Play_history_summary_score_update).FilterKey);
        Assert.AreEqual("type:score", cards.Single(card => card.Label == Resources.Play_history_summary_score_update).FilterText);
        Assert.AreEqual(Resources.Play_history_summary_bp_update, cards.Single(card => card.Label == Resources.Play_history_summary_bp_update).Label);
        Assert.AreEqual("1", cards.Single(card => card.Label == Resources.Play_history_summary_bp_update).Value);
        Assert.AreEqual("type:bp", cards.Single(card => card.Label == Resources.Play_history_summary_bp_update).FilterText);
        Assert.AreEqual(Resources.Play_history_summary_combo_update, cards.Single(card => card.Label == Resources.Play_history_summary_combo_update).Label);
        Assert.AreEqual("1", cards.Single(card => card.Label == Resources.Play_history_summary_combo_update).Value);
        Assert.AreEqual("type:combo", cards.Single(card => card.Label == Resources.Play_history_summary_combo_update).FilterText);
        Assert.AreEqual(Resources.Play_history_summary_clear_update, cards.Single(card => card.Label == Resources.Play_history_summary_clear_update).Label);
        Assert.AreEqual("2", cards.Single(card => card.Label == Resources.Play_history_summary_clear_update).Value);
        Assert.AreEqual("type:clear", cards.Single(card => card.Label == Resources.Play_history_summary_clear_update).FilterText);
        Assert.AreEqual("EASY", cards.Single(card => card.Label == "EASY").Label);
        Assert.AreEqual("1", cards.Single(card => card.Label == "EASY").Value);
        Assert.AreEqual("type:clear newclear:EC", cards.Single(card => card.Label == "EASY").FilterText);
        Assert.IsFalse(cards.Any(card => card.Label == "EXH"));
        Assert.AreEqual("FC", cards.Single(card => card.Label == "FC").Label);
        Assert.AreEqual("1", cards.Single(card => card.Label == "FC").Value);
        Assert.AreEqual("type:clear newclear:FC|PF", cards.Single(card => card.Label == "FC").FilterText);
        Assert.IsFalse(cards.Single(card => card.Label == Resources.Play_history_summary_judge_count).IsFilterable);
        Assert.IsTrue(cards.Single(card => card.Label == Resources.Play_history_summary_score_update).IsFilterable);
    }

    /// <summary>
    /// 選択したscore/FCだけをカードと検索条件へ反映し、集約診断はwarningよりerror詳細を先に示す。
    /// </summary>
    [TestMethod]
    public void PresentationOwnerMapsSelectedSummaryFiltersAndDiagnosticPriority()
    {
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal) { "score", "fc" };
        var summary = PlayHistoryPeriodSummary.FromRows("all", []);

        IReadOnlyList<PlayHistorySummaryCard> cards = PlayHistoryPresentationState.CreateSummaryCards(
            summary,
            PlayHistoryProvider.Lr2,
            selectedKeys);
        IReadOnlyList<string> filterTexts = PlayHistoryPresentationState.GetSummaryFilterTexts(selectedKeys);
        string diagnostic = PlayHistoryPresentationState.FormatDiagnosticSummary(
        [
            null,
            new PlayHistoryDiagnostic { Severity = PlayHistoryDiagnosticSeverity.Warning, Code = "warning" },
            new PlayHistoryDiagnostic { Severity = PlayHistoryDiagnosticSeverity.Error, Code = "error", Message = "detail" }
        ]);

        Assert.IsTrue(cards.Single(card => card.FilterKey == "score").IsSelected);
        Assert.IsTrue(cards.Single(card => card.FilterKey == "fc").IsSelected);
        Assert.IsFalse(cards.Single(card => card.FilterKey == "bp").IsSelected);
        CollectionAssert.AreEquivalent(new[] { "type:score", "type:clear newclear:FC|PF" }, filterTexts.ToArray());
        StringAssert.StartsWith(diagnostic, "Error error: detail");
        Assert.AreEqual(string.Empty, PlayHistoryPresentationState.FormatDiagnosticSummary([null]));
    }

    /// <summary>
    /// playlist summary選択の実構成は履歴集計と診断を消し、archive日付面を保持する。画面切替の残留表示を検出する。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public async Task SelectPlaylistSummaryClearsPlayHistorySummaryPresentation()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        PlayHistoryPeriodTreeItem[] archive = new[] { new PlayHistoryPeriodTreeItem("archive", PlayHistoryPeriodRequest.All()) };
        viewModel.PlayHistory.PresentationState.SetArchivePeriodTree(archive);
        viewModel.PlayHistory.PresentationState.SetSummaryCards(new[] { new PlayHistorySummaryCard(Resources.Play_history_summary_judge_count, "1") });
        viewModel.PlayHistory.PresentationState.SetDiagnosticText("diagnostic");

        viewModel.PlaylistWorkspace.RequestSummarySelection();
        TestUiDispatcherHost.ProcessQueuedPresentation();
        await viewModel.PlaylistWorkspace.WaitForPlaylistSummaryDataBuildIdleAsync()
            .ConfigureAwait(false);
        TestUiDispatcherHost.ProcessQueuedPresentation();

        Assert.AreEqual(0, viewModel.PlayHistory.SummaryCards.Count);
        Assert.AreEqual(string.Empty, viewModel.PlayHistory.SummaryDiagnosticText);
        Assert.AreSame(archive, viewModel.PlayHistory.ArchivePeriodTree);
    }

    /// <summary>
    /// カード選択を子表示へ反映してからrefreshを一度通知し、非検索カードでは通知も表示更新も増やさない。
    /// </summary>
    [TestMethod]
    public void SummaryFilterCommandUpdatesChildPresentationBeforeRequestingRefresh()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistorySummaryCard scoreCard = PlayHistoryPresentationState.CreateSummaryCards(
            PlayHistoryPeriodSummary.FromRows("All", []),
            PlayHistoryProvider.Lr2).Single(card => card.FilterKey == "score");
        owner.PresentationState.SetSummaryCards([scoreCard]);
        var propertyNames = new List<string>();
        int refreshRequests = 0;
        bool selectedWhenRefreshRequested = false;
        owner.PropertyChanged += (_, e) => propertyNames.Add(e.PropertyName!);
        owner.SummaryFilterRefreshRequested += (_, _) =>
        {
            refreshRequests++;
            selectedWhenRefreshRequested = owner.SummaryCards.Single().IsSelected;
        };

        owner.ToggleSummaryFilterCommand.Execute(scoreCard);

        Assert.IsTrue(owner.SummaryCards.Single().IsSelected);
        Assert.IsTrue(selectedWhenRefreshRequested);
        Assert.AreEqual(1, refreshRequests);
        CollectionAssert.AreEqual(new[] { nameof(PlayHistoryWorkflowOwner.SummaryCards) }, propertyNames);

        owner.ToggleSummaryFilterCommand.Execute(new PlayHistorySummaryCard("label", "value"));
        Assert.AreEqual(1, refreshRequests);
        Assert.AreEqual(1, propertyNames.Count);
    }

    /// <summary>
    /// EXH更新内訳と検索条件はbeatorajaだけへ表示し、LR2のカードに非対応clearを追加しない。
    /// </summary>
    [TestMethod]
    public void SummaryCardsIncludeExHardOnlyForBeatorajaProvider()
    {
        var readResult = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("score.db"),
            [
                new BeatorajaPlayHistoryRecord
                {
                    history_id = 1,
                    sha256 = ShaA,
                    played_at = 1000,
                    playcount = 1,
                    old_clear = (int)ClearType.HARD,
                    new_clear = (int)ClearType.EX_HARD,
                    old_exscore = 100,
                    new_exscore = 120,
                    notes = 100
                }
            ],
            [],
            Lr2PlayHistorySchemaStatus.Installed);
        var summary = PlayHistoryPeriodSummary.FromRows(
            "today",
            PlayHistoryRow.ProjectBeatorajaRows(readResult, CreateProjectionIndex()).Rows);

        IReadOnlyList<PlayHistorySummaryCard> beatorajaCards = PlayHistoryPresentationState.CreateSummaryCards(summary, PlayHistoryProvider.Beatoraja);
        IReadOnlyList<PlayHistorySummaryCard> lr2Cards = PlayHistoryPresentationState.CreateSummaryCards(summary, PlayHistoryProvider.Lr2);

        Assert.IsTrue(beatorajaCards.Any(card => card.Label == "EXH" && card.Value == "1"));
        Assert.AreEqual("type:clear newclear:EXH", beatorajaCards.Single(card => card.Label == "EXH").FilterText);
        Assert.IsFalse(lr2Cards.Any(card => card.Label == "EXH"));
    }

    /// <summary>
    /// 集計根拠が未取得のプレイ数・判定・時間は0へ代用せず利用不能と「-」を表示する。
    /// </summary>
    [TestMethod]
    public void SummaryCardsShowDashWhenSnapshotValuesAreUnavailable()
    {
        var summary = PlayHistoryPeriodSummary.FromRows(
            "summary",
            [],
            new PlayHistoryPeriodSummaryOverride(null, null, null));

        IReadOnlyList<PlayHistorySummaryCard> cards = PlayHistoryPresentationState.CreateSummaryCards(summary, PlayHistoryProvider.Beatoraja);

        Assert.IsFalse(summary.PlayCountAvailable);
        Assert.IsFalse(summary.JudgeCountAvailable);
        Assert.IsFalse(summary.PlaytimeAvailable);
        Assert.AreEqual(Resources.Play_history_summary_judge_count, cards.Single(card => card.Label == Resources.Play_history_summary_judge_count).Label);
        Assert.AreEqual("-", cards.Single(card => card.Label == Resources.Play_history_summary_judge_count).Value);
        Assert.AreEqual(Resources.Play_history_summary_play_count, cards.Single(card => card.Label == Resources.Play_history_summary_play_count).Label);
        Assert.AreEqual("-", cards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
        Assert.AreEqual(Resources.Play_history_summary_playtime, cards.Single(card => card.Label == Resources.Play_history_summary_playtime).Label);
        Assert.AreEqual("-", cards.Single(card => card.Label == Resources.Play_history_summary_playtime).Value);
    }

    /// <summary>
    /// 初回BP・スコア割合の前後・増減optionビットを別々に整形し、消えたASSISTビットも表示する。
    /// </summary>
    [TestMethod]
    public void RowProjectionFormatsTransitionsAndOptionHistory()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        1,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 100,
                        newExscore: 120,
                        newTotalNotes: 100,
                        oldClear: 0,
                        newClear: 3,
                        oldMinBp: null,
                        newMinBp: 10,
                        oldMaxCombo: 50,
                        newMaxCombo: 80,
                        oldOpHistory: 0x01000000,
                        newOpHistory: 0x00000010)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();

        Assert.AreEqual("score bp clear combo", row.Kind);
        Assert.AreEqual("10", row.BestBp);
        Assert.AreEqual("50.00 -> 60.00", row.BestRateText);
        Assert.AreEqual("P.A / ASSIST off", row.OpHistory);
    }

    /// <summary>
    /// LR2 clear2の旧ASSISTからEASYビット追加をclear更新として表示/集計し、同じ整数値だからと省略しない。
    /// </summary>
    [TestMethod]
    public void RowProjectionTreatsLr2AssistToEasyBitAsClearUpdate()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        40,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 100,
                        newExscore: 100,
                        newTotalNotes: 100,
                        oldClear: 2,
                        newClear: 2,
                        oldOpHistory: ClearTypeStorageConverter.OptionHistoryAssist,
                        newOpHistory: ClearTypeStorageConverter.OptionHistoryAssist | ClearTypeStorageConverter.OptionHistoryEasy)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        var summary = PlayHistoryPeriodSummary.FromRows("assist-to-easy", projected.Rows);

        Assert.AreEqual(ClearType.INVALID, row.OldBestClear);
        Assert.AreEqual(ClearType.EASY, row.NewBestClear);
        Assert.AreEqual("ASSIST -> EASY", row.BestClear);
        Assert.AreEqual("clear", row.Kind);
        Assert.AreEqual(1, summary.ClearUpdateCount);
        Assert.AreEqual(1, summary.EasyClearUpdateCount);
        Assert.AreEqual(0, summary.AssistClearUpdateCount);
    }

    /// <summary>
    /// LR2 clear2でEASYビットがなければASSIST内訳へ加え、EASYカードへ誤分類しない。
    /// </summary>
    [TestMethod]
    public void RowProjectionCountsLr2Clear2WithoutEasyBitAsAssistUpdate()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        42,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 100,
                        newExscore: 100,
                        newTotalNotes: 100,
                        oldClear: 0,
                        newClear: 2,
                        newOpHistory: 0)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        var summary = PlayHistoryPeriodSummary.FromRows("assist-update", projected.Rows);
        PlayHistorySummaryCard assistCard = PlayHistoryPresentationState.CreateSummaryCards(summary, PlayHistoryProvider.Lr2).Single(card => card.Label == "ASSIST");

        Assert.AreEqual(ClearType.NO_PLAY, row.OldBestClear);
        Assert.AreEqual(ClearType.INVALID, row.NewBestClear);
        Assert.AreEqual("NP -> ASSIST", row.BestClear);
        Assert.AreEqual("clear", row.Kind);
        Assert.AreEqual(1, summary.ClearUpdateCount);
        Assert.AreEqual(1, summary.AssistClearUpdateCount);
        Assert.AreEqual(0, summary.EasyClearUpdateCount);
        Assert.AreEqual("1", assistCard.Value);
        Assert.AreEqual("type:clear newclear:AE|LAE", assistCard.FilterText);
    }

    /// <summary>
    /// clear2・EASYなし・BP負値の初回行はclearだけを更新し、未保存のEXSCORE/COMBO/OPTIONを推測しない。
    /// </summary>
    [TestMethod]
    public void RowProjectionTreatsInitialLr2ForceEasySentinelsAsClearOnly()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        43,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: null,
                        newExscore: 1774,
                        newTotalNotes: 1000,
                        oldClear: 0,
                        newClear: 2,
                        oldMinBp: null,
                        newMinBp: -1,
                        oldMaxCombo: null,
                        newMaxCombo: 935,
                        newOpBest: 12,
                        newOpHistory: 0)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        var summary = PlayHistoryPeriodSummary.FromRows("force-easy", projected.Rows);

        Assert.AreEqual(ClearType.NO_PLAY, row.OldBestClear);
        Assert.AreEqual(ClearType.INVALID, row.NewBestClear);
        Assert.AreEqual("NP -> ASSIST", row.BestClear);
        Assert.AreEqual("clear", row.Kind);
        Assert.IsFalse(row.BestScoreUpdated);
        Assert.IsFalse(row.BestBpUpdated);
        Assert.IsFalse(row.BestComboUpdated);
        Assert.IsNull(row.NewBestExscore);
        Assert.IsNull(row.NewBestBp);
        Assert.IsNull(row.NewBestCombo);
        Assert.AreEqual(string.Empty, row.BestExscore);
        Assert.AreEqual(string.Empty, row.BestDjLevelText);
        Assert.AreEqual(string.Empty, row.BestRateText);
        Assert.AreEqual(string.Empty, row.BestBp);
        Assert.AreEqual(string.Empty, row.BestCombo);
        Assert.AreEqual(string.Empty, row.Option);
        Assert.AreEqual(1, summary.ClearUpdateCount);
        Assert.AreEqual(0, summary.ScoreUpdateCount);
        Assert.AreEqual(0, summary.BpUpdateCount);
        Assert.AreEqual(0, summary.ComboUpdateCount);
        Assert.AreEqual(1, summary.AssistClearUpdateCount);
    }

    /// <summary>
    /// 旧新とも解決後EASYならASSISTビットの増加だけをclear更新にせず、EASY更新集計を増やさない。
    /// </summary>
    [TestMethod]
    public void RowProjectionDoesNotCountSameResolvedEasyClearAsClearUpdate()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(
                        41,
                        HashA,
                        1000,
                        finalized: true,
                        oldExscore: 100,
                        newExscore: 100,
                        newTotalNotes: 100,
                        oldClear: 2,
                        newClear: 2,
                        oldOpHistory: ClearTypeStorageConverter.OptionHistoryEasy,
                        newOpHistory: ClearTypeStorageConverter.OptionHistoryAssist | ClearTypeStorageConverter.OptionHistoryEasy)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        PlayHistoryRow row = projected.Rows.Single();
        var summary = PlayHistoryPeriodSummary.FromRows("same-easy", projected.Rows);

        Assert.AreEqual(ClearType.EASY, row.OldBestClear);
        Assert.AreEqual(ClearType.EASY, row.NewBestClear);
        Assert.IsFalse(row.BestClearUpdated);
        Assert.AreEqual(string.Empty, row.BestClear);
        Assert.AreEqual("play", row.Kind);
        Assert.AreEqual(0, summary.ClearUpdateCount);
        Assert.AreEqual(0, summary.EasyClearUpdateCount);
    }

    /// <summary>
    /// NP→FAILEDはclear更新として数えるが新クリア/FC/PAへ加えず、失敗をクリア実績と誤認しない。
    /// </summary>
    [TestMethod]
    public void SummaryDoesNotCountFailedAsNewClear()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [CreateRawRecord(30, HashA, 1000, finalized: true, oldExscore: 100, newExscore: 120, newTotalNotes: 100, oldClear: 0, newClear: 1)],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            CreateProjectionIndex());

        var summary = PlayHistoryPeriodSummary.FromRows("failed", projected.Rows);

        Assert.AreEqual(1, summary.ClearUpdateCount);
        Assert.AreEqual(0, summary.NewClearCount);
        Assert.AreEqual(0, summary.NewFullComboCount);
        Assert.AreEqual(0, summary.NewPerfectCount);
    }

    /// <summary>
    /// 確定していても未解決ハッシュ行は行数だけに残し、演奏時間・判定・スコア更新へ加えない。
    /// </summary>
    [TestMethod]
    public void SummaryExcludesFinalizedUnknownHashFromAggregates()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [CreateRawRecord(20, HashC, 1000, finalized: true, oldExscore: 100, newExscore: 120, newTotalNotes: 100, oldClear: 0, newClear: 3, perfectDelta: 40, greatDelta: 20, playtimeDelta: 60, judgeDelta: 80)],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);

        var summary = PlayHistoryPeriodSummary.FromRows("diagnostic", projected.Rows);

        Assert.AreEqual(1, summary.RowCount);
        Assert.AreEqual(1L, summary.FinalizedCount);
        Assert.AreEqual(0, summary.SummaryEligibleCount);
        Assert.AreEqual(0L, summary.PlaytimeSeconds);
        Assert.AreEqual(0L, summary.JudgeCount);
        Assert.AreEqual(0, summary.ScoreUpdateCount);
    }

    /// <summary>
    /// 履歴整列は時刻/同時刻IDの降順を守り、通常譜面だけの未対応列は理由付きで拒否する。
    /// </summary>
    [TestMethod]
    public void SortEngine_DefaultsDateDescendingAndRejectsUnknownColumn()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    CreateRawRecord(1, HashA, 1000, finalized: true, oldExscore: 100, newExscore: 120, newTotalNotes: 100),
                    CreateRawRecord(2, HashB, 2000, finalized: true, oldExscore: 100, newExscore: 140, newTotalNotes: 100),
                    CreateRawRecord(3, HashC, 2000, finalized: true, oldExscore: 100, newExscore: 160, newTotalNotes: 100)
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);

        bool sorted = PlayHistorySortEngine.TrySort(projected.Rows, null, out List<PlayHistoryRow> rows, out string sortProfile);

        Assert.IsTrue(sorted);
        Assert.AreEqual(3L, rows[0].HistoryId);
        Assert.AreEqual(2L, rows[1].HistoryId);
        Assert.AreEqual(1L, rows[2].HistoryId);
        StringAssert.Contains(sortProfile, nameof(PlayHistoryRow.PlayedAt));

        bool unknown = PlayHistorySortEngine.TrySort(
            projected.Rows,
            new ChartListSortParameters { ColumnsName = "LibraryChartRowOnlyColumn", Direction = ListSortDirection.Ascending },
            out _,
            out string unknownProfile);

        Assert.IsFalse(unknown);
        Assert.AreEqual("play_history_unknown_column", unknownProfile);
    }

    /// <summary>
    /// 実一覧の整列要求を通常/履歴それぞれの主体へ渡し、片方の利用者指定で他方の条件を上書きしない。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public async Task MainChartListSortRequest_KeepsPlayHistorySortSeparateFromMainSort()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var regularSortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        regularOwner.SortChanged += (_, request) => regularSortChanged.TrySetResult(request);
        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.FolderFilterSelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.Regular);
        viewModel.MainChartList.RequestSort(nameof(ChartFile.Title), ListSortDirection.Ascending);
        await TestUiDispatcherHost.AwaitNotificationAsync(regularSortChanged.Task, Task.CompletedTask, "play-history.sort-condition-notification");
        MainChartListSortRequestedEventArgs regularReceipt = await regularSortChanged.Task;
        Assert.AreEqual(nameof(ChartFile.Title), regularReceipt.ColumnName);

        ChartListSortParameters regularSort = regularOwner.CaptureSortParameters();
        Assert.AreEqual(nameof(ChartFile.Title), regularSort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Ascending, regularSort.Direction);
        Assert.IsNull(viewModel.PlayHistory.CaptureSortParameters(out _));
        Assert.AreEqual(regularSort.ColumnsName, viewModel.MainChartList.SortParameters.ColumnsName);
        Assert.AreEqual(regularSort.Direction, viewModel.MainChartList.SortParameters.Direction);

        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.PlayHistorySelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PlayHistorySelected);

        var playHistorySortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PlayHistory.SortChanged += (_, request) => playHistorySortChanged.TrySetResult(request);
        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.PlayHistory);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.PlayedAt), ListSortDirection.Descending);
        await TestUiDispatcherHost.AwaitNotificationAsync(playHistorySortChanged.Task, Task.CompletedTask, "play-history.sort-condition-notification");
        MainChartListSortRequestedEventArgs playHistoryReceipt = await playHistorySortChanged.Task;
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistoryReceipt.ColumnName);

        ChartListSortParameters playHistorySort = viewModel.PlayHistory.CaptureSortParameters(out _);
        Assert.AreEqual(nameof(ChartFile.Title), regularOwner.CaptureSortParameters().ColumnsName);
        Assert.AreEqual(ListSortDirection.Ascending, regularOwner.CaptureSortParameters().Direction);
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistorySort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Descending, playHistorySort.Direction);
        Assert.AreEqual(playHistorySort.ColumnsName, viewModel.MainChartList.SortParameters.ColumnsName);
        Assert.AreEqual(playHistorySort.Direction, viewModel.MainChartList.SortParameters.Direction);
    }

    /// <summary>
    /// 受付後に表示scopeが切り替わっても捕捉した整列対象へ要求を送り、現在画面の別主体へ誤配送しない。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public async Task MainChartListSortRequest_UsesCapturedSortScopeWhenViewChangesBeforeExecution()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var playHistorySortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PlayHistory.SortChanged += (_, request) => playHistorySortChanged.TrySetResult(request);
        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.FolderFilterSelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.PlayHistory);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.PlayedAt), ListSortDirection.Descending);
        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.Regular);
        await TestUiDispatcherHost.AwaitNotificationAsync(playHistorySortChanged.Task, Task.CompletedTask, "play-history.sort-condition-notification");
        MainChartListSortRequestedEventArgs playHistoryReceipt = await playHistorySortChanged.Task;
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistoryReceipt.ColumnName);

        ChartListSortParameters playHistorySort = viewModel.PlayHistory.CaptureSortParameters(out _);
        Assert.IsNull(regularOwner.CaptureSortParameters());
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistorySort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Descending, playHistorySort.Direction);
        Assert.IsNull(viewModel.MainChartList.SortParameters);

        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.PlayHistorySelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PlayHistorySelected);

        var regularSortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        regularOwner.SortChanged += (_, request) => regularSortChanged.TrySetResult(request);
        viewModel.MainChartList.SetSortPresentation(
            new MainChartListSortPresentation(
                playHistorySort.ColumnsName,
                playHistorySort.Direction),
            MainChartListSortTarget.Regular);
        viewModel.MainChartList.RequestSort(nameof(ChartFile.Title), ListSortDirection.Ascending);
        viewModel.MainChartList.SetSortPresentation(
            new MainChartListSortPresentation(
                playHistorySort.ColumnsName,
                playHistorySort.Direction),
            MainChartListSortTarget.PlayHistory);
        await TestUiDispatcherHost.AwaitNotificationAsync(regularSortChanged.Task, Task.CompletedTask, "play-history.sort-condition-notification");
        MainChartListSortRequestedEventArgs regularReceipt = await regularSortChanged.Task;
        Assert.AreEqual(nameof(ChartFile.Title), regularReceipt.ColumnName);

        ChartListSortParameters regularSort = regularOwner.CaptureSortParameters();
        Assert.AreEqual(nameof(ChartFile.Title), regularSort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Ascending, regularSort.Direction);
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistorySort.ColumnsName);
        Assert.AreEqual(playHistorySort.ColumnsName, viewModel.MainChartList.SortParameters.ColumnsName);
        Assert.AreEqual(playHistorySort.Direction, viewModel.MainChartList.SortParameters.Direction);
    }

    /// <summary>
    /// A→B→Aと戻った整列条件も版で旧要求を除き、通常/履歴それぞれ最後の要求だけを現在とする。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public void MainChartListSortRequest_RejectsStaleAndAbaOwnerRevisions()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var regularRequests = new List<MainChartListSortRequestedEventArgs>();
        var playHistoryRequests = new List<MainChartListSortRequestedEventArgs>();
        regularOwner.SortChanged += (_, request) => regularRequests.Add(request);
        viewModel.PlayHistory.SortChanged += (_, request) => playHistoryRequests.Add(request);

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.Regular);
        viewModel.MainChartList.RequestSort(nameof(ChartFile.Title), ListSortDirection.Ascending);
        viewModel.MainChartList.RequestSort(nameof(ChartFile.Artist), ListSortDirection.Descending);
        viewModel.MainChartList.RequestSort(nameof(ChartFile.Title), ListSortDirection.Ascending);

        Assert.AreEqual(3, regularRequests.Count);
        Assert.IsTrue(regularRequests[0].OwnerRevision < regularRequests[1].OwnerRevision);
        Assert.IsTrue(regularRequests[1].OwnerRevision < regularRequests[2].OwnerRevision);
        Assert.IsFalse(regularOwner.IsCurrentSortRequest(regularRequests[0]));
        Assert.IsFalse(regularOwner.IsCurrentSortRequest(regularRequests[1]));
        Assert.IsTrue(regularOwner.IsCurrentSortRequest(regularRequests[2]));

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.PlayHistory);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.Title), ListSortDirection.Ascending);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.PlayedAt), ListSortDirection.Descending);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.Title), ListSortDirection.Ascending);

        Assert.AreEqual(3, playHistoryRequests.Count);
        Assert.IsTrue(playHistoryRequests[0].OwnerRevision < playHistoryRequests[1].OwnerRevision);
        Assert.IsTrue(playHistoryRequests[1].OwnerRevision < playHistoryRequests[2].OwnerRevision);
        Assert.IsFalse(viewModel.PlayHistory.IsCurrentSortRequest(playHistoryRequests[0]));
        Assert.IsFalse(viewModel.PlayHistory.IsCurrentSortRequest(playHistoryRequests[1]));
        Assert.IsTrue(viewModel.PlayHistory.IsCurrentSortRequest(playHistoryRequests[2]));
    }

    /// <summary>
    /// 通常一覧の整列条件変更時に実行中の行要求を取消し、refresh開始前から古い行反映を禁止する。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public void RegularSortMutation_CancelsTheInFlightRowRequestBeforeRefreshRuns()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner owner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        Assert.IsTrue(owner.TryBeginRequest(out RegularChartListRequestLease lease));

        owner.QueueSort(new MainChartListSortRequestedEventArgs(
            nameof(ChartFile.Title),
            ListSortDirection.Ascending,
            MainChartListSortTarget.Regular));

        Assert.IsTrue(lease.Token.IsCancellationRequested);
    }

    /// <summary>
    /// 最初のrefresh通知を保持中に40要求を合流し、解放後は増加する版で最新通知へ到達する。古い版の後出しを検出する。
    /// </summary>
    /// <remarks>sort通知の到達を観測する。実背景Taskの終端を受け取る窓口はなく、keyword/対象用idleをsort終端へ読み替えない。</remarks>
    [TestMethod]
    public async Task PlayHistorySortRefreshQueue_CoalescesAndNeverPublishesOutOfRevisionOrder()
    {
        var owner = new PlayHistoryWorkflowOwner();
        var changed = new List<MainChartListSortRequestedEventArgs>();
        var refreshed = new List<MainChartListSortRequestedEventArgs>();
        var firstRefreshEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRefreshRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalRefreshPublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        long finalRevision = -1L;
        EventHandler<MainChartListSortRequestedEventArgs> changedHandler = (_, request) => changed.Add(request);
        owner.SortChanged += changedHandler;
        EventHandler<MainChartListSortRequestedEventArgs> refreshHandler = (_, request) =>
        {
            lock (refreshed)
            {
                refreshed.Add(request);
            }
            if (firstRefreshEntered.TrySetResult(true))
            {
                firstRefreshRelease.Task.GetAwaiter().GetResult();
            }
            if (request.OwnerRevision == Volatile.Read(ref finalRevision))
            {
                finalRefreshPublished.TrySetResult(true);
            }
        };
        owner.SortRefreshRequested += refreshHandler;
        try
        {
            owner.QueueSort(new MainChartListSortRequestedEventArgs(
                nameof(PlayHistoryRow.Title),
                ListSortDirection.Ascending,
                MainChartListSortTarget.PlayHistory));
            // 最初の refresh 通知を観測してから更新を集中させ、解放後に最新 revision の通知を待つ。
            await firstRefreshEntered.Task;
            try
            {
                for (int index = 1; index < 40; index++)
                {
                    owner.QueueSort(new MainChartListSortRequestedEventArgs(
                        index % 2 == 0 ? nameof(PlayHistoryRow.Title) : nameof(PlayHistoryRow.PlayedAt),
                        index % 2 == 0 ? ListSortDirection.Ascending : ListSortDirection.Descending,
                        MainChartListSortTarget.PlayHistory));
                }
                Volatile.Write(ref finalRevision, changed[changed.Count - 1].OwnerRevision);
            }
            finally
            {
                firstRefreshRelease.TrySetResult(true);
            }
            await finalRefreshPublished.Task;

            lock (refreshed)
            {
                Assert.IsTrue(refreshed.Count < changed.Count);
                for (int index = 1; index < refreshed.Count; index++)
                {
                    Assert.IsTrue(refreshed[index - 1].OwnerRevision < refreshed[index].OwnerRevision);
                }
                Assert.IsTrue(owner.IsCurrentSortRequest(refreshed[refreshed.Count - 1]));
            }
        }
        finally
        {
            firstRefreshRelease.TrySetResult(true);
            owner.SortChanged -= changedHandler;
            owner.SortRefreshRequested -= refreshHandler;
        }
    }

    /// <summary>
    /// 最初のrefresh購読が例外を投げても次の整列要求を通知し、背景readerが停止したままになる誤りを検出する。
    /// </summary>
    /// <remarks>sort通知の到達を観測する。実背景Taskの終端を受け取る窓口はなく、keyword/対象用idleをsort終端へ読み替えない。</remarks>
    [TestMethod]
    public async Task PlayHistorySortRefreshQueue_RecoversAfterSubscriberFailure()
    {
        var owner = new PlayHistoryWorkflowOwner();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempt = 0;
        EventHandler<MainChartListSortRequestedEventArgs> handler = (_, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                firstAttempt.TrySetResult();
                throw new InvalidOperationException("expected test failure");
            }
            recovered.TrySetResult();
        };
        owner.SortRefreshRequested += handler;
        try
        {
            owner.QueueSort(new MainChartListSortRequestedEventArgs(
                nameof(PlayHistoryRow.Title),
                ListSortDirection.Ascending,
                MainChartListSortTarget.PlayHistory));
            await firstAttempt.Task;

            owner.QueueSort(new MainChartListSortRequestedEventArgs(
                nameof(PlayHistoryRow.PlayedAt),
                ListSortDirection.Descending,
                MainChartListSortTarget.PlayHistory));

            await recovered.Task;
            Assert.AreEqual(2, Volatile.Read(ref attempt));
        }
        finally { owner.SortRefreshRequested -= handler; }
    }

    /// <summary>
    /// 条件変更の購読例外を呼出元へ返しても通常/履歴のrefresh通知を起動し、受理済み更新を欠落させない。
    /// </summary>
    /// <remarks>sort通知の到達を観測する。実背景Taskの終端を受け取る窓口はなく、keyword/対象用idleをsort終端へ読み替えない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public async Task SortRefreshQueues_StartEvenWhenSortChangedSubscriberFails()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var regularRefreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<MainChartListSortRequestedEventArgs> regularChanged = (_, _) => throw new InvalidOperationException("expected regular notification failure");
        regularOwner.SortChanged += regularChanged;
        EventHandler<MainChartListSortRequestedEventArgs> regularRefresh = (_, _) => regularRefreshed.TrySetResult();
        regularOwner.SortRefreshRequested += regularRefresh;

        var playHistoryOwner = new PlayHistoryWorkflowOwner();
        var playHistoryRefreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<MainChartListSortRequestedEventArgs> playChanged = (_, _) => throw new InvalidOperationException("expected play-history notification failure");
        playHistoryOwner.SortChanged += playChanged;
        EventHandler<MainChartListSortRequestedEventArgs> playRefresh = (_, _) => playHistoryRefreshed.TrySetResult();
        playHistoryOwner.SortRefreshRequested += playRefresh;

        try
        {
            Assert.ThrowsException<InvalidOperationException>(() => regularOwner.QueueSort(
                new MainChartListSortRequestedEventArgs(
                    nameof(ChartFile.Title),
                    ListSortDirection.Ascending,
                    MainChartListSortTarget.Regular)));
            await regularRefreshed.Task;

            Assert.ThrowsException<InvalidOperationException>(() => playHistoryOwner.QueueSort(
                new MainChartListSortRequestedEventArgs(
                    nameof(PlayHistoryRow.Title),
                    ListSortDirection.Ascending,
                    MainChartListSortTarget.PlayHistory)));
            await playHistoryRefreshed.Task;
        }
        finally
        {
            regularOwner.SortChanged -= regularChanged;
            regularOwner.SortRefreshRequested -= regularRefresh;
            playHistoryOwner.SortChanged -= playChanged;
            playHistoryOwner.SortRefreshRequested -= playRefresh;
        }
    }

    /// <summary>
    /// 仮想表示は行参照・検索を保ちながらAdd/置換/消去/挿入/削除を拒否し、公開consumerの構造変更を禁止する。
    /// </summary>
    [TestMethod]
    public void VirtualViewExposesReadOnlyRows()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [CreateRawRecord(1, HashA, 1000, finalized: true, oldExscore: null, newExscore: 120, newTotalNotes: 100)],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);
        var view = new PlayHistoryVirtualView(projected.Rows, distinctFolderCount: 3);

        Assert.AreEqual(1, view.Count);
        Assert.AreSame(projected.Rows[0], view[0]);
        Assert.IsTrue(view.IsReadOnly);
        Assert.IsTrue(view.IsFixedSize);
        Assert.IsTrue(view.Contains(projected.Rows[0]));
        Assert.AreEqual(0, view.IndexOf(projected.Rows[0]));
        Assert.AreEqual(3, view.DistinctFolderCount);
        Assert.ThrowsException<NotSupportedException>(() => view.Add(projected.Rows[0]));
        Assert.ThrowsException<NotSupportedException>(() => view[0] = projected.Rows[0]);
        Assert.ThrowsException<NotSupportedException>(() => view.Clear());
        Assert.ThrowsException<NotSupportedException>(() => view.Insert(0, projected.Rows[0]));
        Assert.ThrowsException<NotSupportedException>(() => view.Remove(projected.Rows[0]));
        Assert.ThrowsException<NotSupportedException>(() => view.RemoveAt(0));
    }

    /// <summary>
    /// 実scorelogの通常モードだけを表示へ変換し、ベスト更新・所持譜面/表・検索を接続する。単曲実績や時間は補わない。
    /// </summary>
    /// <remarks>実scorelogとplayer表を読み、所持譜面・プレイリストによる行変換、集計と検索を接続して確認する。</remarks>
    [TestMethod]
    public void BeatorajaReader_ReadsScoreLogUpdateHistory()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: 1000, oldClear: 4, clear: 5, oldScore: 100, score: 133, oldCombo: 70, combo: 80, oldMinBp: 20, minBp: 10);
                InsertBeatorajaScoreLog(db, new string('b', 64), mode: 10000, date: 1050, oldClear: 4, clear: 9, oldScore: 1, score: 99, oldCombo: 1, combo: 10, oldMinBp: 2, minBp: 0);
            }
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertBeatorajaPlayerAggregate(db, 900, playCount: 6, judgeCount: 800, playtime: 300);
                InsertBeatorajaPlayerAggregate(db, 1099, playCount: 7, judgeCount: 893, playtime: 390);
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                ScoresBySha256 = CreateBeatorajaScoreSnapshot((ShaA, 100)),
                ScoreSnapshotVersion = 1,
                PlayedAtFromInclusive = 900,
                PlayedAtToExclusive = 1100
            });
            PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectBeatorajaRows(read, CreateProjectionIndex());
            PlayHistoryRow row = projected.Rows.Single();
            var summary = PlayHistoryPeriodSummary.FromRows("beatoraja", projected.Rows);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, read.SchemaStatus);
            Assert.IsFalse(read.HasErrors);
            Assert.IsTrue(read.PlayerSnapshotsAvailable);
            Assert.AreEqual(2, read.PlayerSnapshots.Count);
            Assert.AreEqual(PlayHistoryProvider.Beatoraja, row.Provider);
            Assert.AreEqual("Resolved Title", row.Title);
            Assert.AreEqual("Resolved Artist", row.Artist);
            Assert.AreEqual("SAT", row.FolderLabels);
            Assert.AreEqual("Satellite", row.PlaylistNames);
            Assert.AreEqual(HashA, row.Md5);
            Assert.AreEqual(ShaA, row.RawHash);
            Assert.AreEqual(ShaA, row.Sha256);
            Assert.AreEqual(1000L, row.PlayedAtUnix);
            Assert.IsNull(row.PlayExscore);
            Assert.AreEqual(string.Empty, row.Judges);
            Assert.AreEqual("100 -> 133", row.BestExscore);
            Assert.AreEqual("20 -> 10", row.BestBp);
            Assert.AreEqual("70 -> 80", row.BestCombo);
            Assert.AreEqual("EASY -> NORMAL", row.BestClear);
            Assert.AreEqual(string.Empty, row.Option);
            Assert.AreEqual("score bp clear combo", row.Kind);
            Assert.AreEqual("C -> B", row.BestDjLevelText);
            Assert.AreEqual("50.00 -> 66.50", row.BestRateText);
            Assert.IsNull(row.PlaytimeSeconds);
            Assert.AreEqual(1, summary.RowCount);
            Assert.AreEqual(1, summary.SummaryEligibleCount);
            Assert.AreEqual(1, summary.ScoreUpdateCount);
            Assert.AreEqual(1, summary.ClearUpdateCount);
            Assert.AreEqual(0L, summary.PlaytimeSeconds);
            Assert.AreEqual(0L, summary.JudgeCount);
            Assert.IsTrue(GridKeywordSearchQuery.Parse("title:resolved artist:artist folder:SAT playlist:Satellite md5:" + HashA.Substring(0, 8) + " hash:" + ShaA.Substring(0, 12) + " sha256:" + ShaA.Substring(0, 12) + " source:beatoraja kind:score clear:CLEAR").MatchesPlayHistoryRow(row));
            BMSTable targetTable = CreateTargetTable(HashA, "FolderA");
            var targetIndex = PlayHistoryDisplayTargetIndex.Create(
                PlayHistoryDisplayTargetItem.FromPlaylist(targetTable),
                [targetTable],
                null);
            Assert.IsTrue(targetIndex.TryApply(row, out PlayHistoryRow displayRow));
            Assert.AreEqual("FolderA", displayRow.FolderLabels);
        });
    }

    /// <summary>
    /// 期間実績は実player累計の境界前差分、全期間は最新累計から求め、表示行件数や単曲時間で代用しない。
    /// </summary>
    /// <remarks>実player表の累計を読み、期間差分・全期間値と投影後の集計値を確認する。</remarks>
    [TestMethod]
    public void BeatorajaPeriodSummaryUsesPlayerAggregateRangeForScreenPeriod()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            long day1 = UtcEpoch(2026, 1, 1);
            long day2 = UtcEpoch(2026, 1, 2);
            long day3 = UtcEpoch(2026, 1, 3);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: day2 + 3600, oldClear: 4, clear: 5, oldScore: 10, score: 25, oldCombo: 8, combo: 12, oldMinBp: 5, minBp: 3);
            }
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertBeatorajaPlayerAggregate(db, day1, playCount: 10, judgeCount: 1000, playtime: 100);
                InsertBeatorajaPlayerAggregate(db, day2, playCount: 12, judgeCount: 1160, playtime: 160);
                InsertBeatorajaPlayerAggregate(db, day3, playCount: 13, judgeCount: 1220, playtime: 220);
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });
            var day2Request = PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc);
            PlayHistoryPeriodSummaryOverride day2SummaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(day2Request, read);
            PlayHistoryPeriodSummaryOverride allSummaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
                PlayHistoryPeriodRequest.Create(PlayHistoryPeriodKind.All, new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc),
                read);
            PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectBeatorajaRows(read, PlayHistoryProjectionIndex.Empty);
            var summary = PlayHistoryPeriodSummary.FromRows(
                "beatoraja",
                [],
                day2SummaryOverride);

            Assert.IsTrue(read.PlayerSnapshotsAvailable);
            Assert.AreEqual(3, read.PlayerSnapshots.Count);
            Assert.AreEqual(2L, day2SummaryOverride.PlayCount);
            Assert.AreEqual(160L, day2SummaryOverride.JudgeCount);
            Assert.AreEqual(60L, day2SummaryOverride.PlaytimeSeconds);
            Assert.AreEqual(13L, allSummaryOverride.PlayCount);
            Assert.AreEqual(1220L, allSummaryOverride.JudgeCount);
            Assert.AreEqual(220L, allSummaryOverride.PlaytimeSeconds);
            Assert.IsNull(projected.Rows.Single().PlaytimeSeconds);
            Assert.AreEqual(0, summary.RowCount);
            Assert.AreEqual(2L, summary.FinalizedCount);
            Assert.AreEqual(160L, summary.JudgeCount);
            Assert.AreEqual(60L, summary.PlaytimeSeconds);
            Assert.IsTrue(summary.PlayCountAvailable);
            Assert.IsTrue(summary.JudgeCountAvailable);
            Assert.IsTrue(summary.PlaytimeAvailable);
        });
    }

    /// <summary>
    /// playerが読めない場合と診断期間では実績を利用不能とし、scorelog更新行から推測しない。
    /// </summary>
    /// <remarks>player表を欠く実DBから診断を取得し、通常・診断期間で利用不能となる集計値を確認する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void BeatorajaPeriodSummaryIsUnavailableForDiagnosticsOrUnreadablePlayerTable()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: UtcEpoch(2026, 1, 2), oldClear: 4, clear: 5, oldScore: 10, score: 25, oldCombo: 8, combo: 12, oldMinBp: 5, minBp: 3);
            }
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("CREATE TABLE unrelated_to_player (id INTEGER);");
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });
            PlayHistoryPeriodSummaryOverride daySummaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
                PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc),
                read);
            PlayHistoryPeriodSummaryOverride diagnosticSummaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
                PlayHistoryPeriodRequest.Create(PlayHistoryPeriodKind.Diagnostics, new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc),
                new BeatorajaPlayHistoryReadResult(
                    PlayHistorySourceProfile.Beatoraja(scoreDbPath),
                    [],
                    [],
                    Lr2PlayHistorySchemaStatus.Installed,
                    [new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = UtcEpoch(2026, 1, 2), PlayCount = 1, JudgeCount = 100, PlaytimeSeconds = 100 }],
                    playerSnapshotsAvailable: true));

            Assert.IsFalse(read.HasErrors);
            Assert.IsFalse(read.PlayerSnapshotsAvailable);
            Assert.AreEqual("play_history_beatoraja_player_aggregate_unreadable", read.Diagnostics.Single().Code);
            Assert.IsNull(daySummaryOverride.PlayCount);
            Assert.IsNull(daySummaryOverride.JudgeCount);
            Assert.IsNull(daySummaryOverride.PlaytimeSeconds);
            Assert.IsNull(diagnosticSummaryOverride.PlayCount);
            Assert.IsNull(diagnosticSummaryOverride.JudgeCount);
            Assert.IsNull(diagnosticSummaryOverride.PlaytimeSeconds);

            var owner = new PlayHistoryWorkflowOwner();
            PlayHistoryViewExecutionDependencies dependencies = CreateViewExecutionDependencies(
                () => PlayHistoryReadSourceContext.Beatoraja(scoreDbPath, BeatorajaPlayHistoryScoreContext.Empty),
                library: CreateHistoryLibrary(scoreDbPath));
            owner.ConfigureViewExecution(dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, ExecuteHistoryRead(owner, dependencies).Status);
            Assert.AreEqual(1, dependencies.MainChartList.Rows.Count);
            foreach (string label in new[] { Resources.Play_history_summary_play_count, Resources.Play_history_summary_judge_count, Resources.Play_history_summary_playtime })
            {
                Assert.AreEqual("-", owner.SummaryCards.Single(card => card.Label == label).Value);
            }

            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertBeatorajaPlayerAggregate(db, UtcEpoch(2026, 1, 2), playCount: 12, judgeCount: 120, playtime: 90);
            }
            PlayHistoryViewExecutionResult warm = ExecuteHistoryRead(owner, dependencies,
                PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc));
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, warm.Status);
            Assert.IsTrue(warm.ReadResult.Read.CacheHit);
            Assert.AreEqual(1, dependencies.MainChartList.Rows.Count);
            foreach (string label in new[] { Resources.Play_history_summary_play_count, Resources.Play_history_summary_judge_count, Resources.Play_history_summary_playtime })
            {
                Assert.AreEqual("-", owner.SummaryCards.Single(card => card.Label == label).Value);
            }

            owner.InvalidateReadCache("score_reload");
            PlayHistoryViewExecutionResult recovered = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, recovered.Status);
            Assert.IsFalse(recovered.ReadResult.Read.CacheHit);
            Assert.AreEqual(1, dependencies.MainChartList.Rows.Count);
            Assert.AreEqual("12", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
            Assert.AreEqual("120", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_judge_count).Value);
            Assert.AreEqual("1:30", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_playtime).Value);
        });
    }

    /// <summary>
    /// プレイ数・判定・時間の減少、大きい累計の減少、負値は実績を利用不能にし、負の差分を成功集計へ渡さない。
    /// </summary>
    [TestMethod]
    public void BeatorajaPeriodSummaryIsUnavailableWhenPlayerAggregateDecreases()
    {
        long day1 = UtcEpoch(2026, 1, 1);
        long day2 = UtcEpoch(2026, 1, 2);
        AssertBeatorajaAggregateDecreaseIsUnavailable(day1, day2, playCount2: 9, judgeCount2: 1160, playtime2: 160);
        AssertBeatorajaAggregateDecreaseIsUnavailable(day1, day2, playCount2: 11, judgeCount2: 900, playtime2: 160);
        AssertBeatorajaAggregateDecreaseIsUnavailable(day1, day2, playCount2: 11, judgeCount2: 1160, playtime2: 90);
        AssertBeatorajaAggregateDecreaseIsUnavailable(
            day1,
            day2,
            playCount1: int.MaxValue + 1000L,
            judgeCount1: int.MaxValue + 1000L,
            playtime1: int.MaxValue + 1000L,
            playCount2: int.MaxValue + 500L,
            judgeCount2: int.MaxValue + 1500L,
            playtime2: int.MaxValue + 1500L);
        AssertBeatorajaAggregateDecreaseIsUnavailable(
            day1,
            day2,
            playCount1: -1,
            judgeCount1: 1000,
            playtime1: 100,
            playCount2: 11,
            judgeCount2: 1160,
            playtime2: 160);
    }

    /// <summary>
    /// int上限を超える累計をlongで保持し、期間差分と全期間の値を切り詰めない。
    /// </summary>
    [TestMethod]
    public void BeatorajaPeriodSummaryPreservesLargePlayerAggregateValues()
    {
        long day1 = UtcEpoch(2026, 1, 1);
        long day2 = UtcEpoch(2026, 1, 2);
        long baseValue = int.MaxValue + 1000L;
        long deltaValue = 500L;
        var read = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("scorelog.db"),
            [],
            [],
            Lr2PlayHistorySchemaStatus.Installed,
            [
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day1, PlayCount = baseValue, JudgeCount = baseValue + 100, PlaytimeSeconds = baseValue + 200 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day2, PlayCount = baseValue + deltaValue, JudgeCount = baseValue + 100 + deltaValue, PlaytimeSeconds = baseValue + 200 + deltaValue }
            ],
            playerSnapshotsAvailable: true);

        PlayHistoryPeriodSummaryOverride daySummary = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc),
            read);
        PlayHistoryPeriodSummaryOverride allSummary = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.All(),
            read);

        Assert.AreEqual(deltaValue, daySummary.PlayCount);
        Assert.AreEqual(deltaValue, daySummary.JudgeCount);
        Assert.AreEqual(deltaValue, daySummary.PlaytimeSeconds);
        Assert.AreEqual(baseValue + deltaValue, allSummary.PlayCount);
        Assert.AreEqual(baseValue + 100 + deltaValue, allSummary.JudgeCount);
        Assert.AreEqual(baseValue + 200 + deltaValue, allSummary.PlaytimeSeconds);
    }

    /// <summary>
    /// 無効と判定済みの捕捉面は合計が正でも期間集計を利用不能にし、無効状態を合計で隠さない。
    /// </summary>
    [TestMethod]
    public void BeatorajaPeriodSummaryIsUnavailableWhenAnyRawJudgeAggregateIsNegative()
    {
        long day1 = UtcEpoch(2026, 1, 1);
        long day2 = UtcEpoch(2026, 1, 2);
        var read = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("scorelog.db"),
            [],
            [],
            Lr2PlayHistorySchemaStatus.Installed,
            [
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day1, PlayCount = 10, JudgeCount = 1000, PlaytimeSeconds = 100 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day2, PlayCount = 11, JudgeCount = 1160, PlaytimeSeconds = 160, HasInvalidRawValue = true }
            ],
            playerSnapshotsAvailable: true);

        PlayHistoryPeriodSummaryOverride summaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc),
            read);

        Assert.IsNull(summaryOverride.PlayCount);
        Assert.IsNull(summaryOverride.JudgeCount);
        Assert.IsNull(summaryOverride.PlaytimeSeconds);
    }

    /// <summary>
    /// 途中の累計が無効なら端点が有効でも全期間集計を利用不能とし、途中の異常を飛ばさない。
    /// </summary>
    [TestMethod]
    public void BeatorajaPeriodSummaryIsUnavailableWhenIntermediatePlayerAggregateIsInvalid()
    {
        long day1 = UtcEpoch(2026, 1, 1);
        long day2 = UtcEpoch(2026, 1, 2);
        long day3 = UtcEpoch(2026, 1, 3);
        var read = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("scorelog.db"),
            [],
            [],
            Lr2PlayHistorySchemaStatus.Installed,
            [
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day1, PlayCount = 10, JudgeCount = 1000, PlaytimeSeconds = 100 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day2, PlayCount = 11, JudgeCount = 1160, PlaytimeSeconds = 160, HasInvalidRawValue = true },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day3, PlayCount = 12, JudgeCount = 1220, PlaytimeSeconds = 220 }
            ],
            playerSnapshotsAvailable: true);

        PlayHistoryPeriodSummaryOverride summaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.All(),
            read);

        Assert.IsNull(summaryOverride.PlayCount);
        Assert.IsNull(summaryOverride.JudgeCount);
        Assert.IsNull(summaryOverride.PlaytimeSeconds);
    }

    /// <summary>
    /// 途中で減少した累計は最後に増加していても利用不能とし、端点だけで減少を見落とさない。
    /// </summary>
    [TestMethod]
    public void BeatorajaPeriodSummaryIsUnavailableWhenIntermediatePlayerAggregateDecreases()
    {
        long day1 = UtcEpoch(2026, 1, 1);
        long day2 = UtcEpoch(2026, 1, 2);
        long day3 = UtcEpoch(2026, 1, 3);
        var read = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("scorelog.db"),
            [],
            [],
            Lr2PlayHistorySchemaStatus.Installed,
            [
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day1, PlayCount = 10, JudgeCount = 1000, PlaytimeSeconds = 100 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day2, PlayCount = 9, JudgeCount = 900, PlaytimeSeconds = 90 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day3, PlayCount = 12, JudgeCount = 1220, PlaytimeSeconds = 220 }
            ],
            playerSnapshotsAvailable: true);

        PlayHistoryPeriodSummaryOverride summaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.All(),
            read);

        Assert.IsNull(summaryOverride.PlayCount);
        Assert.IsNull(summaryOverride.JudgeCount);
        Assert.IsNull(summaryOverride.PlaytimeSeconds);
    }

    /// <summary>
    /// 実playerの負の判定内訳を読んで無効情報を引き継ぎ、期間実績を推測しない。
    /// </summary>
    /// <remarks>実player表に負の判定内訳を保存し、捕捉面の無効判定と期間集計の利用不能を確認する。</remarks>
    [TestMethod]
    public void BeatorajaReaderMarksNegativeRawJudgeAggregateAsInvalid()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            long day1 = UtcEpoch(2026, 1, 1);
            long day2 = UtcEpoch(2026, 1, 2);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: day2 + 3600, oldClear: 4, clear: 5, oldScore: 10, score: 25, oldCombo: 8, combo: 12, oldMinBp: 5, minBp: 3);
            }
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertBeatorajaPlayerAggregate(db, day1, playCount: 10, judgeCount: 1000, playtime: 100);
                db.Execute(
                    "INSERT OR REPLACE INTO player (date, playcount, epg, lpg, egr, lgr, egd, lgd, ebd, lbd, epr, lpr, ems, lms, playtime) VALUES (?, ?, ?, ?, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, ?);",
                    day2,
                    11,
                    -1,
                    200,
                    160);
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });
            PlayHistoryPeriodSummaryOverride summaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
                PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc),
                read);

            Assert.IsTrue(read.PlayerSnapshots.Last().HasInvalidRawValue);
            Assert.IsNull(summaryOverride.PlayCount);
            Assert.IsNull(summaryOverride.JudgeCount);
            Assert.IsNull(summaryOverride.PlaytimeSeconds);
        });
    }

    /// <summary>
    /// 履歴DB不在は取得不能の状態と不在診断を返し、空履歴DBの作成や架空行の補完をしない。
    /// </summary>
    /// <remarks>未作成の履歴DBパスをreaderへ渡し、不在診断・履歴0件とファイル不在を確認する。</remarks>
    [TestMethod]
    public void BeatorajaReader_MissingScoreLogReturnsNoUpdateRows()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaPlayerDb(scoreDbPath);

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });

            Assert.IsFalse(File.Exists(scoreLogDbPath));
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, read.SchemaStatus);
            Assert.AreEqual(0, read.Rows.Count);
            Assert.AreEqual("play_history_beatoraja_scorelog_missing", read.Diagnostics.Single().Code);
        });
    }

    /// <summary>
    /// scorelogがなく最新詳細DBだけがあっても履歴へ流用せず、不在診断を返す。
    /// </summary>
    /// <remarks>実詳細DBだけが存在する配置でreaderを呼び、履歴DB不在の診断と履歴0件を確認する。</remarks>
    [TestMethod]
    public void BeatorajaReader_DoesNotFallbackToScoreDataLogWhenScoreLogIsMissing()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaPlayerDb(scoreDbPath);
            string scoreDataLogDbPath = Path.Combine(Path.GetDirectoryName(scoreDbPath)!, "scoredatalog.db");
            CreateBeatorajaScoreDataLogLatestDb(scoreDataLogDbPath);
            using (var db = new SQLiteConnection(scoreDataLogDbPath))
            {
                InsertBeatorajaScoreDataLogLatest(db, ShaA, mode: 0, date: 1000);
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });

            Assert.IsTrue(File.Exists(scoreDataLogDbPath));
            Assert.IsFalse(File.Exists(scoreLogDbPath));
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, read.SchemaStatus);
            Assert.AreEqual(0, read.Rows.Count);
            Assert.AreEqual("play_history_beatoraja_scorelog_missing", read.Diagnostics.Single().Code);
        });
    }

    /// <summary>
    /// beatorajaの診断要求は更新履歴を未確定扱いせず空行を返し、確定履歴と混ぜない。
    /// </summary>
    /// <remarks>実scorelogへ未確定専用要求を渡し、正常なschema状態と履歴0件を確認する。</remarks>
    [TestMethod]
    public void BeatorajaReader_UnfinalizedOnlyReturnsEmptyRows()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: 1000, oldClear: 4, clear: 5, oldScore: 10, score: 25, oldCombo: 8, combo: 12, oldMinBp: 5, minBp: 3);
            }

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath,
                FinalizationFilter = Lr2PlayHistoryFinalizationFilter.UnfinalizedOnly
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, read.SchemaStatus);
            Assert.IsFalse(read.HasErrors);
            Assert.AreEqual(0, read.Rows.Count);
        });
    }

    /// <summary>
    /// 不正なSQLiteファイルはUnreadableと読取り失敗診断を返し、空の正常履歴へ置き換えない。
    /// </summary>
    /// <remarks>SQLiteとして読めない実ファイルをreaderへ渡し、Unreadable・読取り失敗診断と履歴0件を確認する。</remarks>
    [TestMethod]
    public void BeatorajaReader_ReadReportsUnreadableScoreLog()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaPlayerDb(scoreDbPath);
            File.WriteAllText(scoreLogDbPath, "not a sqlite database");

            BeatorajaPlayHistoryReadResult read = new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
            {
                ScoreDbPath = scoreDbPath
            });

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, read.SchemaStatus);
            Assert.IsTrue(read.HasErrors);
            Assert.AreEqual(0, read.Rows.Count);
            Assert.IsTrue(read.Diagnostics.Any(diagnostic => diagnostic.Code == "play_history_beatoraja_read_failed"));
        });
    }

    /// <summary>
    /// 旧BPのint.MaxValueは未プレイへ正規化し、新BPだけを表示して巨大な旧値を出さない。
    /// </summary>
    /// <remarks>実scorelogの旧BP特殊値を読み、表示行で旧BPがなく新BPだけを表示することを確認する。</remarks>
    [TestMethod]
    public void BeatorajaReader_TreatsMaxValueOldMinBpAsUnplayed()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: 1000, oldClear: 5, clear: 5, oldScore: 50, score: 50, oldCombo: 12, combo: 12, oldMinBp: int.MaxValue, minBp: 10);
            }

            PlayHistoryRow row = PlayHistoryRow.ProjectBeatorajaRows(
                new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest { ScoreDbPath = scoreDbPath }),
                CreateProjectionIndex()).Rows.Single();

            Assert.IsNull(row.OldBestBp);
            Assert.AreEqual(10, row.NewBestBp);
            Assert.AreEqual("10", row.BestBp);
            Assert.AreEqual("bp", row.Kind);
            Assert.IsFalse(row.BestBp.Contains(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        });
    }

    /// <summary>
    /// 実scorelogの終了境界前・通常モード行だけを表示し、コースや境界上の更新を混入させない。
    /// </summary>
    /// <remarks>実scorelogへ期間条件を適用して表示行へ変換し、時刻とベスト更新の表示値を確認する。</remarks>
    [TestMethod]
    public void BeatorajaReader_ReadsScoreLogRowsDirectly()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            CreateBeatorajaPlayerDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: 999, oldClear: 4, clear: 5, oldScore: 10, score: 25, oldCombo: 8, combo: 12, oldMinBp: 5, minBp: 3);
                InsertBeatorajaScoreLog(db, ShaA, mode: 10000, date: 1000, oldClear: 4, clear: 9, oldScore: 10, score: 99, oldCombo: 8, combo: 20, oldMinBp: 5, minBp: 0);
                InsertBeatorajaScoreLog(db, new string('b', 64), mode: 0, date: 1000, oldClear: 4, clear: 9, oldScore: 10, score: 99, oldCombo: 8, combo: 20, oldMinBp: 5, minBp: 0);
            }

            PlayHistoryRow row = PlayHistoryRow.ProjectBeatorajaRows(
                new BeatorajaPlayHistoryReader().Read(new BeatorajaPlayHistoryReadRequest
                {
                    ScoreDbPath = scoreDbPath,
                    PlayedAtToExclusive = 1000
                }),
                PlayHistoryProjectionIndex.Empty).Rows.Single();

            Assert.AreEqual(999L, row.PlayedAtUnix);
            Assert.AreEqual("10 -> 25", row.BestExscore);
            Assert.AreEqual("5 -> 3", row.BestBp);
            Assert.AreEqual("EASY -> NORMAL", row.BestClear);
            Assert.AreEqual("score bp clear combo", row.Kind);
            Assert.AreEqual(string.Empty, row.BestDjLevelText);
            Assert.AreEqual(string.Empty, row.BestRateText);
        });
    }

    /// <summary>
    /// 実scorelogの日付索引は通常モードの各日最大時刻を降順に返し、同日の重複やコース日を増やさない。
    /// </summary>
    /// <remarks>実scorelogから通常モードの日付索引を読み、各日の最大時刻と降順を確認する。</remarks>
    [TestMethod]
    public void BeatorajaReader_PeriodIndexUsesScoreLogMaxDatePerLocalDay()
    {
        WithBeatorajaPlayerDb(delegate (string scoreDbPath, string scoreLogDbPath)
        {
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            using (var db = new SQLiteConnection(scoreLogDbPath))
            {
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: UtcEpoch(2026, 1, 1, 1), oldClear: 4, clear: 5, oldScore: 10, score: 20, oldCombo: 5, combo: 10, oldMinBp: 8, minBp: 7);
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: UtcEpoch(2026, 1, 1, 12), oldClear: 5, clear: 5, oldScore: 20, score: 30, oldCombo: 10, combo: 11, oldMinBp: 7, minBp: 6);
                InsertBeatorajaScoreLog(db, ShaA, mode: 0, date: UtcEpoch(2026, 1, 2, 1), oldClear: 5, clear: 5, oldScore: 30, score: 40, oldCombo: 11, combo: 12, oldMinBp: 6, minBp: 5);
                InsertBeatorajaScoreLog(db, new string('b', 64), mode: 10000, date: UtcEpoch(2026, 1, 3, 1), oldClear: 4, clear: 9, oldScore: 1, score: 99, oldCombo: 1, combo: 20, oldMinBp: 5, minBp: 0);
            }

            BeatorajaPlayHistoryPeriodIndexResult result = new BeatorajaPlayHistoryReader().ReadPeriodIndex(
                new BeatorajaPlayHistoryPeriodIndexRequest { ScoreDbPath = scoreDbPath },
                CancellationToken.None);

            CollectionAssert.AreEqual(
                new[] { UtcEpoch(2026, 1, 2, 1), UtcEpoch(2026, 1, 1, 12) },
                result.PlayedAtUnixSeconds.ToArray());
        });
    }

    private static PlayHistoryProjectionIndex CreateProjectionIndex()
    {
        ChartFile file = ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(
        [
            HashA,
            "Resolved Title",
            "",
            "Resolved Artist",
            "",
            "",
            "",
            "C:\\BMS\\resolved.bms",
            "",
            "Folder",
            "",
            "",
            "",
            "",
            "12",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            ""
        ]));
        file = file with { Md5 = HashA, Sha256 = ShaA };
        var resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromCharts([file]);
        var table = new BMSTable
        {
            name = "Satellite",
            symbol = "SAT"
        };
        return PlayHistoryProjectionIndex.Create(
            resolveIndex,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [HashA] = ShaA },
            (md5, sha256) => new PlaylistReferenceDisplay(
                [new PlaylistReferenceTableDisplaySnapshot(table.symbol, table.name)]),
            (sha256, md5) => null);
    }

    private static void AssertPeriodRange(PlayHistoryPeriodKind kind, long expectedFromInclusive, long expectedToExclusive)
    {
        var request = PlayHistoryPeriodRequest.Create(
            kind,
            new DateTimeOffset(2026, 6, 19, 15, 30, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);

        Assert.AreEqual(expectedFromInclusive, request.PlayedAtFromInclusive, kind.ToString());
        Assert.AreEqual(expectedToExclusive, request.PlayedAtToExclusive, kind.ToString());
        Assert.IsFalse(request.IncludeUnfinalized, kind.ToString());
        Assert.AreEqual(Lr2PlayHistoryFinalizationFilter.FinalizedOnly, request.FinalizationFilter, kind.ToString());
    }

    private static long UtcEpoch(int year, int month, int day)
    {
        return UtcEpoch(year, month, day, 0);
    }

    private static long UtcEpoch(int year, int month, int day, int hour)
    {
        return new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    private static void WithScoreDb(Action<string> action)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayHistoryReadModel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            action(Path.Combine(directoryPath, "score.db"));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private static void WithBeatorajaPlayerDb(Action<string, string> action)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_BeatorajaPlayHistory_" + Guid.NewGuid().ToString("N"));
        string playerDirectoryPath = Path.Combine(directoryPath, "player", "player1");
        Directory.CreateDirectory(playerDirectoryPath);
        try
        {
            string scoreDbPath = Path.Combine(playerDirectoryPath, "score.db");
            using (new SQLiteConnection(scoreDbPath))
            {
            }
            action(
                scoreDbPath,
                Path.Combine(playerDirectoryPath, "scorelog.db"));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    private static void CreateBeatorajaScoreLogDb(string scoreLogDbPath)
    {
        using var db = new SQLiteConnection(scoreLogDbPath);
        db.Execute(
            "CREATE TABLE scorelog (sha256 TEXT NOT NULL, mode INTEGER, clear INTEGER, oldclear INTEGER, score INTEGER, oldscore INTEGER, combo INTEGER, oldcombo INTEGER, minbp INTEGER, oldminbp INTEGER, date INTEGER);");
    }

    private static void CreateBeatorajaScoreDataLogLatestDb(string scoreDataLogDbPath)
    {
        using var db = new SQLiteConnection(scoreDataLogDbPath);
        db.Execute(
            "CREATE TABLE scoredatalog (sha256 TEXT NOT NULL, mode INTEGER, clear INTEGER, date INTEGER, scorehash TEXT, PRIMARY KEY(sha256, mode));");
    }

    private static void CreateBeatorajaPlayerDb(string scoreDbPath)
    {
        using var db = new SQLiteConnection(scoreDbPath);
        db.Execute("CREATE TABLE player (date INTEGER PRIMARY KEY, playcount INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, egd INTEGER, lgd INTEGER, ebd INTEGER, lbd INTEGER, epr INTEGER, lpr INTEGER, ems INTEGER, lms INTEGER, playtime INTEGER);");
    }

    private static void InsertBeatorajaPlayerAggregate(SQLiteConnection db, long date, int playCount, int judgeCount, int playtime)
    {
        db.Execute(
            "INSERT OR REPLACE INTO player (date, playcount, epg, lpg, egr, lgr, egd, lgd, ebd, lbd, epr, lpr, ems, lms, playtime) VALUES (?, ?, ?, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, ?);",
            date,
            playCount,
            judgeCount,
            playtime);
    }

    private static IReadOnlyDictionary<string, BMSScore> CreateBeatorajaScoreSnapshot(params (string Sha256, int Notes)[] entries)
    {
        var scoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sha256, int notes) in entries)
        {
            if (string.IsNullOrWhiteSpace(sha256) || notes <= 0)
            {
                continue;
            }
            scoresBySha256[sha256.Trim()] = new BMSScore
            {
                hash = sha256.Trim(),
                totalnotes = notes
            };
        }
        return scoresBySha256;
    }

    private static void AssertBeatorajaAggregateDecreaseIsUnavailable(long day1, long day2, long playCount2, long judgeCount2, long playtime2)
    {
        AssertBeatorajaAggregateDecreaseIsUnavailable(
            day1,
            day2,
            playCount1: 10,
            judgeCount1: 1000,
            playtime1: 100,
            playCount2,
            judgeCount2,
            playtime2);
    }

    private static void AssertBeatorajaAggregateDecreaseIsUnavailable(
        long day1,
        long day2,
        long playCount1,
        long judgeCount1,
        long playtime1,
        long playCount2,
        long judgeCount2,
        long playtime2)
    {
        var read = new BeatorajaPlayHistoryReadResult(
            PlayHistorySourceProfile.Beatoraja("scorelog.db"),
            [],
            [],
            Lr2PlayHistorySchemaStatus.Installed,
            [
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day1, PlayCount = playCount1, JudgeCount = judgeCount1, PlaytimeSeconds = playtime1 },
                new BeatorajaPlayerAggregateSnapshot { DateUnixSeconds = day2, PlayCount = playCount2, JudgeCount = judgeCount2, PlaytimeSeconds = playtime2 }
            ],
            playerSnapshotsAvailable: true);

        PlayHistoryPeriodSummaryOverride summaryOverride = PlayHistoryWorkflowOwner.ResolveBeatorajaPeriodSummaryOverride(
            PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc),
            read);

        Assert.IsNull(summaryOverride.PlayCount);
        Assert.IsNull(summaryOverride.JudgeCount);
        Assert.IsNull(summaryOverride.PlaytimeSeconds);
    }

    private static void InsertBeatorajaScoreDataLogLatest(SQLiteConnection db, string sha256, int mode, long date)
    {
        db.Execute(
            "INSERT OR REPLACE INTO scoredatalog (sha256, mode, clear, date, scorehash) VALUES (?, ?, ?, ?, ?);",
            sha256,
            mode,
            5,
            date,
            "latest-detail-only");
    }

    private static void InsertBeatorajaScoreLog(
        SQLiteConnection db,
        string sha256,
        int mode,
        long date,
        int oldClear,
        int clear,
        int oldScore,
        int score,
        int oldCombo,
        int combo,
        int oldMinBp,
        int minBp)
    {
        db.Execute(
            "INSERT INTO scorelog (sha256, mode, clear, oldclear, score, oldscore, combo, oldcombo, minbp, oldminbp, date) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
            sha256,
            mode,
            clear,
            oldClear,
            score,
            oldScore,
            combo,
            oldCombo,
            minBp,
            oldMinBp,
            date);
    }

    private static void CreateInstalledScoreDb(string scoreDbPath)
    {
        CreateBaseScoreDb(scoreDbPath);
        Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, result.Status);
    }

    private static void CreateBaseScoreDb(string scoreDbPath)
    {
        using var db = new SQLiteConnection(scoreDbPath);
        db.CreateTable<LR2ScoreDB.score>();
        db.CreateTable<LR2ScoreDB.player>();
    }

    private static void AssertSingleDiagnostic(
        Lr2PlayHistoryReadResult result,
        PlayHistoryDiagnosticSeverity severity,
        string code,
        string sourcePath)
    {
        PlayHistoryDiagnostic diagnostic = result.Diagnostics.Single();
        Assert.AreEqual(PlayHistoryProvider.Lr2, diagnostic.Provider);
        Assert.AreEqual("read", diagnostic.Stage);
        Assert.AreEqual(severity, diagnostic.Severity);
        Assert.AreEqual(code, diagnostic.Code);
        Assert.AreEqual(sourcePath ?? string.Empty, diagnostic.SourcePath);
        Assert.IsFalse(string.IsNullOrWhiteSpace(diagnostic.Message));
    }

    private static void InsertHistory(SQLiteConnection db, long historyId, string hash, long playedAt, bool finalized, int? newExscore)
    {
        db.Execute(
            "INSERT INTO bms_lr2_play_history (history_id, hash, played_at, finalized, score_write_type, new_playcount, playcount_delta, new_exscore, new_totalnotes) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);",
            historyId, hash, playedAt, finalized ? 1 : 0, "update", 1, 1, newExscore, 100);
    }

    private static Lr2PlayHistoryRecord CreateRawRecord(
        long historyId,
        string hash,
        long playedAt,
        bool finalized,
        int? oldExscore,
        int? newExscore,
        int? newTotalNotes,
        int? oldClear = null,
        int? newClear = null,
        int? oldMinBp = null,
        int? newMinBp = null,
        int? oldMaxCombo = null,
        int? newMaxCombo = null,
        int? perfectDelta = null,
        int? greatDelta = null,
        int? goodDelta = null,
        int? badDelta = null,
        int? poorDelta = null,
        int? playtimeDelta = null,
        int? judgeDelta = null,
        int? newOpBest = null,
        int? oldOpHistory = null,
        int? newOpHistory = null)
    {
        return new Lr2PlayHistoryRecord
        {
            history_id = historyId,
            hash = hash,
            played_at = playedAt,
            finalized = finalized ? 1 : 0,
            score_write_type = "update",
            old_playcount = historyId == 1 ? null : 1,
            new_playcount = historyId == 1 ? 1 : 2,
            playcount_delta = 1,
            old_clear = oldClear,
            new_clear = newClear,
            old_minbp = oldMinBp,
            new_minbp = newMinBp,
            old_exscore = oldExscore,
            new_exscore = newExscore,
            old_maxcombo = oldMaxCombo,
            new_maxcombo = newMaxCombo,
            new_totalnotes = newTotalNotes,
            perfect_delta = perfectDelta,
            great_delta = greatDelta,
            good_delta = goodDelta,
            bad_delta = badDelta,
            poor_delta = poorDelta,
            playtime_delta = playtimeDelta,
            judge_delta = judgeDelta,
            new_op_best = newOpBest,
            old_op_history = oldOpHistory,
            new_op_history = newOpHistory
        };
    }

    private static PlayHistoryRow CreateProjectedRow(string hash, string sha256 = "", string initialFolderLabels = "")
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [CreateRawRecord(100, hash, 1000, finalized: true, oldExscore: null, newExscore: 100, newTotalNotes: 100)],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            string.IsNullOrWhiteSpace(sha256)
                ? PlayHistoryProjectionIndex.Empty
                : PlayHistoryProjectionIndex.Create(
                    PlaylistLibraryResolveIndexSnapshot.Empty,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [hash] = sha256 }));
        PlayHistoryRow row = projected.Rows.Single();
        return string.IsNullOrEmpty(initialFolderLabels) ? row : row.WithPlaylistDisplay(initialFolderLabels);
    }

    private static PlayHistoryViewState CreatePlayHistoryViewState(
        PlayHistoryViewRequest request,
        SortSnapshot sortSnapshot,
        string keywordFilter,
        long keywordRevision,
        PlayHistoryDisplayTargetItem displayTarget,
        long displayTargetRevision)
    {
        IReadOnlyList<PlayHistoryRow> rows = [CreateProjectedRow(HashA)];
        return new PlayHistoryViewState(
            request.RequestId,
            request.PeriodRequest,
            rows,
            rows,
            rows,
            [],
            PlayHistoryProvider.Lr2,
            Lr2PlayHistorySchemaStatus.Installed,
            rows.Count,
            sortSnapshot,
            keywordFilter,
            keywordRevision,
            displayTarget,
            displayTargetRevision);
    }

    private static BMSTable CreateTargetTable(string md5, string folder, string sha256 = "")
    {
        string sha256Json = string.IsNullOrWhiteSpace(sha256) ? string.Empty : ",\"sha256\":\"" + sha256 + "\"";
        var table = new BMSTable
        {
            playlist_id = 7,
            name = "Satellite",
            symbol = "SAT",
            org_symbol = "SAT",
            entries =
            [
                new BMSTableEntry(JObject.Parse("{\"md5\":\"" + md5 + "\"" + sha256Json + ",\"title\":\"Target\",\"level\":\"" + folder + "\"}"))
            ]
        };
        return table;
    }

    private static void SetPrivateField<T>(MainWindowViewModel viewModel, string fieldName, T value)
    {
        typeof(MainWindowViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
    }

    private static T GetPrivateField<T>(MainWindowViewModel viewModel, string fieldName)
    {
        return (T)typeof(MainWindowViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
    }

    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string HashC = "cccccccccccccccccccccccccccccccc";

    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>
    /// 次の要求は旧tokenを取消し、最新のキーワード・対象識別・版を保持する。非選択化は現在要求を消して取消す。
    /// </summary>
    [TestMethod]
    public void WorkflowOwner_TransfersRequestIdentityAndCancelsPreviousLifecycle()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest first = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            "keyword-a",
            "target-a",
            displayTargetRevision: 3);
        System.Threading.CancellationToken firstToken = owner.GetCancellationToken(first.RequestId);

        PlayHistoryViewRequest second = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            "keyword-b",
            "target-b",
            displayTargetRevision: 4);

        Assert.IsTrue(firstToken.IsCancellationRequested);
        Assert.IsFalse(owner.IsCurrentRequest(first.RequestId));
        Assert.IsTrue(owner.IsCurrentRequest(second.RequestId));
        Assert.AreSame(second, owner.SnapshotActiveRequest());
        Assert.AreEqual("keyword-b", owner.PresentationState.CurrentKeywordIdentity);
        Assert.AreEqual("target-b", owner.PresentationState.CurrentDisplayTargetIdentity);
        Assert.AreEqual(4L, owner.PresentationState.DisplayTargetRevision);

        bool selectionDeactivated = false;
        System.Threading.CancellationToken secondToken = owner.GetCancellationToken(second.RequestId);
        owner.Deactivate(() => selectionDeactivated = true);

        Assert.IsTrue(selectionDeactivated);
        Assert.IsTrue(secondToken.IsCancellationRequested);
        Assert.IsNull(owner.SnapshotActiveRequest());
        Assert.IsFalse(owner.IsCurrentRequest(second.RequestId));
    }

    /// <summary>
    /// cache失効は要求を取消して理由を診断へ出し、履歴画面の選択中状態は維持する。
    /// </summary>
    [TestMethod]
    public void WorkflowOwner_InvalidateReadCache_CancelsRequestPreservesActivityAndLogsReason()
    {
        var logMessages = new List<string>();
        var owner = new PlayHistoryWorkflowOwner(logMessages.Add);
        PlayHistoryViewRequest request = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);
        System.Threading.CancellationToken token = owner.GetCancellationToken(request.RequestId);

        owner.InvalidateReadCache("score_reload");

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsNull(owner.SnapshotActiveRequest());
        Assert.IsTrue(owner.IsViewActive);
        CollectionAssert.AreEqual(
            new[] { "play_history_read_cache_invalidated reason=score_reload" },
            logMessages);
    }

    /// <summary>
    /// 最初の選択と最後の非選択化だけactivityを通知し、再読込みや要求置換で余分な切替通知を出さない。
    /// </summary>
    [TestMethod]
    public void WorkflowOwner_PublishesViewActiveFromRequestLifecycle()
    {
        var owner = new PlayHistoryWorkflowOwner();
        List<bool> activeNotifications = [];
        owner.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(PlayHistoryWorkflowOwner.IsViewActive), StringComparison.Ordinal))
            {
                activeNotifications.Add(owner.IsViewActive);
            }
        };

        Assert.IsFalse(owner.IsViewActive);
        PlayHistoryViewRequest first = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);

        Assert.IsTrue(owner.IsViewActive);
        Assert.IsTrue(owner.IsCurrentRequest(first.RequestId));
        Assert.AreEqual(1, activeNotifications.Count);
        Assert.IsTrue(activeNotifications[0]);

        owner.Deactivate(clearViewActivity: false);
        Assert.IsTrue(owner.IsViewActive, "A data refresh may cancel the request while keeping the play-history shell selected.");
        Assert.AreEqual(1, activeNotifications.Count);

        owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            "reloaded",
            string.Empty,
            displayTargetRevision: 0);
        Assert.IsTrue(owner.IsViewActive);
        Assert.AreEqual(1, activeNotifications.Count, "Restoring a request in the selected view does not change activity.");

        owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            "next",
            string.Empty,
            displayTargetRevision: 0);
        Assert.AreEqual(1, activeNotifications.Count, "Replacing an active request keeps the owner active.");

        owner.Deactivate();

        Assert.IsFalse(owner.IsViewActive);
        Assert.AreEqual(2, activeNotifications.Count);
        Assert.IsFalse(activeNotifications[1]);
    }

    /// <summary>
    /// 期間選択/非選択callbackの失敗でも旧tokenと現在要求を退役させ、外部callbackを要求lock外で呼ぶ。
    /// </summary>
    [TestMethod]
    public void WorkflowOwner_PeriodActivationFailureDoesNotLeaveRequestLifecycleActive()
    {
        var owner = new PlayHistoryWorkflowOwner();
        owner.ConfigureDetailSourceRetirement(
            () => new PlaylistSourceRetirementRequest(1, null));
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => false,
            () => [],
            _ => { });
        PlayHistoryViewRequest first = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);
        System.Threading.CancellationToken firstToken = owner.GetCancellationToken(first.RequestId);

        owner.PeriodRequestActivated += (_, _) => throw new InvalidOperationException("selection failed");
        Assert.ThrowsException<InvalidOperationException>(() => owner.ActivatePeriod(
            PlayHistoryPeriodRequest.All(),
            string.Empty));

        Assert.IsTrue(firstToken.IsCancellationRequested);
        Assert.IsNull(owner.SnapshotActiveRequest());
        Assert.IsFalse(owner.IsCurrentRequest(owner.CurrentRequestId));

        PlayHistoryViewRequest next = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);
        System.Threading.CancellationToken nextToken = owner.GetCancellationToken(next.RequestId);
        bool lockHeldDuringDeactivateSelection = false;
        Assert.ThrowsException<InvalidOperationException>(() => owner.Deactivate(
            () =>
            {
                lockHeldDuringDeactivateSelection =
                    Monitor.IsEntered(owner.PresentationState.SyncRoot);
                throw new InvalidOperationException("deactivation failed");
            }));
        Assert.IsFalse(lockHeldDuringDeactivateSelection);
        Assert.IsTrue(nextToken.IsCancellationRequested);
        Assert.IsNull(owner.SnapshotActiveRequest());
    }

    /// <summary>
    /// 履歴の非選択通知が失敗した場合、実shellは既存の保留パッケージ操作contextを維持し、未完了の画面切替で対象を変えない。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public void MainViewSelectionKeepsExistingChartOperationContextWhenDeactivationRaises()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        PlayHistoryWorkflowOwner playHistory = viewModel.PlayHistory;
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
        playHistory.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);
        playHistory.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(PlayHistoryWorkflowOwner.IsViewActive), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("deactivation notification failed");
            }
        };

        MethodInfo setSelection = typeof(MainWindowViewModel).GetMethod(
            "SetTreeViewFilterSelection",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.ThrowsException<TargetInvocationException>(() => setSelection.Invoke(
            viewModel,
            new object[] { MainViewUpdateMode.FolderFilterSelected, null! }));

        Assert.AreEqual(MainViewOperationSection.InstallPending, viewModel.MainChartList.CurrentOperationContext.OperationSection);
        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, viewModel.MainChartList.CurrentOperationContext.SourceScope);
        Assert.IsNull(playHistory.SnapshotActiveRequest());
    }

    /// <summary>
    /// 詳細入力の退役と期間受付通知を要求lock外で呼び、返した要求へ退役情報と正規化キーワードを引き継ぐ。
    /// </summary>
    [TestMethod]
    public void WorkflowOwner_ActivatePeriodRaisesAfterRequestLockIsReleased()
    {
        var owner = new PlayHistoryWorkflowOwner();
        var detailSourceRetirement = new PlaylistSourceRetirementRequest(1, null);
        bool lockHeldDuringRetirement = false;
        owner.ConfigureDetailSourceRetirement(() =>
        {
            lockHeldDuringRetirement = Monitor.IsEntered(owner.PresentationState.SyncRoot);
            return detailSourceRetirement;
        });
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => false,
            () => [],
            _ => { });
        PlayHistoryViewRequest? activated = null;
        bool lockWasHeld = false;
        owner.PeriodRequestActivated += (_, args) =>
        {
            activated = args.Request;
            lockWasHeld = Monitor.IsEntered(owner.PresentationState.SyncRoot);
        };

        PlayHistoryViewRequest request = owner.ActivatePeriod(
            PlayHistoryPeriodRequest.All(),
            "  keyword  ");

        Assert.AreSame(request, activated);
        Assert.AreSame(detailSourceRetirement, request.DetailSourceRetirement);
        Assert.IsFalse(lockHeldDuringRetirement);
        Assert.IsFalse(lockWasHeld);
        Assert.AreEqual(PlaylistRequestFactory.NormalizeKeywordFilter("  keyword  "), owner.CurrentKeywordIdentity);
        Assert.IsTrue(owner.IsCurrentRequest(request.RequestId));
    }

    /// <summary>
    /// keyword/対象refreshは同版重複を拒否し、双方の完了までidleを保留する。32並列受付は一件だけ取り、旧完了で最新要求を失わない。
    /// </summary>
    [TestMethod]
    public async Task WorkflowOwner_OwnsRefreshRevisionCoalescingAndActivity()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest activeRequest =
            owner.BeginRequest(PlayHistoryPeriodRequest.All(), string.Empty, string.Empty, 0);
        var detailSourceRetirement = new PlaylistSourceRetirementRequest(
            requestVersion: 7,
            buildCancellation: null);
        activeRequest.DetailSourceRetirement = detailSourceRetirement;

        Assert.IsTrue(owner.TryBeginKeywordRefresh(string.Empty, advanceRevision: false, out PlayHistoryViewRequest initialRequest));
        Assert.AreEqual(0L, initialRequest.KeywordFilterRevision);
        Assert.AreSame(detailSourceRetirement, initialRequest.DetailSourceRetirement);
        owner.CompleteKeywordRefresh(initialRequest.KeywordFilterRevision);

        Task alreadyIdle = owner.WaitForRefreshQueuesIdleAsync();
        Assert.IsTrue(alreadyIdle.IsCompletedSuccessfully);

        Assert.IsTrue(owner.TryBeginKeywordRefresh("keyword", advanceRevision: true, out PlayHistoryViewRequest keywordRequest));
        Task keywordIdle = owner.WaitForRefreshQueuesIdleAsync();
        Assert.IsFalse(keywordIdle.IsCompleted);
        Assert.IsFalse(owner.TryBeginKeywordRefresh("keyword", advanceRevision: false, out _));
        Assert.IsTrue(owner.TryBeginDisplayTargetRefresh("target", advanceRevision: true, out PlayHistoryViewRequest displayRequest));
        Task displayIdle = owner.WaitForRefreshQueuesIdleAsync();
        Assert.AreSame(keywordIdle, displayIdle);
        Assert.AreSame(detailSourceRetirement, keywordRequest.DetailSourceRetirement);
        Assert.AreSame(detailSourceRetirement, displayRequest.DetailSourceRetirement);
        Assert.IsFalse(owner.TryBeginDisplayTargetRefresh("target", advanceRevision: false, out _));
        Assert.IsFalse(owner.AreRefreshQueuesIdle);

        owner.ClearQueuedRefreshes();
        Assert.IsFalse(owner.AreRefreshQueuesIdle, "Active workers keep the owner non-idle after queued revisions are cleared.");
        Assert.IsFalse(keywordIdle.IsCompleted, "Clearing revisions must not report idle while their workers remain active.");
        owner.CompleteKeywordRefresh(keywordRequest.KeywordFilterRevision);
        Assert.IsFalse(keywordIdle.IsCompleted, "The display-target worker still owns active work.");
        owner.CompleteDisplayTargetRefresh(displayRequest.DisplayTargetRevision);

        await keywordIdle;
        Assert.IsTrue(owner.AreRefreshQueuesIdle);
        Assert.AreEqual(keywordRequest.KeywordFilterRevision, owner.KeywordRevision);
        Assert.AreEqual(displayRequest.DisplayTargetRevision, owner.DisplayTargetRevision);
        Assert.AreEqual("target", owner.PresentationState.CurrentDisplayTargetIdentity);

        owner.UpdateKeywordIdentity("latest", advanceRevision: true);
        long latestKeywordRevision = owner.KeywordRevision;
        Assert.IsFalse(owner.TryBeginKeywordRefresh("stale", advanceRevision: false, out _));
        Assert.AreEqual(latestKeywordRevision, owner.KeywordRevision);
        Assert.AreEqual("latest", owner.PresentationState.CurrentKeywordIdentity);
        owner.AdvanceDisplayTargetRevision("latest-target");
        long latestDisplayRevision = owner.DisplayTargetRevision;
        Assert.IsFalse(owner.TryBeginDisplayTargetRefresh("stale-target", advanceRevision: false, out _));
        Assert.AreEqual(latestDisplayRevision, owner.DisplayTargetRevision);
        Assert.AreEqual("latest-target", owner.PresentationState.CurrentDisplayTargetIdentity);

        int keywordReservations = 0;
        owner.UpdateKeywordIdentity("concurrent", advanceRevision: true);
        PlayHistoryViewRequest? concurrentRequest = null;
        System.Threading.Tasks.Parallel.For(0, 32, _ =>
        {
            if (owner.TryBeginKeywordRefresh("concurrent", advanceRevision: false, out PlayHistoryViewRequest request))
            {
                concurrentRequest = request;
                Interlocked.Increment(ref keywordReservations);
            }
        });
        Assert.AreEqual(1, keywordReservations);
        owner.CompleteKeywordRefresh(concurrentRequest!.KeywordFilterRevision);
        Assert.ThrowsException<InvalidOperationException>(() => owner.CompleteKeywordRefresh(concurrentRequest.KeywordFilterRevision));
        await owner.WaitForRefreshQueuesIdleAsync();

        Assert.IsTrue(owner.TryBeginKeywordRefresh("stale-current", advanceRevision: true, out PlayHistoryViewRequest staleRequest));
        Assert.IsTrue(owner.TryBeginKeywordRefresh("stale-current", advanceRevision: true, out PlayHistoryViewRequest currentRequest));
        Task requeuedIdle = owner.WaitForRefreshQueuesIdleAsync();
        owner.CompleteKeywordRefresh(staleRequest.KeywordFilterRevision);
        Assert.IsFalse(requeuedIdle.IsCompleted, "Completing a stale worker must retain the current queued revision.");
        owner.CompleteKeywordRefresh(currentRequest.KeywordFilterRevision);
        await requeuedIdle;
    }

    /// <summary>
    /// 受理したkeyword refreshを既存callbackへ一度渡し、callback復帰後に実idleを完了する。
    /// </summary>
    [TestMethod]
    public async Task WorkflowOwner_QueuesViewRefreshAndCompletesRevisionAfterRefreshCallback()
    {
        var owner = new PlayHistoryWorkflowOwner();
        owner.BeginRequest(PlayHistoryPeriodRequest.All(), string.Empty, string.Empty, 0);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<PlayHistoryViewRequest>();
        owner.ConfigureViewRefreshScheduler(
            () => false,
            request =>
            {
                lock (requests)
                {
                    requests.Add(request);
                }
                refreshed.TrySetResult();
            });

        Task? idle = null;
        Exception? testFailure = null;
        try
        {
            owner.QueueKeywordFilterRefresh(string.Empty, advanceRevision: false);

            idle = owner.WaitForRefreshQueuesIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(refreshed.Task, idle, "play-history.refresh-arrived");
            await idle;
            lock (requests)
            {
                Assert.AreEqual(1, requests.Count);
                Assert.AreEqual(0L, requests[0].KeywordFilterRevision);
            }
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try { await (idle ?? owner.WaitForRefreshQueuesIdleAsync()); }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }
        }
    }

    /// <summary>
    /// 古い表示要求は取得元の解決前にstaleを返し、構成依存へ到達しない。
    /// </summary>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public void WorkflowOwner_ExecuteViewRejectsStaleRequestBeforeCallingCompositionDependencies()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest active = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        bool dependencyCalled = false;
        owner.ConfigureViewExecution(CreateViewExecutionDependencies(
            () =>
            {
                dependencyCalled = true;
                throw new AssertFailedException("A stale request must not resolve its source.");
            }));

        PlayHistoryViewExecutionResult result = owner.ExecuteView(
            new PlayHistoryViewExecutionRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.PlayHistorySelected,
                parameter: null,
                Stopwatch.StartNew(),
                new PlayHistoryViewRequest(PlayHistoryPeriodRequest.All(), active.RequestId + 1),
                keywordFilter: string.Empty,
                PlayHistoryDisplayTargetItem.All));

        Assert.AreEqual(PlayHistoryViewExecutionStatus.Stale, result.Status);
        Assert.IsFalse(dependencyCalled);
    }

    /// <summary>
    /// 明示された現在要求で表示判断を行い、別parameterや後から渡したkeywordで要求の期間/識別を上書きしない。
    /// </summary>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public void WorkflowOwner_ExecuteViewUsesExplicitActiveRequest()
    {
        var owner = new PlayHistoryWorkflowOwner();
        owner.ConfigureViewExecution(CreateViewExecutionDependencies());
        var period = PlayHistoryPeriodRequest.Create(
            PlayHistoryPeriodKind.Today,
            new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.Zero),
            TimeZoneInfo.Utc);
        PlayHistoryViewRequest active = owner.BeginRequest(
            period,
            PlaylistRequestFactory.NormalizeKeywordFilter("  title  "),
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);

        PlayHistoryViewExecutionResult result = owner.ExecuteView(
            new PlayHistoryViewExecutionRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.SortUpdated,
                parameter: null,
                Stopwatch.StartNew(),
                active,
                keywordFilter: "  title  ",
                PlayHistoryDisplayTargetItem.All));

        Assert.AreEqual(PlayHistoryViewExecutionStatus.NoCurrentMatchingState, result.Status);
        Assert.AreEqual(period.Kind, active.PeriodRequest.Kind);
        Assert.AreEqual(owner.CurrentRequestId, active.RequestId);
        Assert.AreEqual(
            PlaylistRequestFactory.NormalizeKeywordFilter("  title  "),
            owner.CurrentKeywordIdentity);

        PlayHistoryViewExecutionResult existingRequestResult = owner.ExecuteView(
            new PlayHistoryViewExecutionRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.SortUpdated,
                parameter: active,
                Stopwatch.StartNew(),
                active,
                keywordFilter: "ignored",
                PlayHistoryDisplayTargetItem.All));

        Assert.AreEqual(PlayHistoryViewExecutionStatus.NoCurrentMatchingState, existingRequestResult.Status);
        Assert.AreEqual(active.RequestId, owner.CurrentRequestId);
    }

    /// <summary>
    /// keyword変更は現在の変換済み行を再利用し、取得元読取りなしで検索する。表示dispatchが不採用ならstaleを返す。
    /// </summary>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public void WorkflowOwner_ExecuteViewKeywordFilterUpdatedReusesCurrentProjectionWithoutReadingSource()
    {
        var owner = new PlayHistoryWorkflowOwner();
        var detailSourceRetirement = new PlaylistSourceRetirementRequest(
            requestVersion: 1,
            buildCancellation: null);
        PlayHistoryViewRequest active = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0,
            detailSourceRetirement);
        PlayHistoryRow row = CreateProjectedRow(HashA);
        IReadOnlyList<PlayHistoryRow> allProjectedRows = [row];
        IReadOnlyList<PlayHistoryRow> filterSourceRows = [row];
        owner.PresentationState.CurrentView = new PlayHistoryViewState(
            active.RequestId,
            active.PeriodRequest,
            allProjectedRows,
            filterSourceRows,
            [row],
            diagnostics: [],
            PlayHistoryProvider.Lr2,
            schemaStatus: default,
            sourceCount: 1,
            new SortSnapshot(null, null, revision: 0),
            keywordFilter: string.Empty,
            keywordFilterRevision: owner.KeywordRevision,
            PlayHistoryDisplayTargetItem.All,
            displayTargetRevision: owner.DisplayTargetRevision);
        Assert.IsTrue(owner.TryBeginKeywordRefresh(
            "title:missing",
            advanceRevision: true,
            out PlayHistoryViewRequest keywordRequest));
        Assert.AreSame(detailSourceRetirement, keywordRequest.DetailSourceRetirement);

        int presentationInvocationCount = 0;
        owner.ConfigureViewExecution(CreateViewExecutionDependencies(
            () => throw new AssertFailedException("A keyword-only refresh must not resolve its source."),
            _ =>
            {
                presentationInvocationCount++;
                return false;
            }));

        PlayHistoryViewExecutionResult result;
        try
        {
            result = owner.ExecuteView(
                new PlayHistoryViewExecutionRequest(
                    MainViewUpdateMode.PlayHistorySelected,
                    MainViewUpdateMode.KeywordFilterUpdated,
                    parameter: null,
                    Stopwatch.StartNew(),
                    keywordRequest,
                    keywordFilter: "title:missing",
                    PlayHistoryDisplayTargetItem.All));
        }
        finally
        {
            owner.CompleteKeywordRefresh(keywordRequest.KeywordFilterRevision);
        }

        Assert.AreEqual(PlayHistoryViewExecutionStatus.Stale, result.Status);
        Assert.IsTrue(result.FromSortOnly);
        Assert.AreEqual(1, presentationInvocationCount);
        Assert.IsNull(result.ReadResult);
        Assert.AreEqual(0L, result.ReadMs);
        Assert.AreEqual(0L, result.ProjectionIndexMs);
        Assert.IsFalse(result.ProjectionIndexCacheHit);
        Assert.AreEqual(0, result.ProjectionIndexStaleRetries);
        Assert.AreEqual(0L, result.ProjectionMs);
        Assert.AreEqual(0L, result.PeriodIndexMs);
        Assert.IsNotNull(result.PresentationOnlyResult);
        Assert.AreEqual(PlayHistoryPresentationOnlyBuildStatus.Built, result.PresentationOnlyResult.Status);
        Assert.IsTrue(result.PresentationOnlyResult.KeywordFilterApplied);
        Assert.AreEqual(keywordRequest.KeywordFilterRevision, result.PresentationOnlyResult.State.KeywordFilterRevision);
        Assert.AreEqual(owner.DisplayTargetRevision, result.PresentationOnlyResult.State.DisplayTargetRevision);
        Assert.AreSame(row, result.PresentationOnlyResult.State.AllProjectedRows.Single());
        Assert.AreSame(row, result.PresentationOnlyResult.State.FilterSourceRows.Single());
        Assert.AreEqual(0, result.PresentationOnlyResult.State.ProjectedRows.Count);
    }

    /// <summary>
    /// 表示対象が進んだ古い面はstaleとし、最新対象版のrefreshを受理して実idleまで完了する。
    /// </summary>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public async Task WorkflowOwner_ExecuteViewRequeuesLatestDisplayTargetAfterPresentationStale()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest active = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        PlayHistoryViewState state = new(
            active.RequestId,
            active.PeriodRequest,
            [],
            [],
            [],
            [],
            PlayHistoryProvider.Lr2,
            default,
            sourceCount: 0,
            new SortSnapshot(null, null, revision: 0),
            string.Empty,
            owner.KeywordRevision,
            PlayHistoryDisplayTargetItem.All,
            owner.DisplayTargetRevision);
        owner.PresentationState.CurrentView = state;

        var latestTarget = PlayHistoryDisplayTargetItem.FromTargetSet(
            new PlayHistoryDisplayTargetSet { Name = "latest-target" });
        owner.AdvanceDisplayTargetRevision(latestTarget.Identity);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlayHistoryViewRequest? refreshRequest = null;
        owner.ConfigureViewRefreshScheduler(
            () => false,
            request =>
            {
                refreshRequest = request;
                refreshed.TrySetResult();
            });
        owner.ConfigureViewExecution(CreateViewExecutionDependencies());

        Task? idle = null;
        Exception? testFailure = null;
        try
        {
            PlayHistoryViewExecutionResult result = owner.ExecuteView(
                new PlayHistoryViewExecutionRequest(
                    MainViewUpdateMode.PlayHistorySelected,
                    MainViewUpdateMode.SortUpdated,
                    parameter: null,
                    Stopwatch.StartNew(),
                    active,
                    keywordFilter: string.Empty,
                    PlayHistoryDisplayTargetItem.All));

            Assert.AreEqual(PlayHistoryViewExecutionStatus.Stale, result.Status);
            idle = owner.WaitForRefreshQueuesIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(refreshed.Task, idle, "play-history.refresh-arrived");
            await idle;
            Assert.IsNotNull(refreshRequest);
            Assert.AreEqual(owner.DisplayTargetRevision, refreshRequest.DisplayTargetRevision);
            Assert.AreEqual(latestTarget.Identity, owner.CurrentDisplayTargetIdentity);
        }
        catch (Exception ex)
        {
            testFailure = ex;
            throw;
        }
        finally
        {
            try { await (idle ?? owner.WaitForRefreshQueuesIdleAsync()); }
            catch (Exception cleanupFailure)
            {
                if (testFailure is null) { throw; }
                System.Diagnostics.Trace.WriteLine("後片付けの二次失敗: " + cleanupFailure);
            }
        }
    }

    /// <summary>
    /// 同期表示dispatchが利用不能なら現在要求でもstaleを返し、反映成功として扱わない。
    /// </summary>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public void WorkflowOwner_ExecuteViewReturnsStaleWhenSynchronousPresentationDispatchIsUnavailable()
    {
        var owner = new PlayHistoryWorkflowOwner();
        PlayHistoryViewRequest active = owner.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            PlayHistoryDisplayTargetItem.All.Identity,
            displayTargetRevision: 0);
        owner.PresentationState.CurrentView = new PlayHistoryViewState(
            active.RequestId,
            active.PeriodRequest,
            [],
            [],
            [],
            [],
            PlayHistoryProvider.Lr2,
            default,
            sourceCount: 0,
            new SortSnapshot(null, null, revision: 0),
            string.Empty,
            owner.KeywordRevision,
            PlayHistoryDisplayTargetItem.All,
            owner.DisplayTargetRevision);
        owner.ConfigureViewExecution(CreateViewExecutionDependencies(invokePresentation: _ => false));

        PlayHistoryViewExecutionResult result = owner.ExecuteView(
            new PlayHistoryViewExecutionRequest(
                MainViewUpdateMode.PlayHistorySelected,
                MainViewUpdateMode.SortUpdated,
                parameter: null,
                Stopwatch.StartNew(),
                active,
                keywordFilter: string.Empty,
                PlayHistoryDisplayTargetItem.All));

        Assert.AreEqual(PlayHistoryViewExecutionStatus.Stale, result.Status);
    }

    /// <summary>非連携プロファイルはDBを開かない情報スキップとして表示し、連携プロファイルの実読取り不能と区別する。</summary>
    /// <remarks>実reader/cacheから実owner終端まで接続し、非連携のwarmキャッシュと、同じ欠落パスで連携判定を変えた読取り失敗を確認する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void WorkflowOwner_NonLinkedProfileShowsInformationalSkipInsteadOfReadFailure()
    {
        WithScoreDb(scoreDbPath =>
        {
            bool isLinkedProfile = false;
            var owner = new PlayHistoryWorkflowOwner();
            PlayHistoryViewExecutionDependencies dependencies = CreateViewExecutionDependencies(
                () => PlayHistoryReadSourceContext.Lr2(scoreDbPath, isLinkedProfile));
            owner.ConfigureViewExecution(dependencies);
            PlayHistoryViewExecutionResult skipped = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, skipped.Status);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.SkippedProfile, skipped.ReadResult.Read.SchemaStatus);
            Assert.IsFalse(skipped.ReadResult.Read.CacheHit);
            Assert.AreEqual(PlayHistoryPeriodIndexStageStatus.SkippedSchemaUnavailable, skipped.ReadResult.PeriodIndex.Status);
            Assert.IsFalse(skipped.ReadResult.PeriodIndex.CacheHit);
            Assert.AreEqual(0, skipped.ReadResult.PeriodIndex.DiagnosticCount);
            Assert.AreEqual(PlayHistoryDiagnosticSeverity.Info, skipped.ReadResult.Presentation.State.Diagnostics.Single().Severity);
            StringAssert.Contains(owner.SummaryDiagnosticText, "play_history_lr2_skipped_profile");
            Assert.AreEqual(0, dependencies.MainChartList.Rows.Count);
            Assert.AreEqual(0, owner.ArchivePeriodTree.Count);
            Assert.AreNotEqual(Resources.Play_history_read_failed, dependencies.MainChartList.SummaryText);
            Assert.AreNotEqual(Resources.Play_history_read_failed, owner.SummaryDiagnosticText);
            Assert.IsFalse(File.Exists(scoreDbPath));

            PlayHistoryViewExecutionResult warm = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, warm.Status);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.SkippedProfile, warm.ReadResult.Read.SchemaStatus);
            Assert.IsTrue(warm.ReadResult.Read.CacheHit);
            Assert.AreEqual(PlayHistoryPeriodIndexStageStatus.SkippedSchemaUnavailable, warm.ReadResult.PeriodIndex.Status);
            Assert.IsFalse(warm.ReadResult.PeriodIndex.CacheHit);
            Assert.AreEqual(PlayHistoryDiagnosticSeverity.Info, warm.ReadResult.Presentation.State.Diagnostics.Single().Severity);
            StringAssert.Contains(owner.SummaryDiagnosticText, "play_history_lr2_skipped_profile");

            isLinkedProfile = true;
            PlayHistoryViewExecutionResult unreadable = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.ReadFailed, unreadable.Status);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, unreadable.ReadResult.Read.SchemaStatus);
            Assert.AreEqual(Resources.Play_history_read_failed, dependencies.MainChartList.SummaryText);
            Assert.AreEqual(Resources.Play_history_read_failed, owner.SummaryDiagnosticText);
            Assert.IsFalse(File.Exists(scoreDbPath));
        });
    }

    /// <summary>
    /// 正常な表示の後でも現在要求の読取り失敗を表示し、行・カード・選択を退役する。次の正常0件は追加失効なしで採用する。
    /// </summary>
    /// <remarks>実DBのschema検査後のSELECT故障から、実MainChartList/Workspaceの失敗表示と通常取得による復帰まで接続する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void WorkflowOwner_ReadFailureShowsFailureAndNextEmptySuccessRecovers()
    {
        WithScoreDb(scoreDbPath =>
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, 1, HashA, UtcEpoch(2026, 1, 2), true, 200);
            }
            var owner = new PlayHistoryWorkflowOwner();
            PlayHistoryViewExecutionDependencies dependencies = CreateViewExecutionDependencies(
                () => PlayHistoryReadSourceContext.Lr2(scoreDbPath, true), library: CreateHistoryLibrary(scoreDbPath));
            owner.ConfigureViewExecution(dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, ExecuteHistoryRead(owner, dependencies).Status);
            Assert.AreEqual(1, dependencies.MainChartList.Rows.Count);
            Assert.AreEqual("1", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
            dependencies.MainChartList.SelectedIndex = 0;
            owner.InvalidateReadCache("test_source_update");
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                // schemaはInstalledのまま、実SELECTだけが失敗するDBへ変える。
                db.Execute("PRAGMA writable_schema = ON;");
                db.Execute("UPDATE sqlite_master SET sql = replace(sql, 'played_at INTEGER NOT NULL', 'played_at INTEGER COLLATE missing_history_collation NOT NULL') WHERE type = 'table' AND name = 'bms_lr2_play_history';");
                db.Execute("PRAGMA writable_schema = OFF;");
            }

            PlayHistoryViewExecutionResult failed = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.ReadFailed, failed.Status);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, failed.ReadResult.Read.SchemaStatus);
            Assert.AreEqual(0, dependencies.MainChartList.Rows.Count);
            Assert.AreEqual(-1, dependencies.MainChartList.SelectedIndex);
            Assert.AreEqual(0, owner.SummaryCards.Count);
            Assert.AreEqual(Resources.Play_history_read_failed, dependencies.MainChartList.SummaryText);
            Assert.AreEqual(Resources.Play_history_read_failed, owner.SummaryDiagnosticText);
            Assert.IsTrue(owner.IsViewActive);

            File.Delete(scoreDbPath);
            CreateInstalledScoreDb(scoreDbPath);
            PlayHistoryViewExecutionResult recovered = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, recovered.Status);
            Assert.IsFalse(recovered.ReadResult.Read.CacheHit);
            Assert.AreEqual(0, dependencies.MainChartList.Rows.Count);
            Assert.AreEqual("0", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
            Assert.AreNotEqual(Resources.Play_history_read_failed, dependencies.MainChartList.SummaryText);
            Assert.AreNotEqual(Resources.Play_history_read_failed, owner.SummaryDiagnosticText);
        });
    }

    /// <summary>
    /// keyword/表示対象のA→B→Aは同じ期間要求IDでも版で区別し、正常な最新Aへ古いAの失敗案内や未取得終端を適用しない。
    /// </summary>
    /// <remarks>実readerの失敗から表示dispatchまで進め、その直前に実ownerのrefresh受付と正常Aの採用を接続して順序を固定する。</remarks>
    [DataTestMethod]
    [DataRow("keyword")]
    [DataRow("display-target")]
    [DoNotParallelize]
    public void WorkflowOwner_ReadFailureRejectsOldRevisionAfterConditionsReturnToSameValue(string changedCondition)
    {
        WithScoreDb(scoreDbPath =>
        {
            var owner = new PlayHistoryWorkflowOwner();
            bool advanceBeforeApply = false;
            string? latestDiagnostic = null;
            PlayHistoryViewExecutionDependencies dependencies = CreateViewExecutionDependencies(
                () => PlayHistoryReadSourceContext.Lr2(scoreDbPath, true),
                apply =>
                {
                    if (advanceBeforeApply)
                    {
                        advanceBeforeApply = false;
                        long requestId = owner.CurrentRequestId;
                        PlayHistoryViewRequest BeginRefresh(string identity)
                        {
                            bool accepted = changedCondition == "keyword"
                                ? owner.TryBeginKeywordRefresh(identity, true, out PlayHistoryViewRequest request)
                                : owner.TryBeginDisplayTargetRefresh(identity, true, out request);
                            Assert.IsTrue(accepted);
                            return request;
                        }
                        void CompleteRefresh(PlayHistoryViewRequest request)
                        {
                            if (changedCondition == "keyword")
                            {
                                owner.CompleteKeywordRefresh(request.KeywordFilterRevision);
                            }
                            else
                            {
                                owner.CompleteDisplayTargetRefresh(request.DisplayTargetRevision);
                            }
                        }

                        PlayHistoryViewRequest away = BeginRefresh("different");
                        try
                        {
                            PlayHistoryViewRequest back = BeginRefresh(changedCondition == "keyword"
                                ? string.Empty : PlayHistoryDisplayTargetItem.All.Identity);
                            try
                            {
                                Assert.AreEqual(requestId, back.RequestId);
                                CreateInstalledScoreDb(scoreDbPath);
                                using (var db = new SQLiteConnection(scoreDbPath))
                                {
                                    InsertHistory(db, 2, HashA, UtcEpoch(2026, 1, 2), true, 250);
                                }
                                PlayHistoryViewExecutionResult latest = owner.ExecuteView(new PlayHistoryViewExecutionRequest(
                                    MainViewUpdateMode.PlayHistorySelected, MainViewUpdateMode.KeywordFilterUpdated,
                                    null, Stopwatch.StartNew(), back, string.Empty, PlayHistoryDisplayTargetItem.All));
                                Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, latest.Status);
                                latestDiagnostic = owner.SummaryDiagnosticText;
                            }
                            finally
                            {
                                CompleteRefresh(back);
                            }
                        }
                        finally
                        {
                            CompleteRefresh(away);
                        }
                    }
                    apply();
                    return true;
                },
                library: CreateHistoryLibrary(scoreDbPath));
            owner.ConfigureViewExecution(dependencies);
            // 初回の実読取りはDB不在で失敗し、表示dispatchより前に正常な最新Aを採用する。
            advanceBeforeApply = true;
            PlayHistoryViewExecutionResult stale = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Stale, stale.Status);
            Assert.AreEqual(latestDiagnostic, owner.SummaryDiagnosticText);
            Assert.AreEqual(2L, dependencies.MainChartList.Rows.Cast<PlayHistoryRow>().Single().HistoryId);
            Assert.AreEqual("1", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
            Assert.IsTrue(owner.WaitForRefreshQueuesIdleAsync().IsCompletedSuccessfully);
        });
    }

    /// <summary>
    /// 期間索引だけが失敗した場合も現在期間の新しい行・集計を採用し、過去日項目を外す。固定期間の取得は継続する。
    /// </summary>
    /// <remarks>旧ツリーのある実ownerへ期間外異常時刻と正常な新行を追加し、部分失敗の表示終端を確認する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void WorkflowOwner_PeriodIndexFailureClearsArchiveAndKeepsSuccessfulRows()
    {
        WithScoreDb(scoreDbPath =>
        {
            CreateInstalledScoreDb(scoreDbPath);
            long playedAt = UtcEpoch(2026, 1, 2);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, 1, HashA, playedAt, true, 200);
            }
            var owner = new PlayHistoryWorkflowOwner();
            PlayHistoryViewExecutionDependencies dependencies = CreateViewExecutionDependencies(
                () => PlayHistoryReadSourceContext.Lr2(scoreDbPath, true), library: CreateHistoryLibrary(scoreDbPath));
            owner.ConfigureViewExecution(dependencies);
            var period = PlayHistoryPeriodRequest.CreateDay(2026, 1, 2, TimeZoneInfo.Utc);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, ExecuteHistoryRead(owner, dependencies, period).Status);
            Assert.AreNotEqual(0, owner.ArchivePeriodTree.Count);
            owner.InvalidateReadCache("test_history_changed");
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, 2, HashA, playedAt + 1, true, 250);
                InsertHistory(db, 3, HashA, long.MaxValue, true, 300);
            }
            PlayHistoryViewExecutionResult result = ExecuteHistoryRead(owner, dependencies, period);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, result.Status);
            Assert.AreEqual(PlayHistoryPeriodIndexStageStatus.Failed, result.ReadResult.PeriodIndex.Status);
            CollectionAssert.AreEqual(new long[] { 2, 1 }, dependencies.MainChartList.Rows.Cast<PlayHistoryRow>().Select(row => row.HistoryId).ToArray());
            Assert.AreEqual("2", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
            StringAssert.Contains(owner.SummaryDiagnosticText, "play_history_period_index_failed");
            Assert.AreEqual(0, owner.ArchivePeriodTree.Count);
            var fixedPeriod = PlayHistoryPeriodRequest.Create(PlayHistoryPeriodKind.Today,
                new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, ExecuteHistoryRead(owner, dependencies, fixedPeriod).Status);
            Assert.AreEqual(2, dependencies.MainChartList.Rows.Count);
            Assert.AreEqual(0, owner.ArchivePeriodTree.Count);
        });
    }

    /// <summary>失敗表示のdispatch直前に通常画面へ移動した場合、旧履歴要求は通常の行・選択・summary・操作contextを変更しない。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void WorkflowOwner_ReadFailureDoesNotOverwriteNormalViewAfterDeactivation()
    {
        WithScoreDb(scoreDbPath =>
        {
            var owner = new PlayHistoryWorkflowOwner();
            Action changeView = () => throw new AssertFailedException("画面遷移fixtureが未設定です。");
            PlayHistoryViewExecutionDependencies dependencies = CreateViewExecutionDependencies(
                () => PlayHistoryReadSourceContext.Lr2(scoreDbPath, true),
                apply =>
                {
                    changeView();
                    apply();
                    return true;
                });
            var normalRow = LibraryChartRow.FromChartFile(ChartSongStorageMapping.FromBmsRow(
                new LR2SongDB.song { hash = HashA, title = "Normal", path = "normal.bms" }));
            string normalSummary = string.Empty;
            changeView = () =>
            {
                owner.Deactivate();
                dependencies.MainChartList.SetRows(new[] { normalRow }, distinctFolderCount: 1, updateSummary: true);
                dependencies.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                dependencies.MainChartList.SelectedIndex = 0;
                normalSummary = dependencies.MainChartList.SummaryText;
            };
            owner.ConfigureViewExecution(dependencies);
            PlayHistoryViewExecutionResult stale = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Stale, stale.Status);
            Assert.AreEqual(1, dependencies.MainChartList.Rows.Count);
            Assert.AreSame(normalRow, dependencies.MainChartList.Rows[0]);
            Assert.AreEqual(0, dependencies.MainChartList.SelectedIndex);
            Assert.AreEqual(normalSummary, dependencies.MainChartList.SummaryText);
            Assert.AreEqual(MainViewOperationSection.Library, dependencies.MainChartList.CurrentOperationContext.OperationSection);
            Assert.IsFalse(owner.IsViewActive);
            Assert.AreNotEqual(Resources.Play_history_read_failed, owner.SummaryDiagnosticText);
        });
    }

    /// <summary>
    /// 実際に参照する楽曲DBが壊れて投影索引を取得できない場合、未解決hashの正常行で代用せず失敗を表示する。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void WorkflowOwner_ProjectionDatabaseFailureDoesNotAdoptUnresolvedFallback()
    {
        WithScoreDb(scoreDbPath =>
        {
            CreateInstalledScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                InsertHistory(db, 1, HashA, UtcEpoch(2026, 1, 2), true, 200);
            }
            TestBmsLibrary library = CreateHistoryLibrary(scoreDbPath);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(scoreDbPath)!, "song.db"), "not a SQLite database");
            var owner = new PlayHistoryWorkflowOwner();
            PlayHistoryViewExecutionDependencies dependencies = CreateViewExecutionDependencies(
                () => PlayHistoryReadSourceContext.Lr2(scoreDbPath, true), library: library);
            owner.ConfigureViewExecution(dependencies);
            PlayHistoryViewExecutionResult failed = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.ReadFailed, failed.Status);
            Assert.AreEqual("projection", failed.ReadResult.FailedStage);
            Assert.IsNull(failed.ReadResult.Presentation);
            Assert.IsNull(owner.PresentationState.CurrentView);
            Assert.AreEqual(0, dependencies.MainChartList.Rows.Count);
            Assert.AreEqual(0, owner.SummaryCards.Count);
        });
    }

    /// <summary>beatoraja履歴の欠落はcacheへ採用せず、scorelogを用意した次の通常要求で正常0件を取得し、以後は成功cacheを使う。</summary>
    [TestMethod]
    public void ReadCache_BeatorajaMissingScoreLogIsNotCachedAndNormalEmptyRecovers()
    {
        WithBeatorajaPlayerDb((scoreDbPath, scoreLogDbPath) =>
        {
            CreateBeatorajaPlayerDb(scoreDbPath);
            var cache = new PlayHistoryReadCache();
            var request = new BeatorajaPlayHistoryReadRequest { ScoreDbPath = scoreDbPath };
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, cache.ReadBeatoraja(request, CancellationToken.None, out bool failedHit).SchemaStatus);
            Assert.IsFalse(failedHit);
            Assert.IsFalse(File.Exists(scoreLogDbPath));
            CreateBeatorajaScoreLogDb(scoreLogDbPath);
            BeatorajaPlayHistoryReadResult recovered = cache.ReadBeatoraja(request, CancellationToken.None, out bool recoveredHit);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, recovered.SchemaStatus);
            Assert.IsFalse(recoveredHit);
            Assert.AreEqual(0, recovered.Rows.Count);
            cache.ReadBeatoraja(request, CancellationToken.None, out bool normalHit);
            Assert.IsTrue(normalHit);
        });
    }

    private static TestBmsLibrary CreateHistoryLibrary(string scoreDbPath)
    {
        string songDbPath = Path.Combine(Path.GetDirectoryName(scoreDbPath)!, "song.db");
        using (var songDb = new LR2SongDBExtended(songDbPath))
        {
            songDb.CreateTable<LR2SongDB.song>();
            BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
            BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        }
        return new TestBmsLibrary(songDbPath);
    }

    private static PlayHistoryViewExecutionResult ExecuteHistoryRead(PlayHistoryWorkflowOwner owner,
        PlayHistoryViewExecutionDependencies dependencies, PlayHistoryPeriodRequest? period = null)
    {
        PlayHistoryDisplayTargetItem displayTarget = PlayHistoryDisplayTargetItem.All;
        PlayHistoryViewRequest request = owner.BeginRequest(period ?? PlayHistoryPeriodRequest.All(), string.Empty,
            displayTarget.Identity, owner.DisplayTargetRevision,
            dependencies.PlaylistWorkspace.PrepareDetailSourceRetirementWithoutPublishing());
        return owner.ExecuteView(new PlayHistoryViewExecutionRequest(MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.PlayHistorySelected, null, Stopwatch.StartNew(), request, string.Empty, displayTarget));
    }

    private static PlayHistoryViewExecutionDependencies CreateViewExecutionDependencies(
        Func<PlayHistoryReadSourceContext>? sourceResolver = null,
        Func<Action, bool>? invokePresentation = null,
        BMSLibrary? library = null)
    {
        return new PlayHistoryViewExecutionDependencies(
            () => library,
            () => null,
            sourceResolver ?? (() => PlayHistoryReadSourceContext.Lr2(string.Empty, isLr2LinkedProfile: false)),
            invokePresentation ?? (action =>
            {
                action();
                return true;
            }),
            (_, _) => { },
            () => MainViewUpdateMode.PlayHistorySelected,
            new MainChartListViewModel(),
            new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
    }

    /// <summary>
    /// 重なる候補更新を一投稿へ合流し、実行/終了中の破棄でidleへ戻す。空候補でも全件対象を保持する。
    /// </summary>
    [TestMethod]
    public void WorkflowOwner_OwnsDisplayTargetCatalogRefreshScheduling()
    {
        var owner = new PlayHistoryWorkflowOwner();
        var scheduled = new Queue<Action>();
        bool shutdown = false;
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => shutdown,
            () => [],
            action => scheduled.Enqueue(action));

        owner.QueueDisplayTargetCatalogRefresh();
        owner.QueueDisplayTargetCatalogRefresh(queueRefreshWhenSelectionChanges: false);

        Assert.AreEqual(1, scheduled.Count);
        Assert.IsFalse(owner.IsDisplayTargetCatalogRefreshIdle);

        scheduled.Dequeue()();

        Assert.IsTrue(owner.IsDisplayTargetCatalogRefreshIdle);
        Assert.AreEqual(1, owner.DisplayTargets.Count);
        Assert.AreEqual(PlayHistoryDisplayTargetItem.All.Identity, owner.SelectedDisplayTarget.Identity);

        owner.QueueDisplayTargetCatalogRefresh();
        shutdown = true;
        scheduled.Dequeue()();

        Assert.IsTrue(owner.IsDisplayTargetCatalogRefreshIdle);
        StringAssert.Contains(owner.DescribeDisplayTargetCatalogRefresh(), "displayTargetsRequestedRevision=");
    }

    /// <summary>
    /// 設定した候補snapshot providerを一度呼び、返した表を選択肢へ反映する。別の読取り元へ迂回しない。
    /// </summary>
    [TestMethod]
    public void WorkflowOwner_RefreshDisplayTargetCatalogUsesConfiguredSnapshotProvider()
    {
        var owner = new PlayHistoryWorkflowOwner();
        int snapshotCount = 0;
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => false,
            () =>
            {
                snapshotCount++;
                return [new BMSTable { name = "Configured" }];
            },
            _ => { });

        owner.RefreshDisplayTargetCatalog(queueRefreshWhenSelectionChanges: false);

        Assert.AreEqual(1, snapshotCount);
        Assert.IsTrue(owner.DisplayTargets.Any(item => item.Kind == PlayHistoryDisplayTargetKind.Playlist && item.Table?.name == "Configured"));
    }

    /// <summary>
    /// 候補snapshotの取得失敗は現在の全件選択を維持し、設定presetの適用を先に進めない。
    /// </summary>
    [TestMethod]
    public void WorkflowOwner_RefreshDisplayTargetSetsFromSettingsSnapshotsBeforeMutation()
    {
        var owner = new PlayHistoryWorkflowOwner();
        owner.ReplaceDisplayTargetCatalog([], queueRefreshWhenSelectionChanges: false);
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => false,
            () => throw new InvalidOperationException("snapshot failed"),
            _ => { });

        Assert.ThrowsException<InvalidOperationException>(() => owner.RefreshDisplayTargetSetsFromSettings(
            PlayHistoryDisplayTargetSetStore.Serialize(
            [
                new PlayHistoryDisplayTargetSet { Name = "Configured" }
            ]),
            queueRefreshWhenSelectionChanges: false));
        Assert.AreEqual(1, owner.DisplayTargets.Count);
        Assert.AreEqual(PlayHistoryDisplayTargetItem.All.Identity, owner.SelectedDisplayTarget.Identity);
    }

    /// <summary>
    /// 不正preset JSONは既存の候補集合と選択識別を維持して失敗し、解析失敗を空設定へ置き換えない。
    /// </summary>
    [TestMethod]
    public void WorkflowOwner_RefreshDisplayTargetSetsFromSettingsInvalidJsonPreservesState()
    {
        var owner = new PlayHistoryWorkflowOwner();
        owner.ReplaceDisplayTargetSets(
            [new PlayHistoryDisplayTargetSet
            {
                Name = "Existing",
                Targets = [new PlayHistoryDisplayTargetReference { PlaylistId = 1 }]
            }],
            [],
            queueRefreshWhenSelectionChanges: false);
        owner.ConfigureDisplayTargetPersistence(_ => { });
        PlayHistoryDisplayTargetItem selected = owner.DisplayTargets.First(item => item.Kind == PlayHistoryDisplayTargetKind.TargetSet);
        owner.SelectedDisplayTarget = selected;
        string[] displayTargetIdentities = owner.DisplayTargets.Select(item => item.Identity).ToArray();
        string selectedDisplayTargetIdentity = owner.SelectedDisplayTarget.Identity;
        owner.ConfigureDisplayTargetCatalogRefresh(
            () => false,
            () => [],
            _ => { });

        Assert.ThrowsException<InvalidOperationException>(() => owner.RefreshDisplayTargetSetsFromSettings(
            "not-json",
            queueRefreshWhenSelectionChanges: false));

        CollectionAssert.AreEqual(displayTargetIdentities, owner.DisplayTargets.Select(item => item.Identity).ToArray());
        Assert.AreEqual(selectedDisplayTargetIdentity, owner.SelectedDisplayTarget.Identity);
    }
}
