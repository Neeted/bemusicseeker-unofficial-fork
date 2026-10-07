using System;
using System.Collections.Concurrent;
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
public sealed class Lr2SongDbSyncCommittedPathReceiptTests
{
    [TestMethod]
    public void Receipt_IsTakenOnceWhenBmsRowsVersionMatches()
    {
        using var scope = TestDatabaseScope.Create();
        TestBmsLibrary library = CreateLr2Library(scope.SongDbPath);
        var owner = (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
        string path = Path.Combine(scope.DirectoryPath, "committed.bms");
        library.BmsCharts = [ReceiptChart(path)];
        var result = new SongTableFileCheckResult();
        result.CommittedLr2SongDbSyncBmsPaths.Add(path);

        owner.PublishLr2SongDbSyncCommittedPathReceipt(result, "test_reload");
        Lr2SongDbSyncInput input = owner.CreateLr2SongDbSyncInput();

        Lr2SongDbSyncCommittedPathReceipt first = owner.TakeLr2SongDbSyncCommittedPathReceipt(input, "test_reload");
        Lr2SongDbSyncCommittedPathReceipt second = owner.TakeLr2SongDbSyncCommittedPathReceipt(input, "test_reload_again");

        Assert.IsNotNull(first);
        CollectionAssert.AreEquivalent(new[] { path }, first.CommittedBmsPaths.ToArray());
        Assert.IsNull(second);
    }

    [TestMethod]
    public void Receipt_OwnedCollectionVersionMismatchDoesNotInvalidateMatchingBmsRows()
    {
        using var scope = TestDatabaseScope.Create();
        TestBmsLibrary library = CreateLr2Library(scope.SongDbPath);
        var owner = (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
        string path = Path.Combine(scope.DirectoryPath, "mismatch.bms");
        library.BmsCharts = [ReceiptChart(path)];
        Lr2SongDbSyncInput input = owner.CreateLr2SongDbSyncInput();
        Lr2SongDbSyncInput ownedChangedInput = WithOwnedCollectionVersion(
            input,
            input.OwnedCollectionVersion + 1);
        owner.CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt(
            library.BmsCharts);

        Lr2SongDbSyncCommittedPathReceipt receipt = owner.TakeLr2SongDbSyncCommittedPathReceipt(
            ownedChangedInput,
            "test_owned_version_mismatch");

        Assert.IsNotNull(receipt);
        CollectionAssert.AreEquivalent(
            new[] { Path.Combine(scope.DirectoryPath, "mismatch.bms") },
            receipt.CommittedBmsPaths.ToArray());
        Assert.IsNull(owner.TakeLr2SongDbSyncCommittedPathReceipt(
            ownedChangedInput,
            "test_owned_version_mismatch_again"));
        Assert.IsNull(owner.CommittedPathReceipt);
    }

    [TestMethod]
    public void Receipt_SamePathAndHashItemReplacementIsDiscardedWithoutReuse()
    {
        using var scope = TestDatabaseScope.Create();
        TestBmsLibrary library = CreateLr2Library(scope.SongDbPath);
        var owner = (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
        string path = Path.Combine(scope.DirectoryPath, "mismatch.bms");
        library.BmsCharts = [ReceiptChart(path)];
        Lr2SongDbSyncInput input = owner.CreateLr2SongDbSyncInput();
        owner.CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt(library.BmsCharts);
        library.BmsCharts = [ReceiptChart(path)];

        Assert.IsNull(owner.TakeLr2SongDbSyncCommittedPathReceipt(input, "test_bms_version_mismatch"));
        Assert.IsNull(owner.CommittedPathReceipt);
    }

    [TestMethod]
    public void Receipt_SameTokenDigestChangeIsDiscardedWithoutReuse()
    {
        ChartFile captured = ReceiptChart("C:\\Songs\\changed.bms");
        var receipt = new Lr2SongDbSyncCommittedPathReceipt([captured]);
        ChartFile changed = captured with { Md5 = new string('c', 32), Sha256 = new string('d', 64) };
        Assert.IsFalse(receipt.MatchesTargets([changed]));
        Assert.IsTrue(receipt.MatchesTargets([captured with { Title = "表示のみ", Score = ChartScoreSnapshot.NoScore(captured.Path) }]));
    }

    [TestMethod]
    public void Receipt_NullInputIsDiscardedWithoutReuse()
    {
        using var scope = TestDatabaseScope.Create();
        TestBmsLibrary library = CreateLr2Library(scope.SongDbPath);
        var owner = (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
        owner.CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt([ReceiptChart(Path.Combine(scope.DirectoryPath, "null-input.bms"))]);

        Assert.IsNull(owner.TakeLr2SongDbSyncCommittedPathReceipt(null, "test_null_input"));
        Assert.IsNull(owner.CommittedPathReceipt);
    }

    [TestMethod]
    public void Receipt_ManualQueueOriginDiscardsWithoutTaking()
    {
        using var scope = TestDatabaseScope.Create();
        TestBmsLibrary library = CreateLr2Library(scope.SongDbPath);
        var owner = (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
        owner.CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt([ReceiptChart(Path.Combine(scope.DirectoryPath, "manual.bms"))]);
        library.StartupBackgroundTaskScheduler = (_, _, _, _) => true;

        library.QueueLr2SongDbSync("test_manual", force: true);

        Assert.IsNull(owner.CommittedPathReceipt);
    }

    [TestMethod]
    public void Receipt_IsNotRestoredAfterDisposalOrNewOwner()
    {
        using var scope = TestDatabaseScope.Create();
        TestBmsLibrary firstLibrary = CreateLr2Library(scope.SongDbPath);
        var firstOwner = (BMSLibrary.Lr2SynchronizationOwner)firstLibrary.Lr2Synchronization;
        firstOwner.CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt([ReceiptChart(Path.Combine(scope.DirectoryPath, "disposed.bms"))]);

        firstOwner.DisposeCancellation();

        Assert.IsNull(firstOwner.CommittedPathReceipt);
        TestBmsLibrary secondLibrary = CreateLr2Library(scope.SongDbPath);
        var secondOwner = (BMSLibrary.Lr2SynchronizationOwner)secondLibrary.Lr2Synchronization;
        Assert.IsNull(secondOwner.CommittedPathReceipt);
    }

    [TestMethod]
    public void ReceiptEligibleSongRowsSkipReaderAndCurrentnessRead()
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
            CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt([skippedFile]),
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

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(1000)]
    [DataRow(1001)]
    [DataRow(10001)]
    public void ReceiptEligibleSongRows_CompleteAcrossChunkAndOrderingWindowBoundaries(int rowCount)
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
        // 実譜面を作らず、証票対象の読取りや解析への流入を回数で検出する。
        var progressEvents = new ConcurrentQueue<Lr2SongDbSyncProgress>();
        int readerCalls = 0;
        int chartInfoResolverCalls = 0;
        using var cancellation = new CancellationTokenSource();
        // 停止を失敗として検出する期限。正常完了は Run の帰還で判断する。
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "receipt-chunk-boundaries",
            RunId = "receipt-chunk-boundaries",
            SongRows = songRows,
            CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt(songRows),
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
            CancellationToken = cancellation.Token
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

    private static ChartFile ReceiptChart(string path) => ChartTestValues.Empty() with
    { Path = path, Md5 = new string('a', 32), Sha256 = new string('b', 64), Token = new OwnedChartToken() };

    private static TestBmsLibrary CreateLr2Library(string songDbPath)
    {
        return new TestBmsLibrary(
            songDbPath,
            getLR2Config: null,
            _lr2ScoreDB: null,
            startupRequiredFileScanReason: null,
            optionsSnapshotProvider: () => new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true
            });
    }

    private static Lr2SongDbSyncInput WithOwnedCollectionVersion(
        Lr2SongDbSyncInput input,
        int ownedCollectionVersion)
    {
        return new Lr2SongDbSyncInput(input.RootDirectories, input.ChartPaths, input.NormalFolderDirectoryPaths, input.FolderInfoFilePaths, input.FolderInfoFileEntries, input.DirectoryEntries, input.Lr2FolderDiscoveryDirectories, input.Lr2FolderPruneDirectories, input.Lr2RootPath, input.Lr2NormalCustomFolderOutputBaseDir, input.Lr2AdditionalNormalCustomFolderOutputBaseDirs, input.Lr2RootCustomFolderOutputBaseDir, input.Lr2BuiltinFolderSourceDirectories, input.Lr2BuiltinCustomFolderSettings, input.Lr2FolderFilePaths, input.Lr2FolderFileEntries, input.Lr2FolderFileDiscoveryComplete, input.SongRows, input.TextFileDirectories, input.ScanSurfaceGeneration, ownedCollectionVersion);
    }
}
