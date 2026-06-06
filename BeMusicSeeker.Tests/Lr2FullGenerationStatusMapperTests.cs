using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FullGenerationStatusMapperTests
{
    [TestMethod]
    public void Create_NeededBuildsWarningStatus()
    {
        Lr2FullGenerationRuntimeStatus status = Lr2FullGenerationStatusMapper.Create(new Lr2FullGenerationStatusSnapshot
        {
            Status = Lr2FullGenerationStatusKind.Needed,
            StoredStatus = Lr2FullGenerationStatusKind.Failed,
            Stage = "song_rows",
            LastError = "failed before",
            ProcessedCursor = 12,
            TotalCount = 100
        }, new DateTime(2026, 6, 5, 12, 0, 0));

        Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, status.Kind);
        Assert.AreEqual(Resources.Lr2_full_generation_status_needed, status.StatusText);
        Assert.IsTrue(status.HasWarningStatus);
        Assert.IsTrue(status.CanRetry);
        Assert.IsFalse(status.CanCancel);
        Assert.IsFalse(status.CanCleanupStartupScanBlockers);
        StringAssert.Contains(status.Detail, "[12/100]");
        StringAssert.Contains(status.Detail, "song rows");
        StringAssert.Contains(status.Detail, "failed before");
        Assert.IsFalse(status.Detail.Contains(Resources.Lr2_full_generation_status_needed));
    }

    [TestMethod]
    public void Create_CompletedClearsWarningStatus()
    {
        Lr2FullGenerationRuntimeStatus status = Lr2FullGenerationStatusMapper.Create(new Lr2FullGenerationStatusSnapshot
        {
            Status = Lr2FullGenerationStatusKind.Completed,
            Stage = "completed"
        }, DateTime.MinValue);

        Assert.AreEqual(Resources.Lr2_full_generation_status_completed, status.StatusText);
        Assert.IsFalse(status.HasWarningStatus);
        Assert.IsFalse(status.CanRetry);
        Assert.IsFalse(status.CanCancel);
        Assert.IsFalse(status.CanCleanupStartupScanBlockers);
    }

    [TestMethod]
    public void Create_RunningBuildsVisibleRuntimeStatusWithoutRetry()
    {
        Lr2FullGenerationRuntimeStatus status = Lr2FullGenerationStatusMapper.Create(new Lr2FullGenerationStatusSnapshot
        {
            Status = Lr2FullGenerationStatusKind.Running,
            Stage = "song_rows",
            ProcessedCursor = 12,
            TotalCount = 100,
            StageProcessedCount = 4,
            StageTotalCount = 50
        }, new DateTime(2026, 6, 5, 12, 0, 0));

        Assert.AreEqual(Resources.Lr2_full_generation_status_running, status.StatusText);
        Assert.IsTrue(status.HasWarningStatus);
        Assert.IsFalse(status.CanRetry);
        Assert.IsTrue(status.CanCancel);
        Assert.IsFalse(status.CanCleanupStartupScanBlockers);
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
        Lr2FullGenerationRuntimeStatus failed = Lr2FullGenerationStatusMapper.Create(new Lr2FullGenerationStatusSnapshot
        {
            Status = Lr2FullGenerationStatusKind.Failed,
            LastError = "boom"
        }, DateTime.MinValue);
        Lr2FullGenerationRuntimeStatus incomplete = Lr2FullGenerationStatusMapper.Create(new Lr2FullGenerationStatusSnapshot
        {
            Status = Lr2FullGenerationStatusKind.Incomplete,
            LastError = "startup scan blockers"
        }, DateTime.MinValue);

        Assert.IsTrue(failed.HasWarningStatus);
        Assert.AreEqual(Resources.Lr2_full_generation_status_failed, failed.StatusText);
        StringAssert.Contains(failed.Detail, "boom");
        Assert.IsTrue(incomplete.HasWarningStatus);
        Assert.AreEqual(Resources.Lr2_full_generation_status_incomplete, incomplete.StatusText);
        StringAssert.Contains(incomplete.Detail, "startup scan blockers");
    }

    [TestMethod]
    public void Create_StartupScanBlockersIncompleteEnablesCleanup()
    {
        Lr2FullGenerationRuntimeStatus status = Lr2FullGenerationStatusMapper.Create(new Lr2FullGenerationStatusSnapshot
        {
            Status = Lr2FullGenerationStatusKind.Incomplete,
            Stage = Lr2FullGenerationSyncService.StartupScanBlockersStage,
            LastError = "startup scan blockers"
        }, DateTime.MinValue);

        Assert.IsFalse(status.CanRetry);
        Assert.IsTrue(status.CanCleanupStartupScanBlockers);
    }

    [TestMethod]
    public void Create_IncompleteNonStartupScanBlockerDoesNotEnableCleanup()
    {
        Lr2FullGenerationRuntimeStatus status = Lr2FullGenerationStatusMapper.Create(new Lr2FullGenerationStatusSnapshot
        {
            Status = Lr2FullGenerationStatusKind.Incomplete,
            Stage = "song_rows",
            LastError = "song rows"
        }, DateTime.MinValue);

        Assert.IsTrue(status.CanRetry);
        Assert.IsFalse(status.CanCleanupStartupScanBlockers);
    }
}
