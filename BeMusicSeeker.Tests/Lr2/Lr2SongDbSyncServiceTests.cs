using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLitePCL;
using static BeMusicSeeker.Tests.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncServiceTests
{

    /// <summary>
    /// 通常・folderinfo・カスタム・楽曲の実対象数で進捗を報告し、準備完了時には旧保存行とカーソルを保持し、生成反映後も全体の完了保存は要求coordinatorへ残す。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_ReportsActualStageTargetsAndKeepsPreparationSeparateFromCommit()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "FolderProgressRoot");
        string firstDirectory = Path.Combine(rootDirectory, "First");
        string secondDirectory = Path.Combine(rootDirectory, "Second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        DateTime rootTime = new(2026, 6, 8, 6, 0, 0, DateTimeKind.Utc);
        DateTime firstTime = rootTime.AddMinutes(1);
        DateTime secondTime = rootTime.AddMinutes(2);
        string firstFolderInfo = Path.Combine(firstDirectory, "folderinfo.txt");
        string secondFolderInfo = Path.Combine(secondDirectory, "folderinfo.txt");
        File.WriteAllText(firstFolderInfo, "#TITLE First folder", Encoding.ASCII);
        File.WriteAllText(secondFolderInfo, "#TITLE Second folder", Encoding.ASCII);
        string firstCustomFolder = Path.Combine(firstDirectory, "custom.lr2folder");
        string secondCustomFolder = Path.Combine(secondDirectory, "custom.lr2folder");
        File.WriteAllText(firstCustomFolder, "#TITLE First custom", Encoding.ASCII);
        File.WriteAllText(secondCustomFolder, "#TITLE Second custom", Encoding.ASCII);
        string firstChart = Path.Combine(firstDirectory, "chart.bms");
        string secondChart = Path.Combine(secondDirectory, "chart.bms");
        WriteBasicBms(firstChart, "First chart");
        WriteBasicBms(secondChart, "Second chart");
        ChartFile firstSong = CreateSyncTestFile(firstChart, ChartFileContentReader.ReadSnapshot(firstChart));
        ChartFile secondSong = CreateSyncTestFile(secondChart, ChartFileContentReader.ReadSnapshot(secondChart));
        string stalePath = ToFolderPath(Path.Combine(scope.DirectoryPath, "stale"));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(firstSong), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(secondSong), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = stalePath,
            title = "Durable before reconciliation",
            date = 1
        }, typeof(LR2SongDB.folder));

        var progressEvents = new ConcurrentQueue<Lr2SongDbSyncProgress>();
        bool observedPreCommitState = false;
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "folder-reconciliation-progress",
            RunId = "folder-reconciliation-progress",
            RootDirectories = [rootDirectory],
            NormalFolderDirectoryPaths = [rootDirectory, firstDirectory, secondDirectory],
            FolderInfoFilePaths = [firstFolderInfo, secondFolderInfo],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [firstCustomFolder, secondCustomFolder],
            Lr2FolderFileEntries = CreateFileEntryMap(firstCustomFolder, secondCustomFolder),
            ChartPaths = [firstChart, secondChart],
            SongRows = [firstSong, secondSong],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
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

                progressEvents.Enqueue(progress);
                if (progress.Stage == "folder_reconciliation"
                    && progress.StageProcessedCount == 5
                    && progress.StageTotalCount == 5
                    && progress.ProcessedCursor == 0
                    && songDb.Find<LR2SongDB.folder>(stalePath)?.title == "Durable before reconciliation"
                    && songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName)?.status == "Running"
                    && songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName)?.processed_cursor == 0)
                {
                    observedPreCommitState = true;
                }
            }
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsTrue(observedPreCommitState);
        List<Lr2SongDbSyncProgress> reconciliationProgress = [.. progressEvents
            .Where(progress => progress.Stage == "folder_reconciliation")];
        Assert.IsTrue(reconciliationProgress.Count > 0);
        int stageTotal = reconciliationProgress[0].StageTotalCount;
        Assert.AreEqual(5, stageTotal, "通常3行とカスタム2行を引き継ぐ。親3行は通常ディレクトリと同じパス。");
        for (int index = 0; index < reconciliationProgress.Count; index++)
        {
            Lr2SongDbSyncProgress progress = reconciliationProgress[index];
            Assert.AreEqual(stageTotal, progress.StageTotalCount);
            Assert.IsTrue(progress.StageProcessedCount >= 0);
            Assert.IsTrue(progress.StageProcessedCount <= stageTotal);
        }

        Assert.AreEqual(result.FolderTableReconciliationResult.GeneratedCount, stageTotal);
        Assert.AreEqual(result.FolderTableReconciliationResult.GeneratedCount, songDb.Table<LR2SongDB.folder>().Count());
        foreach ((string stage, int total) in new (string, int)[]
        {
            ("lr2folder_files", 2), ("directory_metadata", 3), ("normal_folder_roots", 1),
            ("normal_folders", 3), ("folder_projection_candidates", 3),
            ("custom_folder_rows", 2), ("custom_folder_parents", 3), ("song_rows", 2)
        })
        {
            Lr2SongDbSyncProgress[] notifications = [.. progressEvents.Where(progress => progress.Stage == stage)];
            Assert.IsTrue(notifications.Length > 0, stage);
            Assert.IsTrue(notifications.All(progress => progress.StageTotalCount == total
                && progress.StageProcessedCount >= 0 && progress.StageProcessedCount <= total), stage);
            Assert.IsTrue(notifications.Any(progress => progress.StageProcessedCount == total), stage);
        }
        foreach (string stage in new[] { "input_preparation", "folder_projection_preparation", "folder_projection_validation",
            "folder_existing_rows", "folder_saving", "song_rows_preparation", "final_validation" })
        {
            Assert.IsTrue(progressEvents.Any(progress => progress.Stage == stage && progress.StageTotalCount == 0), stage);
        }
        Lr2SongDbSyncProgress[] events = progressEvents.ToArray();
        Assert.IsTrue(Array.FindIndex(events, progress => progress.Stage == "song_rows_preparation")
            < Array.FindIndex(events, progress => progress.Stage == "song_rows"));
        Assert.IsFalse(events.Any(progress => progress.Stage == "song_rows_saving"));
        Assert.IsFalse(events.Any(progress => progress.Stage == Lr2SongDbSyncService.CompletedStage
            || progress.Stage == "sync_state_saving"), "生成処理だけで全体の完了を通知しない。");
        Assert.AreEqual("First folder", songDb.Find<LR2SongDB.folder>(ToFolderPath(firstDirectory)).title);
        Assert.AreEqual("Second custom", songDb.Find<LR2SongDB.folder>(secondCustomFolder).title);
        LR2SongDBExtended.lr2_song_db_sync_status completed = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Running", completed.status);
        Assert.IsNull(completed.completed_at);
        Assert.AreEqual(completed.total_count, completed.processed_cursor);
        Assert.AreEqual(9, completed.total_count, "保存カーソルは通常3・folderinfo2・カスタム2・楽曲2の入力位置を保つ。");
    }

    /// <summary>
    /// チャンク境界の1001件と順序枠上限を超える10001件を実更新し、順序回収・末尾・保存位置と利用者設定を保持する。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [DataTestMethod]
    [DataRow(1001)]
    [DataRow(10001)]
    public void SyncService_RealSongUpdatesKeepFiniteRoleAcrossChunkCommits(int targetCount)
    {
        using var scope = TestDatabaseScope.Create();
        string directory = Path.Combine(scope.DirectoryPath, "ChunkProgress");
        Directory.CreateDirectory(directory);
        var songs = new List<ChartFile>(targetCount);
        for (int index = 0; index < targetCount; index++)
        {
            string path = Path.Combine(directory, index + ".bms");
            // 読取り依存だけを固定バイト列へ置換し、1万個の物理ファイル準備を保証へ混ぜません。
            ChartFile song = ChartTestValues.Empty() with { Path = path, Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" };
            song = song with { Title = "Old " + index, RawTitle = "Old " + index };
            song = song with { Tag = "User tag" };
            song = song with { Favorite = 7 };
            songs.Add(song);
        }
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.RunInTransaction(() =>
        {
            foreach (ChartFile song in songs)
            {
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(song), typeof(LR2SongDB.song));
            }
        });
        Lr2SongDbSyncProgress? latest = null;
        var events = new ConcurrentQueue<Lr2SongDbSyncProgress>();
        var beforeCommit = new List<Lr2SongDbSyncProgress?>();
        var afterCommit = new List<Lr2SongDbSyncProgress?>();
        var logs = new ConcurrentQueue<string>();
        Func<CatalogChartInfoWriteRequest, CatalogChartInfoWriteReceipt> writer = CreateDirectChartInfoWriter(songDb);
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            LogInstallPerformance = logs.Enqueue,
            Signature = "real-chunk-progress",
            RunId = "real-chunk-progress",
            SongRows = songs,
            ChartFileBufferReader = path => new ChartFileReadBuffer(path,
                Encoding.ASCII.GetBytes("#PLAYER 1\r\n#TITLE Current " + Path.GetFileNameWithoutExtension(path)
                    + "\r\n#BPM 120\r\n#WAV01 sound.wav\r\n#00111:01\r\n"),
                new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc)),
            ChartInfoChunkWriter = request =>
            {
                beforeCommit.Add(Volatile.Read(ref latest));
                return writer(request);
            },
            ChartInfoRowsCommitted = _ => afterCommit.Add(Volatile.Read(ref latest)),
            ProgressReporter = progress =>
            {
                events.Enqueue(progress);
                Volatile.Write(ref latest, progress);
            }
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(targetCount, result.SongRowProcessedCount);
        Assert.AreEqual((targetCount + 999) / 1000, beforeCommit.Count);
        string[] chunkStarts = [.. logs.Where(line => line.Contains(" chunk_start "))];
        Assert.AreEqual(beforeCommit.Count, chunkStarts.Length);
        for (int chunk = 0; chunk < chunkStarts.Length; chunk++)
        {
            StringAssert.Contains(chunkStarts[chunk], " offset=" + (chunk * 1000) + " count=" + Math.Min(1000, targetCount - chunk * 1000));
        }
        Assert.AreEqual(beforeCommit.Count, afterCommit.Count);
        foreach (Lr2SongDbSyncProgress? progress in beforeCommit.Concat(afterCommit))
        {
            Assert.IsNotNull(progress);
            Assert.AreEqual("song_rows", progress.Stage);
            Assert.AreEqual(targetCount, progress.StageTotalCount);
            Assert.IsTrue(progress.StageProcessedCount >= 0 && progress.StageProcessedCount <= targetCount);
        }
        Lr2SongDbSyncProgress[] songEvents = [.. events.Where(progress => progress.Stage == "song_rows")];
        Assert.IsTrue(songEvents.All(progress => progress.StageTotalCount == targetCount
            && progress.StageProcessedCount >= 0 && progress.StageProcessedCount <= targetCount));
        Assert.IsTrue(songEvents.Any(progress => progress.StageProcessedCount == targetCount));
        int[] expectedCursors = [.. Enumerable.Range(0, beforeCommit.Count + 1).Select(chunk => Math.Min(chunk * 1000, targetCount))];
        CollectionAssert.AreEqual(expectedCursors, songEvents.Select(progress => progress.ProcessedCursor).Distinct().ToArray());
        Assert.AreEqual(targetCount, result.TotalCount);
        Assert.AreEqual(targetCount, result.ProcessedCount);
        Assert.AreEqual(0, result.SongRowSkippedCount);
        LR2SongDBExtended.lr2_song_db_sync_status durable = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual(targetCount, durable.total_count);
        Assert.AreEqual(targetCount, durable.processed_cursor);
        Assert.AreEqual("Running", durable.status);
        Assert.IsNull(durable.completed_at);
        Assert.IsFalse(events.Any(progress => progress.Stage == "song_rows_saving"));
        using var observation = new SQLite.SQLiteConnection(scope.SongDbPath,
            SQLite.SQLiteOpenFlags.ReadOnly | SQLite.SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        Assert.AreEqual(targetCount, observation.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info;"));
        for (int index = 0; index < targetCount; index++)
        {
            LR2SongDB.song stored = observation.Find<LR2SongDB.song>(songs[index].Path);
            Assert.AreEqual("Current " + index, stored.title);
            Assert.AreEqual("User tag", stored.tag);
            Assert.AreEqual(7, stored.favorite);
        }
    }

    /// <summary>
    /// 進捗購読先が例外を投げてもフォルダ反映と生成成功・確定cursorを保ち、全体の完了保存を先取りしたり通知失敗で確定結果を失敗へ変えたりしない。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_FolderReconciliationProgressReporterFailureDoesNotChangeResult()
    {
        using var scope = TestDatabaseScope.Create();
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
        Assert.AreEqual("Running", completed.status);
        Assert.IsNull(completed.completed_at);
        Assert.AreEqual(completed.total_count, completed.processed_cursor);
    }

    /// <summary>
    /// 空入力の生成に成功しても有件数の段階通知を出さず、存在しない作業量を表示する誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SyncService_EmptyInputOmitsCountedStages(bool withFolder)
    {
        using var scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        var progressEvents = new List<Lr2SongDbSyncProgress>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            RootDirectories = withFolder ? [scope.DirectoryPath] : [],
            NormalFolderDirectoryPaths = withFolder ? [scope.DirectoryPath] : [],
            DirectoryEntries = withFolder ? CreateDirectoryEntryMap(scope.DirectoryPath) : [],
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
        Assert.IsTrue(progressEvents.Any(progress => progress.Stage == "final_validation"));
        Assert.AreEqual(withFolder ? 1 : 0, result.TotalCount);
        Assert.AreEqual(result.TotalCount, result.ProcessedCount);
        Assert.IsFalse(progressEvents.Any(progress => progress.Stage.StartsWith("song_rows", StringComparison.Ordinal)));
        if (withFolder)
        {
            Assert.IsNotNull(songDb.Find<LR2SongDB.folder>(ToFolderPath(scope.DirectoryPath)));
        }
        else
        {
            Assert.IsFalse(progressEvents.Any(progress => progress.StageTotalCount > 0));
            foreach (string stage in new[] { "lr2folder_files", "directory_metadata", "normal_folder_roots", "normal_folders",
            "folder_projection_candidates", "custom_folder_rows", "custom_folder_parents", "folder_reconciliation",
            "folder_reconciliation_completed", "song_rows", "song_rows_completed" })
            {
                Assert.IsFalse(progressEvents.Any(progress => progress.Stage == stage), stage);
            }
            Assert.AreEqual(0, result.FolderTableReconciliationResult.GeneratedCount);
        }
    }

    /// <summary>
    /// 全同期の入力にない既存楽曲も削除せず、解析・フォルダ同期が所持集合を変更する誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_FullStageDoesNotDeleteExistingSongMembership()
    {
        using var scope = TestDatabaseScope.Create();
        string stalePath = Path.Combine(scope.DirectoryPath, "stale.bms");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        ChartFile existing = ((ChartTestValues.Empty() with
        {
            Path = stalePath,
            Tag = "user-owned"
        })) with
        { Md5 = "11111111111111111111111111111111", Favorite = 7 };
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(existing), typeof(LR2SongDB.song));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "full-membership-delete",
            RunId = "full-membership-delete",
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.IsNotNull(songDb.Find<LR2SongDB.song>(stalePath));
    }

    /// <summary>
    /// 全同期へ渡した未所持楽曲を新規保存せず、解析補完が所持集合へ楽曲を追加する誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_FullStageDoesNotInsertSongMembership()
    {
        using var scope = TestDatabaseScope.Create();
        string chartPath = Path.Combine(scope.DirectoryPath, "new.bms");
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = chartPath
        };
        file = file with { Title = "New chart", RawTitle = "New chart" };
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

    /// <summary>
    /// 保存時刻と同じ定義でも実内容を再読して現在タイトルへ更新し、時刻一致だけで古い定義を保持する誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_FullStageReparsesSameMtimeLr2FolderContent()
    {
        using var scope = TestDatabaseScope.Create();
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

    /// <summary>
    /// 捕捉したエポック時刻を0として保存し、未取得時刻と混同して実行時刻へ置換する誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_FullStagePreservesEpochFolderDate()
    {
        using var scope = TestDatabaseScope.Create();
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

    /// <summary>
    /// コピーしたDBで実譜面からタイトルとMD5を更新してもお気に入り・追加時刻・タグを保持し、複製環境での利用者設定消失を検出する。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_PreservesUserColumnsWhenRunningOnCopiedSongDb()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE Copied User Columns\r\n#ARTIST Parsed Artist\r\n#00111:01\r\n", Encoding.ASCII);
        ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        ChartFile file = CreateSyncTestFile(chartPath, chartSnapshot);

        using (var sourceDb = new LR2SongDBExtended(scope.SongDbPath))
        {
            sourceDb.CreateTable<LR2SongDB.song>();
            ChartFile existing = ((ChartTestValues.Empty() with
            {
                Path = chartPath,
                AddDate = 123456,
                Tag = "copied-user-tag"
            })) with
            { Md5 = "cccccccccccccccccccccccccccccccc", Favorite = 5 };
            existing = existing with { Title = "Old Copied Title", RawTitle = "Old Copied Title" };
            sourceDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(existing), typeof(LR2SongDB.song));
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

    /// <summary>
    /// 譜面が読めない場合は失敗件数を報告し既存タイトル・解析列・文字コード指定を保持しながらフォルダとtxtを更新して、欠落入力による解析列破損と指定失効を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_FallsBackToSongCopyWhenChartSnapshotCannotBeRead()
    {
        using var scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Missing");
        Directory.CreateDirectory(songDirectory);
        string missingChartPath = Path.Combine(songDirectory, "missing.bms");
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = missingChartPath
        };
        file = file with { Title = "Existing Title", RawTitle = "Existing Title" };
        file = file with { Artist = "Existing Artist", RawArtist = "Existing Artist" };
        file = file with { Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" };
        file = file with { Sha256 = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff" };
        file = file with { Txt = 1 };
        file = file with { Folder = "stale-folder" };
        LR2SongDB.song capturedStorageRow = ChartSongStorageMapping.ToBmsRow(file);
        capturedStorageRow.parent = "stale-parent";
        capturedStorageRow.maxbpm = 314;
        capturedStorageRow.minbpm = 157;
        capturedStorageRow.karinotes = 2718;
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(capturedStorageRow, typeof(LR2SongDB.song));
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.InsertOrReplace(new LR2SongDBExtended.maintenance
        { path = missingChartPath, hash = file.Md5, encoding = "big5", is_encoding_fixed = true });

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
        Assert.AreEqual(314, songDb.ExecuteScalar<int>("SELECT maxbpm FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual(157, songDb.ExecuteScalar<int>("SELECT minbpm FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual(2718, songDb.ExecuteScalar<int>("SELECT karinotes FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual("big5", songDb.ExecuteScalar<string>("SELECT encoding FROM maintenance WHERE path = ?;", missingChartPath));
        Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT is_encoding_fixed FROM maintenance WHERE path = ?;", missingChartPath));
    }

    /// <summary>
    /// エポック直前の譜面更新時刻を-1として保存して生成に成功し、負値時刻の補正や失敗扱いを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_PreservesNegativeSongDate()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "negative-date.bms");
        File.WriteAllText(chartPath, "#TITLE Negative Date\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
        DateTime preEpoch = new(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(chartPath, preEpoch);
        ChartFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));

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
        Assert.AreEqual("Running", row.status);
        Assert.IsNull(row.completed_at);
    }

    /// <summary>
    /// スナップショット未取得時のnull更新時刻を保存して生成に成功し、未取得値の推測補完を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_PreservesNullSongDate()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "null-date.bms");
        File.WriteAllText(chartPath, "#TITLE Null Date\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
        ChartFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        file = file with { Date = null };
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));

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
        Assert.AreEqual("Running", row.status);
        Assert.IsNull(row.completed_at);
    }

    /// <summary>
    /// 生成対象にない旧通常フォルダ行を全同期で削除し、旧形式行を除去対象から漏らす誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_DeletesLegacyNormalFolderRowWhenNotExpected()
    {
        using var scope = TestDatabaseScope.Create();
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
        Assert.AreEqual("Running", row.status);
        Assert.IsNull(row.completed_at);
        string legacyFolderPath = ToFolderPath(legacyDirectory);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == legacyFolderPath));
    }

    /// <summary>
    /// 二行目のSQL失敗でチャンク全体を戻して失敗位置を保存し、原因除去後の再実行で両行を更新して部分確定や再試行漏れを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_RollsBackFailedSongRowChunkAndRetriesFromChunkStart()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "RollbackSongRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string firstPath = Path.Combine(songDirectory, "first.bms");
        string secondPath = Path.Combine(songDirectory, "second.bms");
        File.WriteAllText(firstPath, "#TITLE first rollback\r\n", Encoding.ASCII);
        File.WriteAllText(secondPath, "#TITLE second rollback\r\n", Encoding.ASCII);
        ChartFile firstFile = CreateSyncTestFile(firstPath, ChartFileContentReader.ReadSnapshot(firstPath));
        ChartFile secondFile = CreateSyncTestFile(secondPath, ChartFileContentReader.ReadSnapshot(secondPath));
        firstFile = firstFile with { Title = "stale first", RawTitle = "stale first" };
        secondFile = secondFile with { Title = "stale second", RawTitle = "stale second" };
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(firstFile), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(secondFile), typeof(LR2SongDB.song));
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
        Assert.AreEqual("Running", completed.status);
        Assert.IsNull(completed.completed_at);
        Assert.AreEqual(4, completed.processed_cursor);
    }

    /// <summary>
    /// BOMなし・ありのUTF-8譜面を実判定で解析し、先頭タイトルと作者のSQLite保存・確定通知へ文字化けを渡す誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SyncService_ParsesSongRowsWithDetectedUtf8Encoding(bool withBom)
    {
        using var scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Utf8");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "utf8.bms");
        File.WriteAllText(chartPath, "#TITLE 解析タイトル\r\n#ARTIST 解析アーティスト\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: withBom));
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = chartPath
        };
        file = file with { Md5 = snapshot.Md5 };
        file = file with { Sha256 = snapshot.Sha256 };
        file = file with { Title = "Stale Title", RawTitle = "Stale Title" };
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
        var notifiedTitles = new List<string>();

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "utf8-song",
            RunId = "utf8-song",
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            Lr2CompatibilityFactsCommitted = _ => notifiedTitles.Add(songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath)),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(0, result.SongRowParseFailureCount);
        Assert.AreEqual("解析タイトル", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual("解析アーティスト", songDb.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", chartPath));
        CollectionAssert.AreEqual(new[] { "解析タイトル" }, notifiedTitles);
    }

    /// <summary>同内容の確定Big5・UTF-8・CP932だけを生5項目へ採用し、混在CP932資源の長さ・警告と単回読取りを保ちます。</summary>
    /// <remarks>実RunとSQLite保存を境界とし、期待文字列は固定入力から定めます。指定の由来による分岐は設けません。</remarks>
    [TestMethod]
    [DataRow("big5", "中文標題", "副標題", "作者", "編曲", "音樂")]
    [DataRow("utf-8", "日本語題名", "副題", "作曲者", "編曲者", "ジャンル")]
    [DataRow("shift_jis", "日本語題名", "副題", "作曲者", "編曲者", "ジャンル")]
    public void SyncService_AppliesFixedEncodingWithIndependentCp932Facts(
        string encoding, string title, string subtitle, string artist, string subartist, string genre)
    {
        using var scope = TestDatabaseScope.Create();
        string path = Path.Combine(scope.DirectoryPath, "fixed.bms");
        byte[] metadata = Encoding.GetEncoding(encoding).GetBytes(
            $"#PLAYER 1\r\n#TITLE {title}\r\n#SUBTITLE {subtitle}\r\n#ARTIST {artist}\r\n#SUBARTIST {subartist}\r\n#GENRE {genre}\r\n#BPM 120\r\n");
        File.WriteAllBytes(path, [.. metadata, .. Encoding.GetEncoding(932).GetBytes("#WAV01 ソ.wav\r\n#00111:01\r\n")]);
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(path);
        ChartFile file = ChartFileProjection.WithMaintenance(CreateSyncTestFile(path, snapshot), new ResourceHealthMaintenanceSnapshot
        {
            Path = path,
            Hash = snapshot.Md5.ToUpperInvariant(),
            Encoding = encoding,
            EncodingFixed = true
        });
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        // 全体同期は所持済みsongを更新するため、入力と同じ保存行を先に用意します。
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.InsertOrReplace(new LR2SongDBExtended.maintenance
        { path = path, hash = snapshot.Md5.ToUpperInvariant(), encoding = encoding, is_encoding_fixed = true });
        int reads = 0;

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "fixed-metadata",
            RunId = "fixed-metadata",
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            ChartFileBufferReader = requestedPath =>
            {
                Interlocked.Increment(ref reads);
                return ChartFileContentReader.ReadBuffer(requestedPath);
            }
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(0, result.SongRowParseFailureCount);
        Assert.AreEqual(1, reads);
        using LR2SongDBExtended read = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
        LR2SongDB.song saved = read.Table<LR2SongDB.song>().Single();
        Assert.AreEqual(title, saved.title);
        Assert.AreEqual(subtitle, saved.subtitle);
        Assert.AreEqual(artist, saved.artist);
        Assert.AreEqual(subartist, saved.subartist);
        Assert.AreEqual(genre, saved.genre);
        Assert.AreEqual(6, read.ExecuteScalar<int>("SELECT lr2_resource_max_relative_cp932_bytes FROM maintenance WHERE path = ?;", path));
        Assert.AreEqual(0, read.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", path));
        Assert.AreEqual(encoding, read.ExecuteScalar<string>("SELECT encoding FROM maintenance WHERE path = ?;", path));
        Assert.AreEqual(1, read.ExecuteScalar<int>("SELECT is_encoding_fixed FROM maintenance WHERE path = ?;", path));
    }

    /// <summary>未指定・非具体名・未確定・別path・別hashではBOMなしUTF-8の自動検出を保ち、古い指定を実読取りへ誤適用しません。</summary>
    /// <remarks>条件差は実Runの保存生5列へ集約します。BOM有無の検出は既存DetectedUtf8系列、hash変更後の非復活はLibraryのDB再読込みケースが分担します。</remarks>
    [TestMethod]
    [DataRow("absent")]
    [DataRow("empty")]
    [DataRow("unknown")]
    [DataRow("estimated")]
    [DataRow("not-fixed")]
    [DataRow("other-path")]
    [DataRow("path-case")]
    [DataRow("other-hash")]
    public void SyncService_DetectsEncodingUnlessConcreteFixedIdentityMatches(string condition)
    {
        using var scope = TestDatabaseScope.Create();
        string path = Path.Combine(scope.DirectoryPath, "metadata.bms");
        // BOMによる強制UTF-8復号で誤指定が隠れないようにし、Big5・CP932で誤復号した場合は固定literalと異なる入力にします。
        File.WriteAllText(path, "#TITLE 日本語題名\r\n#SUBTITLE 副題\r\n#ARTIST 作曲者\r\n#SUBARTIST 編曲者\r\n#GENRE ジャンル\r\n", new UTF8Encoding(false));
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(path);
        ChartFile file = CreateSyncTestFile(path, snapshot);
        if (condition != "absent")
        {
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot
            {
                Path = condition == "other-path" ? path + ".other" : condition == "path-case" ? path.ToUpperInvariant() : path,
                Hash = condition == "other-hash" ? new string('a', 32) : snapshot.Md5,
                Encoding = condition switch { "empty" => "", "unknown" => "UnKnOwN", "estimated" => "shift_jis?", _ => "big5" },
                EncodingFixed = condition != "not-fixed"
            });
        }
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        // 全体同期の生成列更新は既存songだけを対象にします。
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "encoding-detection",
            RunId = "encoding-detection",
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb)
        });

        Assert.AreEqual(0, result.SongRowParseFailureCount);
        using LR2SongDBExtended read = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
        LR2SongDB.song saved = read.Table<LR2SongDB.song>().Single();
        Assert.AreEqual("日本語題名", saved.title);
        Assert.AreEqual("副題", saved.subtitle);
        Assert.AreEqual("作曲者", saved.artist);
        Assert.AreEqual("編曲者", saved.subartist);
        Assert.AreEqual("ジャンル", saved.genre);
    }

    /// <summary>
    /// 最新・旧版・MD5不一致の解析入力を区別し、最新結果を採用し他を再解析しながら各譜面の読取りを一度に抑える。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_UsesResolverFactsAndReadsEachSongRowSnapshotOnce()
    {
        using var scope = TestDatabaseScope.Create();
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
        ChartFile currentFile = CreateSyncTestFile(currentPath, currentSnapshot);
        ChartFile staleFile = CreateSyncTestFile(stalePath, staleSnapshot);
        ChartFile mismatchFile = CreateSyncTestFile(mismatchPath, mismatchSnapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        BeMusicSeeker.Models.ChartDetails currentInfo = CreateChartInfo(currentSnapshot.Sha256, currentSnapshot.Md5, level: 77);
        BeMusicSeeker.Models.ChartDetails staleInfo = CreateChartInfo(staleSnapshot.Sha256, staleSnapshot.Md5, level: 99, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1);
        BeMusicSeeker.Models.ChartDetails mismatchInfo = CreateChartInfo(mismatchSnapshot.Sha256, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", level: 99);
        songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(currentInfo), typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(staleInfo), typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(mismatchInfo), typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(currentFile), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(staleFile), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(mismatchFile), typeof(LR2SongDB.song));
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

    /// <summary>
    /// 現在版の解析失敗が既知の楽曲は再解析・解析保存通知を出さず、失敗入力を毎回処理し直す誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_SkipsChartInfoParseWhenCurrentParseFailureIsKnown()
    {
        using var scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfoFailureSkip");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "failure-skip.bms");
        File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE failure skip\r\n#BPM 150\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        ChartFile file = CreateSyncTestFile(chartPath, snapshot);
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
            ChartInfoParseFailuresCommitted = (_, _, _) => parseFailureCallbackCalled = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(0, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        Assert.IsFalse(chartInfoCallbackCalled);
        Assert.IsFalse(parseFailureCallbackCalled);
    }

    /// <summary>
    /// 要求で渡された最新解析値を既知失敗より優先してsongへ反映し、提供済み結果を無視する誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_UsesRequestChartInfoResolverWithoutChartInfoDbLookup()
    {
        using var scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfoResolver");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "resolver.bms");
        File.WriteAllText(chartPath, "#TITLE resolver\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        ChartFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));

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

    /// <summary>
    /// 要求窓口が旧版解析行を返しても実譜面から最新結果を保存し、古い解析値の採用を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_RebuildsChartInfoWhenRequestResolverReturnsStaleRow()
    {
        using var scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "StaleResolver");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "stale-resolver.bms");
        File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE stale resolver\r\n#BPM 150\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        ChartFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));

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
        BeMusicSeeker.Models.ChartDetails chartInfo = songDb.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", snapshot.Sha256).Single();
        Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, chartInfo.parser_version);
        Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COALESCE(karinotes, -1) FROM song WHERE path = ?;", chartPath));
    }

    /// <summary>
    /// 開始時の想定外取消を例外と失敗位置として保存し、処理を完了と誤報することを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_UnexpectedCancellationMarksFailedStatus()
    {
        using var scope = TestDatabaseScope.Create();
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
        Assert.AreEqual("folder_projection_preparation", row.stage);
        Assert.AreEqual(0, row.processed_cursor);
        Assert.AreEqual(0, row.total_count);
    }

    /// <summary>
    /// フォルダ反映トランザクション中の終了取消で全行を元へ戻し、終了時の部分確定を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_ShutdownCancellationAtFolderCommitRollsBackWholeTable()
    {
        using var scope = TestDatabaseScope.Create();
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

    /// <summary>
    /// 解析保存後の終了flagとtoken取消の順にかかわらず、チャンクを巻き戻して正規の終了取消を返し、終了時の部分保存とFailed保存を防ぐ。
    /// </summary>
    /// <remarks>起動時と同じ既設スキーマ、同じ1譜面と実SQLite writerで、保存後のflag＋即時token取消／flagのみを分担する。flagのみではservice終端までtokenを取消せず、実transaction終端と必須確定callback不在を確認する。</remarks>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SyncService_ShutdownCancellationAtSongCommitRollsBackWholeChunk(bool cancelTokenImmediately)
    {
        using var scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "SongCommitBarrier");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Current song title\r\n#ARTIST Current artist\r\n#BPM 120\r\n#WAV01 sound.wav\r\n#00111:01\r\n", Encoding.ASCII);
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        ChartFile file = CreateSyncTestFile(chartPath, snapshot);
        file = file with { Title = "Durable song before shutdown", RawTitle = "Durable song before shutdown" };
        file = file with { Artist = "Stale artist before shutdown", RawArtist = "Stale artist before shutdown" };
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));

        using var cancellation = new CancellationTokenSource();
        bool shutdownRequested = false;
        int chartInfoWriterCallCount = 0;
        int chartInfoRowsWrittenBeforeShutdown = 0;
        int committedCallbackCount = 0;
        OperationCanceledException failure = Assert.ThrowsException<OperationCanceledException>(() => Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "song-commit-shutdown",
            RunId = "song-commit-shutdown",
            SongRows = [file],
            CancellationToken = cancellation.Token,
            IsShutdownRequested = () => shutdownRequested,
            ChartInfoChunkWriter = request =>
            {
                chartInfoWriterCallCount++;
                CatalogChartInfoWriteReceipt receipt = CreateDirectChartInfoWriter(songDb)(request);
                chartInfoRowsWrittenBeforeShutdown = songDb.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM chart_info WHERE sha256 = ?;", snapshot.Sha256);
                shutdownRequested = true;
                if (cancelTokenImmediately)
                {
                    cancellation.Cancel();
                }
                return receipt;
            },
            ChartInfoRowsCommitted = _ => Interlocked.Increment(ref committedCallbackCount),
            ChartInfoParseFailuresCommitted = (_, _, _) => Interlocked.Increment(ref committedCallbackCount),
            Lr2CompatibilityFactsCommitted = _ => Interlocked.Increment(ref committedCallbackCount),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        }));

        Assert.IsTrue(shutdownRequested);
        Assert.AreEqual(cancellation.Token, failure.CancellationToken);
        Assert.AreEqual(cancelTokenImmediately, cancellation.IsCancellationRequested,
            "flagのみの正規終了は、要求tokenが未取消のままserviceから返る。");
        Assert.AreEqual(1, chartInfoWriterCallCount);
        Assert.AreEqual(1, chartInfoRowsWrittenBeforeShutdown);
        Assert.AreEqual(0, committedCallbackCount);
        Assert.AreEqual(1, raw.sqlite3_get_autocommit(songDb.Handle));
        Assert.AreEqual("Durable song before shutdown", songDb.ExecuteScalar<string>(
            "SELECT title FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual("Stale artist before shutdown", songDb.ExecuteScalar<string>(
            "SELECT artist FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_info WHERE sha256 = ?;", snapshot.Sha256));
        LR2SongDBExtended.lr2_song_db_sync_status status = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual(0, status.processed_cursor);
        Assert.IsNull(status.completed_at);
        Assert.AreNotEqual("Failed", status.status);
        Assert.AreNotEqual("Completed", status.status);
    }

    /// <summary>
    /// フォルダ反映後の想定外取消は楽曲段階の失敗位置を保存し、次の実行で楽曲も完了して中断位置の誤再利用を防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_UnexpectedCancellationFailsAndRetryStartsFromZero()
    {
        using var scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "CancelRetryRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE cancel retry\r\n#00111:01\r\n", Encoding.ASCII);
        ChartFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
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
        Assert.AreEqual("Running", completed.status);
        Assert.IsNull(completed.completed_at);
        Assert.AreEqual(3, completed.processed_cursor);
        Assert.AreEqual(3, completed.total_count);
    }

    /// <summary>
    /// 実参照のLR2長さ警告を保存・通知しても既存の音声健康件数を保持し、互換情報更新による健康情報上書きを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_UpsertsLr2CompatibilityFactsWithoutReplacingMaintenanceHealth()
    {
        using var scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Lr2Compatibility");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        WriteBasicBms(chartPath, "lr2 compatibility", CreateLr2TooLongResourcePath());
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        ChartFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = chartPath, hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", wav_files_defined = 99, wav_files_existing = 88 }, typeof(LR2SongDBExtended.maintenance));

        var committedCompatibilityFacts = new List<ResourceHealthMaintenanceSnapshot>();
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
        Assert.AreEqual(chartPath, committedCompatibilityFacts[0].Path);
        Assert.AreEqual(file.Md5, songDb.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(99, songDb.ExecuteScalar<int>("SELECT wav_files_defined FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(88, songDb.ExecuteScalar<int>("SELECT wav_files_existing FROM maintenance WHERE path = ?;", chartPath));
        int flags = songDb.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", chartPath);
        Assert.IsTrue((flags & (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0);
    }

    /// <summary>内容変更の指定失効後に同じチャンクのcursor保存が失敗しても、旧hashと指定をまとめて巻き戻します。</summary>
    /// <remarks>実SQLiteのcursor更新だけをtriggerで失敗させ、互換性更新後の失敗を観測します。読取不能時の非失効は既存の旧生成列保持ケースへ分担します。</remarks>
    [TestMethod]
    public void SyncService_RollsBackEncodingExpiryWhenChunkCommitFails()
    {
        using var scope = TestDatabaseScope.Create();
        string path = Path.Combine(scope.DirectoryPath, "changed.bms");
        WriteBasicBms(path, "changed");
        ChartFile file = CreateSyncTestFile(path, ChartFileContentReader.ReadSnapshot(path));
        const string oldHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        BmsLibraryDbGateway.EnsureLr2SongDbSyncStatusSchema(songDb);
        songDb.InsertOrReplace(new LR2SongDBExtended.maintenance
        { path = path, hash = oldHash, encoding = "big5", is_encoding_fixed = true, wav_files_defined = 99, wav_files_existing = 88 });
        songDb.Execute("CREATE TRIGGER fail_song_cursor BEFORE INSERT ON lr2_song_db_sync_status "
            + "WHEN NEW.stage = 'song_rows' AND NEW.processed_cursor > 0 BEGIN SELECT RAISE(ABORT, 'chunk cursor failed'); END;");

        Assert.ThrowsException<SQLite.SQLiteException>(() => Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "encoding-expiry-rollback",
            RunId = "encoding-expiry-rollback",
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb)
        }));

        using LR2SongDBExtended read = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
        LR2SongDBExtended.maintenance saved = read.Table<LR2SongDBExtended.maintenance>().Single();
        Assert.AreEqual(oldHash, saved.hash);
        Assert.AreEqual("big5", saved.encoding);
        Assert.IsTrue(saved.is_encoding_fixed);
        Assert.AreEqual(99, saved.wav_files_defined);
        Assert.AreEqual(88, saved.wav_files_existing);
        Assert.AreEqual(0, read.Table<LR2SongDB.song>().Count());
    }

    /// <summary>
    /// 大文字小文字が違う既存行を残して完全一致パスへ互換情報を保存し、別保存行の情報を奪う誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_UpsertsLr2CompatibilityFactsByExactPath()
    {
        using var scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Lr2CompatibilityExact");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        WriteBasicBms(chartPath, "lr2 compatibility exact", CreateLr2TooLongResourcePath());
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        ChartFile file = CreateSyncTestFile(chartPath, snapshot);
        string existingPath = Path.Combine(songDirectory, "CHART.BMS");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = existingPath, hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, typeof(LR2SongDBExtended.maintenance));

        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "lr2-compatibility-exact",
            RunId = "lr2-compatibility-exact",
            SongRows = [file],
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowLr2CompatibilityAppliedCount);
        Assert.AreEqual(2, songDb.Table<LR2SongDBExtended.maintenance>().Count());
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", songDb.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?;", existingPath));
        Assert.AreEqual(file.Md5, songDb.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?;", chartPath));
        int flags = songDb.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", chartPath);
        Assert.IsTrue((flags & (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0);
    }

    /// <summary>
    /// 譜面未取得時は新しい互換情報の反映を行わず既存警告・参照長・親参照・健康件数を保持する。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_PreservesLr2CompatibilityFactsWhenSongRowFallsBackWithoutSnapshot()
    {
        using var scope = TestDatabaseScope.Create();
        string chartPath = Path.Combine(scope.DirectoryPath, "Missing", "chart.bms");
        ChartFile file = ((ChartTestValues.Empty() with
        {
            Path = chartPath,
            Date = 123456
        })) with
        { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Favorite = null };
        file = file with { Title = "fallback row", RawTitle = "fallback row" };
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        int existingFlags = (int)(Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported
            | Lr2CompatibilityWarningFlags.ResourcePathTooLong);
        songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = chartPath, hash = file.Md5, wav_files_defined = 7, wav_files_existing = 6, lr2_warning_flags = existingFlags, lr2_resource_max_relative_cp932_bytes = 120, lr2_resource_has_parent_traversal = true }, typeof(LR2SongDBExtended.maintenance));

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

    /// <summary>
    /// 完了した列挙にないカスタム定義の保存行を削除して完了し、不在定義を保存集合へ残す誤りを検出する。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_DeletesMissingDiscoveredLr2FolderRow()
    {
        using var scope = TestDatabaseScope.Create();
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

    /// <summary>
    /// 定義ファイルの現在時刻が変わっても捕捉した列挙時刻を行へ保存し、同期中の追加読取りで入力時点を変える誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_UsesEnumeratedLr2FolderTimestampForGeneratedRow()
    {
        using var scope = TestDatabaseScope.Create();
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

    /// <summary>
    /// 譜面ルート外のLR2組込み定義も相対パスの親分類行と共に反映し、組込み定義の親欠落を検出する。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_IncludesBuiltinLr2FolderParent()
    {
        using var scope = TestDatabaseScope.Create();
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

    /// <summary>
    /// ディレクトリの現在時刻が変わっても捕捉済み時刻を通常行へ保存し、同期中に入力を取り直す誤りを防ぐ。
    /// </summary>
    /// <remarks>実SQLiteへの同期と状態保存を観測し、入力準備・解析・確定通知と保存結果の接続を確認する。</remarks>
    [TestMethod]
    public void SyncService_UsesEnumeratedDirectoryTimestampForNormalFolderRow()
    {
        using var scope = TestDatabaseScope.Create();
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

    /// <summary>詳細の未知解析故障は基本行生成と入力順の確定を止めず、正常詳細・利用者列・全開始済み健全解析の実終端を保持する。</summary>
    /// <remarks>実4譜面・基本解析・SQLiteを使い、最後の健全詳細解析をgateで保持する。同期fixtureはDBのprocess lockを同threadで所有し、全Task終端後に解放する。</remarks>
    [TestMethod]
    public void SyncService_UnknownDetailFailurePreservesBasicUserValuesAndOrderedCompletion()
    {
        using var scope = TestDatabaseScope.Create();
        using var releaseHealthy = new ManualResetEventSlim();
        var healthyEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ChartFile> charts = [];
        for (int index = 0; index < 4; index++)
        {
            string path = Path.Combine(scope.DirectoryPath, index + ".bms");
            File.WriteAllText(path, "#TITLE detail" + index + "\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            charts.Add(BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)) with { Level = 2, Title = "old" });
        }
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        songDb.RunInTransaction(() =>
        {
            foreach (ChartFile chart in charts)
            {
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song));
            }
            songDb.Execute("UPDATE song SET favorite = 3, tag = 'keep', adddate = 123;");
        });
        int activeParses = 0;
        var parser = new ChartInfoBuildService(null, parseBytes: (bytes, path, md5, sha256, timeout) =>
        {
            Interlocked.Increment(ref activeParses);
            try
            {
                if (path == charts[1].Path) { throw new InvalidOperationException("injected detail parser fault"); }
                if (path == charts[3].Path)
                {
                    healthyEntered.TrySetResult(true);
                    releaseHealthy.Wait();
                }
                return ChartInfoParser.ParseBytesDetailed(bytes, path, md5, sha256, timeout: timeout, retainChartString: false);
            }
            finally { Interlocked.Decrement(ref activeParses); }
        });
        var request = new Lr2SongDbSyncRequest
        {
            Signature = "detail-input-skip",
            RunId = "detail-input-skip",
            SongRows = charts,
            ChartInfoBuildService = parser,
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            ChartInfoResolver = _ => null,
            ChartInfoResolverIsThreadSafe = true,
            IsShutdownRequested = () => false
        };
        Task<Lr2SongDbSyncResult> operation = Task.Run(() => Lr2SongDbSyncService.Run(songDb, request));
        try
        {
            // 通常到達は局所期限を設けず、操作の早期終了だけを識別します。
            Task completed = Task.WhenAny(healthyEntered.Task, operation).GetAwaiter().GetResult();
            if (completed == operation)
            {
                operation.GetAwaiter().GetResult();
                Assert.Fail("健全詳細解析のgate到達前に操作が完了しました。");
            }
            healthyEntered.Task.GetAwaiter().GetResult();
            Assert.IsFalse(operation.IsCompleted, "詳細skip後も開始済み健全解析の実終端を待ちます。");
            releaseHealthy.Set();
            Lr2SongDbSyncResult result = operation.GetAwaiter().GetResult();
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
            Assert.AreEqual(4, result.SongRowProcessedCount);
            Assert.AreEqual(0, result.SongRowSkippedCount);
            Assert.AreEqual(3, result.SongRowChartInfoAppliedCount);
            Assert.AreEqual(0, activeParses);
            using var verify = new LR2SongDBExtended(scope.SongDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(4, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE level = 9 AND favorite = 3 AND tag = 'keep' AND adddate = 123;"));
            for (int index = 0; index < charts.Count; index++)
            {
                Assert.AreEqual("detail" + index, verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", charts[index].Path));
            }
            Assert.AreEqual(3, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", charts[1].Sha256));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
            Assert.AreEqual(4, verify.ExecuteScalar<int>("SELECT processed_cursor FROM lr2_song_db_sync_status WHERE name = ?;", Lr2SongDbSyncStatusService.DefaultStatusName));
        }
        finally
        {
            releaseHealthy.Set();
            try { operation.GetAwaiter().GetResult(); }
            catch (Exception) { /* 一次表明を保持し、全Task終端後にDBを解放します。 */ }
        }
    }

    /// <summary>基本読取りの未知失敗は今回だけ対象を飛ばし、旧基本値・利用者列と正常対象の保存、全処理位置、次の明示同期での再評価を保つ。</summary>
    /// <remarks>少数の実譜面を実SQLiteと基本解析・writerへ通す。既存reader代替は未知読取り失敗と開始済み健全処理の待合せだけに使い、raw失敗表と保存行を外側から観測する。</remarks>
    [TestMethod]
    public void SyncService_UnknownBasicReadFailurePreservesOldRowAndCompletesNormalSiblings()
    {
        using var scope = TestDatabaseScope.Create();
        using var releaseHealthy = new ManualResetEventSlim();
        var healthyEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ChartFile> charts = [];
        for (int index = 0; index < 4; index++)
        {
            string path = Path.Combine(scope.DirectoryPath, "basic" + index + ".bms");
            File.WriteAllText(path, "#TITLE basic" + index + "\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            charts.Add(CreateSyncTestFile(path, ChartFileContentReader.ReadSnapshot(path)) with
            {
                Level = 2,
                Title = "old",
                RawTitle = "old"
            });
        }
        int ownerThread = Environment.CurrentManagedThreadId;
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        songDb.RunInTransaction(() =>
        {
            foreach (ChartFile chart in charts)
            {
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song));
            }
            songDb.Execute("UPDATE song SET favorite = 3, tag = 'keep', adddate = 123;");
        });
        int activeReaders = 0;
        var request = new Lr2SongDbSyncRequest
        {
            Signature = "basic-input-skip",
            RunId = "basic-input-skip",
            SongRows = charts,
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            ChartInfoResolver = _ => null,
            ChartInfoResolverIsThreadSafe = true,
            IsShutdownRequested = () => false,
            ChartFileBufferReader = path =>
            {
                Interlocked.Increment(ref activeReaders);
                try
                {
                    if (path == charts[1].Path) { throw new InvalidOperationException("unknown basic read failure"); }
                    if (path == charts[3].Path)
                    {
                        healthyEntered.TrySetResult(true);
                        releaseHealthy.Wait();
                    }
                    return ChartFileContentReader.ReadBuffer(path);
                }
                finally { Interlocked.Decrement(ref activeReaders); }
            }
        };
        Task<Lr2SongDbSyncResult> operation = Task.Run(() => Lr2SongDbSyncService.Run(songDb, request));
        try
        {
            Task completed = Task.WhenAny(healthyEntered.Task, operation).GetAwaiter().GetResult();
            if (completed == operation)
            {
                operation.GetAwaiter().GetResult();
                Assert.Fail("健全readerのgate到達前に操作が完了しました。");
            }
            healthyEntered.Task.GetAwaiter().GetResult();
            Assert.IsFalse(operation.IsCompleted, "開始済み健全readerの実終端を待ちます。");
            releaseHealthy.Set();
            Lr2SongDbSyncResult result = operation.GetAwaiter().GetResult();
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
            Assert.AreEqual(4, result.SongRowProcessedCount);
            Assert.AreEqual(1, result.SongRowSkippedCount);
            Assert.AreEqual(0, result.SongRowParseFailureCount);
            Assert.AreEqual(0, activeReaders);
            using (var verify = new LR2SongDBExtended(scope.SongDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual(4, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE favorite = 3 AND tag = 'keep' AND adddate = 123;"));
                for (int index = 0; index < charts.Count; index++)
                {
                    Assert.AreEqual(index == 1 ? "old" : "basic" + index,
                        verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", charts[index].Path));
                    Assert.AreEqual(index == 1 ? 2 : 9,
                        verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", charts[index].Path));
                }
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", charts[1].Sha256));
                Assert.AreEqual(3, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info;"));
                Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
                Assert.AreEqual(4, verify.ExecuteScalar<int>("SELECT processed_cursor FROM lr2_song_db_sync_status WHERE name = ?;", Lr2SongDbSyncStatusService.DefaultStatusName));
                Assert.AreEqual("Running", verify.ExecuteScalar<string>("SELECT status FROM lr2_song_db_sync_status WHERE name = ?;", Lr2SongDbSyncStatusService.DefaultStatusName));
                Assert.IsNull(verify.ExecuteScalar<string>("SELECT completed_at FROM lr2_song_db_sync_status WHERE name = ?;", Lr2SongDbSyncStatusService.DefaultStatusName));
            }
            Lr2SongDbSyncResult next = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
            {
                Signature = "basic-input-next",
                RunId = "basic-input-next",
                SongRows = charts,
                ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
                ChartInfoResolver = _ => null,
                ChartInfoResolverIsThreadSafe = true,
                IsShutdownRequested = () => false
            });
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, next.FinalStage);
            Assert.AreEqual(0, next.SongRowSkippedCount);
            Assert.AreEqual("basic1", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", charts[1].Path));
            Assert.AreEqual(4, songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE level = 9 AND favorite = 3 AND tag = 'keep' AND adddate = 123;"));
            Assert.AreEqual(4, songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
        }
        finally
        {
            releaseHealthy.Set();
            try { operation.GetAwaiter().GetResult(); }
            catch (Exception) { /* 一次表明を保持し、全Task終端後にDBを解放します。 */ }
        }
        Assert.AreEqual(ownerThread, Environment.CurrentManagedThreadId);
        Assert.IsTrue(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
    }

    /// <summary>要求取消を伝播し、全callback終端後も確定済みチャンク・利用者列・処理位置を保持する。</summary>
    /// <remarks>1001行で既定チャンクを確定後、末尾の実詳細解析を待たせて取り消す。CPU数によらず実行し、通常Taskを回収してから接続を生成threadで解放する。未知の基本入力は少数の対象スキップケースが分担する。</remarks>
    [TestMethod]
    public void SyncService_RequestCancellationPreservesCommittedPrefixAndJoinsWorkers()
    {
        const int targetCount = 1001;
        using var scope = TestDatabaseScope.Create();
        using var cleanupCancellation = new CancellationTokenSource();
        using var prefixCommitted = new ManualResetEventSlim();
        using var releasePrimary = new ManualResetEventSlim();
        var primaryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string logicalDirectory = Path.Combine(scope.DirectoryPath, "LogicalCharts");
        Directory.CreateDirectory(logicalDirectory);
        var templates = new ChartFileReadBuffer[2];
        for (int index = 0; index < templates.Length; index++)
        {
            string path = Path.Combine(scope.DirectoryPath, "template" + index + ".bms");
            File.WriteAllText(path, "#TITLE template" + index + "\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            templates[index] = ChartFileContentReader.ReadBuffer(path);
        }
        BeMusicSeeker.Models.ChartDetails currentInfo = ChartInfoParser.ParseBytesDetailed(
            templates[0].Bytes, templates[0].Path, retainChartString: false).Row;
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        List<ChartFile> charts = [];
        Assert.IsFalse(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
        int songDbThreadId = Environment.CurrentManagedThreadId;
        using (var songDb = new LR2SongDBExtended(scope.SongDbPath))
        {
            Assert.IsTrue(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
            songDb.CreateTable<LR2SongDB.song>();
            BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
            songDb.RunInTransaction(() =>
            {
                for (int index = 0; index < targetCount; index++)
                {
                    string path = Path.Combine(logicalDirectory, index.ToString("D5") + ".bms");
                    indices.Add(path, index);
                    ChartFile chart = ChartTestValues.Empty() with { Path = path, Level = 2, Title = "old" };
                    charts.Add(chart);
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(chart), typeof(LR2SongDB.song));
                }
                songDb.Execute("UPDATE song SET favorite = 3, tag = 'keep', adddate = 123;");
            });
            int? observedPrefixCursor = null;
            int activeCallbacks = 0;
            int publishedFailures = 0;
            var parser = new ChartInfoBuildService(null, parseBytes: (bytes, path, md5, sha256, timeout) =>
            {
                Interlocked.Increment(ref activeCallbacks);
                try
                {
                    int index = indices[path];
                    if (index == 1000)
                    {
                        primaryEntered.TrySetResult(true);
                        releasePrimary.Wait();
                    }
                    return ChartInfoParser.ParseBytesDetailed(bytes, path, md5, sha256,
                        timeout: timeout, retainChartString: false);
                }
                finally
                {
                    Interlocked.Decrement(ref activeCallbacks);
                }
            });
            var request = new Lr2SongDbSyncRequest
            {
                Signature = "pipeline-request-cancel",
                RunId = "pipeline-run",
                SongRows = charts,
                ChartInfoBuildService = parser,
                ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
                ChartInfoResolverIsThreadSafe = true,
                ChartInfoResolver = chart => indices[chart.Path] == 1000 ? null : currentInfo,
                CancellationToken = cleanupCancellation.Token,
                IsShutdownRequested = () => false,
                ChartFileBufferReader = path =>
                {
                    Interlocked.Increment(ref activeCallbacks);
                    try
                    {
                        int index = indices[path];
                        if (index >= 1000)
                        {
                            prefixCommitted.Wait(cleanupCancellation.Token);
                        }
                        ChartFileReadBuffer template = templates[index == 1000 ? 1 : 0];
                        return new ChartFileReadBuffer(path, template.Bytes, template.LastWriteTimeUtc);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeCallbacks);
                    }
                },
                ChartInfoParseFailuresCommitted = (persisted, _, _) => Interlocked.Add(ref publishedFailures, persisted),
                LogInstallPerformance = message =>
                {
                    if (message.StartsWith("lr2_song_db_sync chunk_done stage=song_rows offset=0 count=1000", StringComparison.Ordinal))
                    {
                        observedPrefixCursor = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName).processed_cursor;
                        prefixCommitted.Set();
                    }
                }
            };
            Task<Lr2SongDbSyncResult> operation = Task.Run(() => Lr2SongDbSyncService.Run(songDb, request));
            try
            {
                WaitForLr2FaultStage(primaryEntered.Task, operation);
                Assert.AreEqual<int?>(1000, observedPrefixCursor);
                cleanupCancellation.Cancel();
                releasePrimary.Set();
                Exception? failure = null;
                try
                {
                    operation.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                Assert.IsNotNull(failure);
                Assert.IsInstanceOfType<OperationCanceledException>(failure);
                Assert.AreEqual(cleanupCancellation.Token, ((OperationCanceledException)failure).CancellationToken);
                Assert.AreEqual(0, activeCallbacks);
                Assert.AreEqual(0, publishedFailures);
                LR2SongDBExtended.lr2_song_db_sync_status status = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
                Assert.AreEqual("Failed", status.status);
                Assert.AreEqual("song_rows", status.stage);
                Assert.AreEqual(1000, status.processed_cursor);
                StringAssert.Contains(status.last_error, "Unexpected operation cancellation.");
                Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
                Assert.AreEqual(targetCount, songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM song WHERE favorite = 3 AND tag = 'keep' AND adddate = 123;"));
                for (int index = 0; index < charts.Count; index++)
                {
                    Assert.AreEqual(index < 1000 ? 9 : 2,
                        songDb.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", charts[index].Path));
                }
            }
            finally
            {
                cleanupCancellation.Cancel();
                prefixCommitted.Set();
                releasePrimary.Set();
                try
                {
                    operation.GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    /* 本体の一次表明を保持し、全Task終端後にだけDBを破棄します。 */
                }
            }
            Assert.AreEqual(songDbThreadId, Environment.CurrentManagedThreadId);
            Assert.IsTrue(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
        }
        Assert.IsFalse(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
    }

    private static void WaitForLr2FaultStage(Task stage, Task operation)
    {
        Task completed = Task.WhenAny(stage, operation).GetAwaiter().GetResult();
        if (completed == operation)
        {
            operation.GetAwaiter().GetResult();
            Assert.Fail("必要なgateの観測前に操作が完了しました。");
        }
        stage.GetAwaiter().GetResult();
    }

    /// <summary>必須詳細/互換性callbackの失敗を元stack付きで返し、任意進捗・ログも故障している条件でもcommit済み行・利用者列・実cursorを保持する。</summary>
    /// <remarks>正常譜面の実詳細解析・SQLite書込みを通し、callback内の別readonly接続でcommit完了を観測する。callback内で表明しない。</remarks>
    [DataTestMethod]
    [DataRow("chart_info")]
    [DataRow("compatibility")]
    public void SyncService_RequiredCommittedCallbackFailureKeepsDurableFactsAndCursor(string failedFact)
    {
        using var scope = TestDatabaseScope.Create();
        string path = Path.Combine(scope.DirectoryPath, "required-callback.bms");
        WriteBasicBms(path, "committed callback title", CreateLr2TooLongResourcePath());
        File.AppendAllText(path, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n");
        ChartFile file = CreateSyncTestFile(path, ChartFileContentReader.ReadSnapshot(path)) with
        {
            Title = "old",
            RawTitle = "old",
            Favorite = 5,
            Tag = "keep",
            AddDate = 123
        };
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
        int rowsCalls = 0;
        int compatibilityCalls = 0;
        int rollbackCalls = 0;
        int? callbackCursor = null;
        string? callbackTitle = null;
        int callbackChartInfoCount = 0;
        int callbackCompatibilityFlags = 0;
        strdelegate_authorizer authorizer = (_, code, transaction, _, _, _) =>
        {
            if (code == raw.SQLITE_TRANSACTION && transaction == "ROLLBACK") { rollbackCalls++; }
            return raw.SQLITE_OK;
        };
        Assert.AreEqual(raw.SQLITE_OK, raw.sqlite3_set_authorizer(songDb.Handle, authorizer, null));
        void ObserveCommit()
        {
            using LR2SongDBExtended readback = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
            callbackTitle = readback.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", path);
            callbackCursor = readback.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName).processed_cursor;
            callbackChartInfoCount = readback.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_info WHERE sha256 = ?;", file.Sha256);
            callbackCompatibilityFlags = readback.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", path);
        }
        try
        {
            InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(() =>
                Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
                {
                    Signature = "required-callback",
                    RunId = "required-callback",
                    SongRows = [file],
                    ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
                    ChartInfoRowsCommitted = _ =>
                    {
                        rowsCalls++;
                        if (failedFact == "chart_info") { ObserveCommit(); ThrowRequiredCommittedCallbackFault(failedFact); }
                    },
                    Lr2CompatibilityFactsCommitted = _ =>
                    {
                        compatibilityCalls++;
                        if (failedFact == "compatibility") { ObserveCommit(); ThrowRequiredCommittedCallbackFault(failedFact); }
                    },
                    ProgressReporter = _ => throw new InvalidOperationException("optional progress fault"),
                    LogInstallPerformance = _ => throw new InvalidOperationException("optional performance fault")
                }));
            Assert.AreEqual("required committed callback fault: " + failedFact, failure.Message);
            StringAssert.Contains(failure.StackTrace ?? string.Empty, nameof(ThrowRequiredCommittedCallbackFault));
            Assert.AreEqual(1, rowsCalls);
            Assert.AreEqual(1, compatibilityCalls, "別の必須callbackの失敗でも確定互換性事実を引き渡す。");
            Assert.AreEqual(0, rollbackCalls, "commit後の必須反映失敗をrollbackへ送らない。");
            Assert.AreEqual("committed callback title", callbackTitle);
            Assert.AreEqual(1, callbackCursor);
            Assert.AreEqual(1, callbackChartInfoCount);
            Assert.IsTrue(callbackCompatibilityFlags > 0);
            LR2SongDBExtended.lr2_song_db_sync_status status = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.AreEqual("Failed", status.status);
            Assert.AreEqual(1, status.processed_cursor);
            Assert.AreEqual("committed callback title", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", path));
            Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_info WHERE sha256 = ?;", file.Sha256));
            Assert.AreEqual(callbackCompatibilityFlags, songDb.ExecuteScalar<int>("SELECT lr2_warning_flags FROM maintenance WHERE path = ?;", path));
            Assert.AreEqual(5, songDb.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", path));
            Assert.AreEqual("keep", songDb.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?;", path));
            Assert.AreEqual(123, songDb.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?;", path));
        }
        finally
        {
            raw.sqlite3_set_authorizer(songDb.Handle, (strdelegate_authorizer?)null, null);
        }
    }

    /// <summary>先着した所有取消は必須callback実故障を隠さず、先着した非所有取消は後発shutdownやcallback故障で置換せず、実確定prefixと利用者列を保持する。</summary>
    /// <remarks>実1001行・二つの正常譜面入力と別reader/callback gateを使い、prefixの実詳細解析・確定を通す。詳細／互換性の2条件はCPU1でも所有取消の先着とreader joinを観測し、第3条件はowner未取消時のreader OCE分類を終端まで保持する。非UIの同期fixtureでdefault接続の生成・解放threadとprocess lock所有を維持し、全Task回収とauthorizer解除後に同threadで接続を解放する。</remarks>
    [DataTestMethod]
    [DataRow("chart_info", false)]
    [DataRow("compatibility", false)]
    [DataRow("chart_info", true)]
    public void SyncService_PipelineCancellationRacePreservesGenuineCauseAndCommittedPrefix(
        string failedFact, bool readerCancelsBeforeOwner)
    {
        string callbackFailureMessage = "required committed callback fault: " + failedFact;
        string expectedFailureMessage = readerCancelsBeforeOwner
            ? "injected LR2 reader cancellation before owner cancellation"
            : callbackFailureMessage;
        using var scope = TestDatabaseScope.Create();
        using var cancellation = new CancellationTokenSource();
        using var releaseTailReader = new ManualResetEventSlim();
        using var releaseCommittedCallback = new ManualResetEventSlim();
        var tailReaderEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var committedCallbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPipelineFault = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var committedProjectionFailed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var compatibilityCallbackExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string prefixPath = Path.Combine(scope.DirectoryPath, "prefix-template.bms");
        WriteBasicBms(prefixPath, "prefix committed", CreateLr2TooLongResourcePath());
        File.AppendAllText(prefixPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n");
        ChartFileReadBuffer prefix = ChartFileContentReader.ReadBuffer(prefixPath);
        ChartFileSnapshot prefixSnapshot = ChartFileContentReader.ReadSnapshot(prefixPath);
        string tailPath = Path.Combine(scope.DirectoryPath, "tail-template.bms");
        WriteBasicBms(tailPath, "tail must remain old");
        File.AppendAllText(tailPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n");
        ChartFileReadBuffer tail = ChartFileContentReader.ReadBuffer(tailPath);
        string tailSha256 = ChartFileContentReader.ReadSnapshot(tailPath).Sha256;
        List<ChartFile> files = [.. Enumerable.Range(0, 1001).Select(index => ChartTestValues.Empty() with
        {
            Path = Path.Combine(scope.DirectoryPath, index.ToString("D4") + ".bms"),
            Title = "old",
            RawTitle = "old",
            Favorite = 7,
            Tag = "keep",
            AddDate = 321
        })];
        Assert.IsFalse(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
        int songDbThreadId = Environment.CurrentManagedThreadId;
        using (var songDb = new LR2SongDBExtended(scope.SongDbPath))
        {
            Assert.IsTrue(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
            songDb.CreateTable<LR2SongDB.song>();
            songDb.RunInTransaction(() =>
            {
                foreach (ChartFile file in files)
                {
                    songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                }
            });
            int shutdownRequested = 0;
            int activeReaders = 0;
            int activeCallbacks = 0;
            int tailReaderEntries = 0;
            int tailReaderExits = 0;
            int rowsCalls = 0;
            int parseFailureCalls = 0;
            int clearedParseFailures = 0;
            int compatibilityCalls = 0;
            int rollbackCalls = 0;
            var progress = new ConcurrentQueue<Lr2SongDbSyncProgress>();
            var diagnostics = new ConcurrentQueue<string>();
            strdelegate_authorizer authorizer = (_, code, transaction, _, _, _) =>
            {
                if (code == raw.SQLITE_TRANSACTION && transaction == "ROLLBACK")
                {
                    Interlocked.Increment(ref rollbackCalls);
                }
                return raw.SQLITE_OK;
            };
            void ApplyCommittedFact(string fact)
            {
                Interlocked.Increment(ref activeCallbacks);
                try
                {
                    if (fact == failedFact)
                    {
                        committedCallbackEntered.TrySetResult(true);
                        releaseCommittedCallback.Wait();
                        ThrowRequiredCommittedCallbackFault(fact);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activeCallbacks);
                    if (fact == "compatibility")
                    {
                        compatibilityCallbackExited.TrySetResult(true);
                    }
                }
            }
            void AssertDurablePrefix(string expectedStatus)
            {
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
                LR2SongDBExtended.lr2_song_db_sync_status status = readback.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
                Assert.AreEqual(expectedStatus, status.status);
                Assert.AreEqual("song_rows", status.stage);
                Assert.AreEqual(1000, status.processed_cursor);
                Assert.AreEqual(1001, status.total_count);
                Assert.IsNull(status.completed_at);
                Assert.AreEqual(1000, readback.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM song WHERE title = ? AND hash = ?;", "prefix committed", prefixSnapshot.Md5));
                Assert.AreEqual(1001, readback.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM song WHERE favorite = 7 AND tag = 'keep' AND adddate = 321;"));
                Assert.AreEqual(1, readback.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_info WHERE sha256 = ?;", prefixSnapshot.Sha256));
                Assert.AreEqual(1000, readback.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM maintenance WHERE hash = ? AND lr2_warning_flags > 0;", prefixSnapshot.Md5));
                Assert.AreEqual("old", readback.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", files[^1].Path));
                Assert.AreEqual(0, readback.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_info WHERE sha256 = ?;", tailSha256));
                Assert.AreEqual(0, readback.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", files[^1].Path));
                Assert.AreEqual(0, readback.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_info_parse_failure;"));
                if (expectedStatus == "Failed")
                {
                    Assert.AreEqual(expectedFailureMessage, status.last_error);
                }
            }
            var request = new Lr2SongDbSyncRequest
            {
                Signature = "committed-fault-after-cancel-" + failedFact + readerCancelsBeforeOwner,
                RunId = "committed-fault-after-cancel",
                SongRows = files,
                CancellationToken = cancellation.Token,
                IsShutdownRequested = () => Volatile.Read(ref shutdownRequested) != 0,
                ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
                ChartFileBufferReader = path =>
                {
                    Interlocked.Increment(ref activeReaders);
                    bool isTail = path == files[^1].Path;
                    try
                    {
                        if (isTail)
                        {
                            Interlocked.Increment(ref tailReaderEntries);
                            tailReaderEntered.TrySetResult(true);
                            // 所有tokenを観測しない開始済みreaderの終端も、操作が実際にjoinする必要があります。
                            releaseTailReader.Wait();
                            if (readerCancelsBeforeOwner)
                            {
                                return ThrowLr2UnownedReaderCancellation(cancellation.Token);
                            }
                        }
                        ChartFileReadBuffer template = isTail ? tail : prefix;
                        return new ChartFileReadBuffer(path, template.Bytes, template.LastWriteTimeUtc);
                    }
                    finally
                    {
                        if (isTail)
                        {
                            Interlocked.Increment(ref tailReaderExits);
                        }
                        Interlocked.Decrement(ref activeReaders);
                    }
                },
                ChartInfoRowsCommitted = _ =>
                {
                    Interlocked.Increment(ref rowsCalls);
                    ApplyCommittedFact("chart_info");
                },
                ChartInfoParseFailuresCommitted = (_, cleared, _) =>
                {
                    Interlocked.Increment(ref parseFailureCalls);
                    Interlocked.Add(ref clearedParseFailures, cleared);
                    ApplyCommittedFact("parse_failures");
                },
                Lr2CompatibilityFactsCommitted = _ =>
                {
                    Interlocked.Increment(ref compatibilityCalls);
                    ApplyCommittedFact("compatibility");
                },
                ProgressReporter = progress.Enqueue,
                LogInstallPerformance = message =>
                {
                    diagnostics.Enqueue(message);
                    if (message.StartsWith("lr2_song_db_sync pipeline_failed", StringComparison.Ordinal))
                    {
                        firstPipelineFault.TrySetResult(message);
                    }
                    if (message.StartsWith("lr2_song_db_sync committed_fact_projection_failed", StringComparison.Ordinal))
                    {
                        committedProjectionFailed.TrySetResult(true);
                    }
                }
            };
            Task<Lr2SongDbSyncResult>? operation = null;
            try
            {
                Assert.AreEqual(raw.SQLITE_OK, raw.sqlite3_set_authorizer(songDb.Handle, authorizer, null));
                operation = Task.Run(() => Lr2SongDbSyncService.Run(songDb, request));
                WaitForLr2FaultStage(tailReaderEntered.Task, operation);
                WaitForLr2FaultStage(committedCallbackEntered.Task, operation);
                AssertDurablePrefix("Running");
                Assert.IsFalse(operation.IsCompleted);
                Assert.AreEqual(1, Volatile.Read(ref tailReaderEntries));
                Assert.AreEqual(0, Volatile.Read(ref tailReaderExits));
                Assert.AreEqual(1, Volatile.Read(ref activeCallbacks));

                if (readerCancelsBeforeOwner)
                {
                    // 同じ要求token付きでも、要求未取消時のOCEを後発shutdownで所有取消へ再分類しません。
                    releaseTailReader.Set();
                    WaitForLr2FaultStage(firstPipelineFault.Task, operation);
                    Assert.IsFalse(cancellation.IsCancellationRequested);
                    Assert.AreEqual(0, Volatile.Read(ref shutdownRequested));
                    StringAssert.Contains(firstPipelineFault.Task.GetAwaiter().GetResult(), expectedFailureMessage);
                }
                Volatile.Write(ref shutdownRequested, 1);
                cancellation.Cancel();
                if (!readerCancelsBeforeOwner)
                {
                    WaitForLr2FaultStage(firstPipelineFault.Task, operation);
                }
                StringAssert.Contains(firstPipelineFault.Task.GetAwaiter().GetResult(), "exception=OperationCanceledException");
                releaseCommittedCallback.Set();
                WaitForLr2FaultStage(committedProjectionFailed.Task, operation);
                WaitForLr2FaultStage(compatibilityCallbackExited.Task, operation);
                Assert.AreEqual(1, Volatile.Read(ref rowsCalls));
                Assert.AreEqual(1, Volatile.Read(ref parseFailureCalls));
                Assert.IsTrue(Volatile.Read(ref clearedParseFailures) > 0);
                Assert.AreEqual(1, Volatile.Read(ref compatibilityCalls));
                Assert.AreEqual(0, Volatile.Read(ref activeCallbacks));
                Assert.AreEqual(0, Volatile.Read(ref rollbackCalls));
                if (!readerCancelsBeforeOwner)
                {
                    Assert.IsFalse(operation.IsCompleted, "必須反映の実故障後も、保持中の開始済みreaderの終端を待つ。");
                    Assert.AreEqual(0, Volatile.Read(ref tailReaderExits));
                    AssertDurablePrefix("Running");
                }

                releaseTailReader.Set();
                InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(() => operation.GetAwaiter().GetResult());
                Assert.AreEqual(expectedFailureMessage, failure.Message);
                if (readerCancelsBeforeOwner)
                {
                    Exception originalCause = failure.GetBaseException();
                    Assert.IsInstanceOfType<OperationCanceledException>(originalCause);
                    var originalCancellation = (OperationCanceledException)originalCause;
                    Assert.AreEqual(cancellation.Token, originalCancellation.CancellationToken);
                    Assert.AreEqual(expectedFailureMessage, originalCancellation.Message);
                    StringAssert.Contains(originalCancellation.StackTrace ?? string.Empty, nameof(ThrowLr2UnownedReaderCancellation));
                }
                else
                {
                    StringAssert.Contains(failure.StackTrace ?? string.Empty, nameof(ThrowRequiredCommittedCallbackFault));
                }
                AssertDurablePrefix("Failed");
                Assert.IsFalse(progress.Any(value => value.Stage == Lr2SongDbSyncService.CompletedStage));
                Assert.IsTrue(diagnostics.Any(message => message.StartsWith(
                    "lr2_song_db_sync committed_fact_projection_failed", StringComparison.Ordinal)
                    && message.Contains(callbackFailureMessage, StringComparison.Ordinal)));
                Assert.AreEqual(0, Volatile.Read(ref rollbackCalls));
                Assert.AreEqual(0, Volatile.Read(ref activeReaders));
                Assert.AreEqual(0, Volatile.Read(ref activeCallbacks));
                Assert.AreEqual(1, Volatile.Read(ref tailReaderExits));
            }
            finally
            {
                releaseCommittedCallback.Set();
                releaseTailReader.Set();
                cancellation.Cancel();
                try
                {
                    if (operation != null)
                    {
                        try
                        {
                            operation.GetAwaiter().GetResult();
                        }
                        catch (Exception)
                        {
                            /* 一次表明を保持し、全開始処理の終端後にだけ所有接続を解放します。 */
                        }
                    }
                }
                finally
                {
                    raw.sqlite3_set_authorizer(songDb.Handle, (strdelegate_authorizer?)null, null);
                }
            }
            Assert.AreEqual(songDbThreadId, Environment.CurrentManagedThreadId);
            Assert.IsTrue(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
        }
        Assert.IsFalse(LR2SongDBExtended.IsProcessLockEnteredByCurrentThread());
    }

    /// <summary>終了取消後のmanaged rollback故障は直接SQLの成功なら通常取消を保ち、両故障なら未解決transactionを実失敗として返して確定prefix/cursorを保持する。</summary>
    /// <remarks>authorizerでROLLBACKコンパイルだけ一回／二回拒否する。安全rollbackの即時token取消／flagのみでは全worker join後の末尾writerから終端進捗を観測し、flagのみの通知故障でも元OCEと確定prefixを保持する。</remarks>
    [DataTestMethod]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(1, false)]
    public void SyncService_ShutdownRollbackDistinguishesSafeFallbackFromUnresolvedTransaction(
        int deniedRollbacks, bool cancelTokenImmediately)
    {
        using var scope = TestDatabaseScope.Create();
        string templatePath = Path.Combine(scope.DirectoryPath, "prefix.bms");
        WriteBasicBms(templatePath, "prefix committed");
        ChartFileReadBuffer prefix = ChartFileContentReader.ReadBuffer(templatePath);
        string tailPath = Path.Combine(scope.DirectoryPath, "tail.bms");
        WriteBasicBms(tailPath, "tail must rollback");
        ChartFileReadBuffer tail = ChartFileContentReader.ReadBuffer(tailPath);
        string tailSha256 = ChartFileContentReader.ReadSnapshot(tailPath).Sha256;
        List<ChartFile> files = [.. Enumerable.Range(0, 1001).Select(index => ChartTestValues.Empty() with
        {
            Path = Path.Combine(scope.DirectoryPath, index.ToString("D4") + ".bms"),
            Title = "old", RawTitle = "old", Favorite = 7, Tag = "keep", AddDate = 321
        })];
        var songDb = new LR2SongDBExtended(scope.SongDbPath);
        using var cancellation = new CancellationTokenSource();
        bool shutdown = false;
        int writerCalls = 0;
        int rollbackCalls = 0;
        var diagnostics = new ConcurrentQueue<string>();
        var shutdownProgress = new ConcurrentQueue<(Lr2SongDbSyncProgress Progress, int Autocommit)>();
        strdelegate_authorizer authorizer = (_, code, transaction, _, _, _) =>
        {
            if (code == raw.SQLITE_TRANSACTION && transaction == "ROLLBACK")
            {
                rollbackCalls++;
                if (rollbackCalls <= deniedRollbacks) { return raw.SQLITE_DENY; }
            }
            return raw.SQLITE_OK;
        };
        try
        {
            songDb.CreateTable<LR2SongDB.song>();
            songDb.RunInTransaction(() =>
            {
                foreach (ChartFile file in files) { songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song)); }
            });
            Assert.AreEqual(raw.SQLITE_OK, raw.sqlite3_set_authorizer(songDb.Handle, authorizer, null));
            Exception? failure = null;
            try
            {
                Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
                {
                    Signature = "rollback-authorizer",
                    RunId = "rollback-authorizer",
                    SongRows = files,
                    CancellationToken = cancellation.Token,
                    IsShutdownRequested = () => shutdown,
                    ChartFileBufferReader = path =>
                    {
                        ChartFileReadBuffer template = path == files[^1].Path ? tail : prefix;
                        return new ChartFileReadBuffer(path, template.Bytes, template.LastWriteTimeUtc);
                    },
                    ChartInfoChunkWriter = request =>
                    {
                        CatalogChartInfoWriteReceipt receipt = CreateDirectChartInfoWriter(songDb)(request);
                        if (++writerCalls == 2)
                        {
                            shutdown = true;
                            if (cancelTokenImmediately)
                            {
                                cancellation.Cancel();
                            }
                        }
                        return receipt;
                    },
                    ProgressReporter = value =>
                    {
                        if (!shutdown)
                        {
                            return;
                        }
                        // flagを立てる末尾Flushは全worker join後なので、所有接続の安全な終端を同時に観測できます。
                        shutdownProgress.Enqueue((value, raw.sqlite3_get_autocommit(songDb.Handle)));
                        if (!cancelTokenImmediately)
                        {
                            throw new InvalidOperationException("optional shutdown progress fault");
                        }
                    },
                    LogInstallPerformance = diagnostics.Enqueue
                });
            }
            catch (Exception exception) { failure = exception; }
            Assert.IsNotNull(failure);
            Assert.AreEqual(2, writerCalls);
            Assert.AreEqual(2, rollbackCalls);
            Assert.AreEqual(deniedRollbacks == 1 ? 1 : 0, raw.sqlite3_get_autocommit(songDb.Handle));
            Assert.AreEqual(cancelTokenImmediately, cancellation.IsCancellationRequested);
            if (deniedRollbacks == 1)
            {
                Assert.IsInstanceOfType<OperationCanceledException>(failure);
                Assert.AreEqual(cancellation.Token, ((OperationCanceledException)failure).CancellationToken);
                Assert.IsFalse(shutdownProgress.IsEmpty);
                (Lr2SongDbSyncProgress Progress, int Autocommit) finalProgress = shutdownProgress.ToArray()[^1];
                Assert.AreEqual(1000, finalProgress.Progress.ProcessedCursor);
                Assert.AreEqual(1001, finalProgress.Progress.TotalCount);
                Assert.AreEqual("song_rows", finalProgress.Progress.Stage);
                Assert.AreEqual(0, finalProgress.Progress.StageProcessedCount);
                Assert.AreEqual(0, finalProgress.Progress.StageTotalCount);
                Assert.AreEqual(1, finalProgress.Autocommit);
                Assert.IsTrue(diagnostics.Any(message => message.Contains("managed_rollback_failed fallback=completed", StringComparison.Ordinal)));
            }
            else
            {
                Assert.IsFalse(failure is OperationCanceledException, "未解決transactionを正常終了取消へ読み替えない。");
                StringAssert.Contains(failure.ToString(), "OperationCanceledException");
                Assert.IsTrue(diagnostics.Any(message => message.Contains("rollback_unresolved", StringComparison.Ordinal)
                    && message.Contains("OperationCanceledException", StringComparison.Ordinal)));
                Assert.IsTrue(diagnostics.Any(message => message.Contains("managed_rollback_failed fallback=failed", StringComparison.Ordinal)
                    && message.Contains("not authorized", StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(diagnostics.Any(message => message.Contains("direct_rollback_failed", StringComparison.Ordinal)
                    && message.Contains("not authorized", StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally
        {
            try
            {
                raw.sqlite3_set_authorizer(songDb.Handle, (strdelegate_authorizer?)null, null);
                if (raw.sqlite3_get_autocommit(songDb.Handle) == 0) { songDb.Execute("ROLLBACK;"); }
            }
            finally { songDb.Dispose(); }
        }
        using LR2SongDBExtended readback = new BmsLibraryDbGateway(scope.SongDbPath).OpenSongDbReadOnly();
        Assert.AreEqual("prefix committed", readback.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", files[0].Path));
        Assert.AreEqual("old", readback.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", files[^1].Path));
        Assert.AreEqual(7, readback.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", files[0].Path));
        Assert.AreEqual("keep", readback.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?;", files[0].Path));
        Assert.AreEqual(321, readback.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?;", files[0].Path));
        Assert.AreEqual(0, readback.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_info WHERE sha256 = ?;", tailSha256));
        Assert.AreEqual(0, readback.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", files[^1].Path));
        LR2SongDBExtended.lr2_song_db_sync_status durable = readback.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual(1000, durable.processed_cursor);
        Assert.IsNull(durable.completed_at);
        Assert.AreNotEqual("Completed", durable.status);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ChartFileReadBuffer ThrowLr2UnownedReaderCancellation(CancellationToken requestToken)
    {
        throw new OperationCanceledException("injected LR2 reader cancellation before owner cancellation", requestToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRequiredCommittedCallbackFault(string fact)
    {
        throw new InvalidOperationException("required committed callback fault: " + fact);
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

    private static Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateChartInfoResolver(
        IEnumerable<BeMusicSeeker.Models.ChartDetails> rows)
    {
        var bySha256 = new Dictionary<string, BeMusicSeeker.Models.ChartDetails>(StringComparer.OrdinalIgnoreCase);
        var md5Candidates = new Dictionary<string, SortedDictionary<string, BeMusicSeeker.Models.ChartDetails>>(StringComparer.OrdinalIgnoreCase);
        foreach (BeMusicSeeker.Models.ChartDetails row in rows ?? [])
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
                if (!md5Candidates.TryGetValue(row.md5, out SortedDictionary<string, BeMusicSeeker.Models.ChartDetails>? candidates))
                {
                    candidates = new SortedDictionary<string, BeMusicSeeker.Models.ChartDetails>(StringComparer.OrdinalIgnoreCase);
                    md5Candidates[row.md5] = candidates;
                }
                candidates![row.sha256] = row;
            }
        }

        var byMd5 = md5Candidates
            .Where(pair => pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value.First().Value, StringComparer.OrdinalIgnoreCase);
        return row =>
        {
            if (row == null)
            {
                return null!;
            }
            if (!string.IsNullOrWhiteSpace(row.Sha256)
                && bySha256.TryGetValue(row.Sha256, out BeMusicSeeker.Models.ChartDetails? bySha256Row))
            {
                return bySha256Row!;
            }
            if (!string.IsNullOrWhiteSpace(row.Md5)
                && byMd5.TryGetValue(row.Md5, out BeMusicSeeker.Models.ChartDetails? byMd5Row))
            {
                return byMd5Row!;
            }
            return null!;
        };
    }
}
