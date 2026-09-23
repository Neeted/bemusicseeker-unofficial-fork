using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncStatusMapperTests
{
    [TestMethod]
    public void Create_NeededBuildsWarningStatus()
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Needed,
            StoredStatus = Lr2SongDbSyncStatusKind.Failed,
            Stage = "song_rows",
            LastError = "failed before",
            ProcessedCursor = 12,
            TotalCount = 100
        }, new DateTime(2026, 6, 5, 12, 0, 0));

        Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, status.Kind);
        Assert.AreEqual(Resources.Lr2_song_db_sync_status_needed, status.StatusText);
        Assert.IsTrue(status.HasWarningStatus);
        Assert.IsTrue(status.CanRetry);
        StringAssert.Contains(status.Detail, "[12/100]");
        StringAssert.Contains(status.Detail, "song rows");
        StringAssert.Contains(status.Detail, "failed before");
        Assert.IsFalse(status.Detail.Contains(Resources.Lr2_song_db_sync_status_needed));
    }

    [TestMethod]
    public void Create_CompletedClearsWarningStatus()
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Completed,
            Stage = "completed"
        }, DateTime.MinValue);

        Assert.AreEqual(Resources.Lr2_song_db_sync_status_completed, status.StatusText);
        Assert.IsFalse(status.HasWarningStatus);
        Assert.IsFalse(status.CanRetry);
    }

    [TestMethod]
    public void Create_RunningBuildsVisibleRuntimeStatusWithoutRetry()
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Running,
            Stage = "song_rows",
            ProcessedCursor = 12,
            TotalCount = 100,
            StageProcessedCount = 4,
            StageTotalCount = 50
        }, new DateTime(2026, 6, 5, 12, 0, 0));

        Assert.AreEqual(Resources.Lr2_song_db_sync_status_running, status.StatusText);
        Assert.IsTrue(status.HasWarningStatus);
        Assert.IsFalse(status.CanRetry);
        StringAssert.Contains(status.Detail, "[12/100]");
        StringAssert.Contains(status.Detail, "song rows");
        Assert.AreEqual("song rows [4/50]", status.ProgressText);
        Assert.IsTrue(status.HasProgress);
        Assert.AreEqual(4.0, status.ProgressValue);
        Assert.AreEqual(50.0, status.ProgressMaximum);
    }

    [TestMethod]
    public void Create_FailedAndIncompleteAreWarningStatuses()
    {
        Lr2SongDbSyncRuntimeStatus failed = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Failed,
            LastError = "boom"
        }, DateTime.MinValue);
        Lr2SongDbSyncRuntimeStatus incomplete = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Incomplete,
            LastError = "startup scan blockers"
        }, DateTime.MinValue);

        Assert.IsTrue(failed.HasWarningStatus);
        Assert.AreEqual(Resources.Lr2_song_db_sync_status_failed, failed.StatusText);
        StringAssert.Contains(failed.Detail, "boom");
        Assert.IsTrue(incomplete.HasWarningStatus);
        Assert.AreEqual(Resources.Lr2_song_db_sync_status_incomplete, incomplete.StatusText);
        StringAssert.Contains(incomplete.Detail, "startup scan blockers");
    }

    [TestMethod]
    public void Create_IncompleteRemainsRetryableWithoutCleanupRoute()
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Incomplete,
            Stage = "song_rows",
            LastError = "song rows"
        }, DateTime.MinValue);

        Assert.IsTrue(status.CanRetry);
    }

    [TestMethod]
    public void Create_LegacyCancelledRemainsIncompleteAndRetryable()
    {
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(new Lr2SongDbSyncStatusSnapshot
        {
            Status = Lr2SongDbSyncStatusKind.Cancelled,
            Stage = "cancelled",
            LastError = "legacy status"
        }, DateTime.MinValue);

        Assert.AreEqual(Resources.Lr2_song_db_sync_status_incomplete, status.StatusText);
        Assert.IsTrue(status.HasWarningStatus);
        Assert.IsTrue(status.CanRetry);
    }
}
