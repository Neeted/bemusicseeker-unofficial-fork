using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLitePCL;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SQLiteProviderRuntimeTests
{
    /// <summary>SQLite初期化を繰り返しても実メモリDBで問合せが成立し、二重初期化によるprovider破損を防ぎます。</summary>
    [TestMethod]
    public void RuntimeBootstrap_InitializesProviderIdempotently()
    {
        RuntimeBootstrap.Initialize();
        RuntimeBootstrap.Initialize();

        Assert.IsTrue(raw.sqlite3_libversion_number() > 0);
        using var connection = new SQLiteConnectionEx(":memory:");
        Assert.AreEqual(1L, connection.ExecuteScalar<long>("SELECT 1;"));
    }

    /// <summary>実SQLiteから日本語とNULLを生値で読取り、空文字化や文字化けで保存値を変えません。</summary>
    [TestMethod]
    public void SQLiteCommandExtended_PreservesRawTextAndNullValues()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_RawValues_" + Guid.NewGuid().ToString("N"),
            "song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, []);
        try
        {
            using var connection = new LR2SongDBExtended(databasePath);
            connection.Execute("CREATE TABLE raw_probe (text_value TEXT, null_value TEXT);");
            connection.Execute("INSERT INTO raw_probe (text_value, null_value) VALUES ('日本語', NULL);");

            var command = (LR2SongDBExtended.SQLiteCommandExtended)connection.CreateCommand(
                "SELECT text_value, null_value FROM raw_probe;");
            string[] row = command.GetRawValuesAsString().Single();

            Assert.AreEqual("日本語", row[0]);
            Assert.IsNull(row[1]);
        }
        finally
        {
            string directoryPath = Path.GetDirectoryName(databasePath)!;
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <summary>実triggerによるstep失敗を一括生値読取りから返し、finalizeの後処理で元の診断を上書きしません。</summary>
    [TestMethod]
    public void SQLiteCommandExtended_GetRawValues_ThrowsStepErrorWithoutReplacingItDuringFinalize()
    {
        string databasePath = CreateTemporaryDatabasePath("RawStep");
        try
        {
            using var connection = new LR2SongDBExtended(databasePath);
            connection.Execute("CREATE TABLE raw_probe (text_value TEXT NOT NULL);");
            connection.Execute("CREATE TRIGGER raw_probe_abort BEFORE INSERT ON raw_probe BEGIN SELECT RAISE(ABORT, 'deterministic step failure'); END;");

            var command = (LR2SongDBExtended.SQLiteCommandExtended)connection.CreateCommand(
                "INSERT INTO raw_probe (text_value) VALUES ('value') RETURNING text_value;");

            Exception? failure = null;
            try
            {
                command.GetRawValuesAsString();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure);
            StringAssert.Contains(failure!.Message, "sqlite3_step");
            StringAssert.Contains(failure.Message, "19");
            StringAssert.Contains(failure.Message, "deterministic step failure");
            connection.Execute("DROP TRIGGER raw_probe_abort;");
            connection.Execute("INSERT INTO raw_probe (text_value) VALUES ('next explicit');");
            Assert.AreEqual("next explicit", connection.ExecuteScalar<string>("SELECT text_value FROM raw_probe;"));
        }
        finally
        {
            DeleteTemporaryDatabase(databasePath);
        }
    }

    /// <summary>実triggerによるstep失敗を逐次生値読取りから返し、空の正常終了に置き換えません。</summary>
    [TestMethod]
    public void SQLiteCommandExtended_ForEachRawValue_ThrowsWhenStepReturnsError()
    {
        string databasePath = CreateTemporaryDatabasePath("RawStep");
        try
        {
            using var connection = new LR2SongDBExtended(databasePath);
            connection.Execute("CREATE TABLE raw_probe (text_value TEXT NOT NULL);");
            connection.Execute("CREATE TRIGGER raw_probe_abort BEFORE INSERT ON raw_probe BEGIN SELECT RAISE(ABORT, 'deterministic step failure'); END;");

            var command = (LR2SongDBExtended.SQLiteCommandExtended)connection.CreateCommand(
                "INSERT INTO raw_probe (text_value) VALUES ('value') RETURNING text_value;");

            Exception? failure = null;
            try
            {
                command.ForEachRawValueAsString(_ => { });
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            Assert.IsNotNull(failure);
            StringAssert.Contains(failure!.Message, "sqlite3_step");
            StringAssert.Contains(failure.Message, "19");
            StringAssert.Contains(failure.Message, "deterministic step failure");
        }
        finally
        {
            DeleteTemporaryDatabase(databasePath);
        }
    }

    /// <summary>DELETE journalの実commit Busyを失敗として返し、読取り解除後に未確定行が残らず次の明示transactionが成功する。commitの再呼出しによる偽成功を検出する。</summary>
    [TestMethod]
    public void SQLiteConnectionEx_CommitBusyRemainsFailureAndRollsBackUncommittedChanges()
    {
        string databasePath = CreateTemporaryDatabasePath("CommitBusy");
        try
        {
            using var writer = new SQLiteConnectionEx(databasePath);
            Assert.AreEqual("delete", writer.ExecuteScalar<string>("PRAGMA journal_mode=DELETE;"));
            writer.BusyTimeout = TimeSpan.Zero;
            writer.Execute("CREATE TABLE commit_probe (id INTEGER PRIMARY KEY, value TEXT);");
            writer.Execute("INSERT INTO commit_probe VALUES (1, 'prefix');");
            using (var reader = new SQLiteConnectionEx(databasePath, SQLite.SQLiteOpenFlags.ReadOnly))
            {
                reader.BeginTransaction();
                try
                {
                    Assert.AreEqual("prefix", reader.ExecuteScalar<string>("SELECT value FROM commit_probe WHERE id = 1;"));
                    writer.BeginTransaction();
                    writer.Execute("INSERT INTO commit_probe VALUES (2, 'uncommitted');");
                    SQLite.SQLiteException failure = Assert.ThrowsException<SQLite.SQLiteException>(writer.Commit);
                    Assert.AreEqual(SQLite.SQLite3.Result.Busy, failure.Result);
                }
                finally { reader.Rollback(); }
            }
            Assert.AreEqual(1, writer.ExecuteScalar<int>("SELECT COUNT(*) FROM commit_probe;"));
            Assert.AreEqual("prefix", writer.ExecuteScalar<string>("SELECT value FROM commit_probe WHERE id = 1;"));
            writer.BeginTransaction();
            writer.Execute("INSERT INTO commit_probe VALUES (3, 'next explicit');");
            writer.Commit();
            Assert.AreEqual("next explicit", writer.ExecuteScalar<string>("SELECT value FROM commit_probe WHERE id = 3;"));
            Assert.AreEqual(0, writer.ExecuteScalar<int>("SELECT COUNT(*) FROM commit_probe WHERE id = 2;"));
        }
        finally { DeleteTemporaryDatabase(databasePath); }
    }

    /// <summary>別接続の書込みロックで実Delete・InsertOrReplaceを失敗させ、SQL一回と保存値不変を観測して外側再試行や偽成功を検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SQLiteConnectionEx_WriteContentionExecutesOnceAndPreservesData(bool replace)
    {
        string databasePath = CreateTemporaryDatabasePath("WriteBusy");
        try
        {
            using var holder = new SQLiteConnectionEx(databasePath);
            holder.CreateTable<ContentionRow>();
            holder.Insert(new ContentionRow { Id = 1, Value = "original" });
            using var contender = new SQLiteConnectionEx(databasePath);
            contender.BusyTimeout = TimeSpan.Zero;
            // mapping準備を競合前に済ませ、要求SQLの実行だけを既存providerのtraceで数えます。
            contender.GetMapping<ContentionRow>();
            var statements = new List<string>();
            contender.Trace = true;
            contender.Tracer = statements.Add;
            holder.Execute("BEGIN IMMEDIATE;");
            try
            {
                SQLite.SQLiteException failure = Assert.ThrowsException<SQLite.SQLiteException>(() =>
                {
                    if (replace) { contender.InsertOrReplace(new ContentionRow { Id = 1, Value = "replacement" }, typeof(ContentionRow)); }
                    else { contender.Delete<ContentionRow>(1); }
                });
                Assert.AreEqual(SQLite.SQLite3.Result.Busy, failure.Result);
                Assert.AreEqual(1, statements.Count(statement => statement.Contains(
                    replace ? "insert or replace" : "delete from", StringComparison.OrdinalIgnoreCase)));
            }
            finally { holder.Execute("ROLLBACK;"); }
            Assert.AreEqual("original", holder.Table<ContentionRow>().Single().Value);
        }
        finally { DeleteTemporaryDatabase(databasePath); }
    }

    /// <summary>楽曲・成績DBの通常接続と読取り接続にnativeの60秒busy待機が設定され、外側再試行除去で待機契約が失われません。</summary>
    [TestMethod]
    public void Lr2Connections_KeepNativeBusyTimeoutForReadAndWrite()
    {
        string databasePath = CreateTemporaryDatabasePath("BusyTimeout");
        try
        {
            using (var song = new LR2SongDBExtended(databasePath)) { Assert.AreEqual(60000, song.ExecuteScalar<int>("PRAGMA busy_timeout;")); }
            using (var score = new LR2ScoreDBExtended(databasePath)) { Assert.AreEqual(60000, score.ExecuteScalar<int>("PRAGMA busy_timeout;")); }
            using (var song = new LR2SongDBExtended(databasePath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            { Assert.AreEqual(60000, song.ExecuteScalar<int>("PRAGMA busy_timeout;")); }
            using (var score = new LR2ScoreDBExtended(databasePath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            { Assert.AreEqual(60000, score.ExecuteScalar<int>("PRAGMA busy_timeout;")); }
        }
        finally { DeleteTemporaryDatabase(databasePath); }
    }

    private sealed class ContentionRow
    {
        [SQLite.PrimaryKey]
        public int Id { get; set; }
        public string Value { get; set; } = string.Empty;
    }

    private static string CreateTemporaryDatabasePath(string name)
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_" + name + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        string databasePath = Path.Combine(directoryPath, "song.db");
        File.WriteAllBytes(databasePath, []);
        return databasePath;
    }

    private static void DeleteTemporaryDatabase(string databasePath)
    {
        string directoryPath = Path.GetDirectoryName(databasePath)!;
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

}
