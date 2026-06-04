using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FolderFileDbSyncServiceTests
{
    [TestMethod]
    public void Sync_UpsertsCurrentRowsAndPrunesStaleRowsInScope()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            string outOfScopePath = Path.GetFullPath(@"D:\Other\0000.lr2folder");
            string normalDirectoryPath = outputDirectory + Path.DirectorySeparatorChar;
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = currentPath,
                title = "Old",
                type = 2,
                adddate = 12345
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = outOfScopePath,
                title = "External",
                type = 2
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = normalDirectoryPath,
                title = "Normal",
                type = 1
            }, typeof(LR2SongDB.folder));

            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(
                        [
                            "#TITLE Current",
                            "#COMMAND song.hash in ('abc')",
                            "#MAXTRACKS 40"
                        ])
                    }
                ],
                ScopeDirectories = [outputDirectory],
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true
            });

            Assert.AreEqual(1, result.ItemCount);
            Assert.AreEqual(1, result.GeneratedCount);
            Assert.AreEqual(1, result.UpsertedCount);
            Assert.AreEqual(1, result.DeletedCount);
            LR2SongDB.folder current = songDb.Table<LR2SongDB.folder>().Single(row => row.path == currentPath);
            Assert.AreEqual("Current", current.title);
            Assert.AreEqual("song.hash in ('abc')", current.command);
            Assert.AreEqual(40, current.max);
            Assert.AreEqual(timestamp.ToUnixtime(), current.date);
            Assert.AreEqual(12345, current.adddate);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == outOfScopePath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == normalDirectoryPath));
        });
    }

    [TestMethod]
    public void Sync_DoesNotPruneRowsUnlessAllowed()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                ScopeDirectories = [outputDirectory]
            });

            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
        });
    }

    [TestMethod]
    public void Sync_ReplacesCaseDriftedExistingPath()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string driftPath = Path.Combine(outputDirectory.ToUpperInvariant(), "0000.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = driftPath,
                title = "Old",
                type = 2,
                adddate = 12345
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Current"])
                    }
                ],
                ScopeDirectories = [outputDirectory],
                AllowPrune = true
            });

            Assert.AreEqual(1, result.UpsertedCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == driftPath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == currentPath));
            Assert.AreEqual(12345, songDb.Table<LR2SongDB.folder>().Single(row => row.path == currentPath).adddate);
        });
    }

    [TestMethod]
    public void Sync_ReportsUnsupportedAndMissingRowsWithoutWriting()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = @"D:\BMS\emoji_😀\0000.lr2folder",
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Unsupported"])
                    },
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = @"D:\BMS\#BeMusicSeeker\0000.lr2folder",
                        LastWriteTimeUtc = null,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Missing"])
                    }
                ]
            });

            Assert.AreEqual(2, result.ItemCount);
            Assert.AreEqual(0, result.GeneratedCount);
            Assert.AreEqual(0, result.UpsertedCount);
            Assert.AreEqual(1, result.SkippedUnsupportedPathCount);
            Assert.AreEqual(1, result.SkippedMissingMetadataCount);
            Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
        });
    }

    [TestMethod]
    public void Sync_PrunesRowsForInputsThatCannotGenerateRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string missingPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string unsupportedPath = Path.GetFullPath(@"D:\BMS\emoji_😀\0001.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = missingPath,
                title = "Missing",
                type = 2
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = unsupportedPath,
                title = "Unsupported",
                type = 2
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = missingPath,
                        LastWriteTimeUtc = null,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Missing"])
                    },
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = unsupportedPath,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Unsupported"])
                    }
                ],
                ScopeDirectories = [outputDirectory, Path.GetDirectoryName(unsupportedPath)],
                AllowPrune = true
            });

            Assert.AreEqual(0, result.GeneratedCount);
            Assert.AreEqual(2, result.DeletedCount);
            Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM folder;"));
        });
    }

    [TestMethod]
    public void Sync_DoesNotReplaceReservedNormalDirectoryRow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string path = Path.Combine(outputDirectory, "0000.lr2folder");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = path,
                title = "Normal",
                type = 1
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = path,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Custom"])
                    }
                ],
                ScopeDirectories = [outputDirectory],
                AllowPrune = true
            });

            Assert.AreEqual(0, result.GeneratedCount);
            Assert.AreEqual(0, result.UpsertedCount);
            Assert.AreEqual(0, result.DeletedCount);
            LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(candidate => candidate.path == path);
            Assert.AreEqual(1, row.type);
            Assert.AreEqual("Normal", row.title);
        });
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDbSyncServiceTests), Guid.NewGuid().ToString("N"));
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
}
