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
    public void SyncService_DeletesUnknownRootFolderRowAndCompletes()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string outsideDirectory = Path.Combine(scope.DirectoryPath, "OutsideRoot");
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(outsideDirectory);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(outsideDirectory),
            type = 1,
            date = 1
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "unknown-root-folder",
            RunId = "unknown-root-folder",
            RootDirectories = [rootDirectory],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.UnknownRootFolderRowCount);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == ToFolderPath(outsideDirectory)));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_CompletesWhenCurrentSongRowIsOutsideRootAndKeepsDiagnostic()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string outsideDirectory = Path.Combine(scope.DirectoryPath, "OutsideRoot");
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(outsideDirectory);
        string chartPath = Path.Combine(outsideDirectory, "outside.bms");
        File.WriteAllText(chartPath, "#TITLE Outside Root\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "unknown-root-current-song",
            RunId = "unknown-root-current-song",
            RootDirectories = [rootDirectory],
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsNull(result.IncompleteReason);
        Assert.AreEqual(1, result.StartupScanDiagnosticResult.UnknownRootSongRowCount);
        Assert.IsNotNull(songDb.Find<LR2SongDB.song>(chartPath));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_DoesNotTreatNegativeSongDateAsMissing()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "negative-date.bms");
        File.WriteAllText(chartPath, "#TITLE Negative Date\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        file.date = -1;
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
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateMissingSongRowCount);
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_DoesNotTreatNullSongDateAsMissing()
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
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateMissingSongRowCount);
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_DeletesFolderDateMissingRowAndCompletes()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "broken.lr2folder");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            type = 2,
            date = 0
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "folder-date-missing",
            RunId = "folder-date-missing",
            RootDirectories = [rootDirectory],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateMissingFolderRowCount);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(folder => folder.path == lr2FolderPath));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_RepairsFolderDateWhenStaleAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "table.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE Table");
        DateTime rootTime = new(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc);
        DateTime lr2FolderTime = new(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(lr2FolderPath, lr2FolderTime);
        Directory.SetLastWriteTimeUtc(rootDirectory, rootTime);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(rootDirectory),
            type = 1,
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(rootTime.AddMinutes(-1))
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            type = 2,
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(lr2FolderTime.AddMinutes(-1))
        }, typeof(LR2SongDB.folder));
        const string signature = "folder-date-stale";
        Lr2SongDbSyncStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 2,
            stage: "lr2folder_files_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "folder-date-stale-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = CreateFileEntryMap(lr2FolderPath),
            DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, rootTime)
            },
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(rootTime), songDb.ExecuteScalar<int>("SELECT date FROM folder WHERE path = ?;", ToFolderPath(rootDirectory)));
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(lr2FolderTime), songDb.ExecuteScalar<int>("SELECT date FROM folder WHERE path = ?;", lr2FolderPath));
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
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        string legacyFolderPath = ToFolderPath(legacyDirectory);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == legacyFolderPath));
    }

    [TestMethod]
    public void SyncService_Lr2FolderPruneExcludedPathsProtectsManagedOutputRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string outputDirectory = Path.Combine(rootDirectory, "#BeMusicSeekerOutput", "ManagedTable");
        Directory.CreateDirectory(outputDirectory);
        string managedPath = Path.Combine(outputDirectory, "0000.lr2folder");
        string staleExternalPath = Path.Combine(outputDirectory, "stale.lr2folder");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = managedPath,
            type = 1,
            date = 1
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = staleExternalPath,
            type = 2,
            date = 1
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2folder-prune-excluded-paths",
            RunId = "lr2folder-prune-excluded-paths-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderPruneExcludedPaths = [managedPath],
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == managedPath));
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == staleExternalPath));
    }

    [TestMethod]
    public void SyncService_Lr2FolderPruneExcludedDirectoriesProtectsManagedOutputRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string outputDirectory = Path.Combine(rootDirectory, "#BeMusicSeekerOutput", "ManagedTable");
        Directory.CreateDirectory(outputDirectory);
        string managedPath = Path.Combine(outputDirectory, "0000.lr2folder");
        string managedStalePath = Path.Combine(outputDirectory, "stale.lr2folder");
        string externalPath = Path.Combine(rootDirectory, "external.lr2folder");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = managedPath,
            type = 2,
            date = 1
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = managedStalePath,
            type = 2,
            date = 1
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = externalPath,
            type = 2,
            date = 1
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2folder-prune-excluded-directories",
            RunId = "lr2folder-prune-excluded-directories-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderPruneExcludedDirectories = [outputDirectory],
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == managedPath));
        Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == managedStalePath));
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == externalPath));
    }

    [TestMethod]
    public void SyncService_PruneExcludedLr2FolderDateStaleRemainsDiagnosticOnly()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string outputDirectory = Path.Combine(rootDirectory, "#BeMusicSeekerOutput", "ManagedTable");
        Directory.CreateDirectory(outputDirectory);
        string managedPath = Path.Combine(outputDirectory, "0000.lr2folder");
        DateTime currentTime = new(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = managedPath,
            type = 2,
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(currentTime.AddHours(-1))
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2folder-prune-excluded-date-stale",
            RunId = "lr2folder-prune-excluded-date-stale-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderPruneExcludedPaths = [managedPath],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [managedPath] = new RootFileEnumerationEntry(managedPath, currentTime)
            },
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.FolderDateUpdateCount);
        LR2SongDB.folder managedRow = songDb.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == managedPath);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(currentTime.AddHours(-1)), managedRow.date);
    }

    [TestMethod]
    public void SyncService_IncompleteLr2FolderDiscoveryDoesNotCleanupExistingLr2FolderRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string lr2FolderPath = Path.Combine(rootDirectory, "existing.lr2folder");
        Directory.CreateDirectory(rootDirectory);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            type = 2,
            date = 1
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2folder-discovery-incomplete-preserve",
            RunId = "lr2folder-discovery-incomplete-preserve-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFileDiscoveryComplete = false,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == lr2FolderPath));
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.CleanupFolderRowCount);
    }

    [TestMethod]
    public void SyncService_IncompleteLr2FolderDiscoveryDoesNotCleanupDirectoryRowsInLr2FolderScope()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
        string outputDirectory = Path.Combine(outputBase, "ManagedTable");
        Directory.CreateDirectory(outputDirectory);
        string outputDirectoryRowPath = ToFolderPath(outputDirectory);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = outputDirectoryRowPath,
            type = 1,
            date = 1
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2folder-directory-discovery-incomplete-preserve",
            RunId = "lr2folder-directory-discovery-incomplete-preserve-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [outputBase],
            Lr2FolderPruneDirectories = [outputBase],
            Lr2FolderFileDiscoveryComplete = false,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == outputDirectoryRowPath));
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.CleanupFolderRowCount);
    }

    [TestMethod]
    public void SyncService_DeletesMissingFolderTargetsAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string missingDirectory = Path.Combine(rootDirectory, "MissingPack");
        string missingLr2FolderPath = Path.Combine(rootDirectory, "missing.lr2folder");
        Directory.CreateDirectory(rootDirectory);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(missingDirectory),
            type = 1,
            date = 1
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = missingLr2FolderPath,
            type = 2,
            date = 1
        }, typeof(LR2SongDB.folder));
        const string signature = "folder-target-missing";
        Lr2SongDbSyncStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 2,
            stage: "lr2folder_files_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "folder-target-missing-run",
            RootDirectories = [rootDirectory],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [missingLr2FolderPath],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
        Assert.IsTrue(result.StartupScanDiagnosticResult.IsClean);
        string missingFolderPath = ToFolderPath(missingDirectory);
        string rootFolderPath = ToFolderPath(rootDirectory);
        List<LR2SongDB.folder> folderRows = songDb.Table<LR2SongDB.folder>().ToList();
        Assert.AreEqual(0, folderRows.Count(folder => folder.path == missingFolderPath));
        Assert.AreEqual(0, folderRows.Count(folder => folder.path == missingLr2FolderPath));
        Assert.AreEqual(1, folderRows.Count(folder => folder.path == rootFolderPath));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
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
    public void SyncService_SkipsSongRowsWhenVerifierConfirmsCurrent()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string chartPath = Path.Combine(scope.DirectoryPath, "Current", "chart.bms");
        var file = new TestableBmsFile
        {
            path = chartPath,
            date = 123456
        }.WithHashAndFavorite("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", favoriteValue: null);
        file.ApplySha256("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        file.SetTitleForTest("already current");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
        Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
        var logs = new List<string>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "skip-song-rows",
            RunId = "skip-song-rows",
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc),
            SongRowsSkipVerifier = rows => new Lr2SongDbSyncSongRowsSkipVerificationResult
            {
                CanSkip = true,
                Reason = "test_current",
                TargetRows = rows.Count,
                VerifiedRows = rows.Count
            },
            LogInstallPerformance = logs.Add
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowSkippedCount);
        Assert.AreEqual(result.TotalCount, result.ProcessedCount);
        Assert.IsTrue(logs.Any(log => log.Contains("lr2_song_db_sync song_rows_skip action=skip")));
        Assert.IsFalse(logs.Any(log => log.Contains("pipeline_start stage=song_rows")));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(row.total_count, row.processed_cursor);
    }

    [TestMethod]
    public void SyncService_TransientSongRowSkipPathsSkipOnlyMatchingRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string skippedPath = Path.Combine(scope.DirectoryPath, "Skipped", "already-inserted.bms");
        string processDirectory = Path.Combine(scope.DirectoryPath, "Process");
        Directory.CreateDirectory(processDirectory);
        string processPath = Path.Combine(processDirectory, "process.bms");
        File.WriteAllText(processPath, "#TITLE processed transient remainder\r\n", Encoding.ASCII);
        ChartFileSnapshot processSnapshot = ChartFileContentReader.ReadSnapshot(processPath);
        var skippedFile = new TestableBmsFile
        {
            path = skippedPath,
            date = 123456
        }.WithHashAndFavorite("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", favoriteValue: null);
        skippedFile.ApplySha256("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        TestableBmsFile processFile = CreateSyncTestFile(processPath, processSnapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        var logs = new List<string>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "transient-song-row-skip",
            RunId = "transient-song-row-skip",
            SongRows = [skippedFile, processFile],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            TransientSongRowsSkipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                skippedPath
            },
            StartedAtUtc = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc),
            LogInstallPerformance = logs.Add
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(2, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowSkippedCount);
        Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", skippedPath));
        Assert.AreEqual("processed transient remainder", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", processPath));
        Assert.IsTrue(logs.Any(log => log.Contains("pipeline_start stage=song_rows")
            && log.Contains("transientSkipPaths=1")));
        Assert.IsTrue(logs.Any(log => log.Contains("chunk_done stage=song_rows")
            && log.Contains("transientSkipped=1")));
        Assert.IsTrue(logs.Any(log => log.Contains("pipeline_done stage=song_rows")
            && log.Contains("transientSkipped=1")));
    }

    [TestMethod]
    public void SyncService_ResumesFromCompletedNormalFolderBoundary()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE resume song\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        const string signature = "resume-normal-complete";
        Lr2SongDbSyncStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 3,
            stage: "normal_folders_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        InsertNormalFolderRow(songDb, rootDirectory, Lr2SongFolderParentNormalizer.RootParentHash);
        InsertNormalFolderRow(songDb, songDirectory, Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "resume-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsNull(result.NormalFolderSyncResult);
        Assert.AreEqual(2, songDb.Table<LR2SongDB.folder>().Count());
        Assert.AreEqual("resume song", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(row.total_count, row.processed_cursor);
    }

    [TestMethod]
    public void SyncService_ResyncsExpectedNormalFolderRowsAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeMissingFolderRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE resume missing folder\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        const string signature = "resume-normal-missing-folder";
        Lr2SongDbSyncStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 3,
            stage: "normal_folders_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "resume-missing-folder-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory, songDirectory),
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.MissingExpectedFolderRowCount);
        string rootFolderPath = ToFolderPath(rootDirectory);
        string songFolderPath = ToFolderPath(songDirectory);
        List<LR2SongDB.folder> folderRows = songDb.Table<LR2SongDB.folder>().ToList();
        Assert.AreEqual(1, folderRows.Count(folder => folder.path == rootFolderPath));
        Assert.AreEqual(1, folderRows.Count(folder => folder.path == songFolderPath));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_ResyncsExpectedLr2FolderRowAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeMissingLr2FolderRoot");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "table.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE table\r\n", Encoding.GetEncoding("shift_jis"));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        InsertNormalFolderRow(songDb, rootDirectory, Lr2SongFolderParentNormalizer.RootParentHash);
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            type = 99,
            title = "unsupported type",
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(lr2FolderPath))
        }, typeof(LR2SongDB.folder));
        const string signature = "resume-lr2folder-missing-row";
        Lr2SongDbSyncStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 2,
            stage: "lr2folder_files_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "resume-missing-lr2folder-row-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = CreateFileEntryMap(lr2FolderPath),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.MissingExpectedLr2FolderRowCount);
        LR2SongDB.folder folderRow = songDb.Find<LR2SongDB.folder>(lr2FolderPath);
        Assert.IsNotNull(folderRow);
        Assert.AreEqual(2, folderRow.type);
        Assert.AreEqual("table", folderRow.title);
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_SkipsExpectedLr2FolderRowWhenMetadataIsMissingAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeUnreadableLr2FolderRoot");
        Directory.CreateDirectory(rootDirectory);
        string missingLr2FolderPath = Path.Combine(rootDirectory, "missing.lr2folder");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        InsertNormalFolderRow(songDb, rootDirectory, Lr2SongFolderParentNormalizer.RootParentHash);
        const string signature = "resume-lr2folder-missing-metadata";
        Lr2SongDbSyncStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 2,
            stage: "lr2folder_files_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "resume-missing-metadata-lr2folder-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [missingLr2FolderPath],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.MissingExpectedLr2FolderRowCount);
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_RestartsWhenResumeTotalCountDiffers()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeMismatchRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE resume mismatch\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        const string signature = "resume-total-mismatch";
        Lr2SongDbSyncStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 99,
            stage: "normal_folders_completed",
            detail: "old total",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "restart-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory, songDirectory),
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsNotNull(result.NormalFolderSyncResult);
        Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any());
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(3, row.total_count);
    }

    [TestMethod]
    public void SyncService_ResumesInsideSongRowsFromDurableCursor()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeSongRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string firstPath = Path.Combine(songDirectory, "first.bms");
        string secondPath = Path.Combine(songDirectory, "second.bms");
        File.WriteAllText(firstPath, "#TITLE first updated\r\n");
        File.WriteAllText(secondPath, "#TITLE second updated\r\n");
        ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
        ChartFileSnapshot secondSnapshot = ChartFileContentReader.ReadSnapshot(secondPath);
        TestableBmsFile firstFile = CreateSyncTestFile(firstPath, firstSnapshot);
        TestableBmsFile secondFile = CreateSyncTestFile(secondPath, secondSnapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        var existingFirstRow = new TestableBmsFile
        {
            path = firstPath,
            date = 1
        }.WithHashAndFavorite(firstFile.hash, favoriteValue: null);
        existingFirstRow.SetTitleForTest("first stale");
        songDb.InsertOrReplace(existingFirstRow, typeof(LR2SongDB.song));
        const string signature = "resume-song-row";
        Lr2SongDbSyncStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 3,
            totalCount: 4,
            stage: "song_rows",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        InsertNormalFolderRow(songDb, rootDirectory, Lr2SongFolderParentNormalizer.RootParentHash);
        InsertNormalFolderRow(songDb, songDirectory, Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "resume-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [firstPath, secondPath],
            SongRows = [firstFile, secondFile],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual("first stale", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", firstPath));
        Assert.AreEqual("second updated", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", secondPath));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(4, row.processed_cursor);
    }

    [TestMethod]
    public void SyncService_PrunesStaleSongRowsAndMaintenance()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string songDirectory = Path.Combine(rootDirectory, "Current");
        Directory.CreateDirectory(songDirectory);
        string currentPath = Path.Combine(songDirectory, "current.bms");
        File.WriteAllText(currentPath, "#TITLE current\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile currentFile = CreateSyncTestFile(currentPath, ChartFileContentReader.ReadSnapshot(currentPath));
        string stalePath = Path.Combine(scope.DirectoryPath, "OldRoot", "stale.bms");
        string staleHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
        songDb.InsertOrReplace(new TestableBmsFile
        {
            path = stalePath,
            date = 1
        }.WithHashAndFavorite(staleHash, favoriteValue: null), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(new BMSFileMaintenanceInfo
        {
            path = stalePath,
            hash = staleHash
        }, typeof(LR2SongDBExtended.maintenance));
        songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
        {
            md5 = staleHash,
            sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        }, typeof(LR2SongDBExtended.chart_digest_map));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "prune-stale-song-row",
            RunId = "prune-stale-song-row-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [currentPath],
            SongRows = [currentFile],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, result.StaleSongRowPrunedCount);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.UnknownRootSongRowCount);
        Assert.IsNotNull(songDb.Find<LR2SongDB.song>(currentPath));
        Assert.IsNull(songDb.Find<LR2SongDB.song>(stalePath));
        Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", stalePath));
        Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ?;", staleHash));
        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_PrunesCaseOnlyStaleSongRowsByExactCurrentPath()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string songDirectory = Path.Combine(rootDirectory, "CaseOnly");
        Directory.CreateDirectory(songDirectory);
        string currentPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(currentPath, "#TITLE current\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile currentFile = CreateSyncTestFile(currentPath, ChartFileContentReader.ReadSnapshot(currentPath));
        string stalePath = Path.Combine(songDirectory, "CHART.BMS");
        string staleHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
        songDb.InsertOrReplace(new TestableBmsFile
        {
            path = stalePath,
            date = 1
        }.WithHashAndFavorite(staleHash, favoriteValue: 7), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(new BMSFileMaintenanceInfo
        {
            path = stalePath,
            hash = staleHash
        }, typeof(LR2SongDBExtended.maintenance));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "prune-case-only-stale-song-row",
            RunId = "prune-case-only-stale-song-row-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [currentPath],
            SongRows = [currentFile],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, result.StaleSongRowPrunedCount);
        Assert.IsNotNull(songDb.Find<LR2SongDB.song>(currentPath));
        Assert.IsNull(songDb.Find<LR2SongDB.song>(stalePath));
        Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", stalePath));
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
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        const string signature = "rollback-song-chunk";
        songDb.Execute(
            "CREATE TRIGGER fail_second_song_insert BEFORE INSERT ON song"
            + " WHEN NEW.path = '" + EscapeSqlLiteral(secondPath) + "'"
            + " BEGIN SELECT RAISE(ABORT, 'fail_second_song_insert'); END;");

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
        Assert.AreEqual(0, songDb.Table<LR2SongDB.song>().Count());
        LR2SongDBExtended.lr2_song_db_sync_status failed = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Failed", failed.status);
        Assert.AreEqual("song_rows", failed.stage);
        Assert.AreEqual(2, failed.processed_cursor);
        Assert.AreEqual(4, failed.total_count);

        songDb.Execute("DROP TRIGGER fail_second_song_insert;");
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
    public void SyncService_CancelledRequestMarksCancelledStatus()
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
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Cancelled.ToString(), row.status);
        Assert.AreEqual("final_validation", row.stage);
        Assert.AreEqual(0, row.processed_cursor);
        Assert.AreEqual(0, row.total_count);
    }

    [TestMethod]
    public void SyncService_CancelledAfterFolderStageResumesAndCompletes()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "CancelResumeRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE cancel resume\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        using var cancellation = new CancellationTokenSource();
        const string signature = "cancel-resume-after-folder";
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

        LR2SongDBExtended.lr2_song_db_sync_status cancelled = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Cancelled", cancelled.status);
        Assert.AreEqual("song_rows", cancelled.stage);
        Assert.AreEqual(2, cancelled.processed_cursor);
        Assert.AreEqual(3, cancelled.total_count);
        Assert.AreEqual(2, songDb.Table<LR2SongDB.folder>().Count());
        Assert.AreEqual(0, songDb.Table<LR2SongDB.song>().Count());
        Lr2SongDbSyncProgress songRowsProgress = progressEvents.First(progress => progress.Stage == "song_rows" && progress.StageTotalCount > 0);
        Assert.AreEqual(2, songRowsProgress.ProcessedCursor);
        Assert.AreEqual(3, songRowsProgress.TotalCount);
        Assert.AreEqual(0, songRowsProgress.StageProcessedCount);
        Assert.AreEqual(1, songRowsProgress.StageTotalCount);

        Lr2SongDbSyncResult resumed = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = signature,
            RunId = "resume-after-cancel-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory, songDirectory),
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, resumed.FinalStage);
        Assert.IsNull(resumed.NormalFolderSyncResult);
        Assert.AreEqual(1, resumed.SongRowProcessedCount);
        Assert.AreEqual("cancel resume", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
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
            Lr2FolderFilePaths = [missingPath],
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.Lr2FolderFileSyncResult.ItemCount);
        Assert.AreEqual(0, result.Lr2FolderFileSyncResult.DeletedCount);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == missingPath));
        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsTrue(result.StartupScanDiagnosticResult.IsClean);
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
        Assert.AreEqual(1, result.Lr2FolderFileSyncResult.GeneratedCount);
        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
    }

    [TestMethod]
    public void SyncService_DoesNotReportBuiltinLr2FolderParentAsMissingNormalFolder()
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
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.MissingExpectedFolderRowCount);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.MissingExpectedLr2FolderRowCount);
        Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any(row => row.path == @"LR2files\CustomFolder\INSANE02\"));
    }

    [TestMethod]
    public void SyncService_PreservesUnchangedLr2FolderRowBeforeDefinitionParse()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "table.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE Reparsed Title");
        DateTime timestamp = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            title = "Preserved Title",
            type = 2,
            parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory),
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp),
            adddate = 12345
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2folder-preserve",
            RunId = "lr2folder-preserve-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
            },
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(folder => folder.path == lr2FolderPath);
        Assert.AreEqual(1, result.Lr2FolderFileSyncResult.PreservedCount);
        Assert.AreEqual(0, result.Lr2FolderFileSyncResult.GeneratedCount);
        Assert.AreEqual("Preserved Title", row.title);
        Assert.AreEqual(12345, row.adddate);
        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
    }

    [TestMethod]
    public void SyncService_PreservesEntriesOnlyLr2FolderRowBeforeDefinitionParse()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "table.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE Reparsed Entries Only");
        DateTime timestamp = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            title = "Preserved Entries Only",
            type = 2,
            parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory),
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
        }, typeof(LR2SongDB.folder));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2folder-entries-only-preserve",
            RunId = "lr2folder-entries-only-preserve-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
            },
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(folder => folder.path == lr2FolderPath);
        Assert.AreEqual(1, result.Lr2FolderFileSyncResult.PreservedCount);
        Assert.AreEqual(0, result.Lr2FolderFileSyncResult.GeneratedCount);
        Assert.AreEqual("Preserved Entries Only", row.title);
        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
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
        Assert.AreEqual(1, result.NormalFolderSyncResult.GeneratedCount);
        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
    }

    private static void InsertNormalFolderRow(LR2SongDBExtended songDb, string directoryPath, string parentHash)
    {
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(directoryPath),
            title = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            type = 1,
            parent = parentHash,
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(Directory.GetLastWriteTimeUtc(directoryPath)),
            adddate = Lr2SongRowEnricher.ToLr2UnixSeconds(new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc))
        }, typeof(LR2SongDB.folder));
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
