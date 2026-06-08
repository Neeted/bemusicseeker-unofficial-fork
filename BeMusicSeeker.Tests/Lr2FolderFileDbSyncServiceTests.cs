using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
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
    public void Sync_ReadsOnlyExactAndPruneScopeRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = currentPath,
                title = "Old",
                type = 2,
                date = timestamp.ToUnixtime()
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                title = "Stale",
                type = 2
            }, typeof(LR2SongDB.folder));
            for (int index = 0; index < 25; index++)
            {
                songDb.InsertOrReplace(new LR2SongDB.folder
                {
                    path = Path.GetFullPath($@"D:\Other\Table{index:00}\0000.lr2folder"),
                    title = "Out of scope",
                    type = 2
                }, typeof(LR2SongDB.folder));
            }

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = currentPath,
                        LastWriteTimeUtc = timestamp,
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [outputDirectory],
                ScopePaths = [currentPath],
                AllowPrune = true
            });

            Assert.AreEqual(2, result.ExistingReadCount);
            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(25, songDb.Table<LR2SongDB.folder>().ToList().Count(row => row.path.StartsWith(Path.GetFullPath(@"D:\Other"), StringComparison.OrdinalIgnoreCase)));
        });
    }

    [TestMethod]
    public void Sync_PruneDisabledReadsOnlyExactRows()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            string currentPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string stalePath = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = currentPath,
                title = "Old",
                type = 2,
                date = timestamp.ToUnixtime()
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
                        PreserveExistingRowOnly = true
                    }
                ],
                ScopeDirectories = [outputDirectory],
                ScopePaths = [currentPath],
                AllowPrune = false
            });

            Assert.AreEqual(1, result.ExistingReadCount);
            Assert.AreEqual(1, result.PreservedCount);
            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
        });
    }

    [TestMethod]
    public void EnsureFolderLookupIndexes_AddsFolderNocaseIndexForScopedSync()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string outputDirectory = Path.GetFullPath(@"D:\BMS\#BeMusicSeeker");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = Path.Combine(outputDirectory, "0000.lr2folder"),
                title = "Current",
                type = 2
            }, typeof(LR2SongDB.folder));

            BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);

            Assert.AreEqual(1L, songDb.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = ?;",
                BmsLibraryDbGateway.FolderPathNocaseIndexName));
            songDb.Execute("CREATE TEMP TABLE folder_exact_scope_for_test (path TEXT PRIMARY KEY COLLATE NOCASE);");
            songDb.Execute(
                "INSERT OR IGNORE INTO temp.folder_exact_scope_for_test (path) VALUES (?);",
                Path.Combine(outputDirectory, "0000.lr2folder"));
            AssertQueryUsesFolderNocaseIndex(
                songDb,
                "EXPLAIN QUERY PLAN SELECT f.*"
                + " FROM temp.folder_exact_scope_for_test AS p"
                + " CROSS JOIN folder AS f INDEXED BY " + BmsLibraryDbGateway.FolderPathNocaseIndexName
                + " WHERE f.path = p.path COLLATE NOCASE;");
            string prefix = outputDirectory + Path.DirectorySeparatorChar;
            AssertQueryUsesFolderNocaseIndex(
                songDb,
                "EXPLAIN QUERY PLAN SELECT * FROM folder INDEXED BY " + BmsLibraryDbGateway.FolderPathNocaseIndexName
                + " WHERE path COLLATE NOCASE >= ? AND path COLLATE NOCASE < ?;",
                prefix,
                prefix.Substring(0, prefix.Length - 1) + (char)(prefix[prefix.Length - 1] + 1));
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
            Assert.AreEqual(1, result.ExistingReadCount);
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
    public void Sync_UsesDirectoryMetadataForBuiltinCustomFolderParentRow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string customFolderRoot = Path.Combine(Path.GetDirectoryName(songDbPath), "LR2files", "CustomFolder");
            string randomDirectory = Path.Combine(customFolderRoot, "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            string filePath = Path.Combine(randomDirectory, "select.lr2folder");
            string databasePath = @"LR2files\CustomFolder\RANDOM\select.lr2folder";
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = databasePath,
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Random Select"])
                    }
                ],
                DirectoryRowScopeDirectories = [@"LR2files\CustomFolder"],
                DirectoryRowGenerationScopeDirectories = [@"LR2files\CustomFolder"],
                DirectoryMetadataResolver = directory => string.Equals(directory, randomDirectory, StringComparison.OrdinalIgnoreCase)
                    ? new Lr2FolderDirectoryMetadata(timestamp.AddMinutes(1), "Folder Info Random")
                    : null
            });

            Assert.AreEqual(2, result.UpsertedCount);
            LR2SongDB.folder parentRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == @"LR2files\CustomFolder\RANDOM\");
            Assert.AreEqual(2, parentRow.type);
            Assert.AreEqual("Folder Info Random", parentRow.title);
            Assert.AreEqual(timestamp.AddMinutes(1).ToUnixtime(), parentRow.date);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentRow.parent);
            LR2SongDB.folder childRow = songDb.Table<LR2SongDB.folder>().Single(row => row.path == databasePath);
            Assert.AreEqual("Random Select", childRow.title);
        });
    }

    [TestMethod]
    public void Sync_DoesNotFallbackToDirectoryInfoWhenDirectoryMetadataResolverMisses()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string customFolderRoot = Path.Combine(Path.GetDirectoryName(songDbPath), "LR2files", "CustomFolder");
            string randomDirectory = Path.Combine(customFolderRoot, "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            string filePath = Path.Combine(randomDirectory, "select.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            Lr2FolderFileDbSyncResult result = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items =
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = @"LR2files\CustomFolder\RANDOM\select.lr2folder",
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Random Select"])
                    }
                ],
                DirectoryRowScopeDirectories = [@"LR2files\CustomFolder"],
                DirectoryRowGenerationScopeDirectories = [@"LR2files\CustomFolder"],
                DirectoryMetadataResolver = _ => null
            });

            Assert.AreEqual(1, result.UpsertedCount);
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == @"LR2files\CustomFolder\RANDOM\"));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == @"LR2files\CustomFolder\RANDOM\select.lr2folder"));
        });
    }

    [TestMethod]
    public void CreateLr2FolderParentDirectoryMetadataSnapshot_UsesRequestFolderInfoSurfaceForBuiltinFolderInfo()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        try
        {
            string lr2Root = Path.Combine(tempDirectory, "LR2beta3");
            string randomDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder", "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            string folderInfoPath = Path.Combine(randomDirectory, "folderinfo.txt");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(folderInfoPath, "#TITLE Random Folder Info", Encoding.GetEncoding("shift_jis"));
            string filePath = Path.Combine(randomDirectory, "select.lr2folder");
            Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2FullGenerationSyncService.CreateLr2FolderParentDirectoryMetadataSnapshot(
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = @"LR2files\CustomFolder\RANDOM\select.lr2folder",
                        LastWriteTimeUtc = timestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Random"])
                    }
                ],
                new Lr2FullGenerationSyncRequest
                {
                    Lr2FolderPruneDirectories = [@"LR2files\CustomFolder"],
                    FolderInfoFilePaths = [folderInfoPath],
                    FolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, timestamp)
                    },
                    DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [randomDirectory] = new RootFileEnumerationEntry(randomDirectory, timestamp)
                    }
                });

            Assert.IsTrue(snapshot.TryGetMetadata(randomDirectory, out Lr2FolderDirectoryMetadata metadata));
            Assert.AreEqual("Random Folder Info", metadata.FolderInfoTitle);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CreateLr2FolderParentDirectoryMetadataSnapshot_UsesRequestDirectoryEntryTimestamp()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        try
        {
            string rootDirectory = Path.Combine(tempDirectory, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "Table");
            Directory.CreateDirectory(tableDirectory);
            string filePath = Path.Combine(tableDirectory, "select.lr2folder");
            DateTime enumeratedTimestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            DateTime liveTimestamp = enumeratedTimestamp.AddMinutes(10);
            Directory.SetLastWriteTimeUtc(tableDirectory, liveTimestamp);

            Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2FullGenerationSyncService.CreateLr2FolderParentDirectoryMetadataSnapshot(
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        LastWriteTimeUtc = enumeratedTimestamp,
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Table"])
                    }
                ],
                new Lr2FullGenerationSyncRequest
                {
                    RootDirectories = [rootDirectory],
                    DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [tableDirectory] = new RootFileEnumerationEntry(tableDirectory, enumeratedTimestamp)
                    }
                });

            Assert.IsTrue(snapshot.TryGetMetadata(tableDirectory, out Lr2FolderDirectoryMetadata metadata));
            Assert.AreEqual(enumeratedTimestamp, metadata.LastWriteTimeUtc);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CreateLr2FolderParentDirectoryMetadataSnapshot_DoesNotReadMissingDirectoryTimestamp()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderFileDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        try
        {
            string lr2Root = Path.Combine(tempDirectory, "LR2beta3");
            string randomDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder", "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            File.WriteAllText(Path.Combine(randomDirectory, "folderinfo.txt"), "#TITLE Random Folder Info", Encoding.GetEncoding("shift_jis"));
            string filePath = Path.Combine(randomDirectory, "select.lr2folder");

            Lr2FolderDirectoryMetadataSnapshot snapshot = Lr2FullGenerationSyncService.CreateLr2FolderParentDirectoryMetadataSnapshot(
                [
                    new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = @"LR2files\CustomFolder\RANDOM\select.lr2folder",
                        LastWriteTimeUtc = new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc),
                        Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Random"])
                    }
                ],
                new Lr2FullGenerationSyncRequest
                {
                    Lr2BuiltinFolderSourceDirectories = [Path.Combine(lr2Root, "LR2files", "CustomFolder")],
                    Lr2FolderPruneDirectories = [@"LR2files\CustomFolder"]
                });

            Assert.IsFalse(snapshot.TryGetMetadata(randomDirectory, out _));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void FullGenerationRun_UsesBuiltinFolderInfoForCategoryRow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string rootDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "BMS");
            Directory.CreateDirectory(rootDirectory);
            string lr2Root = Path.Combine(Path.GetDirectoryName(songDbPath), "LR2beta3");
            string builtinRoot = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            string randomDirectory = Path.Combine(builtinRoot, "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            string folderInfoPath = Path.Combine(randomDirectory, "folderinfo.txt");
            File.WriteAllText(folderInfoPath, "#TITLE Random Folder Info", Encoding.GetEncoding("shift_jis"));
            string lr2FolderPath = Path.Combine(randomDirectory, "select.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Random", Encoding.GetEncoding("shift_jis"));
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
            {
                Signature = "test",
                RunId = "run",
                RootDirectories = [rootDirectory],
                ChartPaths = [],
                NormalFolderDirectoryPaths = [rootDirectory],
                Lr2FolderFilePaths = [lr2FolderPath],
                Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp, null)
                },
                DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [randomDirectory] = RootFileEnumerationEntry.FromDirectoryInfo(randomDirectory)
                },
                FolderInfoFilePaths = [folderInfoPath],
                FolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, timestamp, null)
                },
                Lr2FolderDiscoveryDirectories = [builtinRoot],
                Lr2FolderPruneDirectories = [@"LR2files\CustomFolder"],
                Lr2RootPath = lr2Root,
                Lr2BuiltinFolderSourceDirectories = [builtinRoot],
                Lr2FolderFileDiscoveryComplete = true,
                SongRows = [],
                IsSourceCurrent = () => true,
                StartedAtUtc = timestamp
            });

            LR2SongDB.folder category = songDb.Table<LR2SongDB.folder>().ToList().Single(row => row.path == @"LR2files\CustomFolder\RANDOM\");
            Assert.AreEqual("Random Folder Info", category.title);
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
                DirectoryMetadataResolver = CreateDirectoryMetadataResolver(timestamp, tableDirectory),
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
                DirectoryMetadataResolver = CreateDirectoryMetadataResolver(timestamp, outputBase, tableDirectory),
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
                DirectoryMetadataResolver = CreateDirectoryMetadataResolver(timestamp, tableDirectory),
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

    [TestMethod]
    public void Sync_DoesNotFallbackToDirectoryInfoWhenDirectoryMetadataResolverMissing()
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

            Assert.AreEqual(1, result.UpsertedCount);
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory)));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == filePath));
        });
    }

    private static Func<string, Lr2FolderDirectoryMetadata> CreateDirectoryMetadataResolver(
        DateTime timestamp,
        params string[] directories)
    {
        var normalizedDirectories = new HashSet<string>(
            (directories ?? []).Select(Lr2FolderPath.NormalizeDirectoryPath).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        return path => normalizedDirectories.Contains(Lr2FolderPath.NormalizeDirectoryPath(path))
            ? new Lr2FolderDirectoryMetadata(timestamp)
            : null!;
    }

    private static void AssertQueryUsesFolderNocaseIndex(
        LR2SongDBExtended songDb,
        string explainSql,
        params object[] args)
    {
        List<QueryPlanRow> planRows = songDb.Query<QueryPlanRow>(explainSql, args);
        string detail = string.Join(" | ", planRows.Select(row => row.detail ?? string.Empty));
        StringAssert.Contains(detail, BmsLibraryDbGateway.FolderPathNocaseIndexName);
    }

    private sealed class QueryPlanRow
    {
        public string detail { get; set; } = string.Empty;
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
