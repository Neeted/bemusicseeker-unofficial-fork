using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LR2SongDBExtendedUninstallTests
{
    [TestMethod]
    public void BeMusicSeekerOwnedTableNames_CoversAllExtendedTables()
    {
        string[] tableAttributeNames = [.. typeof(LR2SongDBExtended)
            .GetNestedTypes()
            .Select(type => Attribute.GetCustomAttribute(type, typeof(TableAttribute)))
            .OfType<TableAttribute>()
            .Select(attribute => attribute.Name)
            .OrderBy(name => name, StringComparer.Ordinal)];
        string[] uninstallNames = [.. LR2SongDBExtended.BeMusicSeekerOwnedTableNames.OrderBy(name => name, StringComparer.Ordinal)];

        CollectionAssert.AreEqual(tableAttributeNames, uninstallNames);
    }

    [TestMethod]
    public void Uninstall_DropsAllBeMusicSeekerTablesAndKeepsLr2Tables()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(LR2SongDBExtendedUninstallTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
            songDb.CreateTable<LR2SongDB.folder>();
            songDb.CreateTable<LR2SongDBExtended.install>();
            songDb.CreateTable<LR2SongDBExtended.maintenance>();
            BMSPlaylist.EnsureSchema(songDbPath);
            BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
            BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
            songDb.CreateTable<LR2SongDBExtended.ir_score>();
            songDb.CreateTable<LR2SongDBExtended.ir_score_refresh_metadata>();
            BmsLibraryDbGateway.EnsureIrDataSchema(songDb);
            BmsLibraryDbGateway.EnsureLr2SongDbSyncStatusSchema(songDb);
            songDb.Insert(new LR2SongDBExtended.playlist
            {
                name = "uninstall sequence test"
            }, typeof(LR2SongDBExtended.playlist));

            foreach (string tableName in LR2SongDBExtended.BeMusicSeekerOwnedTableNames)
            {
                Assert.IsTrue(TableExists(songDb, tableName), tableName + " should exist before uninstall.");
            }
            foreach (string indexName in LR2SongDBExtended.BeMusicSeekerOwnedNativeIndexNames)
            {
                Assert.IsTrue(IndexExists(songDb, indexName), indexName + " should exist before uninstall.");
            }
            Assert.AreEqual(1L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_sequence WHERE name = ?;", SQLiteTable<LR2SongDBExtended.playlist>.GetTableName()));

            songDb.Uninstall();

            foreach (string tableName in LR2SongDBExtended.BeMusicSeekerOwnedTableNames)
            {
                Assert.IsFalse(TableExists(songDb, tableName), tableName + " should be dropped by uninstall.");
            }
            foreach (string indexName in LR2SongDBExtended.BeMusicSeekerOwnedNativeIndexNames)
            {
                Assert.IsFalse(IndexExists(songDb, indexName), indexName + " should be dropped by uninstall.");
            }
            Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_sequence WHERE name IN (" + string.Join(", ", LR2SongDBExtended.BeMusicSeekerOwnedTableNames.Select(SqlQuote)) + ");"));
            Assert.IsTrue(TableExists(songDb, SQLiteTable<LR2SongDB.song>.GetTableName()));
            Assert.IsTrue(TableExists(songDb, SQLiteTable<LR2SongDB.folder>.GetTableName()));
            Assert.IsTrue(IndexExists(songDb, "hashidx"));
            Assert.IsTrue(IndexExists(songDb, "parentidx"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static bool TableExists(SQLiteConnection db, string tableName)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = ?;", tableName) > 0;
    }

    private static bool IndexExists(SQLiteConnection db, string indexName)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = ?;", indexName) > 0;
    }

    private static string SqlQuote(string value)
    {
        return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
    }
}
