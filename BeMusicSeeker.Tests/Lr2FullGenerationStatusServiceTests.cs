using System;
using System.IO;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FullGenerationStatusServiceTests
{
    [TestMethod]
    public void Evaluate_ReturnsNotNeededWhenFeatureIsDisabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2FullGenerationStatusSnapshot snapshot = Lr2FullGenerationStatusService.Evaluate(
            songDb,
            enabled: false,
            signature: "sig1",
            nowUtc: now);

        Assert.AreEqual(Lr2FullGenerationStatusKind.NotNeeded, snapshot.Status);
        Assert.IsFalse(snapshot.IsNeeded);
    }

    [TestMethod]
    public void Evaluate_ReturnsNeededWhenStatusRowIsMissing()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2FullGenerationStatusSnapshot snapshot = Lr2FullGenerationStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig1",
            nowUtc: now);

        Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, snapshot.Status);
        Assert.IsTrue(snapshot.IsNeeded);
        Assert.AreEqual("sig1", snapshot.Signature);
    }

    [TestMethod]
    public void Evaluate_ReturnsCompletedOnlyForMatchingCompletedSignature()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2FullGenerationStatusService.MarkCompleted(songDb, "sig1", "run1", totalCount: 10, nowUtc: now);

        Lr2FullGenerationStatusSnapshot sameSignature = Lr2FullGenerationStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig1",
            nowUtc: now.AddMinutes(1));
        Lr2FullGenerationStatusSnapshot changedSignature = Lr2FullGenerationStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig2",
            nowUtc: now.AddMinutes(1));

        Assert.AreEqual(Lr2FullGenerationStatusKind.Completed, sameSignature.Status);
        Assert.AreEqual(10, sameSignature.ProcessedCursor);
        Assert.AreEqual(10, sameSignature.TotalCount);
        Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, changedSignature.Status);
        Assert.AreEqual(Lr2FullGenerationStatusKind.Completed, changedSignature.StoredStatus);
    }

    [TestMethod]
    public void Evaluate_ReturnsNeededForRestartableTerminalStates()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2FullGenerationStatusService.MarkFailed(songDb, "sig1", "run1", processedCursor: 3, totalCount: 10, stage: "song", error: "failed", nowUtc: now);
        AssertNeededWithStoredStatus(songDb, Lr2FullGenerationStatusKind.Failed);

        Lr2FullGenerationStatusService.MarkCancelled(songDb, "sig1", "run2", processedCursor: 4, totalCount: 10, stage: "folder", nowUtc: now.AddMinutes(1));
        AssertNeededWithStoredStatus(songDb, Lr2FullGenerationStatusKind.Cancelled);

        Lr2FullGenerationStatusService.MarkIncomplete(songDb, "sig1", "run3", processedCursor: 5, totalCount: 10, stage: "finalize", detail: "stale", nowUtc: now.AddMinutes(2));
        AssertNeededWithStoredStatus(songDb, Lr2FullGenerationStatusKind.Incomplete);
    }

    [TestMethod]
    public void UpdateCursor_PersistsProgressForResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        DateTime now = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);

        Lr2FullGenerationStatusService.MarkRunning(songDb, "sig1", "run1", totalCount: 100, stage: "song", nowUtc: now);
        Lr2FullGenerationStatusService.UpdateCursor(songDb, "sig1", "run1", processedCursor: 40, totalCount: 100, stage: "song", nowUtc: now.AddMinutes(1));

        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
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

        Lr2FullGenerationStatusService.MarkRunning(songDb, "sig1", "run1", totalCount: 100, stage: "song", nowUtc: now);
        Lr2FullGenerationStatusService.UpdateCursor(songDb, "sig1", "run1", processedCursor: 40, totalCount: 100, stage: "song", nowUtc: now.AddMinutes(1));
        Lr2FullGenerationStatusService.MarkFailed(songDb, "sig1", "run1", processedCursor: null, totalCount: null, stage: "failed", error: "boom", nowUtc: now.AddMinutes(2));

        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
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

        songDb.InsertOrReplace(new LR2SongDBExtended.lr2_full_generation_status
        {
            name = Lr2FullGenerationStatusService.DefaultStatusName,
            status = "Completed",
            signature = "sig1",
            updated_at = DateTime.UtcNow
        }, typeof(LR2SongDBExtended.lr2_full_generation_status));
        Assert.IsNotNull(songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName));
    }

    private static void AssertNeededWithStoredStatus(LR2SongDBExtended songDb, Lr2FullGenerationStatusKind expectedStoredStatus)
    {
        Lr2FullGenerationStatusSnapshot snapshot = Lr2FullGenerationStatusService.Evaluate(
            songDb,
            enabled: true,
            signature: "sig1",
            nowUtc: new DateTime(2026, 6, 4, 2, 0, 0, DateTimeKind.Utc));

        Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, snapshot.Status);
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
            string directoryPath = Path.Combine(Path.GetTempPath(), nameof(Lr2FullGenerationStatusServiceTests), Guid.NewGuid().ToString("N"));
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
