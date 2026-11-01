using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.StartupProgressTestSupport;

namespace BeMusicSeeker.Tests;


/// <remarks>純粋な進捗判断はownerへ入力通知を渡して検査します。実起動・設定完了のケースはMainWindowのL/P解放・必須UI・後続scheduler接続の順序を検査するため実構成とDBを使い、Dispatcherでcallbackと終端を回収します。</remarks>
[TestClass]
public sealed class MainWindowViewModelStartupProgressTests
{
    // MSTestが各ケースの実行前に必ず注入します。
    public TestContext TestContext { get; set; } = null!;

    /// <summary>UI操作禁止を同じ値で繰り返し設定しても、真偽の実遷移だけを各一回通知して重複更新を防ぎます。</summary>
    [TestMethod]
    public void StartupProgress_InteractionBlockPublishesOnlyForStateTransitions()
    {
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        var changedProperties = new List<string>();
        owner.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);

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

    /// <summary>進捗操作の開始前に失敗してもUI操作禁止を解除し、active操作がないことを理由に操作不能を残しません。</summary>
    [TestMethod]
    public void StartupProgress_FailureClearsInteractionBlockBeforeActiveOperationCheck()
    {
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        owner.SetStartupUiInteractionBlocked(true);

        owner.FailStartupProgressOperation("startup failed before operation");

        Assert.IsFalse(owner.IsStartupUiInteractionBlocked);
    }

    /// <summary>同じ操作で失敗前に予約した表示更新を失敗後に実行しても、現在の失敗名と入力原因を通常表示や空の詳細へ戻しません。</summary>
    [TestMethod]
    public void StartupProgress_QueuedPresentationCannotOverwriteCurrentFailure()
    {
        var queued = new Queue<Action>();
        bool holdPresentation = false;
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create(dispatch: action =>
        {
            if (holdPresentation) { queued.Enqueue(action); }
            else { action(); }
        });
        try
        {
            long token = owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);
            holdPresentation = true;
            owner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyData, token);
            Assert.IsTrue(queued.Count > 0);

            holdPresentation = false;
            const string cause = "required UI failed before delayed progress";
            owner.FailStartupProgressOperation(cause);
            string expectedLabel = string.Format(Resources.Statusbar_progress_operation_failed_format, Resources.Statusbar_progress_startup);
            Assert.AreEqual(token, owner.GetActiveStartupProgressOperationToken());
            Assert.IsTrue(owner.IsFailed);
            Assert.AreEqual(expectedLabel, owner.Label);
            Assert.AreEqual(cause, owner.SubLabel);

