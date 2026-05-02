using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowViewModelStartupProgressTests
{
    [TestMethod]
    public void StartupProgress_InitialExpectedCounts_AreFixedByOperation()
    {
        Assert.AreEqual(13, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("Startup"));
        Assert.AreEqual(10, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("ReloadFiles"));
        Assert.AreEqual(5, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("ReloadTables"));
    }

    [TestMethod]
    public void StartupProgress_ReloadTables_IgnoresScoreRankingMaintenanceRequests()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ReloadTables",
            "request:ScoreHydrationDone",
            "request:RankingRefreshDone",
            "request:MaintenanceDeferredDone");

        Assert.AreEqual(5, result.ExpectedCount);
        Assert.AreEqual(1, result.CompletedCount);
        Assert.AreEqual(0, result.RequestedCount);
        Assert.AreEqual(3, result.IgnoredRequestCount);
    }

    [TestMethod]
    public void StartupProgress_StaleCompletionWithoutRequest_DoesNotCompleteBackgroundPhase()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ReloadFiles",
            "complete:ScoreHydrationDone");

        Assert.AreEqual(10, result.ExpectedCount);
        Assert.AreEqual(1, result.CompletedCount);
        Assert.AreEqual(1, result.IgnoredCompleteCount);
        Assert.IsFalse(result.IsCompleted);
    }

    [TestMethod]
    public void StartupProgress_SkipCompletesWithoutIncreasingMaximum()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ReloadFiles",
            "skip:ChartDigestBackfillDone");

        Assert.AreEqual(10, result.ExpectedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(1, result.SkippedCount);
    }

    [TestMethod]
    public void StartupProgress_RequestAfterSkip_DoesNotMovePhaseBackToPending()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ReloadFiles",
            "skip:ChartDigestBackfillDone",
            "request:ChartDigestBackfillDone");

        Assert.AreEqual(10, result.ExpectedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(1, result.RequestedCount);
        Assert.AreEqual(1, result.SkippedCount);
    }

    [TestMethod]
    public void StartupProgress_OperableWithBackgroundWork_UsesOperableBackgroundLabel()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ReloadFiles",
            "complete:StartupReadyOperable");

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, result.Label);
        Assert.IsFalse(result.IsCompleted);
    }

    [TestMethod]
    public void StartupProgress_ReloadFiles_DirectPlaylistEntriesHydrationCanComplete()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ReloadFiles",
            "request:PlaylistEntriesHydrationDone",
            "complete:PlaylistEntriesHydrationDone");

        Assert.AreEqual(10, result.ExpectedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(0, result.IgnoredCompleteCount);
    }

    [TestMethod]
    public void StartupProgress_ReloadTables_CompletesWithExternalSyncAndSkippedReference()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ReloadTables",
            "complete:StartupReadyOperable",
            "request:ExternalPlaylistSyncDone",
            "complete:ExternalPlaylistSyncDone",
            "skip:PlaylistEntriesHydrationDone",
            "skip:PlaylistReferenceApplied");

        Assert.AreEqual(5, result.ExpectedCount);
        Assert.AreEqual(5, result.CompletedCount);
        Assert.AreEqual(Resources.Statusbar_progress_complete_reload, result.Label);
        Assert.IsTrue(result.IsCompleted);
    }

    [TestMethod]
    public void StartupProgress_OperableBackgroundResource_IsPresent()
    {
        Assert.AreEqual("操作可能(バックグラウンド更新中)", Resources.Statusbar_progress_operable_background);
    }
}
