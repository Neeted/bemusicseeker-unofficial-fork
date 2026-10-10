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
    /// <summary>今回DB確定済みのパスだけ読取りを省き、混在する未確定パスの通常読取り・保存を維持します。</summary>
    [TestMethod]
    public void CommittedInputSongRowsSkipReaderAndCurrentnessRead()
    {
        using var scope = TestDatabaseScope.Create();
        string skippedPath = Path.Combine(scope.DirectoryPath, "skipped.bms");
        string processedPath = Path.Combine(scope.DirectoryPath, "processed.bms");
        File.WriteAllText(processedPath, "#TITLE receipt processed\r\n");
        ChartFileSnapshot processedSnapshot = ChartFileContentReader.ReadSnapshot(processedPath);
        ChartFile skippedFile = ((ChartTestValues.Empty() with
        {
            Path = skippedPath,
            Date = 123456
        })) with
        { Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Favorite = null };
        ChartFile processedFile = CreateSyncTestFile(processedPath, processedSnapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(processedFile), typeof(LR2SongDB.song));
        int skippedReaderCalls = 0;
        int processedReaderCalls = 0;
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "receipt-reader-gate",
            RunId = "receipt-reader-gate",
            SongRows = [skippedFile, processedFile],
            CommittedBmsPaths = new HashSet<string>([skippedFile.Path], StringComparer.OrdinalIgnoreCase),
            ChartInfoChunkWriter = CreateDirectChartInfoWriter(songDb),
            ChartFileBufferReader = path =>
            {
                if (string.Equals(path, skippedPath, StringComparison.OrdinalIgnoreCase))
                {
                    skippedReaderCalls++;
                }
                else
                {
                    processedReaderCalls++;
                }
                return ChartFileContentReader.ReadBuffer(path);
            },
            StartedAtUtc = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(2, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowSkippedCount);
        Assert.AreEqual(0, skippedReaderCalls);
        Assert.AreEqual(1, processedReaderCalls);
        Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", skippedPath));
        Assert.AreEqual("receipt processed", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", processedPath));
    }

    /// <summary>空・単件・チャンク境界・順序枠超過でも確定入力の件数、cursor、永続完了と利用者属性を維持します。</summary>
    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(1000)]
    [DataRow(1001)]
    [DataRow(10001)]
    public void CommittedInputSongRows_CompleteAcrossChunkAndOrderingWindowBoundaries(int rowCount)
    {
        using var scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        ChartFile[] songRows = [.. Enumerable.Range(0, rowCount).Select(index =>
        {
            ChartFile file = ((ChartTestValues.Empty() with {
                Path = Path.Combine(scope.DirectoryPath, "committed-" + index + ".bms"),
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

        // 10,001件は現行の順序枠上限を超え、最後の部分チャンクも含む。
        // 実譜面を作らず、今回の確定入力が読取りや解析へ流入する誤りを回数で検出する。
        var progressEvents = new ConcurrentQueue<Lr2SongDbSyncProgress>();
        int readerCalls = 0;
        int chartInfoResolverCalls = 0;
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "receipt-chunk-boundaries",
            RunId = "receipt-chunk-boundaries",
            SongRows = songRows,
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
        Assert.AreEqual(rowCount, result.TotalCount);
        Assert.AreEqual(rowCount, result.ProcessedCount);
        Assert.AreEqual(rowCount, result.SongRowProcessedCount);
        Assert.AreEqual(rowCount, result.SongRowSkippedCount);
        Assert.AreEqual(0, result.SongRowParseFailureCount);
        Assert.AreEqual(0, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(0, result.SongRowLr2CompatibilityAppliedCount);
        Assert.AreEqual(0, readerCalls);
        Assert.AreEqual(0, chartInfoResolverCalls);

        Lr2SongDbSyncProgress[] songProgress = [.. progressEvents.Where(progress => progress.Stage == "song_rows")];
        if (rowCount > 0)
        {
            Assert.IsTrue(songProgress.Length > 0);
            Assert.IsTrue(songProgress.All(progress => progress.StageTotalCount == rowCount
                && progress.TotalCount == rowCount
                && progress.StageProcessedCount >= 0
                && progress.StageProcessedCount <= rowCount));
            int[] expectedCursors = [.. Enumerable.Range(0, ((rowCount + 999) / 1000) + 1)
                .Select(chunk => Math.Min(chunk * 1000, rowCount))];
            int[] actualCursors = [.. songProgress.Select(progress => progress.ProcessedCursor).Distinct().OrderBy(cursor => cursor)];
            CollectionAssert.AreEqual(expectedCursors, actualCursors);
        }
        else
        {
            Assert.AreEqual(0, songProgress.Length);
        }

        LR2SongDBExtended.lr2_song_db_sync_status completed = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(
            Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed.ToString(), completed.status);
        Assert.AreEqual(rowCount, completed.total_count);
        Assert.AreEqual(rowCount, completed.processed_cursor);
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