            while (queued.Count > 0) { queued.Dequeue()(); }
            Assert.AreEqual(token, owner.GetActiveStartupProgressOperationToken());
            Assert.IsTrue(owner.IsFailed);
            Assert.AreEqual(expectedLabel, owner.Label);
            Assert.AreEqual(cause, owner.SubLabel);
        }
        finally
        {
            holdPresentation = false;
            while (queued.Count > 0) { queued.Dequeue()(); }
        }
    }

    /// <summary>全再初期化が失敗した後は対応tokenのcleanup終端で再試行可へ進み、失敗状態を保ってUI操作禁止を解除します。</summary>
    [TestMethod]
    public void StartupProgress_FullReinitializeFailureBecomesRetryableAfterCleanup()
    {
        StartupProgressWorkflowOwner owner = TestStartupProgressOwnerFactory.Create();
        long operationToken = owner.StartStartupProgressOperation(
            StartupProgressOperationKind.FullReinitialize);

        owner.FailStartupProgressOperation("directory preflight failed");
        owner.MarkStartupProgressFailureCleanupComplete(operationToken);

        Assert.IsTrue(owner.IsFailed);
        Assert.IsTrue(owner.IsRetryableFailure);
        Assert.IsFalse(owner.IsStartupUiInteractionBlocked);
    }

    /// <summary>スコア再読込みの実要求と対応完了通知で必須進捗を完了し、操作名付きの完了表示へ進みます。</summary>
    [TestMethod]
    public void StartupProgress_ScoreOnlyCompletesThroughRuntimeRoutes()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ScoreOnly);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        owner.TrackStartupProgressScoreHydrationRequested(1);
        owner.TryCompleteStartupProgressScoreHydration(1);

        Assert.AreEqual(owner.Maximum, owner.Value);
        Assert.AreEqual(string.Format(Resources.Statusbar_progress_operation_completed_format, Resources.Statusbar_progress_reload_scores), owner.Label);
    }

    /// <summary>起動から表再読込みへ切り替えた後のスコア要求を受け付けず、現在操作の進捗を変える古い操作判定を検出します。</summary>
    [TestMethod]
    public void StartupProgress_RequestEligibilityUsesCurrentOperationAtomically()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        owner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);

        double valueBefore = owner.Value;
        double maximumBefore = owner.Maximum;
        owner.TrackStartupProgressScoreHydrationRequested(1);

        Assert.AreEqual(maximumBefore, owner.Maximum);
        Assert.AreEqual(valueBefore, owner.Value);
        Assert.AreEqual(StartupProgressOperationKind.ReloadTables, owner.CurrentOperationKind);
    }

    /// <summary>新しい操作を開始した後の旧token完了通知は進捗と名称を変えず、別操作を誤って完了させません。</summary>
    [TestMethod]
    public void StartupProgress_StaleTokenCannotCompleteNewOperation()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        long staleToken = owner.GetActiveStartupProgressOperationToken();
        owner.StartStartupProgressOperation(StartupProgressOperationKind.ReloadTables);

        double valueBefore = owner.Value;
        double maximumBefore = owner.Maximum;
        owner.MarkStartupProgressPhaseCompleted(StartupProgressPhase.StartupReadyOperable, staleToken);

        Assert.AreEqual(valueBefore, owner.Value);
        Assert.AreEqual(maximumBefore, owner.Maximum);
        Assert.AreEqual(Resources.Statusbar_progress_reload_tables, owner.Label);
    }

    /// <summary>旧操作token・scheduler世代の後続callbackを拒否し、現世代の必須登録閉鎖だけが後続workを開始してidleへ進めます。</summary>
    [TestMethod]
    public async Task StartupPostInitialization_StaleCallbackCannotOpenNewGenerationBarrier()
    {
        var postEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fullyIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner scheduler = null!;
        scheduler = new(
            () => false,
            _ => { },
            _ => { },
            _ => { },
            value => value ?? string.Empty,
            (_, _) =>
            {
                if (scheduler.IsFullyIdle)
                {
                    fullyIdle.TrySetResult(true);
                }
            },
            new object());
        scheduler.Queue("post_initialize_gc", "test", null, () =>
        {
            postEntered.TrySetResult(true);
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
        Assert.IsFalse(postEntered.Task.IsCompleted);

        long currentGeneration = scheduler.CurrentGeneration;
        bool currentAccepted = MainWindowViewModel.IsCurrentStartupPostInitializationCallback(
            currentOperationToken,
            currentGeneration,
            token => token == currentOperationToken,
            scheduler.IsCurrentGeneration);

        Assert.IsTrue(currentAccepted);
        Assert.IsTrue(scheduler.MarkRequiredInitializationSchedulingComplete(currentGeneration));
        await postEntered.Task;
        await fullyIdle.Task;
        Assert.IsTrue(scheduler.IsFullyIdle, scheduler.DescribeWaitState());
    }

    /// <summary>準備callbackへ新tokenを渡す前に現在tokenを公開し、callback内で旧操作を参照する順序の誤りを検出します。</summary>
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
            new object());

        long startedToken = owner.StartStartupProgressOperation(StartupProgressOperationKind.Startup);

        Assert.AreEqual(startedToken, observedCallbackToken);
        Assert.AreEqual(startedToken, observedToken);
    }

    /// <summary>要求を追跡していないスコア完了通知は再初期化の進捗を進めず、無関係な終端通知の混入を検出します。</summary>
    [TestMethod]
    public void StartupProgress_ScoreCompletionWithoutRequestIsIgnored()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);

        double valueBefore = owner.Value;
        double maximumBefore = owner.Maximum;
        owner.TryCompleteStartupProgressScoreHydration(1);

        Assert.AreEqual(valueBefore, owner.Value);
        Assert.AreEqual(maximumBefore, owner.Maximum);
        Assert.AreEqual(Resources.Statusbar_progress_full_reinitialize, owner.Label);
    }

    /// <summary>ファイル列挙・差分・UI適用・プレイリスト参照とhydrationの実通知を対応付け、ファイル再読込みを完了表示へ進めます。</summary>
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
        Assert.AreEqual(string.Format(Resources.Statusbar_progress_operation_completed_format, Resources.Statusbar_progress_reload_files), owner.Label);
    }

    /// <summary>必須初期化完了後の表示猶予gate中はactiveを維持し、猶予終端でinactiveを通知して完了表示の寿命を閉じます。</summary>
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

        CompleteRequiredProgressForPresentation(owner);
        owner.CompleteRequiredInitialization(owner.GetActiveStartupProgressOperationToken());

        await delayEntered.Task;
        Assert.IsTrue(owner.IsOperationActive);
        releaseDelay.TrySetResult(true);
        await inactivePublished.Task;

        Assert.IsFalse(owner.IsOperationActive);
    }


    /// <summary>プレイリスト行の直接hydration要求にも完了通知を対応付け、別経路を通らない要求の進捗欠落を防ぎます。</summary>
    [TestMethod]
    public void StartupProgress_PlaylistEntriesHydrationCanCompleteDirectly()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.FullReinitialize);
        owner.TrackStartupProgressPlaylistEntriesHydrationDirectRequest(1, "test");
        owner.TryCompleteStartupProgressPlaylistEntriesHydration(1);

        Assert.AreEqual(2.0, owner.Value);
    }

    /// <summary>実追跡した要求だけを起動親へ所属させ、token・世代・source・機能版の違いと任意順位要求を拒否して親進捗を変えません。</summary>
    [TestMethod]
    public void StartupProgress_RequestMembershipRequiresActualTrackedOriginAndFeatureVersion()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ScoreOnly);
        long token = owner.GetActiveStartupProgressOperationToken();
        var score = new OperationProgressRequest(5, token, "score_hydration_deferred", 7);
        var ranking = new OperationProgressRequest(5, token, "ranking_refresh_deferred", 9);
        owner.TrackStartupProgressScoreHydrationRequested(7, score);
        double value = owner.Value;
        Assert.IsTrue(owner.IsExecutionProgressPartOfStartup(score));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(ranking), "任意順位の要求を必須スコアの親へ混ぜません。");
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { OperationToken = 0 }));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { OperationToken = token + 1 }));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { Generation = 6 }));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { Source = "scheduler:score_hydration_deferred" }));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { Version = 9 }));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(score with { Source = "ranking_refresh_deferred" }));
        Assert.AreEqual(value, owner.Value);

    }

    /// <summary>同じ数値版を持つ外部同期・参照・行hydrationをsource別に追跡し、古い参照完了では進めず最新対応版の完了で一段だけ進めます。</summary>
    [TestMethod]
    public void StartupProgress_SharedPlaylistNumericSlotsDoNotConfuseDifferentRequestSources()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.ReloadTables);
        long token = owner.GetActiveStartupProgressOperationToken();
        var external = new OperationProgressRequest(4, token, "external_playlist_sync", 3);
        var reference = new OperationProgressRequest(4, token, "playlist_ref_apply", 3);
        var entries = new OperationProgressRequest(4, token, "playlist_entries_hydration", 3);
        owner.TrackStartupProgressExternalSyncRequest("ReloadTables", 3, token, external);
        owner.TrackStartupProgressPlaylistReferenceRequest("PlaylistEntriesHydration", 3, token, reference);
        owner.TrackStartupProgressPlaylistEntriesHydrationDirectRequest(3, "ReloadTables", token, entries);
        Assert.IsTrue(owner.IsExecutionProgressPartOfStartup(external));
        Assert.IsTrue(owner.IsExecutionProgressPartOfStartup(reference));
        Assert.IsTrue(owner.IsExecutionProgressPartOfStartup(entries));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(reference with { Source = "scheduler:playlist_ref_apply" }));
        OperationProgressRequest latestReference = reference with { Version = 2 };
        owner.TrackStartupProgressPlaylistReferenceRequest("PlaylistEntriesHydration", 2, token, latestReference);
        Assert.IsTrue(owner.IsExecutionProgressPartOfStartup(latestReference));
        Assert.IsFalse(owner.IsExecutionProgressPartOfStartup(reference));
        double before = owner.Value;
        owner.TryCompleteStartupProgressPlaylistReference(2, token);
        Assert.AreEqual(before, owner.Value, "表示用要求の実版を添えても既存 Math.Max による完了版判定を変えない。");
        owner.TryCompleteStartupProgressPlaylistReference(3, token);
        Assert.AreEqual(before + 1, owner.Value);
    }

    /// <summary>起動進捗が失敗しても別途稼働中のLR2同期行は名称・詳細・分子分母を維持し、親失敗による表示抑止を防ぎます。</summary>
    [TestMethod]
    public void Lr2SongDbSyncRunningStatus_IsVisibleWhenStartupProgressFailed()
    {

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
        hub.UpdateLr2SongDbSyncStatus(status);
        Assert.IsTrue(hub.Rows.Any(row => row.Key == "lr2"));

        hub.StartupProgress.FailStartupProgressOperation("startup failed");

        Assert.IsTrue(hub.Rows.Any(row => row.Key == "lr2"));
        Assert.AreEqual(status.StatusText, hub.Rows.Single(row => row.Key == "lr2").Label);
        Assert.AreEqual(status.ProgressText, hub.Rows.Single(row => row.Key == "lr2").Detail);
        Assert.AreEqual(status.ProgressValue, hub.Rows.Single(row => row.Key == "lr2").Value);
        Assert.AreEqual(status.ProgressMaximum, hub.Rows.Single(row => row.Key == "lr2").Maximum);
    }

    /// <summary>DB・列挙・差分の実通知から子行を切り替え、件数と対象pathを表示して完了時に除去し、必須UI中だけUI準備行を表示します。</summary>
    [TestMethod]
    public void StartupProgress_LibraryChildrenFollowActualNotificationsAndUiPreparationBoundary()
    {
        StartupProgressWorkflowOwner owner = Start(StartupProgressOperationKind.Startup);
        long token = owner.GetActiveStartupProgressOperationToken();
        Assert.AreEqual(string.Empty, owner.SubLabel);
        Assert.IsFalse(owner.DetailRows.Any());
        owner.UpdateStartupProgressLibraryInitializationStatuses([
            new(1, BMSLibrary.LibraryInitializationProgressStage.DatabaseLoad, "", 0, 0, "", token)]);
        Assert.AreEqual(Resources.Statusbar_progress_phase_library_db_load, owner.DetailRows.Single().Label);
        owner.TryCompleteStartupProgressLibraryDatabaseLoad(1);
        owner.UpdateStartupProgressLibraryInitializationStatuses([
            new(2, BMSLibrary.LibraryInitializationProgressStage.FileEnumeration, "Everything", 0, 0, "", token)]);
        Assert.AreEqual(Resources.Statusbar_progress_phase_file_enumeration + " (Everything)", owner.DetailRows.Single().Label);
        owner.TryCompleteStartupProgressLibraryFileEnumeration(1);
        owner.UpdateStartupProgressLibraryInitializationStatuses([
            new(3, BMSLibrary.LibraryInitializationProgressStage.FileDiff, "", 10, 3, "added.bms", token)]);
        Assert.AreEqual("[3/10] " + Resources.Statusbar_progress_phase_file_diff, owner.DetailRows.Single().Label);
        Assert.AreEqual("added.bms", owner.DetailRows.Single().Detail);
        owner.TryCompleteStartupProgressLibraryFileDiff(1);
        Assert.IsFalse(owner.DetailRows.Any());
        Mark(owner, StartupProgressPhase.StartupReadyData);
        Assert.AreEqual(Resources.Statusbar_progress_phase_ui_prepare, owner.DetailRows.Single().Label);
        Mark(owner, StartupProgressPhase.StartupReadyUi);
        Assert.AreEqual("task:ui_prepare", owner.DetailRows.Single().Key);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        Assert.IsFalse(owner.DetailRows.Any());
    }

    /// <summary>旧版の譜面情報進捗を無視し、現版の大量件数を保持して親の必須進捗へ任意workを加算しません。</summary>
    [TestMethod]
    public void StartupProgress_ChartInfoHydrationRejectsOldRequestAndKeepsCurrentCounts()
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
            StartupProgressPhase.ScoreHydrationDone,
        })
        {
            Skip(owner, phase);
        }
        owner.TrackStartupProgressChartInfoHydrationRequested(2);
        double parentValue = owner.Value;
        double parentMaximum = owner.Maximum;
        owner.UpdateStartupProgressChartInfoHydrationStatus(new(1, 99, 99, string.Empty));
        Assert.IsFalse(owner.DetailRows.Any(row => row.Key == "task:chart_info_hydration"));
        owner.UpdateStartupProgressChartInfoHydrationStatus(new(2, 209999, 1200, string.Empty));
        OperationProgressRow current = owner.DetailRows.Single(row => row.Key == "task:chart_info_hydration");
        Assert.AreEqual(209999d, current.Maximum);
        Assert.AreEqual(1200d, current.Value);
        owner.UpdateStartupProgressChartInfoHydrationStatus(new(1, 99, 99, string.Empty));
        Assert.AreEqual(current, owner.DetailRows.Single(row => row.Key == "task:chart_info_hydration"));

        StringAssert.Contains(current.Label, Resources.Statusbar_progress_phase_chart_info_load);
        Assert.AreEqual(string.Empty, current.Detail);
        Assert.AreEqual(parentValue, owner.Value);
        Assert.AreEqual(parentMaximum, owner.Maximum);
    }

    /// <summary>表再読込みの外部同期と参照適用を対応要求で完了し、不要なhydrationをskipして操作名付き完了表示へ進みます。</summary>
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
        Assert.AreEqual(string.Format(Resources.Statusbar_progress_operation_completed_format, Resources.Statusbar_progress_reload_tables), owner.Label);
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


    private static void CompleteRequiredProgressForPresentation(StartupProgressWorkflowOwner owner)
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
            StartupProgressPhase.ScoreHydrationDone,
        })
        {
            Skip(owner, phase);
        }
    }
    /// <summary>各操作は実行中と操作可能時の名称を保ち、失敗では操作名と原因詳細を分けて空原因でも名称を失いません。</summary>
    [TestMethod]
    [DataRow(StartupProgressOperationKind.Startup, "Statusbar_progress_startup")]
    [DataRow(StartupProgressOperationKind.ReloadFileDiff, "Statusbar_progress_reload_files")]
    [DataRow(StartupProgressOperationKind.ScoreOnly, "Statusbar_progress_reload_scores")]
    [DataRow(StartupProgressOperationKind.FullReinitialize, "Statusbar_progress_full_reinitialize")]
    [DataRow(StartupProgressOperationKind.ReloadTables, "Statusbar_progress_reload_tables")]
    public void StartupProgress_OperationNameAndFailureReasonStayDistinct(object operationKindValue, string resourceKey)
    {
        var operationKind = (StartupProgressOperationKind)operationKindValue;
        StartupProgressWorkflowOwner owner = Start(operationKind);
        string expected = Resources.ResourceManager.GetString(resourceKey) ?? throw new InvalidOperationException(resourceKey);
        Assert.AreEqual(expected, owner.Label);
        Mark(owner, StartupProgressPhase.StartupReadyOperable);
        Assert.AreEqual(expected, owner.Label);
        Assert.AreEqual(string.Empty, owner.SubLabel);
        owner.FailStartupProgressOperation("");
        Assert.AreEqual(string.Format(Resources.Statusbar_progress_operation_failed_format, expected), owner.Label);
        Assert.AreEqual(string.Empty, owner.SubLabel);
        owner.FailStartupProgressOperation("failure detail");
        Assert.AreEqual("failure detail", owner.SubLabel);
    }

}
