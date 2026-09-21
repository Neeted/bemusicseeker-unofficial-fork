using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLitePCL;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class SQLiteProviderRuntimeTests
{
    [TestMethod]
    public void RuntimeBootstrap_InitializesProviderIdempotently()
    {
        RuntimeBootstrap.Initialize();
        RuntimeBootstrap.Initialize();

        Assert.IsTrue(raw.sqlite3_libversion_number() > 0);
        using var connection = new SQLiteConnectionEx(":memory:");
        Assert.AreEqual(1L, connection.ExecuteScalar<long>("SELECT 1;"));
    }

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
        }
        finally
        {
            DeleteTemporaryDatabase(databasePath);
        }
    }

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
