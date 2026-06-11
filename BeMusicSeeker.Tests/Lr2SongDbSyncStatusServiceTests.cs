using System;
using System.IO;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncStatusServiceTests
{
    [TestMethod]
    public void Evaluate_ReturnsNotNeededWhenFeatureIsDisabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusSnapshot snapshot = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: false,
            signature: "sig1",
            nowUtc: now);

        Assert.AreEqual(Lr2SongDbSyncStatusKind.NotNeeded, snapshot.Status);
        Assert.IsFalse(snapshot.IsNeeded);
    }

    [TestMethod]
    public void Evaluate_ReturnsNeededWhenStatusRowIsMissing()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusSnapshot snapshot = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig1",
            nowUtc: now);

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, snapshot.Status);
        Assert.IsTrue(snapshot.IsNeeded);
        Assert.AreEqual("sig1", snapshot.Signature);
    }

    [TestMethod]
    public void Evaluate_ReturnsCompletedOnlyForMatchingCompletedSignature()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkCompleted(songDb, "sig1", "run1", totalCount: 10, nowUtc: now);

        Lr2SongDbSyncStatusSnapshot sameSignature = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig1",
            nowUtc: now.AddMinutes(1));
        Lr2SongDbSyncStatusSnapshot changedSignature = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig2",
            nowUtc: now.AddMinutes(1));

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, sameSignature.Status);
        Assert.AreEqual(10, sameSignature.ProcessedCursor);
        Assert.AreEqual(10, sameSignature.TotalCount);
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, changedSignature.Status);
        Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, changedSignature.StoredStatus);
    }

    [TestMethod]
    public void Evaluate_ReturnsNeededForRestartableTerminalStates()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkFailed(songDb, "sig1", "run1", processedCursor: 3, totalCount: 10, stage: "song", error: "failed", nowUtc: now);
        AssertNeededWithStoredStatus(songDb, Lr2SongDbSyncStatusKind.Failed);

        Lr2SongDbSyncStatusService.MarkCancelled(songDb, "sig1", "run2", processedCursor: 4, totalCount: 10, stage: "folder", nowUtc: now.AddMinutes(1));
        AssertNeededWithStoredStatus(songDb, Lr2SongDbSyncStatusKind.Cancelled);

        Lr2SongDbSyncStatusService.MarkIncomplete(songDb, "sig1", "run3", processedCursor: 5, totalCount: 10, stage: "finalize", detail: "stale", nowUtc: now.AddMinutes(2));
        AssertNeededWithStoredStatus(songDb, Lr2SongDbSyncStatusKind.Incomplete);
    }

    [TestMethod]
    public void UpdateCursor_PersistsProgressForResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkRunning(songDb, "sig1", "run1", totalCount: 100, stage: "song", nowUtc: now);
        Lr2SongDbSyncStatusService.UpdateCursor(songDb, "sig1", "run1", processedCursor: 40, totalCount: 100, stage: "song", nowUtc: now.AddMinutes(1));

        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Running", row.status);
        Assert.AreEqual("sig1", row.signature);
        Assert.AreEqual("run1", row.run_id);
        Assert.AreEqual(40, row.processed_cursor);
        Assert.AreEqual(100, row.total_count);
        Assert.AreEqual("song", row.stage);
        Assert.IsNull(row.completed_at);
    }

    [TestMethod]
    public void MarkFailed_PreservesSameRunCursorWhenNoCursorIsProvided()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2SongDbSyncStatusService.MarkRunning(songDb, "sig1", "run1", totalCount: 100, stage: "song", nowUtc: now);
        Lr2SongDbSyncStatusService.UpdateCursor(songDb, "sig1", "run1", processedCursor: 40, totalCount: 100, stage: "song", nowUtc: now.AddMinutes(1));
        Lr2SongDbSyncStatusService.MarkFailed(songDb, "sig1", "run1", processedCursor: null, totalCount: null, stage: "failed", error: "boom", nowUtc: now.AddMinutes(2));

        LR2SongDBExtended.lr2_song_db_sync_status row = songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
        Assert.AreEqual("Failed", row.status);
        Assert.AreEqual(40, row.processed_cursor);
        Assert.AreEqual(100, row.total_count);
        Assert.AreEqual("failed", row.stage);
    }

    [TestMethod]
    public void EnsureAppOwnedSchema_CreatesStatusTable()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);

        BmsLibraryDbGateway.EnsureAppOwnedSchema(songDb);

        songDb.InsertOrReplace(new LR2SongDBExtended.lr2_song_db_sync_status
        {
            name = Lr2SongDbSyncStatusService.DefaultStatusName,
            status = "Completed",
            signature = "sig1",
            updated_at = DateTime.UtcNow
        }, typeof(LR2SongDBExtended.lr2_song_db_sync_status));
        Assert.IsNotNull(songDb.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName));
    }

    private static void AssertNeededWithStoredStatus(LR2SongDBExtended songDb, Lr2SongDbSyncStatusKind expectedStoredStatus)
    {
        Lr2SongDbSyncStatusSnapshot snapshot = Lr2SongDbSyncStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig1",
            nowUtc: new DateTime(2026, 6, 4, 2, 0, 0, DateTimeKind.Utc));

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, snapshot.Status);
        Assert.AreEqual(expectedStoredStatus, snapshot.StoredStatus);
    }

    private sealed class TestDatabaseScope : IDisposable
    {
        private readonly string directoryPath;

        public string SongDbPath { get; }

        private TestDatabaseScope(string directoryPath)
        {
            this.directoryPath = directoryPath;
            SongDbPath = Path.Combine(directoryPath, "song.db");
        }

        public static TestDatabaseScope Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), nameof(Lr2SongDbSyncStatusServiceTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return new TestDatabaseScope(directoryPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
