using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
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
        Assert.AreEqual(14.0, Start(StartupProgressOperationKind.Startup).Maximum);
        Assert.AreEqual(6.0, Start(StartupProgressOperationKind.ReloadFileDiff).Maximum);
        Assert.AreEqual(4.0, Start(StartupProgressOperationKind.ScoreOnly).Maximum);
        Assert.AreEqual(15.0, Start(StartupProgressOperationKind.FullReinitialize).Maximum);
        Assert.AreEqual(5.0, Start(StartupProgressOperationKind.ReloadTables).Maximum);
    }

    [TestMethod]
    public void StartupProgress_StartupDoesNotWaitForPostInitializationMaintenance()
    {
        StartupProgressPhase expected = StartupProgressWorkflowOwner.GetInitialExpectedStartupProgressPhases(StartupProgressOperationKind.Startup);

        Assert.AreEqual(StartupProgressPhase.None, expected & StartupProgressPhase.PlaylistReferenceApplied);
        Assert.AreEqual(StartupProgressPhase.None, expected & StartupProgressPhase.ExternalPlaylistSyncDone);
        Assert.AreEqual(StartupProgressPhase.None, expected & StartupProgressPhase.RankingRefreshDone);
        Assert.AreEqual(StartupProgressPhase.None, expected & StartupProgressPhase.MaintenanceDeferredDone);
        Assert.AreEqual(StartupProgressPhase.None, expected & StartupProgressPhase.InstallableMaintenanceDeferredDone);
        Assert.AreNotEqual(StartupProgressPhase.None, expected & StartupProgressPhase.PlaylistEntriesHydrationDone);
        Assert.AreNotEqual(StartupProgressPhase.None, expected & StartupProgressPhase.ChartInfoHydrationDone);
        Assert.AreNotEqual(StartupProgressPhase.None, expected & StartupProgressPhase.StartupBackgroundTasksDone);
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
    public void StartupProgress_InteractionBlockPublishesOnlyForStateTransitions()
    {
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        var changedProperties = new List<string>();
        owner.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        owner.SetStartupUiInteractionBlocked(true);
        owner.SetStartupUiInteractionBlocked(true);
        owner.SetStartupUiInteractionBlocked(false);
        owner.SetStartupUiInteractionBlocked(false);

        Assert.IsFalse(owner.IsStartupUiInteractionBlocked);
        CollectionAssert.AreEqual(
            new[]
            {
                nameof(StartupProgressWorkflowOwner.IsStartupUiInteractionBlocked),
                nameof(StartupProgressWorkflowOwner.IsStartupUiInteractionBlocked)
            },
            changedProperties);
    }

    [TestMethod]
    public void StartupProgress_FailureClearsInteractionBlockBeforeActiveOperationCheck()
    {
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        owner.SetStartupUiInteractionBlocked(true);

        owner.FailStartupProgressOperation("startup failed before operation");

        Assert.IsFalse(owner.IsStartupUiInteractionBlocked);
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
    public void StartupPostInitialization_StaleCallbackCannotOpenNewGenerationBarrier()
    {
        var postEntered = new ManualResetEventSlim();
        StartupBackgroundTaskSchedulerOwner scheduler = new(
            () => false,
            _ => { },
            _ => { },
            _ => { },
            value => value ?? string.Empty,
            (_, _) => { },
            new object());
        scheduler.Queue("post_initialize_gc", "test", null, () =>
        {
            postEntered.Set();
            return Task.CompletedTask;
        });

        long staleGeneration = scheduler.CurrentGeneration;
        scheduler.Reset(startImmediately: true);
        scheduler.MarkPostInitializationSchedulingComplete();

        const long currentOperationToken = 2L;
        bool staleAccepted = MainWindowViewModel.IsCurrentStartupPostInitializationCallback(
            operationToken: 1L,
            schedulerGeneration: staleGeneration,
            isOperationTokenCurrent: token => token == currentOperationToken,
            isSchedulerGenerationCurrent: scheduler.IsCurrentGeneration);

        Assert.IsFalse(staleAccepted);
        Assert.IsFalse(postEntered.IsSet);

        long currentGeneration = scheduler.CurrentGeneration;
        bool currentAccepted = MainWindowViewModel.IsCurrentStartupPostInitializationCallback(
            currentOperationToken,
            currentGeneration,
            token => token == currentOperationToken,
            scheduler.IsCurrentGeneration);

        Assert.IsTrue(currentAccepted);
        Assert.IsTrue(scheduler.MarkRequiredInitializationSchedulingComplete(currentGeneration));
        Assert.IsTrue(postEntered.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(
            SpinWait.SpinUntil(() => scheduler.IsFullyIdle, TimeSpan.FromSeconds(5)),
            scheduler.DescribeWaitState());
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

        Assert.AreEqual(14.0, owner.Value);
        Assert.AreEqual(Resources.Statusbar_progress_complete, owner.Label);
    }

    [TestMethod]
    public void StartupProgress_BackgroundTasksWaitForRequiredEnrollment()
    {
        bool enrollmentReady = false;
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create(() => enrollmentReady);
        owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        SkipAllOptionalStartupPhases(owner);

        Mark(owner, StartupProgressPhase.StartupBackgroundTasksDone);
        Assert.AreNotEqual(Resources.Statusbar_progress_complete, owner.Label);

        enrollmentReady = true;
        Mark(owner, StartupProgressPhase.StartupBackgroundTasksDone);
        Assert.AreEqual(Resources.Statusbar_progress_complete, owner.Label);
    }

    [TestMethod]
    public async Task StartupProgress_CompletionHidePublishesInactiveLifecycleChange()
    {
        var delayEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inactivePublished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create(
            completionHideDelay: () =>
            {
                delayEntered.TrySetResult(true);
                return releaseDelay.Task;
            });
        owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
        owner.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(StartupProgressWorkflowOwner.IsOperationActive)
                && !owner.IsOperationActive)
            {
                inactivePublished.TrySetResult(true);
            }
        };

        CompleteUntilBackground(owner);
        Skip(owner, StartupProgressPhase.InstallableMaintenanceDeferredDone);
        Mark(owner, StartupProgressPhase.StartupBackgroundTasksDone);

        await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Assert.IsTrue(owner.IsOperationActive);
        releaseDelay.TrySetResult(true);
        await inactivePublished.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

        Assert.IsFalse(owner.IsOperationActive);
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
        Assert.IsFalse(StartupPresentationPolicy.IsReadyUiMaskSatisfied(false, true, true));
        Assert.IsTrue(StartupPresentationPolicy.IsReadyUiMaskSatisfied(true, false, false));
    }

    [TestMethod]
    public void StartupPresentationDeferPolicyKeepsBasicCatalogFlushRules()
    {
        Assert.IsFalse(StartupPresentationPolicy.IsPresentationDeferred(
            MainViewUpdateMode.FolderFilterSelected, true, true, false, false, false));
        Assert.IsFalse(StartupPresentationPolicy.IsPresentationDeferred(
            MainViewUpdateMode.FolderFilterSelected, true, false, true, false, false));
        Assert.IsTrue(StartupPresentationPolicy.IsPresentationDeferred(
            MainViewUpdateMode.FolderFilterSelected, true, false, false, false, true));
        Assert.IsTrue(StartupPresentationPolicy.IsPresentationDeferred(
            MainViewUpdateMode.FileMissingFilterSelected, true, true, false, false, false));
    }

    [TestMethod]
    public void LibraryFolderContinuation_ReusesOnlyCurrentStartupAdmission()
    {
        Assert.IsFalse(StartupPresentationPolicy.ShouldDeferLibraryFolderRefresh(
            deferredContinuation: true,
            continuationOperationToken: 42,
            activeOperationToken: 42,
            startupOperationActive: true));
        Assert.IsTrue(StartupPresentationPolicy.ShouldDeferLibraryFolderRefresh(
            deferredContinuation: true,
            continuationOperationToken: 0,
            activeOperationToken: 42,
            startupOperationActive: true));
        Assert.IsTrue(StartupPresentationPolicy.ShouldDeferLibraryFolderRefresh(
            deferredContinuation: true,
            continuationOperationToken: 41,
            activeOperationToken: 42,
            startupOperationActive: true));
        Assert.IsTrue(StartupPresentationPolicy.ShouldDeferLibraryFolderRefresh(
            deferredContinuation: false,
            continuationOperationToken: 42,
            activeOperationToken: 42,
            startupOperationActive: true));
    }

    [TestMethod]
    public void StartupReadiness_OperableAndRequiredSchedulerStartBeforeBlockedFolderReaderCompletes()
    {
        string tempRootPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_StartupReadiness_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);

        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }

            MainWindowViewModel owner = MainWindowViewModelTestFactory.Create();
            StartupProgressWorkflowOwner startupProgress = owner.ProgressHub.StartupProgress;
            long operationToken = startupProgress.StartStartupProgressOperation(
                StartupProgressOperationKind.Startup);
            SetPrivateField(owner, "startupReadyInstallStopwatch", Stopwatch.StartNew());
            SetPrivateField(owner, "startupReadyOperableStopwatch", Stopwatch.StartNew());

            Type refreshChannelType = typeof(MainWindowViewModel).GetNestedType(
                "UiRefreshChannel",
                BindingFlags.NonPublic)!;
            object readyMask = Enum.ToObject(refreshChannelType, 1 | 4 | 8);
            object folderMask = Enum.ToObject(refreshChannelType, 2);
            InvokePrivate(
                owner,
                "TryLogStartupReadyUi",
                new[] { readyMask, (object)operationToken });

            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = []
            };
            library.SearchTargets = [tempRootPath];
            IDisposable writerGuard = AcquireBmsFileWriterGuard(library);
            try
            {
                int refreshCompletions = 0;
                owner.LibraryFolderTree.DeferredRefreshCompleted += (_, _) => refreshCompletions++;
                owner.LibraryFolderTree.AttachLibrary(library);
                Task<int> folderListRead = Task.Run(
                    () => owner.LibraryFolderTree.BMSParentFolderList.Count);
                Assert.IsTrue(
                    folderListRead.Wait(TimeSpan.FromSeconds(1)),
                    "Folder-tree presentation reads must not wait for the model reader guard.");
                StartupBackgroundTaskSchedulerOwner scheduler = GetPrivateField<StartupBackgroundTaskSchedulerOwner>(
                    owner,
                    "startupBackgroundTaskScheduler");
                using var requiredTaskStarted = new ManualResetEventSlim();
                Assert.IsTrue(scheduler.Queue(
                    "startup_readiness_required_test",
                    "test",
                    null,
                    () =>
                    {
                        requiredTaskStarted.Set();
                        return Task.CompletedTask;
                    }));

                InvokePrivate(
                    owner,
                    "FlushPendingUiRefresh",
                    new[]
                    {
                        folderMask,
                        (object)operationToken,
                        (object)false,
                        (object)true
                    });

                Assert.IsTrue(scheduler.IsStarted);
                Assert.IsTrue(requiredTaskStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(0, refreshCompletions);
                FieldInfo queuedField = typeof(LibraryFolderTreeViewModel)
                    .GetField("deferredRefreshQueued", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.IsTrue((bool)queuedField.GetValue(owner.LibraryFolderTree)!);
            }
            finally
            {
                writerGuard.Dispose();
                FieldInfo queuedField = typeof(LibraryFolderTreeViewModel)
                    .GetField("deferredRefreshQueued", BindingFlags.Instance | BindingFlags.NonPublic)!;
                SpinWait.SpinUntil(
                    () => !(bool)queuedField.GetValue(owner.LibraryFolderTree)!,
                    TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
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

    private static T GetPrivateField<T>(object target, string name)
    {
        return (T)target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);
    }

    private static void InvokePrivate(object target, string name, object[] arguments)
    {
        MethodInfo method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == name
                && candidate.GetParameters().Length == arguments.Length);
        method.Invoke(target, arguments);
    }

    private static IDisposable AcquireBmsFileWriterGuard(BMSLibrary library)
    {
        PropertyInfo property = typeof(BMSLibrary)
            .GetProperty("rwlockBMSFiles", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object wrapper = property.GetValue(library)!;
        return (IDisposable)wrapper.GetType()
            .GetMethod("GetWriterGuard", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(wrapper, null)!;
    }
}
