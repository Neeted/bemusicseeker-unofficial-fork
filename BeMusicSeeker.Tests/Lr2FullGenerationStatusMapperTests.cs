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
        StringAssert.Contains(status.Detail, "[12/100]");
        StringAssert.Contains(status.Detail, "song rows");
        StringAssert.Contains(status.Detail, "failed before");
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
}
