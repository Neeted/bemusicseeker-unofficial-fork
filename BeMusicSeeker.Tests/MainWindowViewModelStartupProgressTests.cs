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
        Assert.AreEqual(17, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("Startup"));
        Assert.AreEqual(6, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("ReloadFileDiff"));
        Assert.AreEqual(4, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("ScoreOnly"));
        Assert.AreEqual(14, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("FullReinitialize"));
        Assert.AreEqual(5, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("ReloadTables"));
    }

    [TestMethod]
    public void StartupProgress_ReloadTables_IgnoresScoreRankingMaintenanceRequests()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ReloadTables",
            "request:ScoreHydrationDone",
            "request:RankingRefreshDone",
            "request:MaintenanceDeferredDone",
            "request:InstallableMaintenanceDeferredDone");

        Assert.AreEqual(5, result.ExpectedCount);
        Assert.AreEqual(1, result.CompletedCount);
        Assert.AreEqual(0, result.RequestedCount);
        Assert.AreEqual(4, result.IgnoredRequestCount);
    }

    [TestMethod]
    public void StartupProgress_ScoreOnly_TracksOnlyScorePhases()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ScoreOnly",
            "complete:StartupReadyOperable",
            "request:ScoreHydrationDone",
            "complete:ScoreHydrationDone",
            "request:RankingRefreshDone",
            "complete:RankingRefreshDone",
            "request:PlaylistEntriesHydrationDone",
            "request:ExternalPlaylistSyncDone",
            "request:PlaylistReferenceApplied");

        Assert.AreEqual(4, result.ExpectedCount);
        Assert.AreEqual(4, result.CompletedCount);
        Assert.AreEqual(2, result.RequestedCount);
        Assert.AreEqual(3, result.IgnoredRequestCount);
        Assert.AreEqual(Resources.Statusbar_progress_complete_scores, result.Label);
        Assert.IsTrue(result.IsCompleted);
    }

    [TestMethod]
    public void StartupBackgroundScheduler_ResetKeepsPostStartupReloadTasksRunnable()
    {
        Assert.IsFalse(MainWindowViewModel.ShouldStartStartupBackgroundTaskSchedulerAfterResetForTest("Startup", operableReached: false));
        Assert.IsFalse(MainWindowViewModel.ShouldStartStartupBackgroundTaskSchedulerAfterResetForTest("Startup", operableReached: true));
        Assert.IsTrue(MainWindowViewModel.ShouldStartStartupBackgroundTaskSchedulerAfterResetForTest("ScoreOnly", operableReached: true));
        Assert.IsFalse(MainWindowViewModel.ShouldStartStartupBackgroundTaskSchedulerAfterResetForTest("ReloadTables", operableReached: false));
        Assert.IsTrue(MainWindowViewModel.ShouldStartStartupBackgroundTaskSchedulerAfterResetForTest("ReloadTables", operableReached: true));
        Assert.IsTrue(MainWindowViewModel.ShouldStartStartupBackgroundTaskSchedulerAfterResetForTest("ReloadFileDiff", operableReached: true));
        Assert.IsTrue(MainWindowViewModel.ShouldStartStartupBackgroundTaskSchedulerAfterResetForTest("FullReinitialize", operableReached: true));
    }

    [TestMethod]
    public void StartupReadyUiMask_RequiresInstallTreeOnly()
    {
        Assert.IsFalse(MainWindowViewModel.IsStartupReadyUiMaskSatisfiedForTest(
            installTree: false,
            libraryMainView: true,
            playlistTree: true));
        Assert.IsTrue(MainWindowViewModel.IsStartupReadyUiMaskSatisfiedForTest(
            installTree: true,
            libraryMainView: false,
            playlistTree: false));
        Assert.IsTrue(MainWindowViewModel.IsStartupReadyUiMaskSatisfiedForTest(
            installTree: true,
            libraryMainView: true,
            playlistTree: true));
    }

    [TestMethod]
    public void StartupProgress_ReloadFileDiff_TracksOnlyFileDiffAndPlaylistPhases()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ReloadFileDiff",
            "complete:LibraryFileEnumerationDone",
            "complete:LibraryFileDiffDone",
            "complete:StartupReadyOperable",
            "request:ChartInfoHydrationDone",
            "request:InstallableMaintenanceDeferredDone",
            "request:PlaylistReferenceApplied",
            "request:PlaylistEntriesHydrationDone",
            "complete:PlaylistReferenceApplied",
            "complete:PlaylistEntriesHydrationDone");

        Assert.AreEqual(6, result.ExpectedCount);
        Assert.AreEqual(6, result.CompletedCount);
        Assert.AreEqual(2, result.IgnoredRequestCount);
        Assert.IsTrue(result.IsCompleted);
    }

    [TestMethod]
    public void StartupProgress_StaleCompletionWithoutRequest_DoesNotCompleteBackgroundPhase()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "complete:ScoreHydrationDone");

        Assert.AreEqual(14, result.ExpectedCount);
        Assert.AreEqual(1, result.CompletedCount);
        Assert.AreEqual(1, result.IgnoredCompleteCount);
        Assert.IsFalse(result.IsCompleted);
    }

    [TestMethod]
    public void StartupProgress_ChartInfoCompletionWithoutRequest_DoesNotCompleteBackgroundPhase()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "Startup",
            "complete:ChartInfoHydrationDone",
            "complete:ChartInfoBackfillDone",
            "complete:ChartDigestBackfillDone");

        Assert.AreEqual(17, result.ExpectedCount);
        Assert.AreEqual(1, result.CompletedCount);
        Assert.AreEqual(3, result.IgnoredCompleteCount);
        Assert.IsFalse(result.IsCompleted);
    }

    [TestMethod]
    public void StartupProgress_SkipCompletesWithoutIncreasingMaximum()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "skip:ChartDigestBackfillDone");

        Assert.AreEqual(14, result.ExpectedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(1, result.SkippedCount);
    }

    [TestMethod]
    public void StartupProgress_RequestAfterSkip_DoesNotMovePhaseBackToPending()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "skip:ChartDigestBackfillDone",
            "request:ChartDigestBackfillDone");

        Assert.AreEqual(14, result.ExpectedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(1, result.RequestedCount);
        Assert.AreEqual(1, result.SkippedCount);
    }

    [TestMethod]
    public void StartupProgress_OperableWithBackgroundWork_UsesOperableBackgroundLabel()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "complete:StartupReadyOperable");

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, result.Label);
        Assert.IsFalse(result.IsCompleted);
    }

    [TestMethod]
    public void StartupProgress_FullReinitialize_DirectPlaylistEntriesHydrationCanComplete()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "request:PlaylistEntriesHydrationDone",
            "complete:PlaylistEntriesHydrationDone");

        Assert.AreEqual(14, result.ExpectedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(0, result.IgnoredCompleteCount);
    }

    [TestMethod]
    public void StartupProgress_InstallableMaintenanceRequiresRequestBeforeCompletion()
    {
        MainWindowViewModel.StartupProgressTestResult stale = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "complete:InstallableMaintenanceDeferredDone");

        Assert.AreEqual(14, stale.ExpectedCount);
        Assert.AreEqual(1, stale.CompletedCount);
        Assert.AreEqual(1, stale.IgnoredCompleteCount);

        MainWindowViewModel.StartupProgressTestResult requested = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "request:InstallableMaintenanceDeferredDone",
            "complete:InstallableMaintenanceDeferredDone");

        Assert.AreEqual(14, requested.ExpectedCount);
        Assert.AreEqual(2, requested.CompletedCount);
        Assert.AreEqual(1, requested.RequestedCount);
        Assert.AreEqual(0, requested.IgnoredCompleteCount);
    }

    [TestMethod]
    public void StartupProgress_InstallableMaintenanceRequestAfterSkip_DoesNotMoveBackToPending()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "skip:InstallableMaintenanceDeferredDone",
            "request:InstallableMaintenanceDeferredDone");

        Assert.AreEqual(14, result.ExpectedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(1, result.RequestedCount);
        Assert.AreEqual(1, result.SkippedCount);
    }

    [TestMethod]
    public void StartupProgress_InstallableMaintenanceUsesDedicatedSubLabel()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "complete:LibraryDatabaseLoadDone",
            "complete:LibraryFileEnumerationDone",
            "complete:LibraryFileDiffDone",
            "complete:StartupReadyOperable",
            "skip:PlaylistEntriesHydrationDone",
            "skip:ChartInfoHydrationDone",
            "skip:ChartInfoBackfillDone",
            "skip:ChartDigestBackfillDone",
            "skip:PlaylistReferenceApplied",
            "skip:ScoreHydrationDone",
            "skip:RankingRefreshDone",
            "skip:MaintenanceDeferredDone",
            "request:InstallableMaintenanceDeferredDone");

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, result.Label);
        Assert.AreEqual(Resources.Statusbar_progress_phase_installable_maintenance, result.SubLabel);
        Assert.IsFalse(result.IsCompleted);
    }

    [TestMethod]
    public void StartupProgress_LibraryLoadSubLabel_FollowsSubPhase()
    {
        MainWindowViewModel.StartupProgressTestResult db = MainWindowViewModel.ReduceStartupProgressForTest("Startup");
        Assert.AreEqual(Resources.Statusbar_progress_phase_library_db_load, db.SubLabel);

        MainWindowViewModel.StartupProgressTestResult enumeration = MainWindowViewModel.ReduceStartupProgressForTest(
            "Startup",
            "complete:LibraryDatabaseLoadDone",
            "library:FileEnumeration|0|0||Everything");
        Assert.AreEqual(Resources.Statusbar_progress_phase_file_enumeration + " (Everything)", enumeration.SubLabel);

        MainWindowViewModel.StartupProgressTestResult diff = MainWindowViewModel.ReduceStartupProgressForTest(
            "Startup",
            "complete:LibraryDatabaseLoadDone",
            "complete:LibraryFileEnumerationDone",
            "library:FileDiff|10|3|C:\\BMS\\added.bms|");
        Assert.AreEqual("[3/10] " + Resources.Statusbar_progress_phase_file_diff + " added.bms", diff.SubLabel);
    }

    [TestMethod]
    public void StartupProgress_CountSubLabel_PutsFractionFirst()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "Startup",
            "complete:LibraryDatabaseLoadDone",
            "complete:LibraryFileEnumerationDone",
            "complete:LibraryFileDiffDone",
            "complete:StartupReadyData",
            "complete:StartupReadyUi",
            "complete:StartupReadyOperable",
            "skip:PlaylistEntriesHydrationDone",
            "skip:ChartInfoHydrationDone",
            "request:ChartInfoBackfillDone",
            "chartinfo:209999|6695|C:\\BMS\\metadata.bms");

        Assert.AreEqual("[6695/209999] " + Resources.Statusbar_progress_phase_chart_info + " metadata.bms", result.SubLabel);
    }

    [TestMethod]
    public void StartupProgress_ChartInfoHydrationSubLabel_HidesFraction()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "Startup",
            "complete:LibraryDatabaseLoadDone",
            "complete:LibraryFileEnumerationDone",
            "complete:LibraryFileDiffDone",
            "complete:StartupReadyData",
            "complete:StartupReadyUi",
            "complete:StartupReadyOperable",
            "skip:PlaylistEntriesHydrationDone",
            "request:ChartInfoHydrationDone",
            "hydrate:209999|1200");

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, result.Label);
        Assert.AreEqual(Resources.Statusbar_progress_phase_chart_info_load, result.SubLabel);
        Assert.IsFalse(result.SubLabel.Contains("["));
    }


    [TestMethod]
    public void StartupProgress_ChartInfoBackfillRequestedBeforeSkip_RemainsVisibleAfterHydration()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "Startup",
            "complete:LibraryDatabaseLoadDone",
            "complete:LibraryFileEnumerationDone",
            "complete:LibraryFileDiffDone",
            "complete:StartupReadyData",
            "complete:StartupReadyUi",
            "complete:StartupReadyOperable",
            "skip:PlaylistEntriesHydrationDone",
            "request:ChartInfoHydrationDone",
            "request:ChartInfoBackfillDone",
            "skip:ChartInfoBackfillDone",
            "complete:ChartInfoHydrationDone",
            "chartinfo:209875|6695|C:\\BMS\\metadata.bms");

        Assert.IsFalse(result.IsCompleted);
        Assert.AreEqual(Resources.Statusbar_progress_operable_background, result.Label);
        Assert.AreEqual("[6695/209875] " + Resources.Statusbar_progress_phase_chart_info + " metadata.bms", result.SubLabel);
        Assert.AreEqual(1, result.SkippedCount);
    }

    [TestMethod]
    public void StartupProgress_ReloadTables_CompletesWithExternalSyncAndDirectReferenceApply()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "ReloadTables",
            "complete:StartupReadyOperable",
            "request:ExternalPlaylistSyncDone",
            "request:PlaylistReferenceApplied",
            "complete:PlaylistReferenceApplied",
            "complete:ExternalPlaylistSyncDone",
            "skip:PlaylistEntriesHydrationDone");

        Assert.AreEqual(5, result.ExpectedCount);
        Assert.AreEqual(5, result.CompletedCount);
        Assert.AreEqual(Resources.Statusbar_progress_complete_reload, result.Label);
        Assert.IsTrue(result.IsCompleted);
    }

    [TestMethod]
    public void StartupProgress_OperableBackgroundResource_IsPresent()
    {
        Assert.AreEqual("操作可能(バックグラウンド更新中)", Resources.Statusbar_progress_operable_background);
        Assert.AreEqual("譜面メタデータ反映", Resources.Statusbar_progress_phase_chart_info_load);
    }
}
