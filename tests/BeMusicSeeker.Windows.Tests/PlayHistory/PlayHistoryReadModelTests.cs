using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PlayHistoryReadModelTestFixture;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class PlayHistoryReadModelTests
{
    /// <summary>このケースのモデルと接続で共有するDBの所有者です。</summary>
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// playlist summary選択の実構成は履歴集計と診断を消し、archive日付面を保持する。画面切替の残留表示を検出する。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public async Task SelectPlaylistSummaryClearsPlayHistorySummaryPresentation()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        PlayHistoryPeriodTreeItem[] archive = new[] { new PlayHistoryPeriodTreeItem("archive", PlayHistoryPeriodRequest.All()) };
        viewModel.PlayHistory.PresentationState.SetArchivePeriodTree(archive);
        viewModel.PlayHistory.PresentationState.SetSummaryCards(new[] { new PlayHistorySummaryCard(Resources.Play_history_summary_judge_count, "1") });
        viewModel.PlayHistory.PresentationState.SetDiagnosticText("diagnostic");

        viewModel.PlaylistWorkspace.RequestSummarySelection();
        TestUiDispatcherHost.ProcessQueuedPresentation();
        await viewModel.PlaylistWorkspace.WaitForPlaylistSummaryDataBuildIdleAsync()
            .ConfigureAwait(false);
        TestUiDispatcherHost.ProcessQueuedPresentation();

        Assert.AreEqual(0, viewModel.PlayHistory.SummaryCards.Count);
        Assert.AreEqual(string.Empty, viewModel.PlayHistory.SummaryDiagnosticText);
        Assert.AreSame(archive, viewModel.PlayHistory.ArchivePeriodTree);
    }

    /// <summary>
    /// 実一覧の整列要求を通常/履歴それぞれの主体へ渡し、片方の利用者指定で他方の条件を上書きしない。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public async Task MainChartListSortRequest_KeepsPlayHistorySortSeparateFromMainSort()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var regularSortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        regularOwner.SortChanged += (_, request) => regularSortChanged.TrySetResult(request);
        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.FolderFilterSelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.Regular);
        viewModel.MainChartList.RequestSort(nameof(ChartFile.Title), ListSortDirection.Ascending);
        await TestUiDispatcherHost.AwaitNotificationAsync(regularSortChanged.Task, Task.CompletedTask, "play-history.sort-condition-notification");
        MainChartListSortRequestedEventArgs regularReceipt = await regularSortChanged.Task;
        Assert.AreEqual(nameof(ChartFile.Title), regularReceipt.ColumnName);

        ChartListSortParameters regularSort = regularOwner.CaptureSortParameters();
        Assert.AreEqual(nameof(ChartFile.Title), regularSort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Ascending, regularSort.Direction);
        Assert.IsNull(viewModel.PlayHistory.CaptureSortParameters(out _));
        Assert.AreEqual(regularSort.ColumnsName, viewModel.MainChartList.SortParameters.ColumnsName);
        Assert.AreEqual(regularSort.Direction, viewModel.MainChartList.SortParameters.Direction);

        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.PlayHistorySelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PlayHistorySelected);

        var playHistorySortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PlayHistory.SortChanged += (_, request) => playHistorySortChanged.TrySetResult(request);
        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.PlayHistory);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.PlayedAt), ListSortDirection.Descending);
        await TestUiDispatcherHost.AwaitNotificationAsync(playHistorySortChanged.Task, Task.CompletedTask, "play-history.sort-condition-notification");
        MainChartListSortRequestedEventArgs playHistoryReceipt = await playHistorySortChanged.Task;
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistoryReceipt.ColumnName);

        ChartListSortParameters playHistorySort = viewModel.PlayHistory.CaptureSortParameters(out _);
        Assert.AreEqual(nameof(ChartFile.Title), regularOwner.CaptureSortParameters().ColumnsName);
        Assert.AreEqual(ListSortDirection.Ascending, regularOwner.CaptureSortParameters().Direction);
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistorySort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Descending, playHistorySort.Direction);
        Assert.AreEqual(playHistorySort.ColumnsName, viewModel.MainChartList.SortParameters.ColumnsName);
        Assert.AreEqual(playHistorySort.Direction, viewModel.MainChartList.SortParameters.Direction);
    }

    /// <summary>
    /// 受付後に表示scopeが切り替わっても捕捉した整列対象へ要求を送り、現在画面の別主体へ誤配送しない。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public async Task MainChartListSortRequest_UsesCapturedSortScopeWhenViewChangesBeforeExecution()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var playHistorySortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PlayHistory.SortChanged += (_, request) => playHistorySortChanged.TrySetResult(request);
        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.FolderFilterSelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.PlayHistory);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.PlayedAt), ListSortDirection.Descending);
        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.Regular);
        await TestUiDispatcherHost.AwaitNotificationAsync(playHistorySortChanged.Task, Task.CompletedTask, "play-history.sort-condition-notification");
        MainChartListSortRequestedEventArgs playHistoryReceipt = await playHistorySortChanged.Task;
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistoryReceipt.ColumnName);

        ChartListSortParameters playHistorySort = viewModel.PlayHistory.CaptureSortParameters(out _);
        Assert.IsNull(regularOwner.CaptureSortParameters());
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistorySort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Descending, playHistorySort.Direction);
        Assert.IsNull(viewModel.MainChartList.SortParameters);

        SetPrivateField(
            viewModel,
            "treeViewFilterTypeSelected",
            MainViewUpdateMode.PlayHistorySelected);
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PlayHistorySelected);

        var regularSortChanged = new TaskCompletionSource<MainChartListSortRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        regularOwner.SortChanged += (_, request) => regularSortChanged.TrySetResult(request);
        viewModel.MainChartList.SetSortPresentation(
            new MainChartListSortPresentation(
                playHistorySort.ColumnsName,
                playHistorySort.Direction),
            MainChartListSortTarget.Regular);
        viewModel.MainChartList.RequestSort(nameof(ChartFile.Title), ListSortDirection.Ascending);
        viewModel.MainChartList.SetSortPresentation(
            new MainChartListSortPresentation(
                playHistorySort.ColumnsName,
                playHistorySort.Direction),
            MainChartListSortTarget.PlayHistory);
        await TestUiDispatcherHost.AwaitNotificationAsync(regularSortChanged.Task, Task.CompletedTask, "play-history.sort-condition-notification");
        MainChartListSortRequestedEventArgs regularReceipt = await regularSortChanged.Task;
        Assert.AreEqual(nameof(ChartFile.Title), regularReceipt.ColumnName);

        ChartListSortParameters regularSort = regularOwner.CaptureSortParameters();
        Assert.AreEqual(nameof(ChartFile.Title), regularSort.ColumnsName);
        Assert.AreEqual(ListSortDirection.Ascending, regularSort.Direction);
        Assert.AreEqual(nameof(PlayHistoryRow.PlayedAt), playHistorySort.ColumnsName);
        Assert.AreEqual(playHistorySort.ColumnsName, viewModel.MainChartList.SortParameters.ColumnsName);
        Assert.AreEqual(playHistorySort.Direction, viewModel.MainChartList.SortParameters.Direction);
    }

    /// <summary>
    /// A→B→Aと戻った整列条件も版で旧要求を除き、通常/履歴それぞれ最後の要求だけを現在とする。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public void MainChartListSortRequest_RejectsStaleAndAbaOwnerRevisions()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var regularRequests = new List<MainChartListSortRequestedEventArgs>();
        var playHistoryRequests = new List<MainChartListSortRequestedEventArgs>();
        regularOwner.SortChanged += (_, request) => regularRequests.Add(request);
        viewModel.PlayHistory.SortChanged += (_, request) => playHistoryRequests.Add(request);

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.Regular);
        viewModel.MainChartList.RequestSort(nameof(ChartFile.Title), ListSortDirection.Ascending);
        viewModel.MainChartList.RequestSort(nameof(ChartFile.Artist), ListSortDirection.Descending);
        viewModel.MainChartList.RequestSort(nameof(ChartFile.Title), ListSortDirection.Ascending);

        Assert.AreEqual(3, regularRequests.Count);
        Assert.IsTrue(regularRequests[0].OwnerRevision < regularRequests[1].OwnerRevision);
        Assert.IsTrue(regularRequests[1].OwnerRevision < regularRequests[2].OwnerRevision);
        Assert.IsFalse(regularOwner.IsCurrentSortRequest(regularRequests[0]));
        Assert.IsFalse(regularOwner.IsCurrentSortRequest(regularRequests[1]));
        Assert.IsTrue(regularOwner.IsCurrentSortRequest(regularRequests[2]));

        viewModel.MainChartList.SetSortPresentation(null, MainChartListSortTarget.PlayHistory);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.Title), ListSortDirection.Ascending);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.PlayedAt), ListSortDirection.Descending);
        viewModel.MainChartList.RequestSort(nameof(PlayHistoryRow.Title), ListSortDirection.Ascending);

        Assert.AreEqual(3, playHistoryRequests.Count);
        Assert.IsTrue(playHistoryRequests[0].OwnerRevision < playHistoryRequests[1].OwnerRevision);
        Assert.IsTrue(playHistoryRequests[1].OwnerRevision < playHistoryRequests[2].OwnerRevision);
        Assert.IsFalse(viewModel.PlayHistory.IsCurrentSortRequest(playHistoryRequests[0]));
        Assert.IsFalse(viewModel.PlayHistory.IsCurrentSortRequest(playHistoryRequests[1]));
        Assert.IsTrue(viewModel.PlayHistory.IsCurrentSortRequest(playHistoryRequests[2]));
    }

    /// <summary>
    /// 通常一覧の整列条件変更時に実行中の行要求を取消し、refresh開始前から古い行反映を禁止する。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public void RegularSortMutation_CancelsTheInFlightRowRequestBeforeRefreshRuns()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner owner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        Assert.IsTrue(owner.TryBeginRequest(out RegularChartListRequestLease lease));

        owner.QueueSort(new MainChartListSortRequestedEventArgs(
            nameof(ChartFile.Title),
            ListSortDirection.Ascending,
            MainChartListSortTarget.Regular));

        Assert.IsTrue(lease.Token.IsCancellationRequested);
    }

    /// <summary>
    /// 条件変更の購読例外を呼出元へ返しても通常/履歴のrefresh通知を起動し、受理済み更新を欠落させない。
    /// </summary>
    /// <remarks>通常一覧と履歴の並べ替え通知の到達を観測します。通常一覧の表示完了は専用ケースが分担し、履歴側の待機を通常一覧の完了証拠にしません。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public async Task SortRefreshQueues_StartEvenWhenSortChangedSubscriberFails()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        RegularChartListOwner regularOwner = GetPrivateField<RegularChartListOwner>(viewModel, "regularChartListOwner");
        var regularRefreshed = new TaskCompletionSource<RegularChartSortRefreshRequestedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<MainChartListSortRequestedEventArgs> regularChanged = (_, _) => throw new InvalidOperationException("expected regular notification failure");
        regularOwner.SortChanged += regularChanged;
        EventHandler<RegularChartSortRefreshRequestedEventArgs> regularRefresh = (_, request) => regularRefreshed.TrySetResult(request);
        regularOwner.SortRefreshRequested += regularRefresh;

        var playHistoryOwner = new PlayHistoryWorkflowOwner();
        var playHistoryRefreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<MainChartListSortRequestedEventArgs> playChanged = (_, _) => throw new InvalidOperationException("expected play-history notification failure");
        playHistoryOwner.SortChanged += playChanged;
        EventHandler<MainChartListSortRequestedEventArgs> playRefresh = (_, _) => playHistoryRefreshed.TrySetResult();
        playHistoryOwner.SortRefreshRequested += playRefresh;

        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => regularOwner.QueueSort(
                new MainChartListSortRequestedEventArgs(
                    nameof(ChartFile.Title),
                    ListSortDirection.Ascending,
                    MainChartListSortTarget.Regular)));
            await regularRefreshed.Task;

            Assert.ThrowsExactly<InvalidOperationException>(() => playHistoryOwner.QueueSort(
                new MainChartListSortRequestedEventArgs(
                    nameof(PlayHistoryRow.Title),
                    ListSortDirection.Ascending,
                    MainChartListSortTarget.PlayHistory)));
            await playHistoryRefreshed.Task;
        }
        finally
        {
            try
            {
                if (regularRefreshed.Task.IsCompletedSuccessfully) { await regularRefreshed.Task.Result.Presentation; }
            }
            finally
            {
                try { await regularOwner.StopAsync(); }
                finally
                {
                    regularOwner.SortChanged -= regularChanged;
                    regularOwner.SortRefreshRequested -= regularRefresh;
                    playHistoryOwner.SortChanged -= playChanged;
                    playHistoryOwner.SortRefreshRequested -= playRefresh;
                }
            }
        }
    }

    private static void SetPrivateField<T>(MainWindowViewModel viewModel, string fieldName, T value)
    {
        typeof(MainWindowViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
    }

    private static T GetPrivateField<T>(MainWindowViewModel viewModel, string fieldName)
    {
        return (T)typeof(MainWindowViewModel)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
    }

    /// <summary>翻訳は採用済み集計だけから再整形し、未採用CurrentView・行・期間境界・条件版を採用し直さない。解除後には通知が残らない。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void WorkflowOwner_LanguageUsesAdoptedSummaryAndKeepsRequestAndRows()
    {
        string culture = Resources.Culture?.Name ?? CultureInfo.CurrentUICulture.Name;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            WithScoreDb(path =>
            {
                CreateInstalledScoreDb(databaseScope, path);
                using (SQLiteConnectionEx db = databaseScope.OpenProviderConnection(path))
                { InsertHistory(db, 1, HashA, UtcEpoch(2026, 1, 2), true, 200); }
                var source = PlayHistoryReadSourceContext.FromSnapshot(AcquireHistorySnapshot(databaseScope, path));
                var owner = new PlayHistoryWorkflowOwner();
                PlayHistoryViewExecutionDependencies dependencies = CreateViewExecutionDependencies(databaseScope, () => source, library: CreateHistoryLibrary(databaseScope, path));
                owner.ConfigureViewExecution(dependencies);
                owner.ConfigureDisplayTargetPersistence(_ => { });
                owner.ReplaceDisplayTargetSets([], [], false);
                Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, ExecuteHistoryRead(owner, dependencies).Status);
                owner.AttachLanguagePresentation(dependencies.MainChartList);
                try
                {
                    PlayHistoryViewRequest request = owner.SnapshotActiveRequest();
                    object rows = dependencies.MainChartList.Rows;
                    long targetRevision = owner.DisplayTargetRevision;
                    long keywordRevision = owner.KeywordRevision;
                    PlayHistoryViewState adopted = owner.PresentationState.CurrentView;
                    var candidate = new PlayHistoryViewState(request.RequestId, request.PeriodRequest,
                        [], [], [], [], adopted.Provider, adopted.SchemaStatus, 0, adopted.SortSnapshot,
                        adopted.KeywordFilter, adopted.KeywordFilterRevision, adopted.DisplayTarget,
                        adopted.DisplayTargetRevision);
                    owner.PresentationState.CurrentView = candidate;
                    foreach (string language in new[] { "en-US", "ja-JP" })
                    {
                        ResourceService.Current.ChangeCulture(language);
                        Assert.AreEqual("1", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
                        StringAssert.Contains(dependencies.MainChartList.SummaryText, Resources.Play_history_period_all);
                        Assert.AreEqual(Resources.Play_history_period_all, owner.SelectedDisplayTarget.DisplayName);
                        Assert.AreSame(rows, dependencies.MainChartList.Rows);
                        Assert.AreSame(request, owner.SnapshotActiveRequest());
                        Assert.AreEqual(targetRevision, owner.DisplayTargetRevision);
                        Assert.AreEqual(keywordRevision, owner.KeywordRevision);
                    }
                    // 待機候補の採用はその時点のResourcesで整形し、空の終端にも入力を更新する。
                    ResourceService.Current.ChangeCulture("en-US");
                    Assert.AreEqual(PlayHistorySortedRowsApplyStatus.Applied, owner.ApplySortedRows(
                        new PlayHistorySortedRowsApplyRequest(MainViewUpdateMode.PlayHistorySelected,
                            MainViewUpdateMode.PlayHistorySelected, candidate, [], true, "default", string.Empty,
                            owner.SelectedDisplayTarget, null, request.DetailSourceRetirement), Stopwatch.StartNew(),
                        dependencies.MainChartList, dependencies.PlaylistWorkspace).Status);
                    Assert.AreEqual("0", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
                    ResourceService.Current.ChangeCulture("ja-JP");
                    Assert.AreEqual("0", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
                    owner.ClearSummaryPresentation();
                    ResourceService.Current.ChangeCulture("en-US");
                    Assert.AreEqual(0, owner.SummaryCards.Count);
                    owner.DetachLanguagePresentation();
                    int notifications = 0;
                    owner.PropertyChanged += (_, _) => notifications++;
                    ResourceService.Current.ChangeCulture("ja-JP");
                    Assert.AreEqual(0, notifications);
                }
                finally { owner.DetachLanguagePresentation(); }
            });
        }
        finally { ResourceService.Current.ChangeCulture(culture); }
    }

    /// <summary>
    /// 履歴の非選択通知が失敗した場合、実shellは既存の保留パッケージ操作contextを維持し、未完了の画面切替で対象を変えない。
    /// </summary>
    /// <remarks>実shell構成から一覧/履歴主体への配送を確認するためVMを使う。Windowの表示やDataContextは観測しない。</remarks>
    // 実shell構成または共有設定providerへの接続を使うため直列化する。
    [DoNotParallelize]
    [TestMethod]
    public void MainViewSelectionKeepsExistingChartOperationContextWhenDeactivationRaises()
    {
        MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create();
        using BeMusicSeeker.ViewModels.SettingsDialogViewModel settingsLifetime = viewModel.SettingDialog;
        PlayHistoryWorkflowOwner playHistory = viewModel.PlayHistory;
        viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
        playHistory.BeginRequest(
            PlayHistoryPeriodRequest.All(),
            string.Empty,
            string.Empty,
            displayTargetRevision: 0);
        playHistory.PropertyChanged += (_, args) =>
        {
            if (string.Equals(args.PropertyName, nameof(PlayHistoryWorkflowOwner.IsViewActive), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("deactivation notification failed");
            }
        };

        MethodInfo setSelection = typeof(MainWindowViewModel).GetMethod(
            "SetTreeViewFilterSelection",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.ThrowsExactly<TargetInvocationException>(() => setSelection.Invoke(
            viewModel,
            new object[] { MainViewUpdateMode.FolderFilterSelected, null!, null! }));

        Assert.AreEqual(MainViewOperationSection.InstallPending, viewModel.MainChartList.CurrentOperationContext.OperationSection);
        Assert.AreEqual(ChartOperationSourceScope.PendingPackage, viewModel.MainChartList.CurrentOperationContext.SourceScope);
        Assert.IsNull(playHistory.SnapshotActiveRequest());
    }

    /// <summary>
    /// 正常な表示の後でも許可取得の失敗を表示し、行・カード・選択を退役する。期間操作では失敗を保持し、次の許可取得で正常0件を採用する。
    /// </summary>
    /// <remarks>実モデル取得のschema検査後のSELECT故障から、実MainChartList/Workspaceの失敗表示と次の許可取得による復帰まで接続する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void WorkflowOwner_ReadFailureShowsFailureAndNextEmptySuccessRecovers()
    {
        WithScoreDb(scoreDbPath =>
        {
            CreateInstalledScoreDb(databaseScope, scoreDbPath);
            using (SQLiteConnectionEx db = databaseScope.OpenProviderConnection(scoreDbPath))
            {
                InsertHistory(db, 1, HashA, UtcEpoch(2026, 1, 2), true, 200);
            }
            var historySource = PlayHistoryReadSourceContext.FromSnapshot(AcquireHistorySnapshot(databaseScope, scoreDbPath));
            var owner = new PlayHistoryWorkflowOwner();
            PlayHistoryViewExecutionDependencies dependencies = CreateViewExecutionDependencies(databaseScope,
                () => historySource, library: CreateHistoryLibrary(databaseScope, scoreDbPath));
            owner.ConfigureViewExecution(dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, ExecuteHistoryRead(owner, dependencies).Status);
            Assert.AreEqual(1, dependencies.MainChartList.Rows.Count);
            Assert.AreEqual("1", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
            dependencies.MainChartList.SelectedIndex = 0;
            using (SQLiteConnectionEx db = databaseScope.OpenProviderConnection(scoreDbPath))
            {
                // schemaはInstalledのまま、実SELECTだけが失敗するDBへ変える。
                db.Execute("PRAGMA writable_schema = ON;");
                db.Execute("UPDATE sqlite_master SET sql = replace(sql, 'played_at INTEGER NOT NULL', 'played_at INTEGER COLLATE missing_history_collation NOT NULL') WHERE type = 'table' AND name = 'bms_lr2_play_history';");
                db.Execute("PRAGMA writable_schema = OFF;");
            }

            historySource = PlayHistoryReadSourceContext.FromSnapshot(AcquireHistorySnapshot(databaseScope, scoreDbPath));
            PlayHistoryViewExecutionResult failed = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.ReadFailed, failed.Status);
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, failed.ReadResult.Read.SchemaStatus);
            Assert.AreEqual(0, dependencies.MainChartList.Rows.Count);
            Assert.AreEqual(-1, dependencies.MainChartList.SelectedIndex);
            Assert.AreEqual(0, owner.SummaryCards.Count);
            Assert.AreEqual(Resources.Play_history_read_failed, dependencies.MainChartList.SummaryText);
            Assert.AreEqual(Resources.Play_history_read_failed, owner.SummaryDiagnosticText);
            Assert.IsTrue(owner.IsViewActive);
            AssertAdoptedHistoryLanguage(owner, dependencies.MainChartList, failed: true);

            File.Delete(scoreDbPath);
            CreateInstalledScoreDb(databaseScope, scoreDbPath);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.ReadFailed, ExecuteHistoryRead(owner, dependencies).Status);
            historySource = PlayHistoryReadSourceContext.FromSnapshot(AcquireHistorySnapshot(databaseScope, scoreDbPath));
            PlayHistoryViewExecutionResult recovered = ExecuteHistoryRead(owner, dependencies);
            Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, recovered.Status);
            Assert.IsTrue(recovered.ReadResult.Read.CacheHit);
            Assert.AreEqual(0, dependencies.MainChartList.Rows.Count);
            Assert.AreEqual("0", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
            Assert.AreNotEqual(Resources.Play_history_read_failed, dependencies.MainChartList.SummaryText);
            Assert.AreNotEqual(Resources.Play_history_read_failed, owner.SummaryDiagnosticText);
            AssertAdoptedHistoryLanguage(owner, dependencies.MainChartList, failed: false);
        });
    }

    /// <summary>失敗・正常空の採用済み終端を翻訳し、要求と行を変更しないことを確認して購読・文化圏を必ず復元します。</summary>
    private static void AssertAdoptedHistoryLanguage(PlayHistoryWorkflowOwner owner, MainChartListViewModel chartList, bool failed)
    {
        string culture = Resources.Culture?.Name ?? CultureInfo.CurrentUICulture.Name;
        PlayHistoryViewRequest request = owner.SnapshotActiveRequest();
        object rows = chartList.Rows;
        owner.AttachLanguagePresentation(chartList);
        try
        {
            foreach (string language in new[] { "en-US", "ja-JP" })
            {
                ResourceService.Current.ChangeCulture(language);
                if (failed)
                {
                    Assert.AreEqual(Resources.Play_history_read_failed, chartList.SummaryText);
                    Assert.AreEqual(0, owner.SummaryCards.Count);
                }
                else
                {
                    Assert.AreEqual("0", owner.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
                    StringAssert.Contains(chartList.SummaryText, Resources.Play_history_period_all);
                }
                Assert.AreSame(request, owner.SnapshotActiveRequest());
                Assert.AreSame(rows, chartList.Rows);
            }
        }
        finally { owner.DetachLanguagePresentation(); ResourceService.Current.ChangeCulture(culture); }
    }

}
