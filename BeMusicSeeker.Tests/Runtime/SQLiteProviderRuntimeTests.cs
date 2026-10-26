using System;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>実SQLiteの型付き読取りでdoubleのbit・ticks・NULL・原文を保ち、従来string APIの日本語とNULLも維持します。</summary>
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
            connection.Execute("CREATE TABLE raw_probe (text_value TEXT, null_value TEXT, integer_value INTEGER, real_value REAL, numeric_text TEXT);");
            double precise = Math.BitIncrement(1.5);
            const long ticks = 639029198450000001;
            connection.Execute("INSERT INTO raw_probe VALUES ('日本語', NULL, ?, ?, '001');", ticks, precise);

            var command = (LR2SongDBExtended.SQLiteCommandExtended)connection.CreateCommand(
                "SELECT text_value, null_value FROM raw_probe;");
            string[] row = command.GetRawValuesAsString().Single();

            Assert.AreEqual("日本語", row[0]);
            Assert.IsNull(row[1]);
            var typed = (LR2SongDBExtended.SQLiteCommandExtended)connection.CreateCommand("SELECT * FROM raw_probe;");
            Assert.AreEqual(1, typed.ForEachRawRow(value =>
            {
                Assert.AreEqual(5, value.Count);
                Assert.AreEqual("日本語", value.GetString(0));
                Assert.IsNull(value.GetString(1));
                Assert.IsNull(value.GetInt64(1));
                Assert.IsNull(value.GetDouble(1));
                Assert.IsFalse(value.GetBoolean(1));
                Assert.AreEqual(ticks, value.GetInt64(2));
                Assert.AreEqual(ticks, value.GetDateTime(2)?.Ticks);
                Assert.AreEqual(BitConverter.DoubleToInt64Bits(precise), BitConverter.DoubleToInt64Bits(value.GetDouble(3) ?? double.NaN));
                Assert.AreEqual("001", value.GetString(4));
            }));
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

    /// <summary>型を保持した実SQLite値で整数範囲・REAL拒否・固定TEXT文法・bool・ticksを判定し、native coercionや現在文化への依存を検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("th-TH")]
    public void SQLiteCommandExtended_TypedGettersKeepStorageTypeAndFixedTextGrammar(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        string path = CreateTemporaryDatabasePath("TypedValues");
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            using var connection = new LR2SongDBExtended(path);
            var cases = new (string Sql, string Type, long? Integer, double? Double, bool Boolean, long? Ticks)[]
            {
                ("NULL", "null", null, null, false, null),
                ("0", "integer", 0L, 0d, false, 0L),
                ("'  +12  '", "text", 12L, 12d, true, 12L),
                ("''", "text", null, null, false, null),
                ("'1.0'", "text", null, 1d, false, null),
                ("3155378975999999999", "integer", 3155378975999999999L, 3155378975999999999d, true, 3155378975999999999L),
                ("3155378976000000000", "integer", 3155378976000000000L, 3155378976000000000d, true, null),
                ("2147483648", "integer", 2147483648L, 2147483648d, true, 2147483648L),
                ("-2147483649", "integer", -2147483649L, -2147483649d, true, null),
                ("9223372036854775807", "integer", long.MaxValue, (double)long.MaxValue, true, null),
                ("-9223372036854775808", "integer", long.MinValue, (double)long.MinValue, true, null),
                ("1.0", "real", null, 1d, false, null),
                ("'-2'", "text", -2L, -2d, true, null),
                ("'0'", "text", 0L, 0d, false, 0L),
                ("'2147483647'", "text", int.MaxValue, int.MaxValue, true, int.MaxValue),
                ("'-2147483648'", "text", int.MinValue, int.MinValue, true, null),
                ("'9223372036854775808'", "text", null, 9223372036854775808d, false, null),
                ("'1,234.5'", "text", null, 1234.5d, false, null),
                ("'1e3'", "text", null, 1000d, false, null),
                ("'True'", "text", null, null, true, null),
                ("'False'", "text", null, null, false, null),
                ("'1,5x'", "text", null, null, false, null),
                ("'2026/1/2'", "text", null, null, false, null),
                ("'639029198450000001'", "text", 639029198450000001L, 639029198450000001d, true, 639029198450000001L),
                ("X'3132'", "blob", null, null, false, null)
            };
            foreach ((string Sql, string Type, long? Integer, double? Double, bool Boolean, long? Ticks) item in cases)
            {
                var command = (LR2SongDBExtended.SQLiteCommandExtended)connection.CreateCommand($"SELECT {item.Sql}, typeof({item.Sql});");
                Assert.AreEqual(1, command.ForEachRawRow(row =>
                {
                    Assert.AreEqual(item.Type, row.GetString(1), item.Sql);
                    Assert.AreEqual(item.Integer, row.GetInt64(0), item.Sql);
                    int? integer32 = item.Integer is long integer && integer >= int.MinValue && integer <= int.MaxValue ? (int)integer : null;
                    Assert.AreEqual(integer32, row.GetInt32(0), item.Sql);
                    Assert.AreEqual(item.Double, row.GetDouble(0), item.Sql);
                    Assert.AreEqual(item.Boolean, row.GetBoolean(0), item.Sql);
                    Assert.AreEqual(item.Ticks, row.GetDateTime(0)?.Ticks, item.Sql);
                }));
            }
        }
        finally { CultureInfo.CurrentCulture = previous; DeleteTemporaryDatabase(path); }
    }

    /// <summary>借用callbackの同一例外を返し、statement解放後の明示SQLが成功することを実接続で確認します。</summary>
    [TestMethod]
    public void SQLiteCommandExtended_TypedCallbackPreservesExceptionAndReleasesStatement()
    {
        string path = CreateTemporaryDatabasePath("TypedCallback");
        try
        {
            using var connection = new LR2SongDBExtended(path);
            var failure = new InvalidOperationException("callback failure");
            var command = (LR2SongDBExtended.SQLiteCommandExtended)connection.CreateCommand("SELECT 1;");
            Exception actual = Assert.ThrowsException<InvalidOperationException>(() => command.ForEachRawRow(_ => throw failure));
            Assert.AreSame(failure, actual);
            connection.Execute("CREATE TABLE after_callback (value INTEGER);");
            connection.Execute("INSERT INTO after_callback VALUES (2);");
            Assert.AreEqual(2, connection.ExecuteScalar<int>("SELECT value FROM after_callback;"));
        }
        finally { DeleteTemporaryDatabase(path); }
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
    [DataRow(false)]
    [DataRow(true)]
    public void SQLiteCommandExtended_ForEachRawValue_ThrowsWhenStepReturnsError(bool typed)
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
                if (typed) { command.ForEachRawRow(_ => { }); }
                else { command.ForEachRawValueAsString(_ => { }); }
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
