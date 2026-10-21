using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncCommittedInputTests
{
    /// <summary>
    /// 今回の確定パスだけを大小文字を区別せず除外し、別パス同MD5と基本入力失敗を処理対象へ残します。
    /// </summary>
    /// <remarks>実SQLiteの保存・進捗・入口集計を確認し、入力外の確定パスや除外位置による過剰除外・採番欠落を検出します。</remarks>
    [TestMethod]
    public void CommittedInputSongRowsSkipReaderAndCurrentnessRead()
    {
        using var scope = TestDatabaseScope.Create();
        string processedPath = Path.Combine(scope.DirectoryPath, "processed.bms");
        File.WriteAllText(processedPath, "#TITLE receipt processed\r\n");
        ChartFile processedFile = CreateSyncTestFile(processedPath, ChartFileContentReader.ReadSnapshot(processedPath));
        ChartFile CreateRow(string name) => processedFile with
        {
            Path = Path.Combine(scope.DirectoryPath, name + ".bms"),
            Title = "keep",
            RawTitle = "keep",
            Favorite = 7,
            Tag = "user",
            AddDate = 123
        };
        ChartFile[] excluded = [CreateRow("first"), CreateRow("middle"), CreateRow("last")];
        ChartFile failed = CreateRow("failed");
        ChartFile otherPathSameMd5 = CreateRow("same-md5");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        ChartFile[] rows = [excluded[0], processedFile, excluded[1], failed, otherPathSameMd5, excluded[2]];
        songDb.RunInTransaction(() =>
        {
            foreach (ChartFile file in rows)
            {
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
            }
        });
        var reads = new ConcurrentQueue<string>();
        var committedPaths = new List<string>();
        var progressEvents = new ConcurrentQueue<Lr2SongDbSyncProgress>();
        var logs = new ConcurrentQueue<string>();
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "receipt-reader-gate",
            RunId = "receipt-reader-gate",
            SongRows = rows,
            CommittedBmsPaths = new HashSet<string>(excluded.Select(file => file.Path.ToUpperInvariant())
                .Append(Path.Combine(scope.DirectoryPath, "outside.bms")), StringComparer.OrdinalIgnoreCase),
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            Lr2CompatibilityFactsCommitted = facts => committedPaths.AddRange(facts.Select(fact => fact.Path)),
            ChartFileBufferReader = path =>
            {
                reads.Enqueue(path);
                if (path == failed.Path) { throw new InvalidOperationException("basic input failure"); }
                // 同MD5でも別パスの対象は通常の読取り・解析へ渡します。
                return new ChartFileReadBuffer(path, File.ReadAllBytes(processedPath), File.GetLastWriteTimeUtc(processedPath));
            },
            ProgressReporter = progressEvents.Enqueue,
            LogInstallPerformance = logs.Enqueue
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(3, result.TotalCount);
        Assert.AreEqual(3, result.ProcessedCount);
        Assert.AreEqual(3, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowSkippedCount);
        CollectionAssert.AreEquivalent(new[] { processedPath, failed.Path, otherPathSameMd5.Path }, reads.ToArray());
        CollectionAssert.AreEqual(new[] { processedPath, otherPathSameMd5.Path }, committedPaths);
        Assert.IsTrue(progressEvents.Where(progress => progress.Stage == "song_rows")
            .All(progress => progress.TotalCount == 3 && progress.StageTotalCount == 3));
        Assert.IsTrue(logs.Any(line => line.Contains("input_summary")
            && line.Contains("songRowCandidates=6") && line.Contains("committedPathExcluded=3") && line.Contains("songRows=3")));
        Assert.AreEqual(1, logs.Count(line => line.Contains(" chunk_start ")));
        foreach (ChartFile file in excluded.Append(failed))
        {
            Assert.AreEqual("keep", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", file.Path));
            Assert.AreEqual(7, songDb.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", file.Path));
        }
        Assert.AreEqual("receipt processed", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", processedPath));
        Assert.AreEqual("receipt processed", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", otherPathSameMd5.Path));
        LR2SongDBExtended.lr2_song_db_sync_status status = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual(3, status.total_count);
        Assert.AreEqual(3, status.processed_cursor);
    }

    /// <summary>
    /// 全件の確定入力を投入前に除外して譜面処理・通知を出さず、フォルダ生成・最終検証と保存行・利用者列を保持します。
    /// </summary>
    /// <remarks>実SQLiteの保存と結果読戻しを観測し、保存・カーソル更新を代替しません。</remarks>
    /// <param name="rowCount">単件と複数の全件除外を確認します。実チャンクと順序枠の保証は非除外の実更新テストが担います。</param>
    [DataTestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void CommittedInputSongRows_ExcludeAllAndKeepFoldersWithoutSongStages(int rowCount)
    {
        using var scope = TestDatabaseScope.Create();
        string chartDirectory = Path.Combine(scope.DirectoryPath, "Charts");
        Directory.CreateDirectory(chartDirectory);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        ChartFile[] songRows = [.. Enumerable.Range(0, rowCount).Select(index =>
        {
            ChartFile file = ((ChartTestValues.Empty() with {
                Path = Path.Combine(chartDirectory, "committed-" + index + ".bms"),
                Tag = "user-tag",
                AddDate = 123456,
                Date = 234567
            })) with { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Favorite = 7 };
            file = file with { Title = "receipt committed", RawTitle = "receipt committed" };
            return file;
        })];
        songDb.BeginTransaction();
        try
        {
            foreach (ChartFile file in songRows)
            {
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
            }
            songDb.Commit();
        }
        catch
        {
            songDb.Rollback();
            throw;
        }

        // 除外した譜面だけのフォルダも、全ChartPathsと祖先メタデータから生成します。
        var progressEvents = new ConcurrentQueue<Lr2SongDbSyncProgress>();
        int readerCalls = 0;
        int chartInfoResolverCalls = 0;
        var logs = new ConcurrentQueue<string>();
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "receipt-chunk-boundaries",
            RunId = "receipt-chunk-boundaries",
            SongRows = songRows,
            RootDirectories = [scope.DirectoryPath],
            ChartPaths = songRows.Select(file => file.Path).ToArray(),
            NormalFolderDirectoryPaths = [scope.DirectoryPath, chartDirectory],
            DirectoryEntries = CreateDirectoryEntryMap(scope.DirectoryPath, chartDirectory),
            LogInstallPerformance = logs.Enqueue,
            CommittedBmsPaths = new HashSet<string>(songRows.Select(chart => chart.Path), StringComparer.OrdinalIgnoreCase),
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            ChartFileBufferReader = _ =>
            {
                Interlocked.Increment(ref readerCalls);
                return null;
            },
            ChartInfoResolver = _ =>
            {
                Interlocked.Increment(ref chartInfoResolverCalls);
                return null;
            },
            ProgressReporter = progressEvents.Enqueue,
            CancellationToken = CancellationToken.None
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(2, result.TotalCount);
        Assert.AreEqual(2, result.ProcessedCount);
        Assert.AreEqual(0, result.SongRowProcessedCount);
        Assert.AreEqual(0, result.SongRowSkippedCount);
        Assert.AreEqual(0, result.SongRowParseFailureCount);
        Assert.AreEqual(0, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(0, result.SongRowLr2CompatibilityAppliedCount);
        Assert.AreEqual(0, readerCalls);
        Assert.AreEqual(0, chartInfoResolverCalls);

        Assert.IsFalse(progressEvents.Any(progress => progress.Stage.StartsWith("song_rows", StringComparison.Ordinal)));
        Assert.IsTrue(progressEvents.Any(progress => progress.Stage == "final_validation" && progress.ProcessedCursor == 2));
        Assert.IsFalse(logs.Any(line => line.Contains(" pipeline_start ") || line.Contains(" chunk_start ")));
        Assert.IsNotNull(songDb.Find<LR2SongDB.folder>(ToFolderPath(chartDirectory)));
        Assert.IsNotNull(songDb.Find<LR2SongDB.folder>(ToFolderPath(scope.DirectoryPath)));

        LR2SongDBExtended.lr2_song_db_sync_status completed = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(
            Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Running.ToString(), completed.status);
        Assert.IsNull(completed.completed_at);
        Assert.IsFalse(progressEvents.Any(progress => progress.Stage == Lr2SongDbSyncService.CompletedStage));
        Assert.AreEqual(2, completed.total_count);
        Assert.AreEqual(2, completed.processed_cursor);
        LR2SongDB.song[] persisted = [.. songDb.Table<LR2SongDB.song>()];
        CollectionAssert.AreEquivalent(songRows.Select(file => file.Path).ToArray(), persisted.Select(row => row.path).ToArray());
        Assert.IsTrue(persisted.All(row => row.title == "receipt committed"
            && row.hash == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            && row.tag == "user-tag"
            && row.favorite == 7
            && row.adddate == 123456
            && row.date == 234567));
    }

}
