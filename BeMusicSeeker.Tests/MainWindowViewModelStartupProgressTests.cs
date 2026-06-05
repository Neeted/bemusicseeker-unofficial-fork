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
        Assert.AreEqual(18, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("Startup"));
        Assert.AreEqual(6, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("ReloadFileDiff"));
        Assert.AreEqual(4, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("ScoreOnly"));
        Assert.AreEqual(15, MainWindowViewModel.GetInitialStartupProgressExpectedCountForTest("FullReinitialize"));
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
    public void StartupPresentationDeferPolicy_AllowsBasicCatalogFlushOnlyAtUiSuppressEnd()
    {
        Assert.IsFalse(MainWindowViewModel.IsStartupPresentationDeferredForTest(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            startupUiSuppressFlush: true,
            libraryMainView: true,
            libraryFolderTree: false,
            playlistTree: false,
            duplicateTree: false));
        Assert.IsFalse(MainWindowViewModel.IsStartupPresentationDeferredForTest(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            startupUiSuppressFlush: true,
            libraryMainView: false,
            libraryFolderTree: true,
            playlistTree: false,
            duplicateTree: false));
        Assert.IsFalse(MainWindowViewModel.IsStartupPresentationDeferredForTest(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            startupUiSuppressFlush: true,
            libraryMainView: false,
            libraryFolderTree: false,
            playlistTree: true,
            duplicateTree: false));
        Assert.IsTrue(MainWindowViewModel.IsStartupPresentationDeferredForTest(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            startupUiSuppressFlush: true,
            libraryMainView: false,
            libraryFolderTree: false,
            playlistTree: false,
            duplicateTree: true));
        Assert.IsTrue(MainWindowViewModel.IsStartupPresentationDeferredForTest(
            MainWindowViewModel.viewUpdateMode.FileMissingFilterSelected,
            startupUiSuppressFlush: true,
            libraryMainView: true,
            libraryFolderTree: false,
            playlistTree: false,
            duplicateTree: false));
        Assert.IsFalse(MainWindowViewModel.IsStartupPresentationDeferredForTest(
            MainWindowViewModel.viewUpdateMode.FullScanAllChartsFilterSelected,
            startupUiSuppressFlush: true,
            libraryMainView: true,
            libraryFolderTree: false,
            playlistTree: false,
            duplicateTree: false));
        foreach (MainWindowViewModel.viewUpdateMode maintenanceMode in new[]
        {
            MainWindowViewModel.viewUpdateMode.FileMissingFilterSelected,
            MainWindowViewModel.viewUpdateMode.FileMissingIgnoredFilterSelected,
            MainWindowViewModel.viewUpdateMode.DuplicateFilterSelected,
            MainWindowViewModel.viewUpdateMode.GarbledFilterSelected,
            MainWindowViewModel.viewUpdateMode.GarbleFixedFilterSelected,
            MainWindowViewModel.viewUpdateMode.UnregisteredFilterSelected,
            MainWindowViewModel.viewUpdateMode.ZeroNoteFilterSelected,
            MainWindowViewModel.viewUpdateMode.ChartInfoParseErrorFilterSelected
        })
        {
            Assert.IsTrue(MainWindowViewModel.IsStartupPresentationDeferredForTest(
                maintenanceMode,
                startupUiSuppressFlush: true,
                libraryMainView: true,
                libraryFolderTree: false,
                playlistTree: false,
                duplicateTree: false),
                maintenanceMode.ToString());
        }
        Assert.IsTrue(MainWindowViewModel.IsStartupPresentationDeferredForTest(
            MainWindowViewModel.viewUpdateMode.FolderFilterSelected,
            startupUiSuppressFlush: false,
            libraryMainView: true,
            libraryFolderTree: false,
            playlistTree: false,
            duplicateTree: false));
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

        Assert.AreEqual(15, result.ExpectedCount);
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

        Assert.AreEqual(18, result.ExpectedCount);
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

        Assert.AreEqual(15, result.ExpectedCount);
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

        Assert.AreEqual(15, result.ExpectedCount);
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

        Assert.AreEqual(15, result.ExpectedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(0, result.IgnoredCompleteCount);
    }

    [TestMethod]
    public void StartupProgress_InstallableMaintenanceRequiresRequestBeforeCompletion()
    {
        MainWindowViewModel.StartupProgressTestResult stale = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "complete:InstallableMaintenanceDeferredDone");

        Assert.AreEqual(15, stale.ExpectedCount);
        Assert.AreEqual(1, stale.CompletedCount);
        Assert.AreEqual(1, stale.IgnoredCompleteCount);

        MainWindowViewModel.StartupProgressTestResult requested = MainWindowViewModel.ReduceStartupProgressForTest(
            "FullReinitialize",
            "request:InstallableMaintenanceDeferredDone",
            "complete:InstallableMaintenanceDeferredDone");

        Assert.AreEqual(15, requested.ExpectedCount);
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

        Assert.AreEqual(15, result.ExpectedCount);
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
            "skip:Lr2FullGenerationBackfillDone",
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
    public void StartupProgress_Lr2FullGenerationBackfillUsesDedicatedSubLabel()
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
            "skip:ChartInfoBackfillDone",
            "skip:ChartDigestBackfillDone",
            "skip:PlaylistReferenceApplied",
            "skip:ExternalPlaylistSyncDone",
            "skip:ScoreHydrationDone",
            "skip:RankingRefreshDone",
            "skip:MaintenanceDeferredDone",
            "skip:InstallableMaintenanceDeferredDone",
            "request:Lr2FullGenerationBackfillDone",
            "lr2full:100|25|song_rows");

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, result.Label);
        Assert.AreEqual("[25/100] " + Resources.Statusbar_progress_phase_lr2_full_generation + " song rows", result.SubLabel);
        Assert.IsFalse(result.IsCompleted);
    }

    [TestMethod]
    public void StartupProgress_Lr2FullGenerationRequestAfterSkip_DoesNotMoveBackToPending()
    {
        MainWindowViewModel.StartupProgressTestResult result = MainWindowViewModel.ReduceStartupProgressForTest(
            "Startup",
            "skip:Lr2FullGenerationBackfillDone",
            "request:Lr2FullGenerationBackfillDone");

        Assert.AreEqual(18, result.ExpectedCount);
        Assert.AreEqual(2, result.CompletedCount);
        Assert.AreEqual(1, result.RequestedCount);
        Assert.AreEqual(1, result.SkippedCount);
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
        Assert.AreEqual("LR2 song.db 完全生成", Resources.Statusbar_progress_phase_lr2_full_generation);
    }
}
