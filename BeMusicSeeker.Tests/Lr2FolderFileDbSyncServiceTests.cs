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
    public void Sync_PreservesUnchangedRowsWithoutDefinitionParse()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = currentPath,
                title = "Current",
                type = 2,
                date = timestamp.ToUnixtime(),
                adddate = 12345
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = timestamp,
                        Definition = null,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [outputDirectory],
                ScopePaths = [currentPath],
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true
            });

            Assert.AreEqual(1, result.ItemCount);
            Assert.AreEqual(0, result.GeneratedCount);
            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(0, result.UpsertedCount);
            Assert.AreEqual(1, result.DeletedCount);
            LR2SongDB.folder current = songDb.Table<LR2SongDB.folder>().Single(row => row.path == currentPath);
            Assert.AreEqual("Current", current.title);
            Assert.AreEqual(12345, current.adddate);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
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

    [DataTestMethod]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(6)]
    public void Sync_PreservesExplicitSupportedSpecialFolderType(int folderType)
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string path = Path.GetFullPath(@"D:\LR2beta3\LR2files\CustomFolder\special.lr2folder");

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = path,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        FolderType = folderType,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Special"])
                    }
                ]
            });

            Assert.AreEqual(1, result.GeneratedCount);
            Assert.AreEqual(1, result.UpsertedCount);
            LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(candidate => candidate.path == path);
            Assert.AreEqual(folderType, row.type);
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

    [TestMethod]
    public void Sync_MatchesExistingKnownRelativeLr2FolderRow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string filePath = Path.GetFullPath(@"D:\LR2beta3\LR2files\CustomFolder\favorite.lr2folder");
            string databasePath = @"LR2files\CustomFolder\favorite.lr2folder";
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = databasePath,
                title = "Old",
                type = 2,
                parent = Lr2SongFolderParentNormalizer.RootParentHash,
                adddate = 12345
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = databasePath,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        ParentHash = Lr2SongFolderParentNormalizer.RootParentHash,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Favorite"])
                    }
                ]
            });

            Assert.AreEqual(1, result.GeneratedCount);
            Assert.AreEqual(1, result.UpsertedCount);
            LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(candidate => candidate.path == databasePath);
            Assert.AreEqual("Favorite", row.title);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, row.parent);
        });
    }

    [TestMethod]
    public void Sync_MigratesExistingAbsoluteBuiltinRowToRelativePath()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string filePath = Path.GetFullPath(@"D:\LR2beta3\LR2files\CustomFolder\favorite.lr2folder");
            string databasePath = @"LR2files\CustomFolder\favorite.lr2folder";
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = filePath,
                title = "Old Absolute",
                type = 2,
                parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(filePath)),
                adddate = 12345
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = databasePath,
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        ParentHash = Lr2SongFolderParentNormalizer.RootParentHash,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Favorite"])
                    }
                ]
            });

            Assert.AreEqual(1, result.GeneratedCount);
            Assert.AreEqual(1, result.UpsertedCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(candidate => candidate.path == filePath));
            LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(candidate => candidate.path == databasePath);
            Assert.AreEqual("Favorite", row.title);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, row.parent);
        });
    }

    [TestMethod]
    public void Sync_GeneratesRootOutputDirectoryRowWithoutPruningSiblingTables()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string baseDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "ROOT");
            string tableDirectory = Path.Combine(baseDirectory, "Table");
            string siblingDirectory = Path.Combine(baseDirectory, "Sibling");
            Directory.CreateDirectory(tableDirectory);
            Directory.CreateDirectory(siblingDirectory);
            string filePath = Path.Combine(tableDirectory, "0000.lr2folder");
            string siblingFolderPath = Lr2FolderPath.ToFolderPath(siblingDirectory);
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = siblingFolderPath,
                title = "Sibling",
                type = 1,
                parent = Lr2SongFolderParentNormalizer.RootParentHash,
                date = timestamp.ToUnixtime()
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Level Folder"])
                    }
                ],
                ScopeDirectories = [tableDirectory],
                DirectoryRowScopeDirectories = [tableDirectory],
                DirectoryRowGenerationScopeDirectories = [baseDirectory],
                AllowPrune = true
            });

            Assert.AreEqual(2, result.UpsertedCount);
            Assert.AreEqual(0, result.DeletedCount);
            LR2SongDB.folder parentRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, parentRow.type);
            Assert.AreEqual("Table", parentRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentRow.parent);
            LR2SongDB.folder childRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == filePath);
            Assert.AreEqual(2, childRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), childRow.parent);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == siblingFolderPath));
        });
    }

    [TestMethod]
    public void Sync_GeneratesNormalOutputBaseAndTableDirectoryRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string bmsRoot = Path.Combine(Path.GetDirectoryName(songDbPath), "BMS");
            string outputBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string tableDirectory = Path.Combine(outputBase, "Table");
            Directory.CreateDirectory(tableDirectory);
            string filePath = Path.Combine(tableDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Level Folder"])
                    }
                ],
                ScopeDirectories = [tableDirectory],
                DirectoryRowScopeDirectories = [tableDirectory],
                DirectoryRowGenerationScopeDirectories = [bmsRoot],
                AllowPrune = true
            });

            Assert.AreEqual(3, result.UpsertedCount);
            LR2SongDB.folder outputBaseRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(outputBase));
            Assert.AreEqual(1, outputBaseRow.type);
            Assert.AreEqual("#BeMusicSeeker", outputBaseRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputBaseRow.parent);
            LR2SongDB.folder tableRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, tableRow.type);
            Assert.AreEqual("Table", tableRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputBase), tableRow.parent);
            LR2SongDB.folder childRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == filePath);
            Assert.AreEqual(2, childRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), childRow.parent);
        });
    }

    [TestMethod]
    public void Sync_PreserveExistingChildStillCreatesMissingParentDirectoryRow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string baseDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "ROOT");
            string tableDirectory = Path.Combine(baseDirectory, "Table");
            Directory.CreateDirectory(tableDirectory);
            string filePath = Path.Combine(tableDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = filePath,
                title = "Preserved",
                type = 2,
                parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory),
                date = timestamp.ToUnixtime()
            }, typeof(LR2SongDB.folder));

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        LastWriteTimeUtc = timestamp,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [tableDirectory],
                ScopePaths = [filePath],
                DirectoryRowScopeDirectories = [tableDirectory],
                DirectoryRowGenerationScopeDirectories = [baseDirectory],
                AllowPrune = true
            });

            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(1, result.UpsertedCount);
            LR2SongDB.folder parentRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, parentRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentRow.parent);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == filePath));
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
