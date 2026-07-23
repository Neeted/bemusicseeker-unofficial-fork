using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowViewModelStartupProgressTests
{
    [TestMethod]
    public void StartupProgress_InitialExpectedCounts_ArePublishedByOwner()
    {
        Assert.AreEqual(19.0, Start(StartupProgressOperationKind.Startup).Maximum);
        Assert.AreEqual(6.0, Start(StartupProgressOperationKind.ReloadFileDiff).Maximum);
        Assert.AreEqual(4.0, Start(StartupProgressOperationKind.ScoreOnly).Maximum);
        Assert.AreEqual(15.0, Start(StartupProgressOperationKind.FullReinitialize).Maximum);
        Assert.AreEqual(5.0, Start(StartupProgressOperationKind.ReloadTables).Maximum);
    }

    [TestMethod]
    public void StartupProgress_RequestForUnexpectedPhaseDoesNotChangeOwnerState()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ReloadTables);

        owner.TrackStartupProgressScoreHydrationRequested(1);

        Assert.AreEqual(5.0, owner.Maximum);
        Assert.AreEqual(1.0, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_reload_tables, owner.Label);
    }

    [TestMethod]
    public void StartupProgress_ScoreOnlyCompletesThroughRuntimeRoutes()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ScoreOnly);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        owner.TrackStartupProgressScoreHydrationRequested(1);
        owner.TryCompleteStartupProgressScoreHydration(1);
        owner.TrackStartupProgressRankingRefreshRequested(1);
        owner.TryCompleteStartupProgressRankingRefresh(1);

        Assert.AreEqual(4.0, owner.Value);
        Assert.AreEqual(4.0, owner.Maximum);
        Assert.AreEqual(Resources.Statusbar_progress_complete_scores, owner.Label);
    }

    [TestMethod]
    public void StartupProgress_RequestEligibilityUsesCurrentOperationAtomically()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        owner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);

        owner.TrackStartupProgressScoreHydrationRequested(1);

        Assert.AreEqual(5.0, owner.Maximum);
        Assert.AreEqual(1.0, owner.Value);
        Assert.AreEqual(StartupProgressOperationKind.ReloadTables, owner.CurrentOperationKind);
    }

    [TestMethod]
    public void StartupProgress_StaleTokenCannotCompleteNewOperation()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        long staleToken = owner.GetActiveStartupProgressOperationToken();
        owner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);

        owner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable, staleToken);

        Assert.AreEqual(1.0, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_reload_tables, owner.Label);
    }

    [TestMethod]
    public void StartupProgress_PublishesNewTokenBeforePrepareCallback()
    {
        StartupProgressWorkflowOwner owner = null!;
        long observedToken = 0L;
        long observedCallbackToken = 0L;
        owner = new StartupProgressWorkflowOwner(
            () => new StartupProgressVersionSnapshot(),
            (operationKind, operationToken) =>
            {
                observedCallbackToken = operationToken;
                observedToken = owner.GetActiveStartupProgressOperationToken();
            },
            () => { },
            action => action(),
            _ => { },
            _ => { },
            () => true,
            (_, _) => true,
            new object());

        long startedToken = owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);

        Assert.AreEqual(startedToken, observedCallbackToken);
        Assert.AreEqual(startedToken, observedToken);
    }

    [TestMethod]
    public void StartupProgress_ScoreCompletionWithoutRequestIsIgnored()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);

        owner.TryCompleteStartupProgressScoreHydration(1);

        Assert.AreEqual(1.0, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_full_reinitialize, owner.Label);
    }

    [TestMethod]
    public void StartupProgress_ReloadFileDiffCompletesFileAndPlaylistPhases()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ReloadFileDiff);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        owner.TrackStartupProgressPlaylistReferenceRequest("ReloadFileDiff", 1);
        owner.TrackStartupProgressPlaylistEntriesHydrationDirectRequest(1, "ReloadFileDiff");
        owner.TryCompleteStartupProgressPlaylistReference(1);
        owner.TryCompleteStartupProgressPlaylistEntriesHydration(1);

        Assert.AreEqual(6.0, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_complete_reload, owner.Label);
    }

    [TestMethod]
    public void StartupProgress_BackgroundTasksCompleteAfterSkippedPhases()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        SkipAllOptionalStartupPhases(owner);
        Mark(owner, StartupProgressPhase.StartupBackgroundTasksDone);

        Assert.AreEqual(19.0, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_complete, owner.Label);
    }

    [TestMethod]
    public async Task StartupProgress_CompletionHidePublishesInactiveLifecycleChange()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        var changedProperties = new List<string>();
        owner.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        CompleteUntilBackground(owner);
        Skip(owner, StartupProgressPhase.InstallableMaintenanceDeferredDone);
        Mark(owner, StartupProgressPhase.StartupBackgroundTasksDone);

        Assert.IsTrue(owner.IsOperationActive);
        await Task.Delay(4000).ConfigureAwait(false);

        Assert.IsFalse(owner.IsOperationActive);
        CollectionAssert.Contains(changedProperties, nameof(StartupProgressWorkflowOwner.IsOperationActive));
    }

    [TestMethod]
    public void StartupProgress_SkipKeepsMaximumAndRequestAfterSkipKeepsCompletion()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        Skip(owner, StartupProgressPhase.ChartDigestBackfillDone);
        double valueAfterSkip = owner.Value;

        owner.TrackStartupProgressChartDigestBackfillRequested(1);
        owner.TryCompleteStartupProgressChartDigestBackfill(1);

        Assert.AreEqual(15.0, owner.Maximum);
        Assert.AreEqual(valueAfterSkip, owner.Value);
    }

    [TestMethod]
    public void StartupProgress_OperableWithBackgroundWorkUsesOperableLabel()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, owner.Label);
        Assert.AreEqual(Resources.Statusbar_progress_phase_playlist_load, owner.SubLabel);
    }

    [TestMethod]
    public void StartupProgress_PlaylistEntriesHydrationCanCompleteDirectly()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        owner.TrackStartupProgressPlaylistEntriesHydrationDirectRequest(1, "test");
        owner.TryCompleteStartupProgressPlaylistEntriesHydration(1);

        Assert.AreEqual(2.0, owner.Value);
    }

    [TestMethod]
    public void StartupProgress_InstallableMaintenanceRequiresRequest()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        owner.TryCompleteStartupProgressInstallableMaintenance(1);
        Assert.AreEqual(1.0, owner.Value);

        owner.TrackStartupProgressInstallableMaintenanceRequested(1);
        owner.TryCompleteStartupProgressInstallableMaintenance(1);

        Assert.AreEqual(2.0, owner.Value);
    }

    [TestMethod]
    public void StartupProgress_InstallableMaintenanceUsesDedicatedSubLabel()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        CompleteUntilBackground(owner);
        owner.TrackStartupProgressInstallableMaintenanceRequested(1);

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, owner.Label);
        Assert.AreEqual(Resources.Statusbar_progress_phase_installable_maintenance, owner.SubLabel);
    }

    [TestMethod]
    public void StartupProgress_Lr2SongDbSyncUsesDedicatedStageSubLabel()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        CompleteUntilLr2(owner);
        owner.TrackStartupProgressLr2SongDbSyncRequested(1);
        owner.UpdateStartupProgressLr2SongDbSyncStatus(432464, 243780, "song_rows", 209000, 209684);

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, owner.Label);
        Assert.AreEqual("[209000/209684] " + Resources.Statusbar_progress_phase_lr2_song_db_sync + " song rows", owner.SubLabel);
        Assert.AreEqual(209000.0, owner.Value);
        Assert.AreEqual(209684.0, owner.Maximum);
    }

    [TestMethod]
    public void StartupProgress_Lr2SongDbSyncFailureKeepsFailurePresentation()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        CompleteUntilLr2(owner);
        owner.TrackStartupProgressLr2SongDbSyncRequested(1);
        owner.TryFailStartupProgressLr2SongDbSync(1, "startup scan blockers");

        Assert.AreEqual(Resources.Statusbar_progress_failed, owner.Label);
        Assert.AreEqual("startup scan blockers", owner.SubLabel);
        Assert.IsTrue(owner.IsFailed);
    }

    [TestMethod]
    public void Lr2SongDbSyncWarningStatus_IsVisibleWhenStartupProgressFailed()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
        Lr2SongDbSyncRuntimeStatus status = Lr2SongDbSyncStatusMapper.Create(
            new Lr2SongDbSyncStatusSnapshot
            {
                Status = Lr2SongDbSyncStatusKind.Running,
                Stage = "song_rows",
                StageProcessedCount = 4,
                StageTotalCount = 10
            },
            new DateTime(2026, 6, 5, 12, 0, 0));

        hub.StartupProgress.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        hub.StartupProgress.TrackStartupProgressLr2SongDbSyncRequested(1);
        hub.UpdateLr2SongDbSyncStatus(status);
        Assert.IsFalse(hub.IsLr2SongDbSyncStatusActive);

        hub.StartupProgress.FailStartupProgressOperation("startup failed");

        Assert.IsTrue(hub.IsLr2SongDbSyncStatusActive);
        Assert.AreEqual(status.StatusText, hub.Lr2SongDbSyncStatusLabel);
        Assert.AreEqual(status.ProgressText, hub.Lr2SongDbSyncStatusSubLabel);
        Assert.AreEqual(status.ProgressValue, hub.Lr2SongDbSyncStatusProgressValue);
        Assert.AreEqual(status.ProgressMaximum, hub.Lr2SongDbSyncStatusProgressMaximum);
    }

    [TestMethod]
    public void StartupProgress_LibrarySubLabelFollowsLibraryStage()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        Assert.AreEqual(Resources.Statusbar_progress_phase_library_db_load, owner.SubLabel);

        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.UpdateStartupProgressLibraryInitializationStatus(
            BMSLibrary.LibraryInitializationProgressStage.FileEnumeration,
            "Everything",
            0,
            0,
            string.Empty);
        Assert.AreEqual(Resources.Statusbar_progress_phase_file_enumeration + " (Everything)", owner.SubLabel);

        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.UpdateStartupProgressLibraryInitializationStatus(
            BMSLibrary.LibraryInitializationProgressStage.FileDiff,
            string.Empty,
            10,
            3,
            "C:\\BMS\\added.bms");
        Assert.AreEqual("[3/10] " + Resources.Statusbar_progress_phase_file_diff + " added.bms", owner.SubLabel);
    }

    [TestMethod]
    public void StartupProgress_ChartInfoBackfillUsesCountSubLabel()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        foreach (StartupProgressPhase phase in new[]
        {
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.Lr2SongDbSyncDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone
        })
        {
            Skip(owner, phase);
        }
        owner.TrackStartupProgressChartInfoBackfillRequested(1);
        owner.UpdateStartupProgressChartInfoBackfillStatus(209999, 6695, "C:\\BMS\\metadata.bms");

        Assert.AreEqual("[6695/209999] " + Resources.Statusbar_progress_phase_chart_info + " metadata.bms", owner.SubLabel);
    }

    [TestMethod]
    public void StartupProgress_ChartInfoHydrationHidesFraction()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        foreach (StartupProgressPhase phase in new[]
        {
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.Lr2SongDbSyncDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone
        })
        {
            Skip(owner, phase);
        }
        owner.TrackStartupProgressChartInfoHydrationRequested(1);
        owner.UpdateStartupProgressChartInfoHydrationStatus(209999, 1200);

        Assert.AreEqual(Resources.Statusbar_progress_operable_background, owner.Label);
        Assert.AreEqual(Resources.Statusbar_progress_phase_chart_info_load, owner.SubLabel);
        Assert.IsFalse(owner.SubLabel.Contains("["));
    }

    [TestMethod]
    public void StartupProgress_ReloadTablesCompletesExternalSyncAndReferenceApply()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ReloadTables);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        owner.TrackStartupProgressExternalSyncRequest("ReloadTables", 1);
        owner.TrackStartupProgressPlaylistReferenceRequest("DeferredExternalSync:ReloadTables", 1);
        owner.TryCompleteStartupProgressPlaylistReference(1);
        owner.TryCompleteStartupProgressExternalSync(1);
        Skip(owner, StartupProgressPhase.PlaylistEntriesHydrationDone);

        Assert.AreEqual(5.0, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_complete_reload, owner.Label);
    }

    [TestMethod]
    public void StartupProgress_OperableBackgroundResource_IsPresent()
    {
        Assert.AreEqual("操作可能(バックグラウンド更新中)", Resources.Statusbar_progress_operable_background);
        Assert.AreEqual("譜面メタデータ反映", Resources.Statusbar_progress_phase_chart_info_load);
        Assert.AreEqual("LR2 song.db 同期", Resources.Statusbar_progress_phase_lr2_song_db_sync);
    }

    [TestMethod]
    public void StartupReadyUiMask_RequiresInstallTreeOnly()
    {
        Assert.IsFalse(MainWindowViewModel.IsStartupReadyUiMaskSatisfiedForTest(false, true, true));
        Assert.IsTrue(MainWindowViewModel.IsStartupReadyUiMaskSatisfiedForTest(true, false, false));
    }

    [TestMethod]
    public void StartupPresentationDeferPolicyKeepsBasicCatalogFlushRules()
    {
        Assert.IsFalse(MainWindowViewModel.IsStartupPresentationDeferredForTest(
            MainViewUpdateMode.FolderFilterSelected, true, true, false, false, false));
        Assert.IsTrue(MainWindowViewModel.IsStartupPresentationDeferredForTest(
            MainViewUpdateMode.FolderFilterSelected, true, false, false, false, true));
        Assert.IsTrue(MainWindowViewModel.IsStartupPresentationDeferredForTest(
            MainViewUpdateMode.FileMissingFilterSelected, true, true, false, false, false));
    }

    private static StartupProgressWorkflowOwner Start(StartupProgressOperationKind operationKind)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var owner = TestStartupProgressOwnerFactory.Create();
        owner.StartStartupProgressOperation(operationKind);
        return owner;
    }

    private static void Mark(StartupProgressWorkflowOwner owner, StartupProgressPhase phase)
    {
        owner.MarkStartupProgressPhaseCompleted(phase, owner.GetActiveStartupProgressOperationToken());
    }

    private static void Skip(StartupProgressWorkflowOwner owner, StartupProgressPhase phase)
    {
        owner.SkipStartupProgressPhaseIfExpected(
            phase,
            "test",
            owner.GetActiveStartupProgressOperationToken());
    }

    private static void SkipAllOptionalStartupPhases(StartupProgressWorkflowOwner owner)
    {
        foreach (StartupProgressPhase phase in new[]
        {
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.Lr2SongDbSyncDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone
        })
        {
            Skip(owner, phase);
        }
    }

    private static void CompleteUntilBackground(StartupProgressWorkflowOwner owner)
    {
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        foreach (StartupProgressPhase phase in new[]
        {
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.Lr2SongDbSyncDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone
        })
        {
            Skip(owner, phase);
        }
    }

    private static void CompleteUntilLr2(StartupProgressWorkflowOwner owner)
    {
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        foreach (StartupProgressPhase phase in new[]
        {
            StartupProgressPhase.PlaylistReferenceApplied,
            StartupProgressPhase.ExternalPlaylistSyncDone,
            StartupProgressPhase.PlaylistEntriesHydrationDone,
            StartupProgressPhase.ChartInfoHydrationDone,
            StartupProgressPhase.ChartInfoBackfillDone,
            StartupProgressPhase.ChartDigestBackfillDone,
            StartupProgressPhase.ScoreHydrationDone,
            StartupProgressPhase.RankingRefreshDone,
            StartupProgressPhase.MaintenanceDeferredDone,
            StartupProgressPhase.InstallableMaintenanceDeferredDone
        })
        {
            Skip(owner, phase);
        }
    }
}
