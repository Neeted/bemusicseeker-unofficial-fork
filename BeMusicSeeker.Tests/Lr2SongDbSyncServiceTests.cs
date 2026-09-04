using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncServiceTests
{

    [TestMethod]
    public void SyncService_FolderReconciliationPublishesPreCommitMonotonicProgress()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "FolderProgressRoot");
        string firstDirectory = Path.Combine(rootDirectory, "First");
        string secondDirectory = Path.Combine(rootDirectory, "Second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        DateTime rootTime = new(2026, 6, 8, 6, 0, 0, DateTimeKind.Utc);
        DateTime firstTime = rootTime.AddMinutes(1);
        DateTime secondTime = rootTime.AddMinutes(2);
        string stalePath = ToFolderPath(Path.Combine(scope.DirectoryPath, "stale"));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = stalePath,
            title = "Durable before reconciliation",
            date = 1
        }, typeof(LR2SongDB.folder));

        var progressEvents = new List<Lr2SongDbSyncProgress>();
        bool observedPreCommitState = false;
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "folder-reconciliation-progress",
            RunId = "folder-reconciliation-progress",
            RootDirectories = [rootDirectory],
            NormalFolderDirectoryPaths = [rootDirectory, firstDirectory, secondDirectory],
            DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, rootTime),
                [firstDirectory] = new RootFileEnumerationEntry(firstDirectory, firstTime),
                [secondDirectory] = new RootFileEnumerationEntry(secondDirectory, secondTime)
            },
            StartedAtUtc = rootTime.AddHours(1),
            ProgressReporter = progress =>
            {
                if (progress == null)
                {
                    return;
                }

                progressEvents.Add(progress);
                if (progress.ProcessedCursor == 0
                    && progress.StageTotalCount > 0
                    && progress.StageProcessedCount > 0
                    && songDb.Find<LR2SongDB.folder>(stalePath)?.title == "Durable before reconciliation"
                    && songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName)?.processed_cursor == 0)
                {
                    observedPreCommitState = true;
                }
            }
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsTrue(observedPreCommitState);
        List<Lr2SongDbSyncProgress> reconciliationProgress = [.. progressEvents
            .Where(progress => progress.ProcessedCursor == 0
                && progress.StageTotalCount > 0
                && progress.StageProcessedCount > 0)];
        Assert.IsTrue(reconciliationProgress.Count > 0);
        int stageTotal = reconciliationProgress[0].StageTotalCount;
        Assert.IsTrue(stageTotal > 0);
        Assert.IsTrue(reconciliationProgress.Any(progress =>
            progress.StageProcessedCount > 0
            && progress.StageProcessedCount < stageTotal));
        for (int index = 0; index < reconciliationProgress.Count; index++)
        {
            Lr2SongDbSyncProgress progress = reconciliationProgress[index];
            Assert.AreEqual(stageTotal, progress.StageTotalCount);
            Assert.IsTrue(progress.StageProcessedCount > 0);
            Assert.IsTrue(progress.StageProcessedCount <= stageTotal);
            if (index > 0)
            {
                Assert.IsTrue(progress.StageProcessedCount >= reconciliationProgress[index - 1].StageProcessedCount);
            }
        }

        Assert.AreEqual(result.FolderTableReconciliationResult.GeneratedCount, stageTotal);
        Assert.AreEqual(result.FolderTableReconciliationResult.GeneratedCount, songDb.Table<LR2SongDB.folder>().Count());
        LR2SongDBExtended.lr2_song_db_sync_status completed = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", completed.status);
        Assert.AreEqual(completed.total_count, completed.processed_cursor);
        Assert.IsTrue(completed.total_count > 0);
    }

    [TestMethod]
    public void SyncService_FolderReconciliationProgressReporterFailureDoesNotChangeResult()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "FolderProgressObserverFailure");
        string firstDirectory = Path.Combine(rootDirectory, "First");
        string secondDirectory = Path.Combine(rootDirectory, "Second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        DateTime rootTime = new(2026, 6, 8, 7, 0, 0, DateTimeKind.Utc);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "folder-reconciliation-observer-failure",
            RunId = "folder-reconciliation-observer-failure",
            RootDirectories = [rootDirectory],
            NormalFolderDirectoryPaths = [rootDirectory, firstDirectory, secondDirectory],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory, firstDirectory, secondDirectory),
            StartedAtUtc = rootTime,
            ProgressReporter = _ => throw new InvalidOperationException("progress observer failure")
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsNotNull(result.FolderTableReconciliationResult);
        Assert.AreEqual(result.FolderTableReconciliationResult.GeneratedCount, songDb.Table<LR2SongDB.folder>().Count());
        LR2SongDBExtended.lr2_song_db_sync_status completed = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", completed.status);
        Assert.AreEqual(completed.total_count, completed.processed_cursor);
    }

    [TestMethod]
    public void SyncService_EmptyFolderProjectionDoesNotPublishPositiveStageTotal()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        var progressEvents = new List<Lr2SongDbSyncProgress>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "empty-folder-projection",
            RunId = "empty-folder-projection",
            StartedAtUtc = new DateTime(2026, 6, 8, 8, 0, 0, DateTimeKind.Utc),
            ProgressReporter = progress =>
            {
                if (progress != null)
                {
                    progressEvents.Add(progress);
                }
            }
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsFalse(progressEvents.Any(progress => progress.StageTotalCount > 0));
        Assert.AreEqual(0, result.FolderTableReconciliationResult.GeneratedCount);
    }

    [TestMethod]
    public void SyncService_FullStageDoesNotDeleteExistingSongMembership()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string stalePath = Path.Combine(scope.DirectoryPath, "stale.bms");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        var existing = new TestableBmsFile
        {
            path = stalePath,
            tag = "user-owned"
        }.WithHashAndFavorite("11111111111111111111111111111111", 7);
        songDb.InsertOrReplace(existing, typeof(LR2SongDB.song));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "full-membership-delete",
            RunId = "full-membership-delete",
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsNotNull(songDb.Find<LR2SongDB.song>(stalePath));
    }

    [TestMethod]
    public void SyncService_FullStageDoesNotInsertSongMembership()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string chartPath = Path.Combine(scope.DirectoryPath, "new.bms");
        var file = new TestableBmsFile
        {
            path = chartPath
        };
        file.SetTitleForTest("New chart");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "full-membership-insert",
            RunId = "full-membership-insert",
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsNull(songDb.Find<LR2SongDB.song>(chartPath));
    }

    [TestMethod]
    public void SyncService_FullStageReparsesSameMtimeLr2FolderContent()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "same-mtime.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE Current title\r\n", Encoding.ASCII);
        DateTime mtime = new(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(lr2FolderPath, mtime);
        Directory.SetLastWriteTimeUtc(rootDirectory, mtime);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            type = 2,
            title = "Stale title",
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(mtime),
            parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory)
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "full-lr2folder-reparse",
            RunId = "full-lr2folder-reparse",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, mtime)
            },
            DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, mtime)
            },
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual("Current title", songDb.ExecuteScalar<string>(
            "SELECT title FROM folder WHERE path = ?;",
            lr2FolderPath));
    }

    [TestMethod]
    public void SyncService_FullStagePreservesEpochFolderDate()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "EpochRoot");
        Directory.CreateDirectory(rootDirectory);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        DateTime epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "full-epoch-folder-date",
            RunId = "full-epoch-folder-date",
            RootDirectories = [rootDirectory],
            DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, epoch)
            },
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, songDb.ExecuteScalar<int>(
            "SELECT date FROM folder WHERE path = ?;",
            ToFolderPath(rootDirectory)));
    }

    [TestMethod]
    public void SyncService_PreservesUserColumnsWhenRunningOnCopiedSongDb()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE Copied User Columns\r\n#ARTIST Parsed Artist\r\n#00111:01\r\n", Encoding.ASCII);
        ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, chartSnapshot);

        using (var sourceDb = new LR2SongDBExtended(scope.SongDbPath))
        {
            sourceDb.CreateTable<LR2SongDB.song>();
            var existing = new TestableBmsFile
            {
                path = chartPath,
                adddate = 123456,
                tag = "copied-user-tag"
            }.WithHashAndFavorite("cccccccccccccccccccccccccccccccc", 5);
            existing.SetTitleForTest("Old Copied Title");
            sourceDb.InsertOrReplace(existing, typeof(LR2SongDB.song));
        }

        string copiedDirectory = Path.Combine(scope.DirectoryPath, "CopiedUserColumns");
        Directory.CreateDirectory(copiedDirectory);
        string copiedSongDbPath = Path.Combine(copiedDirectory, "song.db");
        File.Copy(scope.SongDbPath, copiedSongDbPath, overwrite: true);
        using var copiedDb = new LR2SongDBExtended(copiedSongDbPath);
        copiedDb.CreateTable<LR2SongDB.folder>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(copiedDb, new Lr2SongDbSyncRequest
        {
            Signature = "copied-user-columns",
            RunId = "copied-user-columns-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(copiedDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual("Copied User Columns", copiedDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual(chartSnapshot.Md5, copiedDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual(5, copiedDb.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual(123456, copiedDb.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual("copied-user-tag", copiedDb.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void SyncService_FallsBackToSongCopyWhenChartSnapshotCannotBeRead()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Missing");
        Directory.CreateDirectory(songDirectory);
        string missingChartPath = Path.Combine(songDirectory, "missing.bms");
        var file = new TestableBmsFile
        {
            path = missingChartPath
        };
        file.SetTitleForTest("Existing Title");
        file.SetArtistForTest("Existing Artist");
        file.SetHash("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        file.ApplySha256("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        file.SetTextGroupFlag(1);
        file.folder = "stale-folder";
        file.parent = "stale-parent";
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "missing-song",
            RunId = "missing-song",
            SongRows = [file],
            TextFileDirectories = [],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowParseFailureCount);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(songDirectory), songDb.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual("Existing Title", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual("Existing Artist", songDb.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual("stale-folder", file.folder);
        Assert.AreEqual("stale-parent", file.parent);
    }

    [TestMethod]
    public void SyncService_PreservesNegativeSongDate()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "negative-date.bms");
        File.WriteAllText(chartPath, "#TITLE Negative Date\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
        DateTime preEpoch = new(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(chartPath, preEpoch);
        TestableBmsFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "negative-song-date",
            RunId = "negative-song-date",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(-1, songDb.ExecuteScalar<int>("SELECT date FROM song WHERE path = ?;", chartPath));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_PreservesNullSongDate()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "null-date.bms");
        File.WriteAllText(chartPath, "#TITLE Null Date\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        file.date = null;
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "null-song-date",
            RunId = "null-song-date",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            ChartFileBufferReader = _ => null,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsNull(songDb.ExecuteScalar<int?>("SELECT date FROM song WHERE path = ?;", chartPath));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_DeletesLegacyNormalFolderRowWhenNotExpected()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string legacyDirectory = Path.Combine(rootDirectory, "Legacy");
        Directory.CreateDirectory(legacyDirectory);
        DateTime legacyTime = new(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(legacyDirectory, legacyTime);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(legacyDirectory),
            type = 0,
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(legacyTime.AddMinutes(-1))
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "legacy-folder-date-stale",
            RunId = "legacy-folder-date-stale-run",
            RootDirectories = [rootDirectory],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        string legacyFolderPath = ToFolderPath(legacyDirectory);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == legacyFolderPath));
    }

    [TestMethod]
    public void SyncService_LeavesIncompleteWhenSourceBecomesStaleBeforeCompletion()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "Root");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE source stale\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        string stalePath = Path.Combine(scope.DirectoryPath, "Stale", "stale.bms");
        songDb.InsertOrReplace(new TestableBmsFile
        {
            path = stalePath,
            date = 1
        }.WithHashAndFavorite("dddddddddddddddddddddddddddddddd", favoriteValue: null), typeof(LR2SongDB.song));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "source-stale",
            RunId = "source-stale",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            IsSourceCurrent = () => false
        });

        Assert.AreEqual(Lr2SongDbSyncService.SourceStaleStage, result.FinalStage);
        Assert.AreEqual(Lr2SongDbSyncService.SourceStaleReason, result.IncompleteReason);
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        Assert.AreEqual(Lr2SongDbSyncService.SourceStaleStage, row.stage);
        StringAssert.Contains(row.last_error, Lr2SongDbSyncService.SourceStaleReason);
        Assert.IsNotNull(songDb.Find<LR2SongDB.song>(stalePath));
    }

    [TestMethod]
    public void SyncService_DoesNotRunLr2FolderStageWhenSourceIsAlreadyStale()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "Root");
        string folderPath = Path.Combine(rootDirectory, "External.lr2folder");
        Directory.CreateDirectory(rootDirectory);
        File.WriteAllText(folderPath, "#TITLE External", Encoding.GetEncoding("shift_jis"));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "source-stale-lr2folder",
            RunId = "source-stale-lr2folder",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFilePaths = [folderPath],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>
            {
                [folderPath] = new RootFileEnumerationEntry(folderPath, File.GetLastWriteTimeUtc(folderPath))
            },
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            IsSourceCurrent = () => false
        });

        Assert.AreEqual(Lr2SongDbSyncService.SourceStaleStage, result.FinalStage);
        Assert.IsNull(result.Lr2FolderFileSyncResult);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(row => row.path == folderPath));
    }

    [TestMethod]
    public void SyncService_RollsBackFailedSongRowChunkAndRetriesFromChunkStart()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "RollbackSongRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string firstPath = Path.Combine(songDirectory, "first.bms");
        string secondPath = Path.Combine(songDirectory, "second.bms");
        File.WriteAllText(firstPath, "#TITLE first rollback\r\n", Encoding.ASCII);
        File.WriteAllText(secondPath, "#TITLE second rollback\r\n", Encoding.ASCII);
        TestableBmsFile firstFile = CreateSyncTestFile(firstPath, ChartFileContentReader.ReadSnapshot(firstPath));
        TestableBmsFile secondFile = CreateSyncTestFile(secondPath, ChartFileContentReader.ReadSnapshot(secondPath));
        firstFile.SetTitleForTest("stale first");
        secondFile.SetTitleForTest("stale second");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(firstFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(secondFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
        const string signature = "rollback-song-chunk";
        songDb.Execute(
            "CREATE TRIGGER fail_second_song_update BEFORE UPDATE ON song"
            + " WHEN NEW.path = '" + EscapeSqlLiteral(secondPath) + "'"
            + " BEGIN SELECT RAISE(ABORT, 'fail_second_song_update'); END;");

        Assert.ThrowsException<SQLite.SQLiteException>(() => Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "rollback-fail-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [firstPath, secondPath],
            SongRows = [firstFile, secondFile],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        }));
        Assert.AreEqual(2, songDb.Table<LR2SongDB.song>().Count());
        Assert.AreEqual("stale first", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", firstPath));
        Assert.AreEqual("stale second", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", secondPath));
        LR2SongDBExtended.lr2_song_db_sync_status failed = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Failed", failed.status);
        Assert.AreEqual("song_rows", failed.stage);
        Assert.AreEqual(2, failed.processed_cursor);
        Assert.AreEqual(4, failed.total_count);

        songDb.Execute("DROP TRIGGER fail_second_song_update;");
        Lr2SongDbSyncResult retry = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "rollback-retry-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [firstPath, secondPath],
            SongRows = [firstFile, secondFile],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, retry.FinalStage);
        Assert.AreEqual(2, retry.SongRowProcessedCount);
        Assert.AreEqual("first rollback", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", firstPath));
        Assert.AreEqual("second rollback", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", secondPath));
        LR2SongDBExtended.lr2_song_db_sync_status completed = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", completed.status);
        Assert.AreEqual(4, completed.processed_cursor);
    }

    [TestMethod]
    public void SyncService_ParsesSongRowsWithDetectedUtf8Encoding()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Utf8");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "utf8.bms");
        File.WriteAllText(chartPath, "#TITLE 解析タイトル\r\n#ARTIST 解析アーティスト\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        var file = new TestableBmsFile
        {
            path = chartPath
        };
        file.SetHash(snapshot.Md5);
        file.ApplySha256(snapshot.Sha256);
        file.SetTitleForTest("Stale Title");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "utf8-song",
            RunId = "utf8-song",
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(0, result.SongRowParseFailureCount);
        Assert.AreEqual("解析タイトル", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual("解析アーティスト", songDb.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual("Stale Title", file.title);
    }

    [TestMethod]
    public void SyncService_UsesResolverFactsAndReadsEachSongRowSnapshotOnce()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfo");
        Directory.CreateDirectory(songDirectory);
        string currentPath = Path.Combine(songDirectory, "current.bms");
        string stalePath = Path.Combine(songDirectory, "stale.bms");
        string mismatchPath = Path.Combine(songDirectory, "mismatch.bms");
        File.WriteAllText(currentPath, "#PLAYER 1\r\n#TITLE current\r\n#BPM 150\r\n#PLAYLEVEL 7\r\n#RANK 3\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        File.WriteAllText(stalePath, "#PLAYER 1\r\n#TITLE stale\r\n#BPM 150\r\n#PLAYLEVEL 9\r\n#RANK 3\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        File.WriteAllText(mismatchPath, "#PLAYER 1\r\n#TITLE mismatch\r\n#BPM 150\r\n#PLAYLEVEL 11\r\n#RANK 3\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        ChartFileSnapshot currentSnapshot = ChartFileContentReader.ReadSnapshot(currentPath);
        ChartFileSnapshot staleSnapshot = ChartFileContentReader.ReadSnapshot(stalePath);
        ChartFileSnapshot mismatchSnapshot = ChartFileContentReader.ReadSnapshot(mismatchPath);
        TestableBmsFile currentFile = CreateSyncTestFile(currentPath, currentSnapshot);
        TestableBmsFile staleFile = CreateSyncTestFile(stalePath, staleSnapshot);
        TestableBmsFile mismatchFile = CreateSyncTestFile(mismatchPath, mismatchSnapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        LR2SongDBExtended.chart_info currentInfo = CreateChartInfo(currentSnapshot.Sha256, currentSnapshot.Md5, level: 77);
        LR2SongDBExtended.chart_info staleInfo = CreateChartInfo(staleSnapshot.Sha256, staleSnapshot.Md5, level: 99, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1);
        LR2SongDBExtended.chart_info mismatchInfo = CreateChartInfo(mismatchSnapshot.Sha256, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", level: 99);
        songDb.InsertOrReplace(currentInfo, typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(staleInfo, typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(mismatchInfo, typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(currentFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(staleFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(mismatchFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
        var serviceReadCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        object serviceReadCountsSync = new();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "chart-info-current",
            RunId = "chart-info-current",
            SongRows = [currentFile, staleFile, mismatchFile],
            ChartInfoResolver = CreateChartInfoResolver([currentInfo, staleInfo, mismatchInfo]),
            ChartFileBufferReader = path =>
            {
                lock (serviceReadCountsSync)
                {
                    serviceReadCounts[path] = serviceReadCounts.TryGetValue(path, out int count) ? count + 1 : 1;
                }
                return ChartFileContentReader.ReadBuffer(path);
            },
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(3, result.SongRowProcessedCount);
        Assert.AreEqual(3, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COALESCE(karinotes, -1) FROM song WHERE path = ?;", currentPath));
        Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COALESCE(karinotes, -1) FROM song WHERE path = ?;", stalePath));
        Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COALESCE(karinotes, -1) FROM song WHERE path = ?;", mismatchPath));
        Assert.AreEqual(3L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        Assert.AreEqual(77, songDb.ExecuteScalar<int>("SELECT level FROM chart_info WHERE sha256 = ?;", currentSnapshot.Sha256));
        Assert.AreEqual(9, songDb.ExecuteScalar<int>("SELECT level FROM chart_info WHERE sha256 = ?;", staleSnapshot.Sha256));
        Assert.AreEqual(11, songDb.ExecuteScalar<int>("SELECT level FROM chart_info WHERE sha256 = ?;", mismatchSnapshot.Sha256));
        Assert.AreEqual(1, serviceReadCounts[currentPath]);
        Assert.AreEqual(1, serviceReadCounts[stalePath]);
        Assert.AreEqual(1, serviceReadCounts[mismatchPath]);
    }

    [TestMethod]
    public void SyncService_SkipsChartInfoParseWhenCurrentParseFailureIsKnown()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfoFailureSkip");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "failure-skip.bms");
        File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE failure skip\r\n#BPM 150\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        bool chartInfoCallbackCalled = false;
        bool parseFailureCallbackCalled = false;

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "chart-info-failure-skip",
            RunId = "chart-info-failure-skip",
            SongRows = [file],
            CurrentChartInfoParseFailureMd5s = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                snapshot.Md5
            },
            ChartInfoRowsCommitted = _ => chartInfoCallbackCalled = true,
            ChartInfoParseFailuresCommitted = (_, _) => parseFailureCallbackCalled = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(0, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        Assert.IsFalse(chartInfoCallbackCalled);
        Assert.IsFalse(parseFailureCallbackCalled);
    }

    [TestMethod]
    public void SyncService_UsesRequestChartInfoResolverWithoutChartInfoDbLookup()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfoResolver");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "resolver.bms");
        File.WriteAllText(chartPath, "#TITLE resolver\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "chart-info-resolver",
            RunId = "chart-info-resolver",
            SongRows = [file],
            ChartInfoResolver = CreateChartInfoResolver(
            [
                CreateChartInfo(snapshot.Sha256, snapshot.Md5, level: 13)
            ]),
            CurrentChartInfoParseFailureMd5s = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                snapshot.Md5
            },
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(13, songDb.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void SyncService_RequestChartInfoResolverUsesStableMd5FallbackWhenSha256DoesNotMatch()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Md5Fallback");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE md5 fallback\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(CreateChartInfo(new string('2', 64), snapshot.Md5, level: 22), typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(CreateChartInfo(new string('1', 64), snapshot.Md5, level: 11), typeof(LR2SongDBExtended.chart_info));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "chart-info-md5",
            RunId = "chart-info-md5",
            SongRows = [file],
            ChartInfoResolver = CreateChartInfoResolver(
            [
                CreateChartInfo(new string('2', 64), snapshot.Md5, level: 22),
                CreateChartInfo(new string('1', 64), snapshot.Md5, level: 11)
            ]),
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(11, songDb.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void SyncService_RebuildsChartInfoWhenRequestResolverReturnsStaleRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "StaleResolver");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "stale-resolver.bms");
        File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE stale resolver\r\n#BPM 150\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "chart-info-stale-resolver",
            RunId = "chart-info-stale-resolver",
            SongRows = [file],
            ChartInfoResolver = _ => CreateChartInfo(snapshot.Sha256, snapshot.Md5, level: 99, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1),
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowChartInfoAppliedCount);
        LR2SongDBExtended.chart_info chartInfo = songDb.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", snapshot.Sha256).Single();
        Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, chartInfo.parser_version);
        Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COALESCE(karinotes, -1) FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void SyncService_UnexpectedCancellationMarksFailedStatus()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsException<OperationCanceledException>(() => Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "sig_cancel",
            RunId = "run_cancel",
            CancellationToken = cancellation.Token
        }));

        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.IsNotNull(row);
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Failed.ToString(), row.status);
        Assert.AreEqual("final_validation", row.stage);
        Assert.AreEqual(0, row.processed_cursor);
        Assert.AreEqual(0, row.total_count);
    }

    [TestMethod]
    public void SyncService_ShutdownCancellationAtFolderCommitRollsBackWholeTable()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "FolderCommitBarrier");
        Directory.CreateDirectory(rootDirectory);
        string rootFolderPath = ToFolderPath(rootDirectory);
        DateTime directoryTime = new(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(rootDirectory, directoryTime);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = rootFolderPath,
            type = 1,
            title = "Durable folder before shutdown",
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(directoryTime.AddMinutes(-1))
        }, typeof(LR2SongDB.folder));

        using var cancellation = new CancellationTokenSource();
        bool shutdownRequested = false;
        bool transactionBarrierObserved = false;
        Assert.ThrowsException<OperationCanceledException>(() => Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "folder-commit-shutdown",
            RunId = "folder-commit-shutdown",
            RootDirectories = [rootDirectory],
            NormalFolderDirectoryPaths = [rootDirectory],
            DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, directoryTime)
            },
            CancellationToken = cancellation.Token,
            IsShutdownRequested = () =>
            {
                string titleInsideTransaction = songDb.ExecuteScalar<string>(
                    "SELECT title FROM folder WHERE path = ?;",
                    rootFolderPath);
                if (!shutdownRequested
                    && string.Equals(titleInsideTransaction, "FolderCommitBarrier", StringComparison.Ordinal))
                {
                    transactionBarrierObserved = true;
                    shutdownRequested = true;
                    cancellation.Cancel();
                }
                return shutdownRequested;
            },
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        }));

        Assert.IsTrue(shutdownRequested);
        Assert.IsTrue(transactionBarrierObserved);
        Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count());
        Assert.AreEqual("Durable folder before shutdown", songDb.ExecuteScalar<string>(
            "SELECT title FROM folder WHERE path = ?;", rootFolderPath));
    }

    [TestMethod]
    public void SyncService_ShutdownCancellationAtSongCommitRollsBackWholeChunk()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "SongCommitBarrier");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Current song title\r\n#ARTIST Current artist\r\n#BPM 120\r\n#WAV01 sound.wav\r\n#00111:01\r\n", Encoding.ASCII);
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        file.SetTitleForTest("Durable song before shutdown");
        file.SetArtistForTest("Stale artist before shutdown");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

        using var cancellation = new CancellationTokenSource();
        bool shutdownRequested = false;
        int chartInfoWriterCallCount = 0;
        Assert.ThrowsException<OperationCanceledException>(() => Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "song-commit-shutdown",
            RunId = "song-commit-shutdown",
            SongRows = [file],
            CancellationToken = cancellation.Token,
            IsShutdownRequested = () => shutdownRequested,
            ChartInfoChunkWriter = request =>
            {
                chartInfoWriterCallCount++;
                shutdownRequested = true;
                cancellation.Cancel();
                return CreateDirectChartInfoWriter(songDb)(request);
            },
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        }));

        Assert.IsTrue(shutdownRequested);
        Assert.AreEqual(1, chartInfoWriterCallCount);
        Assert.AreEqual("Durable song before shutdown", songDb.ExecuteScalar<string>(
            "SELECT title FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual("Stale artist before shutdown", songDb.ExecuteScalar<string>(
            "SELECT artist FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void SyncService_UnexpectedCancellationFailsAndRetryStartsFromZero()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "CancelRetryRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE cancel retry\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
        using var cancellation = new CancellationTokenSource();
        const string signature = "cancel-retry-after-folder";
        var progressEvents = new List<Lr2SongDbSyncProgress>();

        Assert.ThrowsException<OperationCanceledException>(() => Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "cancel-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory, songDirectory),
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken = cancellation.Token,
            ProgressReporter = progress =>
            {
                if (progress != null)
                {
                    progressEvents.Add(progress);
                }
                if (progress?.Stage == "song_rows")
                {
                    cancellation.Cancel();
                }
            }
        }));

        LR2SongDBExtended.lr2_song_db_sync_status failed = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Failed", failed.status);
        Assert.AreEqual("song_rows", failed.stage);
        Assert.AreEqual(2, failed.processed_cursor);
        Assert.AreEqual(3, failed.total_count);
        Assert.AreEqual(2, songDb.Table<LR2SongDB.folder>().Count());
        Assert.AreEqual(1, songDb.Table<LR2SongDB.song>().Count());
        Lr2SongDbSyncProgress songRowsProgress = progressEvents.First(progress => progress.Stage == "song_rows" && progress.StageTotalCount > 0);
        Assert.AreEqual(2, songRowsProgress.ProcessedCursor);
        Assert.AreEqual(3, songRowsProgress.TotalCount);
        Assert.AreEqual(0, songRowsProgress.StageProcessedCount);
        Assert.AreEqual(1, songRowsProgress.StageTotalCount);

        Lr2SongDbSyncResult retried = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "retry-after-cancel-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory, songDirectory),
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, retried.FinalStage);
        Assert.IsNotNull(retried.FolderTableReconciliationResult);
        Assert.AreEqual(1, retried.SongRowProcessedCount);
        Assert.AreEqual("cancel retry", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        LR2SongDBExtended.lr2_song_db_sync_status completed = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", completed.status);
        Assert.AreEqual(3, completed.processed_cursor);
        Assert.AreEqual(3, completed.total_count);
    }

    [TestMethod]
    public void SyncService_UpsertsLr2CompatibilityFactsWithoutReplacingMaintenanceHealth()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Lr2Compatibility");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        WriteBasicBms(chartPath, "lr2 compatibility", CreateLr2TooLongResourcePath());
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.InsertOrReplace(new BMSFileMaintenanceInfo
        {
            path = chartPath,
            hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            wav_files_defined = 99,
            wav_files_existing = 88
        }, typeof(LR2SongDBExtended.maintenance));

        var committedCompatibilityFacts = new List<BMSFileMaintenanceInfo>();
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2-compatibility",
            RunId = "lr2-compatibility",
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            Lr2CompatibilityFactsCommitted = infos => committedCompatibilityFacts.AddRange(infos)
        });

        Assert.AreEqual(1, result.SongRowLr2CompatibilityAppliedCount);
        Assert.AreEqual(1, committedCompatibilityFacts.Count);
        Assert.AreEqual(chartPath, committedCompatibilityFacts[0].path);
        Assert.AreEqual(file.hash, songDb.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(99, songDb.ExecuteScalar<int>("SELECT wav_files_defined FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(88, songDb.ExecuteScalar<int>("SELECT wav_files_existing FROM maintenance WHERE path = ?;", chartPath));
        int flags = songDb.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", chartPath);
        Assert.IsTrue((flags & (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0);
    }

    [TestMethod]
    public void SyncService_UpsertsLr2CompatibilityFactsByExactPath()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Lr2CompatibilityExact");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        WriteBasicBms(chartPath, "lr2 compatibility exact", CreateLr2TooLongResourcePath());
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        string existingPath = Path.Combine(songDirectory, "CHART.BMS");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.InsertOrReplace(new BMSFileMaintenanceInfo
        {
            path = existingPath,
            hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        }, typeof(LR2SongDBExtended.maintenance));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2-compatibility-exact",
            RunId = "lr2-compatibility-exact",
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowLr2CompatibilityAppliedCount);
        Assert.AreEqual(2, songDb.Table<BMSFileMaintenanceInfo>().Count());
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", songDb.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?;", existingPath));
        Assert.AreEqual(file.hash, songDb.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?;", chartPath));
        int flags = songDb.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", chartPath);
        Assert.IsTrue((flags & (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0);
    }

    [TestMethod]
    public void SyncService_PreservesLr2CompatibilityFactsWhenSongRowFallsBackWithoutSnapshot()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string chartPath = Path.Combine(scope.DirectoryPath, "Missing", "chart.bms");
        var file = new TestableBmsFile
        {
            path = chartPath,
            date = 123456
        }.WithHashAndFavorite("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", favoriteValue: null);
        file.SetTitleForTest("fallback row");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        int existingFlags = (int)(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported
            | Lr2CompatibilityWarningFlags.ResourcePathTooLong);
        songDb.InsertOrReplace(new BMSFileMaintenanceInfo
        {
            path = chartPath,
            hash = file.hash,
            wav_files_defined = 7,
            wav_files_existing = 6,
            lr2_warning_flags = existingFlags,
            lr2_resource_max_relative_cp932_bytes = 120,
            lr2_resource_has_parent_traversal = true
        }, typeof(LR2SongDBExtended.maintenance));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2-compatibility-fallback-preserve",
            RunId = "lr2-compatibility-fallback-preserve",
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowParseFailureCount);
        Assert.AreEqual(0, result.SongRowLr2CompatibilityAppliedCount);
        Assert.AreEqual(existingFlags, songDb.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(120, songDb.ExecuteScalar<int>("SELECT lr2_resource_max_relative_cp932_bytes FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT lr2_resource_has_parent_traversal FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(7, songDb.ExecuteScalar<int>("SELECT wav_files_defined FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(6, songDb.ExecuteScalar<int>("SELECT wav_files_existing FROM maintenance WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void SyncService_DeletesMissingDiscoveredLr2FolderRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string missingPath = Path.Combine(rootDirectory, "missing.lr2folder");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = missingPath,
            type = 2,
            title = "Keep",
            date = 1
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "test",
            RunId = "run",
            RootDirectories = [rootDirectory],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.IsNull(result.Lr2FolderFileSyncResult);
        Assert.AreEqual(1, result.FolderTableReconciliationResult.DeletedCount);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == missingPath));
        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
    }

    [TestMethod]
    public void SyncService_UsesEnumeratedLr2FolderTimestampForGeneratedRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "table.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE Table");
        DateTime enumeratedTimestamp = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        DateTime liveTimestamp = enumeratedTimestamp.AddMinutes(10);
        File.SetLastWriteTimeUtc(lr2FolderPath, liveTimestamp);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2folder-enumerated-time",
            RunId = "lr2folder-enumerated-time-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, enumeratedTimestamp)
            },
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(folder => folder.path == lr2FolderPath);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(enumeratedTimestamp), row.date);
        Assert.AreEqual(2, result.FolderTableReconciliationResult.GeneratedCount);
        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
    }

    [TestMethod]
    public void SyncService_IncludesBuiltinLr2FolderParent()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string chartRoot = Path.Combine(scope.DirectoryPath, "BMS");
        string lr2Root = Path.Combine(scope.DirectoryPath, "LR2");
        string builtinRoot = Path.Combine(lr2Root, "LR2files", "CustomFolder");
        string insaneDirectory = Path.Combine(builtinRoot, "INSANE02");
        string lr2FolderPath = Path.Combine(insaneDirectory, "02.lr2folder");
        Directory.CreateDirectory(chartRoot);
        Directory.CreateDirectory(insaneDirectory);
        File.WriteAllText(lr2FolderPath, "#TITLE INSANE02");
        DateTime timestamp = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "builtin-lr2folder-parent-diagnostic",
            RunId = "builtin-lr2folder-parent-diagnostic-run",
            RootDirectories = [chartRoot],
            DirectoryEntries = CreateDirectoryEntryMap(chartRoot, insaneDirectory),
            Lr2RootPath = lr2Root,
            Lr2BuiltinFolderSourceDirectories = [builtinRoot],
            Lr2FolderDiscoveryDirectories = [builtinRoot],
            Lr2FolderPruneDirectories = [@"LR2files\CustomFolder"],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
            },
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == @"LR2files\CustomFolder\INSANE02\"));
    }

    [TestMethod]
    public void SyncService_UsesEnumeratedDirectoryTimestampForNormalFolderRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        DateTime enumeratedTimestamp = new(2026, 6, 5, 5, 0, 0, DateTimeKind.Utc);
        DateTime liveTimestamp = enumeratedTimestamp.AddMinutes(10);
        Directory.SetLastWriteTimeUtc(rootDirectory, liveTimestamp);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "directory-enumerated-time",
            RunId = "directory-enumerated-time-run",
            RootDirectories = [rootDirectory],
            DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, enumeratedTimestamp)
            },
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(folder => folder.path == ToFolderPath(rootDirectory));
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(enumeratedTimestamp), row.date);
        Assert.AreEqual(1, result.FolderTableReconciliationResult.GeneratedCount);
        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
    }

    private static string EscapeSqlLiteral(string value)
    {
        return (value ?? string.Empty).Replace("'", "''");
    }

    private static Func<CatalogChartInfoWriteRequest, CatalogChartInfoWriteReceipt> CreateDirectChartInfoWriter(
        LR2SongDBExtended songDb)
    {
        return request => ApplyChartInfoWriteForDirectServiceTest(songDb, request);
    }

    private static CatalogChartInfoWriteReceipt ApplyChartInfoWriteForDirectServiceTest(
        LR2SongDBExtended songDb,
        CatalogChartInfoWriteRequest request)
    {
        if (request == null || !request.HasChanges)
        {
            return CatalogChartInfoWriteReceipt.NotApplied;
        }
        BmsLibraryDbGateway.UpsertChartInfoBackfillChunk(
            songDb,
            request.DigestEntries,
            request.ChartInfoRows,
            request.ParseFailureRows,
            request.ParseFailureDeleteMd5s);
        return new CatalogChartInfoWriteReceipt(
            applied: true,
            request.DigestEntries.Count,
            request.ChartInfoRows.Count,
            request.ParseFailureRows.Count,
            request.ParseFailureDeleteMd5s.Count);
    }

    private static Func<BMSFile, LR2SongDBExtended.chart_info> CreateChartInfoResolver(
        IEnumerable<LR2SongDBExtended.chart_info> rows)
    {
        var bySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        var md5Candidates = new Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.chart_info row in rows ?? [])
        {
            if (row == null || row.parser_version != BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(row.sha256))
            {
                bySha256[row.sha256] = row;
            }
            if (!string.IsNullOrWhiteSpace(row.md5) && !string.IsNullOrWhiteSpace(row.sha256))
            {
                if (!md5Candidates.TryGetValue(row.md5, out SortedDictionary<string, LR2SongDBExtended.chart_info> candidates))
                {
                    candidates = new SortedDictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
                    md5Candidates[row.md5] = candidates;
                }
                candidates[row.sha256] = row;
            }
        }

        Dictionary<string, LR2SongDBExtended.chart_info> byMd5 = md5Candidates
            .Where(pair => pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value.First().Value, StringComparer.OrdinalIgnoreCase);
        return row =>
        {
            if (row == null)
            {
                return null!;
            }
            if (!string.IsNullOrWhiteSpace(row.sha256)
                && bySha256.TryGetValue(row.sha256, out LR2SongDBExtended.chart_info bySha256Row))
            {
                return bySha256Row;
            }
            if (!string.IsNullOrWhiteSpace(row.hash)
                && byMd5.TryGetValue(row.hash, out LR2SongDBExtended.chart_info byMd5Row))
            {
                return byMd5Row;
            }
            return null!;
        };
    }
}
