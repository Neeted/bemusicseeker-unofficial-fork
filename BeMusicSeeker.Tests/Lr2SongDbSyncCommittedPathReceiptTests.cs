using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public void Receipt_IsTakenOnceWhenVersionsMatch()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        var library = CreateLr2Library(scope.SongDbPath);
        BMSLibrary.Lr2SynchronizationOwner owner = (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
        string path = Path.Combine(scope.DirectoryPath, "committed.bms");
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
    public void Receipt_VersionMismatchIsDiscardedWithoutReuse()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        var library = CreateLr2Library(scope.SongDbPath);
        BMSLibrary.Lr2SynchronizationOwner owner = (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
        Lr2SongDbSyncInput input = owner.CreateLr2SongDbSyncInput();
        owner.CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt(
            input.OwnedChartCollectionVersion + 1,
            input.BmsRowsVersion + 1,
            [Path.Combine(scope.DirectoryPath, "mismatch.bms")]);

        Assert.IsNull(owner.TakeLr2SongDbSyncCommittedPathReceipt(input, "test_mismatch"));
        Assert.IsNull(owner.CommittedPathReceipt);
    }

    [TestMethod]
    public void Receipt_ManualQueueOriginDiscardsWithoutTaking()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        var library = CreateLr2Library(scope.SongDbPath);
        BMSLibrary.Lr2SynchronizationOwner owner = (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
        owner.CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt(
            0,
            0,
            [Path.Combine(scope.DirectoryPath, "manual.bms")]);
        library.StartupBackgroundTaskScheduler = (_, _, _, _) => true;

        library.QueueLr2SongDbSync("test_manual", force: true);

        Assert.IsNull(owner.CommittedPathReceipt);
    }

    [TestMethod]
    public void Receipt_IsNotRestoredAfterDisposalOrNewOwner()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        var firstLibrary = CreateLr2Library(scope.SongDbPath);
        BMSLibrary.Lr2SynchronizationOwner firstOwner = (BMSLibrary.Lr2SynchronizationOwner)firstLibrary.Lr2Synchronization;
        firstOwner.CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt(
            0,
            0,
            [Path.Combine(scope.DirectoryPath, "disposed.bms")]);

        firstOwner.DisposeCancellation();

        Assert.IsNull(firstOwner.CommittedPathReceipt);
        var secondLibrary = CreateLr2Library(scope.SongDbPath);
        BMSLibrary.Lr2SynchronizationOwner secondOwner = (BMSLibrary.Lr2SynchronizationOwner)secondLibrary.Lr2Synchronization;
        Assert.IsNull(secondOwner.CommittedPathReceipt);
    }

    [TestMethod]
    public void ReceiptEligibleSongRowsSkipReaderAndCurrentnessRead()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string skippedPath = Path.Combine(scope.DirectoryPath, "skipped.bms");
        string processedPath = Path.Combine(scope.DirectoryPath, "processed.bms");
        File.WriteAllText(processedPath, "#TITLE receipt processed\r\n");
        ChartFileSnapshot processedSnapshot = ChartFileContentReader.ReadSnapshot(processedPath);
        var skippedFile = new TestableBmsFile
        {
            path = skippedPath,
            date = 123456
        }.WithHashAndFavorite("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", favoriteValue: null);
        TestableBmsFile processedFile = CreateSyncTestFile(processedPath, processedSnapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.InsertOrReplace(processedFile.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
        int skippedReaderCalls = 0;
        int processedReaderCalls = 0;
        Lr2SongDbSyncResult result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
        {
            Signature = "receipt-reader-gate",
            RunId = "receipt-reader-gate",
            SongRows = [skippedFile, processedFile],
            CommittedPathReceipt = new Lr2SongDbSyncCommittedPathReceipt(0, 0, [skippedPath]),
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
}
