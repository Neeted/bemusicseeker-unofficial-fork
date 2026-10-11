using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2PlayHistorySchemaServiceTests
{
    /// <summary>
    /// 非LR2連携ではnullパスでも検査対象外を返し、DBを開こうとして読取り失敗へ変えない。
    /// </summary>
    [TestMethod]
    public void Check_WhenProfileIsNotLr2Linked_SkipsWithoutOpeningDb()
    {
        var service = new Lr2PlayHistorySchemaService();

        Lr2PlayHistorySchemaCheckResult result = service.Check(null, isLr2LinkedProfile: false);

        Assert.AreEqual(Lr2PlayHistorySchemaStatus.SkippedProfile, result.Status);
    }

    /// <summary>
    /// 未導入DBの検査はNotInstalledを返すだけで履歴objectを作らず、確認操作が保存を変える誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void Check_NotInstalled_IsReadOnlyAndDoesNotCreateObjects()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            var service = new Lr2PlayHistorySchemaService();

            Lr2PlayHistorySchemaCheckResult result = service.Check(scoreDbPath, isLr2LinkedProfile: true);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, result.Status);
            using var verify = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
            Assert.AreEqual(0, CountPlayHistoryObjects(verify));
        });
    }

    /// <summary>
    /// 導入を二度実行してもInstalledとなり、履歴表・索引・記録/確定/掃除triggerを実DBへ揃える。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void InstallOrRepair_CreatesRequiredObjectsAndIsIdempotent()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            var service = new Lr2PlayHistorySchemaService();

            Lr2PlayHistorySchemaCheckResult first = service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
            Lr2PlayHistorySchemaCheckResult second = service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, first.Status);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, second.Status);
            using var verify = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
            AssertObjectExists(verify, "table", Lr2PlayHistorySchemaService.LastPlayTableName);
            AssertObjectExists(verify, "table", Lr2PlayHistorySchemaService.PlayHistoryTableName);
            AssertObjectExists(verify, "table", Lr2PlayHistorySchemaService.PlayPendingTableName);
            AssertObjectExists(verify, "index", Lr2PlayHistorySchemaService.HashTimeIndexName);
            AssertObjectExists(verify, "index", Lr2PlayHistorySchemaService.TimeIndexName);
            AssertObjectExists(verify, "trigger", Lr2PlayHistorySchemaService.ScoreInsertTriggerName);
            AssertObjectExists(verify, "trigger", Lr2PlayHistorySchemaService.ScoreUpdateTriggerName);
            AssertObjectExists(verify, "trigger", Lr2PlayHistorySchemaService.PlayerUpdateTriggerName);
            AssertObjectExists(verify, "trigger", Lr2PlayHistorySchemaService.PlayerCleanupTriggerName);
        });
    }

    /// <summary>
    /// 実score追加とplayer更新により履歴を確定し、旧新値・EXSCORE・演奏時間/判定差分・最終プレイ時刻を保存する。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void InstallOrRepair_InstalledTriggersRecordHistory()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            var service = new Lr2PlayHistorySchemaService();
            service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);

            using var db = new SQLiteConnection(scoreDbPath);
            InsertPlayer(db);
            InsertScore(db, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", playcount: 1, clear: 3, perfect: 100, great: 50);
            db.Execute(
                "UPDATE player SET playcount = ?, perfect = ?, great = ?, good = ?, bad = ?, poor = ?, playtime = ?, maxcombo = ? WHERE id = ?;",
                1, 100, 50, 10, 5, 2, 90, 120, "player");

            PlayHistoryProbe history = db.Query<PlayHistoryProbe>("SELECT * FROM bms_lr2_play_history;").Single();
            LastPlayProbe lastPlay = db.Query<LastPlayProbe>("SELECT * FROM bms_lr2_last_play;").Single();
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", history.hash);
            Assert.AreEqual(1, history.finalized);
            Assert.AreEqual("insert", history.score_write_type);
            Assert.IsNull(history.old_playcount);
            Assert.AreEqual(1, history.new_playcount);
            Assert.AreEqual(1, history.playcount_delta);
            Assert.AreEqual(250, history.new_exscore);
            Assert.AreEqual(90, history.playtime_delta);
            Assert.AreEqual(167, history.judge_delta);
            Assert.AreEqual(history.played_at, lastPlay.last_play_at);
        });
    }

    /// <summary>
    /// 既存score更新から旧新スコアとプレイ回数を記録し、続くplayer更新の差分で確定する。更新を追加履歴扱いする誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void InstallOrRepair_ScoreUpdateTriggerRecordsHistory()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            using (var seed = new SQLiteConnection(scoreDbPath))
            {
                InsertPlayer(seed, playcount: 1, perfect: 100, great: 50, good: 10, bad: 5, poor: 2, playtime: 90, maxcombo: 120);
                InsertScore(seed, "cccccccccccccccccccccccccccccccc", playcount: 1, clear: 3, perfect: 100, great: 50);
            }

            var service = new Lr2PlayHistorySchemaService();
            service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);

            using var db = new SQLiteConnection(scoreDbPath);
            db.Execute(
                "UPDATE score SET playcount = ?, clear = ?, perfect = ?, great = ?, good = ?, bad = ?, poor = ?, maxcombo = ?, minbp = ?, clearcount = ?, failcount = ?, scorehash = ? WHERE hash = ?;",
                2, 5, 120, 60, 12, 4, 1, 140, 4, 2, 0, "scorehash2", "cccccccccccccccccccccccccccccccc");
            db.Execute(
                "UPDATE player SET playcount = ?, perfect = ?, great = ?, good = ?, bad = ?, poor = ?, playtime = ?, maxcombo = ? WHERE id = ?;",
                2, 220, 110, 22, 9, 3, 180, 140, "player");

            PlayHistoryProbe history = db.Query<PlayHistoryProbe>("SELECT * FROM bms_lr2_play_history;").Single();
            Assert.AreEqual("update", history.score_write_type);
            Assert.AreEqual(1, history.old_playcount);
            Assert.AreEqual(2, history.new_playcount);
            Assert.AreEqual(1, history.playcount_delta);
            Assert.AreEqual(250, history.old_exscore);
            Assert.AreEqual(300, history.new_exscore);
            Assert.AreEqual(1, history.finalized);
            Assert.AreEqual(90, history.playtime_delta);
            Assert.AreEqual(197, history.judge_delta);
        });
    }

    /// <summary>
    /// 10秒を過ぎたpendingはplayer更新時に掃除し、対応しない履歴を確定しない。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void InstallOrRepair_StalePendingIsCleanedWithoutFinalizingHistory()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            var service = new Lr2PlayHistorySchemaService();
            service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);

            using var db = new SQLiteConnection(scoreDbPath);
            InsertPlayer(db);
            InsertScore(db, "dddddddddddddddddddddddddddddddd", playcount: 1, clear: 3, perfect: 100, great: 50);
            db.Execute("UPDATE bms_lr2_play_pending SET created_at = created_at - 11;");
            db.Execute(
                "UPDATE player SET playcount = ?, perfect = ?, great = ?, good = ?, bad = ?, poor = ?, playtime = ?, maxcombo = ? WHERE id = ?;",
                1, 100, 50, 10, 5, 2, 90, 120, "player");

            PlayHistoryProbe history = db.Query<PlayHistoryProbe>("SELECT * FROM bms_lr2_play_history;").Single();
            Assert.AreEqual(0, history.finalized);
            Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(1) FROM bms_lr2_play_pending;"));
        });
    }

    /// <summary>
    /// ghostのみのscore更新ではプレイ履歴を増やさず、保存補助情報の更新を実プレイと誤認しない。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void InstallOrRepair_GhostOnlyScoreUpdateDoesNotRecordHistory()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            using (var seed = new SQLiteConnection(scoreDbPath))
            {
                InsertScore(seed, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", playcount: 1, clear: 3, perfect: 100, great: 50);
            }

            var service = new Lr2PlayHistorySchemaService();
            service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);

            using var db = new SQLiteConnection(scoreDbPath);
            db.Execute("UPDATE score SET ghost = ? WHERE hash = ?;", new byte[] { 1, 2, 3 }, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");

            Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(1) FROM bms_lr2_play_history;"));
        });
    }

    /// <summary>
    /// 履歴表の必要時刻列が欠けたDBは不足列を示して手動修復を要求し、自動修復可能と誤分類しない。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void Check_TableColumnMissing_IsManualRepairRequired()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("CREATE TABLE bms_lr2_last_play (hash TEXT PRIMARY KEY);");
            }
            var service = new Lr2PlayHistorySchemaService();

            Lr2PlayHistorySchemaCheckResult result = service.Check(scoreDbPath, isLr2LinkedProfile: true);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.ManualRepairRequired, result.Status);
            CollectionAssert.Contains(result.MissingColumns, "bms_lr2_last_play.last_play_at");
        });
    }

    /// <summary>
    /// 履歴キーの主キー制約がないDBは互換不成立を報告し、列名だけで導入済みと判断しない。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void Check_TableColumnConstraintMissing_IsManualRepairRequired()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("CREATE TABLE bms_lr2_last_play (hash TEXT, last_play_at INTEGER NOT NULL);");
            }
            var service = new Lr2PlayHistorySchemaService();

            Lr2PlayHistorySchemaCheckResult result = service.Check(scoreDbPath, isLr2LinkedProfile: true);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.ManualRepairRequired, result.Status);
            CollectionAssert.Contains(result.IncompatibleColumns, "bms_lr2_last_play.hash");
        });
    }

    /// <summary>
    /// LR2 scoreの必要列が欠けたDBでは不足列を報告し、記録triggerを安全に導入できると扱わない。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void Check_BaseScoreColumnMissing_IsManualRepairRequired()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("CREATE TABLE score (hash TEXT PRIMARY KEY);");
                db.CreateTable<LR2ScoreDB.player>();
            }
            var service = new Lr2PlayHistorySchemaService();

            Lr2PlayHistorySchemaCheckResult result = service.Check(scoreDbPath, isLr2LinkedProfile: true);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.ManualRepairRequired, result.Status);
            CollectionAssert.Contains(result.MissingBaseColumns, "score.playcount");
        });
    }

    /// <summary>
    /// 元score表がないDBは検査/導入とも手動修復とし、履歴objectだけを作って不完全な状態にしない。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void Check_BaseScoreTableMissing_IsManualRepairRequiredAndInstallDoesNotCreateObjects()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.CreateTable<LR2ScoreDB.player>();
            }
            var service = new Lr2PlayHistorySchemaService();

            Lr2PlayHistorySchemaCheckResult check = service.Check(scoreDbPath, isLr2LinkedProfile: true);
            Lr2PlayHistorySchemaCheckResult install = service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.ManualRepairRequired, check.Status);
            CollectionAssert.Contains(check.MissingBaseTables, "score");
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.ManualRepairRequired, install.Status);
            using var verify = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
            Assert.AreEqual(0, CountPlayHistoryObjects(verify));
        });
    }

    /// <summary>
    /// 履歴用表名がviewに使われていれば衝突を報告し、既存objectを表と誤認/置換しない。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void Check_ReservedObjectNameCollision_IsManualRepairRequired()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("CREATE VIEW bms_lr2_last_play AS SELECT '' AS hash, 0 AS last_play_at;");
            }
            var service = new Lr2PlayHistorySchemaService();

            Lr2PlayHistorySchemaCheckResult result = service.Check(scoreDbPath, isLr2LinkedProfile: true);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.ManualRepairRequired, result.Status);
            CollectionAssert.Contains(result.ObjectNameCollisions, "bms_lr2_last_play:view");
        });
    }

    /// <summary>
    /// DBファイルが未作成ならUnreadableを返す。
    /// </summary>
    [TestMethod]
    public void Check_MissingScoreDb_IsUnreadable()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            var service = new Lr2PlayHistorySchemaService();

            Lr2PlayHistorySchemaCheckResult result = service.Check(scoreDbPath, isLr2LinkedProfile: true);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, result.Status);
        });
    }

    /// <summary>
    /// 非LR2連携の導入要求はnullパスでも対象外を返し、保存窓口へ到達しない。
    /// </summary>
    [TestMethod]
    public void InstallOrRepair_WhenProfileIsNotLr2Linked_SkipsWithoutOpeningDb()
    {
        var service = new Lr2PlayHistorySchemaService();

        Lr2PlayHistorySchemaCheckResult result = service.InstallOrRepair(null, isLr2LinkedProfile: false);

        Assert.AreEqual(Lr2PlayHistorySchemaStatus.SkippedProfile, result.Status);
    }

    /// <summary>
    /// 索引とtriggerが消失した導入済みDBをRepairableと判定し、再導入で不足objectを修復してInstalledへ戻す。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void InstallOrRepair_RecreatesMissingObjects()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            var service = new Lr2PlayHistorySchemaService();
            service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("DROP INDEX " + Lr2PlayHistorySchemaService.TimeIndexName + ";");
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
            }

            Lr2PlayHistorySchemaCheckResult before = service.Check(scoreDbPath, isLr2LinkedProfile: true);
            Lr2PlayHistorySchemaCheckResult after = service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, before.Status);
            CollectionAssert.Contains(before.MissingIndexes, Lr2PlayHistorySchemaService.TimeIndexName);
            CollectionAssert.Contains(before.MissingTriggers, Lr2PlayHistorySchemaService.ScoreInsertTriggerName);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, after.Status);
        });
    }

    /// <summary>
    /// 定義が違う索引/triggerを修復しても既存履歴行を保持し、schema修復による履歴消失を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void InstallOrRepair_RecreatesMismatchedObjectsWithoutDroppingHistory()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            var service = new Lr2PlayHistorySchemaService();
            service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("INSERT INTO bms_lr2_play_history (hash, played_at, score_write_type, new_playcount, playcount_delta) VALUES (?, ?, ?, ?, ?);",
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 100, "insert", 1, 1);
                db.Execute("DROP INDEX " + Lr2PlayHistorySchemaService.TimeIndexName + ";");
                db.Execute("CREATE INDEX " + Lr2PlayHistorySchemaService.TimeIndexName + " ON bms_lr2_play_history(hash);");
                db.Execute("DROP TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + ";");
                db.Execute("CREATE TRIGGER " + Lr2PlayHistorySchemaService.ScoreInsertTriggerName + " AFTER INSERT ON score BEGIN SELECT 1; END;");
            }

            Lr2PlayHistorySchemaCheckResult before = service.Check(scoreDbPath, isLr2LinkedProfile: true);
            Lr2PlayHistorySchemaCheckResult after = service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, before.Status);
            CollectionAssert.Contains(before.MismatchedIndexes, Lr2PlayHistorySchemaService.TimeIndexName);
            CollectionAssert.Contains(before.MismatchedTriggers, Lr2PlayHistorySchemaService.ScoreInsertTriggerName);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, after.Status);
            using var verify = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM bms_lr2_play_history WHERE hash = ?;", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        });
    }

    /// <summary>
    /// triggerのみの解除は記録用triggerを外して履歴表・索引・行を残し、状態をRepairableへ戻す。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void Uninstall_TriggersOnlyKeepsHistoryAndLeavesSchemaRepairable()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            var service = new Lr2PlayHistorySchemaService();
            service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
            using (var db = new SQLiteConnection(scoreDbPath))
            {
                db.Execute("INSERT INTO bms_lr2_play_history (hash, played_at, score_write_type, new_playcount, playcount_delta) VALUES (?, ?, ?, ?, ?);",
                    "ffffffffffffffffffffffffffffffff", 100, "insert", 1, 1);
            }

            Lr2PlayHistorySchemaCheckResult result = service.Uninstall(
                scoreDbPath,
                isLr2LinkedProfile: true,
                Lr2PlayHistorySchemaUninstallMode.TriggersOnly);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Repairable, result.Status);
            CollectionAssert.Contains(result.MissingTriggers, Lr2PlayHistorySchemaService.ScoreInsertTriggerName);
            CollectionAssert.Contains(result.MissingTriggers, Lr2PlayHistorySchemaService.ScoreUpdateTriggerName);
            CollectionAssert.Contains(result.MissingTriggers, Lr2PlayHistorySchemaService.PlayerUpdateTriggerName);
            CollectionAssert.Contains(result.MissingTriggers, Lr2PlayHistorySchemaService.PlayerCleanupTriggerName);
            using var verify = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
            AssertObjectExists(verify, "table", Lr2PlayHistorySchemaService.LastPlayTableName);
            AssertObjectExists(verify, "table", Lr2PlayHistorySchemaService.PlayHistoryTableName);
            AssertObjectExists(verify, "table", Lr2PlayHistorySchemaService.PlayPendingTableName);
            AssertObjectExists(verify, "index", Lr2PlayHistorySchemaService.HashTimeIndexName);
            AssertObjectExists(verify, "index", Lr2PlayHistorySchemaService.TimeIndexName);
            AssertObjectDoesNotExist(verify, "trigger", Lr2PlayHistorySchemaService.ScoreInsertTriggerName);
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM bms_lr2_play_history WHERE hash = ?;", "ffffffffffffffffffffffffffffffff"));
        });
    }

    /// <summary>
    /// 完全解除は本アプリの履歴objectを全て除き、LR2本来のscore/player表を残してNotInstalledを返す。
    /// </summary>
    /// <remarks>実SQLiteの保存objectとtriggerを検査し、SQLの実行・保存結果を局所的な判定から独立して確認する。</remarks>
    [TestMethod]
    public void Uninstall_TablesAndTriggersRemovesPlayHistoryObjects()
    {
        WithScoreDb(delegate (string scoreDbPath)
        {
            CreateBaseScoreDb(scoreDbPath);
            var service = new Lr2PlayHistorySchemaService();
            service.InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);

            Lr2PlayHistorySchemaCheckResult result = service.Uninstall(
                scoreDbPath,
                isLr2LinkedProfile: true,
                Lr2PlayHistorySchemaUninstallMode.TablesAndTriggers);

            Assert.AreEqual(Lr2PlayHistorySchemaStatus.NotInstalled, result.Status);
            using var verify = new SQLiteConnection(scoreDbPath, SQLiteOpenFlags.ReadOnly);
            Assert.AreEqual(0, CountPlayHistoryObjects(verify));
            AssertObjectExists(verify, "table", "score");
            AssertObjectExists(verify, "table", "player");
        });
    }

    /// <summary>
    /// 導入SQLにLR2のSQLite3.6.7で使えないUPSERT・RETURNING・CTE・window・生成列構文を混ぜない。
    /// </summary>
    [TestMethod]
    public void InstallSqlStatements_DoNotUseKnownSqlite367IncompatibleSyntax()
    {
        string sql = string.Join(Environment.NewLine, Lr2PlayHistorySchemaService.InstallSqlStatements);

        StringAssert.DoesNotMatch(sql, new System.Text.RegularExpressions.Regex(@"\bON\s+CONFLICT\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        StringAssert.DoesNotMatch(sql, new System.Text.RegularExpressions.Regex(@"\bRETURNING\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        StringAssert.DoesNotMatch(sql, new System.Text.RegularExpressions.Regex(@"\bWITH\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        StringAssert.DoesNotMatch(sql, new System.Text.RegularExpressions.Regex(@"\bOVER\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        StringAssert.DoesNotMatch(sql, new System.Text.RegularExpressions.Regex(@"\bGENERATED\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private static void WithScoreDb(Action<string> action)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PlayHistorySchema_" + Guid.NewGuid().ToString("N"));
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

    private static void CreateBaseScoreDb(string scoreDbPath)
    {
        using var db = new SQLiteConnection(scoreDbPath);
        db.RunInTransaction(() =>
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
        });
    }

    private static void InsertPlayer(
        SQLiteConnection db,
        int playcount = 0,
        int perfect = 0,
        int great = 0,
        int good = 0,
        int bad = 0,
        int poor = 0,
        int playtime = 0,
        int maxcombo = 0)
    {
        db.Execute(
            "INSERT INTO player (id, playcount, clear, fail, perfect, great, good, bad, poor, playtime, maxcombo) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
            "player", playcount, 0, 0, perfect, great, good, bad, poor, playtime, maxcombo);
    }

    private static void InsertScore(
        SQLiteConnection db,
        string hash,
        int playcount,
        int clear,
        int perfect,
        int great)
    {
        db.Execute(
            "INSERT INTO score (hash, clear, perfect, great, good, bad, poor, totalnotes, maxcombo, minbp, playcount, clearcount, failcount, rate, clear_db, op_history, scorehash, ghost, clear_sd, clear_ex, op_best, rseed, complete) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
            hash, clear, perfect, great, 10, 5, 2, 200, 120, 7, playcount, playcount, 0, 75, 0, 0, "scorehash", null, 0, 0, 31, 1234, 1);
    }

    private static int CountPlayHistoryObjects(SQLiteConnection db)
    {
        return db.ExecuteScalar<int>("SELECT COUNT(1) FROM sqlite_master WHERE name LIKE 'bms_lr2_%' OR name LIKE 'idx_bms_lr2_%';");
    }

    private static void AssertObjectExists(SQLiteConnection db, string type, string name)
    {
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(1) FROM sqlite_master WHERE type = ? AND name = ?;", type, name), type + ":" + name);
    }

    private static void AssertObjectDoesNotExist(SQLiteConnection db, string type, string name)
    {
        Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(1) FROM sqlite_master WHERE type = ? AND name = ?;", type, name), type + ":" + name);
    }

    private sealed class PlayHistoryProbe
    {
        public string hash { get; set; } = string.Empty;

        public long played_at { get; set; }

        public int finalized { get; set; }

        public string score_write_type { get; set; } = string.Empty;

        public int? old_playcount { get; set; }

        public int new_playcount { get; set; }

        public int playcount_delta { get; set; }

        public int? old_exscore { get; set; }

        public int? new_exscore { get; set; }

        public int playtime_delta { get; set; }

        public int judge_delta { get; set; }
    }

    private sealed class LastPlayProbe
    {
        public long last_play_at { get; set; }
    }
}
