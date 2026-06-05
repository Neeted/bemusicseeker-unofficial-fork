using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2NormalFolderDbSyncServiceTests
{
    [TestMethod]
    public void Sync_UpsertsNormalFolderRowsAndPrunesStaleNormalRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePath = FolderPath(@"D:\BMS\Removed");
            string customPath = FolderPath(@"D:\BMS\Custom");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = FolderPath(@"D:\BMS"),
                type = 1,
                date = 1,
                adddate = 12345
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = customPath,
                type = 2
            }, typeof(LR2SongDB.folder));

            DateTime rootTime = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
            DateTime songTime = rootTime.AddMinutes(2);
            var metadataTimes = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase)
            {
                [Normalize(@"D:\BMS")] = rootTime,
                [Normalize(@"D:\BMS\Pack")] = rootTime.AddMinutes(1),
                [Normalize(@"D:\BMS\Pack\Song")] = songTime
            };

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\Song\chart.bms"],
                FolderInfoFilePaths = [@"D:\BMS\Pack\Song\folderinfo.txt"],
                DirectoryLastWriteTimeUtcResolver = path => metadataTimes.TryGetValue(Normalize(path), out DateTime? timestamp) ? timestamp : null,
                FolderInfoLinesReader = _ => ["#TITLE Folder Info Song"],
                GeneratedAtUtc = rootTime.AddDays(1),
                AllowPrune = true
            });

            Assert.IsTrue(result.HasChanges);
            Assert.AreEqual(3, result.GeneratedCount);
            Assert.AreEqual(3, result.MetadataRequestedDirectoryCount);
            Assert.AreEqual(3, result.MetadataResolvedDirectoryCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(1, result.FolderInfoCandidateCount);
            Assert.AreEqual(1, result.FolderInfoAppliedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == customPath));

            LR2SongDB.folder root = songDb.Table<LR2SongDB.folder>().Single(row => row.path == FolderPath(@"D:\BMS"));
            LR2SongDB.folder song = songDb.Table<LR2SongDB.folder>().Single(row => row.path == FolderPath(@"D:\BMS\Pack\Song"));
            Assert.AreEqual(12345, root.adddate);
            Assert.AreEqual(rootTime.ToUnixtime(), root.date);
            Assert.AreEqual("Folder Info Song", song.title);
            Assert.AreEqual(songTime.ToUnixtime(), song.date);
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
    public void Sync_PrunesOnlyRequestedNormalFolderScope()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string removedPath = FolderPath(@"D:\BMS\Removed");
            string untouchedPath = FolderPath(@"D:\BMS\Other");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = removedPath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = untouchedPath,
                type = 1
            }, typeof(LR2SongDB.folder));

            DateTime timestamp = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                PruneScopeDirectories = [@"D:\BMS\Removed"],
                DirectoryLastWriteTimeUtcResolver = _ => timestamp,
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true
            });

            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == removedPath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == untouchedPath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == FolderPath(@"D:\BMS\Pack")));
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
    public void Sync_PrunesRequestedAncestorScope()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePackagePath = FolderPath(@"D:\BMS\RemovedPackage");
            string staleSongPath = FolderPath(@"D:\BMS\RemovedPackage\Song");
            string untouchedPath = FolderPath(@"D:\BMS\OtherPackage");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePackagePath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = staleSongPath,
                type = 1
            }, typeof(LR2SongDB.folder));
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = untouchedPath,
                type = 1
            }, typeof(LR2SongDB.folder));

            DateTime timestamp = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\CurrentPackage\chart.bms"],
                PruneScopeDirectories = [@"D:\BMS\RemovedPackage"],
                DirectoryLastWriteTimeUtcResolver = _ => timestamp,
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true
            });

            Assert.AreEqual(2, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePackagePath));
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == staleSongPath));
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == untouchedPath));
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
    public void Sync_IgnoresPruneScopeOutsideRootDirectories()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePath = FolderPath(@"D:\BMS\Removed");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));

            DateTime timestamp = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                PruneScopeDirectories = [@"E:\Other"],
                DirectoryLastWriteTimeUtcResolver = _ => timestamp,
                GeneratedAtUtc = timestamp.AddDays(1),
                AllowPrune = true
            });

            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
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
    public void Sync_SuppressesPruneWhenMetadataIsIncomplete()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePath = FolderPath(@"D:\BMS\Removed");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                DirectoryLastWriteTimeUtcResolver = path => string.Equals(Normalize(path), Normalize(@"D:\BMS"), StringComparison.OrdinalIgnoreCase)
                    ? new DateTime(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc)
                    : null,
                AllowPrune = true
            });

            Assert.AreEqual(1, result.SkippedMissingMetadataCount);
            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
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
    public void Sync_DoesNotUseCp932UnsupportedChartPathAsFolderSource()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\emoji_😀\chart.bms"],
                DirectoryLastWriteTimeUtcResolver = _ => new DateTime(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc)
            });

            Assert.AreEqual(1, result.GeneratedCount);
            Assert.AreEqual(1, result.SkippedIncompatibleChartPathCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count());
            Assert.AreEqual(FolderPath(@"D:\BMS"), songDb.Table<LR2SongDB.folder>().Single().path);
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
    public void Sync_PrunesStaleRowsEvenWhenSomeChartPathsAreIncompatible()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePath = FolderPath(@"D:\BMS\Removed");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths =
                [
                    @"D:\BMS\Pack\chart.bms",
                    @"D:\BMS\emoji_😀\chart.bms"
                ],
                DirectoryLastWriteTimeUtcResolver = _ => new DateTime(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc),
                AllowPrune = true
            });

            Assert.AreEqual(1, result.SkippedIncompatibleChartPathCount);
            Assert.AreEqual(1, result.DeletedCount);
            Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == FolderPath(@"D:\BMS\Pack")));
            Assert.IsFalse(songDb.Table<LR2SongDB.folder>().Any(row => row.path == FolderPath(@"D:\BMS\emoji_😀")));
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
    public void Sync_DoesNotPruneRowsUnlessCallerMarksScanComplete()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2NormalFolderDbSyncServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        try
        {
            using var songDb = new LR2SongDBExtended(songDbPath);
            songDb.CreateTable<LR2SongDB.folder>();
            string stalePath = FolderPath(@"D:\BMS\Removed");
            songDb.InsertOrReplace(new LR2SongDB.folder
            {
                path = stalePath,
                type = 1
            }, typeof(LR2SongDB.folder));

            Lr2NormalFolderDbSyncResult result = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = [@"D:\BMS"],
                ChartPaths = [@"D:\BMS\Pack\chart.bms"],
                DirectoryLastWriteTimeUtcResolver = _ => new DateTime(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc)
            });

            Assert.AreEqual(0, result.DeletedCount);
            Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == FolderPath(@"D:\BMS\Pack")));
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
    public void CreateDirectoryMetadataTargets_IncludesRootsAncestorsAndChartDirectories()
    {
        IReadOnlyCollection<string> targets = Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(
            [@"D:\BMS"],
            [@"D:\BMS\Pack\Song\chart.bms", @"E:\Other\chart.bms"]);

        CollectionAssert.AreEqual(
            new[]
            {
                Normalize(@"D:\BMS"),
                Normalize(@"D:\BMS\Pack"),
                Normalize(@"D:\BMS\Pack\Song")
            },
            targets.ToArray());
    }

    private static string Normalize(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath);
        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return root.TrimEnd(Path.AltDirectorySeparatorChar);
        }
        return trimmed;
    }

    private static string FolderPath(string path)
    {
        string normalized = Normalize(path);
        return normalized.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || normalized.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? normalized
                : normalized + Path.DirectorySeparatorChar;
    }
}
