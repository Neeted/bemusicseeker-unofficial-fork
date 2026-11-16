using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using static BeMusicSeeker.TestSupport.Components.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class Lr2SongDbSyncServiceTestSupport
{
    /// <summary>日常と大規模条件で同じ同期chunkの確定件数・順序・保存結果を表明します。</summary>
    /// <param name="databaseScope">観測接続に使う、呼出元ケースのDB所有者です。</param>
    /// <param name="reportOperationElapsed">指定時だけ準備・表明・回収を除く実操作の経過を終端後に通知します。省略時は時計を追加しません。</param>
    internal static void AssertSongChunkCommits(ApplicationDatabaseScope databaseScope, int targetCount, int chunkSize, Lr2SongSyncExecutionOptions? options, Action<TimeSpan>? reportOperationElapsed = null)
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
        using LR2SongDBExtended songDb = scope.DatabaseScope.OpenSongWrite(scope.SongDbPath);
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
        var syncRequest = new Lr2SongDbSyncRequest
        {
            LogInstallPerformance = logs.Enqueue,
            RunId = "real-chunk-progress",
            ExecutionOptions = options,
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
        };
        Stopwatch? operationClock = reportOperationElapsed == null ? null : Stopwatch.StartNew();
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, syncRequest);
        operationClock?.Stop();
        if (operationClock != null) { reportOperationElapsed?.Invoke(operationClock.Elapsed); }

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(targetCount, result.SongRowProcessedCount);
        Assert.AreEqual((targetCount + chunkSize - 1) / chunkSize, beforeCommit.Count);
        string[] chunkStarts = [.. logs.Where(line => line.Contains(" chunk_start "))];
        Assert.AreEqual(beforeCommit.Count, chunkStarts.Length);
        for (int chunk = 0; chunk < chunkStarts.Length; chunk++)
        {
            StringAssert.Contains(chunkStarts[chunk], " offset=" + (chunk * chunkSize) + " count=" + Math.Min(chunkSize, targetCount - chunk * chunkSize));
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
        int[] expectedCursors = [.. Enumerable.Range(0, beforeCommit.Count + 1).Select(chunk => Math.Min(chunk * chunkSize, targetCount))];
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
        using SQLiteConnectionEx observation = databaseScope.OpenProviderConnection(scope.SongDbPath,
            SQLite.SQLiteOpenFlags.ReadOnly | SQLite.SQLiteOpenFlags.FullMutex);
        Assert.AreEqual(targetCount, observation.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info;"));
        for (int index = 0; index < targetCount; index++)
        {
            LR2SongDB.song stored = observation.Find<LR2SongDB.song>(songs[index].Path);
            Assert.AreEqual("Current " + index, stored.title);
            Assert.AreEqual("User tag", stored.tag);
            Assert.AreEqual(7, stored.favorite);
        }
    }

}
