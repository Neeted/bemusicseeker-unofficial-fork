using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <remarks>検索・能力・表示対象の判断は履歴主体の下位ケースが担います。ここではLoadedのcompiled表・toolbar・メニューから行文脈と操作が届く境界を検証し、共有Resourcesの復元まで所有します。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class MainWindowPlayHistoryWpfTests
{
    /// <summary>playlistとpresetの同対象更新を実queueと項目完了接続からcompiled表へ届け、同譜面の別プレイを区別して選択を保つ。明示切替とその直後の依存更新は旧対象の選択を運ばない。一般表の選択組合せはCustomTable系列へ分担する。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlayHistoryDependencyRefresh_PreservesSelectedRawRecordThroughCompiledTable(bool usePreset)
    {
        CultureInfo? previousCulture = Resources.Culture;
        try
        {
            RegularChartListOwnerTestSupport.WithTemporarySongDb(songDb =>
            {
                using (var db = new BeMusicSeeker.Models.LR2.LR2SongDBExtended(songDb)) { BmsLibraryDbGateway.EnsureChartInfoSchema(db); }
                var library = new TestBmsLibrary(songDb);
                MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(MainWindowViewModelTestFactory.CreateIsolatedSettings(), (scope, vm, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, vm, window);
                Lr2PlayHistoryRecord first = CreateResolvedPlayHistoryRow(playedAt: 1000).Raw;
                Lr2PlayHistoryRecord second = CreateResolvedPlayHistoryRow(playedAt: 1001).Raw;
                second.history_id = first.history_id + 1;
                var raw = new Lr2PlayHistoryReadResult(PlayHistorySourceProfile.Lr2("score.db"), [first, second], [], Lr2PlayHistorySchemaStatus.Installed);
                var snapshot = new BMSLibrary.ScoreSnapshot
                { PlayHistorySnapshot = new ScorePlayHistorySnapshot(ActiveScoreSource.Lr2, "score.db", raw), SourceGeneration = 1 };
                TestBmsPlaylist playlist = MainWindowViewModelTestFactory.CreatePlaylist(songDb, MainWindowViewModelTestFactory.CreateIsolatedSettings(), library: library);
                BMSTable targetTable = CreateTargetTable("Before");
                playlist.BMSTables = [targetTable];
                vm.PlayHistory.ConfigureDisplayTargetCatalogRefresh(() => false, () => playlist.BMSTables.ToArray(), action => action());
                vm.PlayHistory.ReplaceDisplayTargetSets(
                    [new PlayHistoryDisplayTargetSet { Name = "Saved", Targets = [new PlayHistoryDisplayTargetReference { PlaylistId = 42 }] }],
                    playlist.BMSTables, false);
                vm.PlayHistory.SelectedDisplayTargetIdentity = usePreset ? "set:SAVED" : "playlist:42";
                vm.PlayHistory.ConfigureViewExecution(new PlayHistoryViewExecutionDependencies(() => library, () => playlist,
                    () => PlayHistoryReadSourceContext.FromSnapshot(snapshot), action => { TestUiDispatcherHost.Dispatcher.Invoke(action); return true; },
                    (_, _) => { }, () => MainViewUpdateMode.PlayHistorySelected, vm.MainChartList, vm.PlaylistWorkspace));
                TaskCompletionSource<PlayHistoryViewExecutionResult>? refreshCompletion = null;
                vm.PlayHistory.ConfigureViewRefreshScheduler(() => false, _ =>
                {
                    try { refreshCompletion?.TrySetResult(ExecuteCurrent()); }
                    catch (Exception ex) { refreshCompletion?.TrySetException(ex); throw; }
                });
                vm.PlayHistory.BeginRequest(PlayHistoryPeriodRequest.All(), string.Empty,
                    vm.PlayHistory.SelectedDisplayTargetIdentity, vm.PlayHistory.DisplayTargetRevision,
                    vm.PlaylistWorkspace.PrepareDetailSourceRetirementWithoutPublishing());
                Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, ExecuteCurrent().Status);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var table = (CustomTableView)window.FindName("customTableView");
                table.SelectRowsByPredicate(row => row is PlayHistoryRow history && ReferenceEquals(history.Raw, first));
                Assert.AreEqual(1, table.GetSelectedRowsSnapshot().Count);
                object previousRow = table.GetSelectedRowsSnapshot().Single();
                Assert.IsTrue(vm.PlayHistory.ApplyLibraryChanges(new NormalLibraryRefreshNotificationBatch(2, 1,
                    LibraryChartRefreshEffects.None, [], false, referenceCommits: [new PlaylistReferenceCommitReceipt(1, 2, [])])));
                Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, ExecuteCurrent().Status);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var selected = (PlayHistoryRow)table.GetSelectedRowsSnapshot().Single();
                Assert.AreSame(first, selected.Raw);
                Assert.AreNotSame(previousRow, selected);
                Assert.AreEqual(2, vm.MainChartList.Rows.Count);

                // MainWindowの実項目完了購読が、同対象の依存更新として既存queueを起動する。
                Refresh(() => Assert.IsTrue(vm.PlaylistWorkspace.PublishPlaylistEntriesHydrationCompleted(1)));
                AssertSelected("Before");

                // 同IDの表参照交換は、presetも現在参照先を解決して再投影する。
                targetTable = CreateTargetTable("After");
                playlist.BMSTables = [targetTable];
                Refresh(() => vm.PlayHistory.QueueDisplayTargetCatalogRefresh());
                AssertSelected("After");
                if (usePreset)
                {
                    // 同じpresetの確定参照先を変更しても、残る履歴identityは保持する。
                    playlist.BMSTables.Add(CreateTargetTable("Confirmed", playlistId: 43));
                    Refresh(() => vm.PlayHistory.RefreshDisplayTargetSetsFromSettings(
                        PlayHistoryDisplayTargetSetStore.Serialize(
                            [new PlayHistoryDisplayTargetSet { Name = "Saved", Targets = [new PlayHistoryDisplayTargetReference { PlaylistId = 43 }] }]),
                        queueRefreshWhenSelectionChanges: true));
                    AssertSelected("Confirmed");
                }

                Refresh(() => vm.PlayHistory.SelectedDisplayTargetIdentity = "all");
                Assert.AreEqual(0, table.GetSelectedRowsSnapshot().Count, "別対象への明示切替はResetする。");
                table.SelectRowsByPredicate(row => row is PlayHistoryRow history && ReferenceEquals(history.Raw, first));
                // 切替要求の実行前に同対象の項目完了を受けても、採用viewの対象scopeで旧選択を除外する。
                using (var started = new System.Threading.ManualResetEventSlim())
                using (var release = new System.Threading.ManualResetEventSlim())
                {
                    vm.PlayHistory.ConfigureViewRefreshScheduler(() => false, _ =>
                    {
                        started.Set();
                        release.Wait();
                        try { refreshCompletion?.TrySetResult(ExecuteCurrent()); }
                        catch (Exception ex) { refreshCompletion?.TrySetException(ex); throw; }
                    });
                    refreshCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        vm.PlayHistory.SelectedDisplayTargetIdentity = usePreset ? "set:SAVED" : "playlist:42";
                        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(10)), "対象切替workerが開始する。");
                        Assert.IsFalse(vm.PlayHistory.SnapshotActiveRequest().PreserveSelection);
                        Assert.IsTrue(vm.PlaylistWorkspace.PublishPlaylistEntriesHydrationCompleted(2));
                    }
                    finally
                    {
                        release.Set();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.PlayHistory.WaitForRefreshQueuesIdleAsync(), "history-target.switch-idle");
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(refreshCompletion.Task, "history-target.switch-adopted");
                    Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, refreshCompletion.Task.GetAwaiter().GetResult().Status);
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, table.GetSelectedRowsSnapshot().Count, "後続の同対象更新でも旧対象の選択は運ばない。");
                table.SelectRowsByPredicate(row => row is PlayHistoryRow history && ReferenceEquals(history.Raw, first));
                object rows = vm.MainChartList.Rows;
                PlayHistoryViewRequest adoptedRequest = vm.PlayHistory.SnapshotActiveRequest();
                foreach (string language in new[] { "en-US", "ja-JP" })
                {
                    ResourceService.Current.ChangeCulture(language);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual("2", vm.PlayHistory.SummaryCards.Single(card => card.Label == Resources.Play_history_summary_play_count).Value);
                    StringAssert.Contains(vm.MainChartList.SummaryText, Resources.Play_history_period_all);
                    Assert.AreSame(rows, vm.MainChartList.Rows);
                    Assert.AreSame(adoptedRequest, vm.PlayHistory.SnapshotActiveRequest());
                    Assert.AreSame(first, ((PlayHistoryRow)table.GetSelectedRowsSnapshot().Single()).Raw);
                }
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler onClosed = (_, _) => closed.TrySetResult();
                window.Closed += onClosed;
                try
                {
                    window.Close();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(window.CloseCompletion, "history-language.close-completion");
                    if (!closed.Task.IsCompleted) { window.Close(); }
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "history-language.closed");
                }
                finally { window.Closed -= onClosed; }
                int notifications = 0;
                vm.PlayHistory.PropertyChanged += (_, _) => notifications++;
                vm.MainChartList.PropertyChanged += (_, _) => notifications++;
                ResourceService.Current.ChangeCulture("en-US");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, notifications);

                PlayHistoryViewExecutionResult ExecuteCurrent()
                {
                    return vm.PlayHistory.ExecuteView(new PlayHistoryViewExecutionRequest(
                        MainViewUpdateMode.PlayHistorySelected, MainViewUpdateMode.TreeViewFilterNotChanged, null,
                        System.Diagnostics.Stopwatch.StartNew(), vm.PlayHistory.SnapshotActiveRequest(), string.Empty,
                        vm.PlayHistory.SelectedDisplayTarget));
                }

                void Refresh(Action change)
                {
                    refreshCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    try { change(); }
                    finally { TestUiDispatcherHost.AwaitTaskOnDispatcher(vm.PlayHistory.WaitForRefreshQueuesIdleAsync(), "history-target.refresh-idle"); }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(refreshCompletion.Task, "history-target.refresh-adopted");
                    Assert.AreEqual(PlayHistoryViewExecutionStatus.Applied, refreshCompletion.Task.GetAwaiter().GetResult().Status);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }

                void AssertSelected(string folder)
                {
                    Assert.AreEqual(2, vm.MainChartList.Rows.Count);
                    var current = (PlayHistoryRow)table.GetSelectedRowsSnapshot().Single();
                    Assert.AreSame(first, current.Raw);
                    StringAssert.Contains(current.FolderLabels, folder);
                }

                BMSTable CreateTargetTable(string folder, int playlistId = 42) => new()
                {
                    playlist_id = playlistId,
                    name = "Selected",
                    symbol = "S",
                    entries =
                    [new BMSTableEntry { playlist_id = playlistId, md5 = first.hash, folder = folder }]
                };
            });
            });
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
            Resources.Culture = previousCulture;
        }
    }

    /// <summary>実compiled履歴列メニューのMD5チェックが同じ設定の可視性と表の実列へ双方向反映されることを確認します。初期化と保存主体は既存列設定系列へ分担します。</summary>
    [TestMethod]
    public void PlayHistoryColumnMenu_TogglesMd5ColumnThroughCompiledBinding()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(MainWindowViewModelTestFactory.CreateIsolatedSettings(), (scope, vm, window) =>
        {
            MainWindowPresentationTestHarness.ShowCompiledContent(scope, vm, window);
            var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
            vm.MainChartList.ApplyRows(new MainChartListRowsApplyRequest
            {
                Rows = new[] { CreateResolvedPlayHistoryRow() },
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Reset,
                Summary = MainChartListSummaryUpdate.Preserve(),
                Stopwatch = System.Diagnostics.Stopwatch.StartNew()
            });
            TestUiDispatcherHost.ProcessQueuedPresentation();
            var table = (CustomTableView)window.FindName("customTableView");
            var menu = (ContextMenu)window.FindResource("playHistoryColumnHeaderContextMenu");
            MenuItem md5 = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "MD5 HASH"));
            Assert.IsTrue(System.Windows.Data.BindingOperations.IsDataBound(md5, MenuItem.IsCheckedProperty));
            foreach (bool visible in new[] { true, false })
            {
                md5.SetCurrentValue(MenuItem.IsCheckedProperty, visible);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(visible ? Visibility.Visible : Visibility.Hidden, settings.Hash.Visibility);
                Assert.AreEqual(visible, table.VisibleColumns.Any(column => column.Id == "Hash"));
                Assert.AreSame(settings, table.ColumnsSettings);
            }
        });
    }

    /// <summary>履歴の有効化/解除に実dropdownの表示が追随し、保存した表示対象の選択変更が主体へ戻ることを確認し、履歴状態とcompiled双方向Bindingの切断を検出します。</summary>
    [TestMethod]
    public void PlayHistoryDisplayTargetDropdown_BindsToPlayHistoryViewState()
    {
        PlayHistoryWorkflowOwner? startupPlayHistory = null;
        PropertyChangedEventHandler? startupDeactivateHandler = null;
        var startupDeactivated = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (scope, viewModel, window) =>
                {
                    MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                    var toolbar = (Border)window.FindName("mainTableToolbar");
                    ComboBox combo = FindVisualChildren<ComboBox>(toolbar).Single();
                    ComboBox dropdownContainer = combo;
                    var keyFilter = (DropDownMenuButton)window.FindName("KeyCountFilterButton");
                    ChartModeFilter savedKeys = viewModel.ChartFilters.ModeFilter;
                    Assert.AreEqual(new Thickness(1), combo.BorderThickness);
                    Assert.AreSame(window.FindResource("App.Canonical.ComboBoxTemplate"), combo.Template);

                    VisibilityObservation startupCollapsed = ObserveVisibility(
                        dropdownContainer,
                        Visibility.Collapsed);
                    try
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            startupDeactivated.Task,
                            "play-history startup deactivation");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            startupCollapsed.Completion,
                            "play-history dropdown collapsed after startup deactivation");

                        Assert.IsFalse(viewModel.PlayHistory.IsViewActive);
                        Assert.AreEqual(Visibility.Collapsed, dropdownContainer.Visibility);
                        Assert.AreEqual(Visibility.Visible, keyFilter.Visibility);
                    }
                    finally
                    {
                        startupCollapsed.Dispose();
                    }

                    VisibilityObservation activatedVisible = ObserveVisibility(
                        dropdownContainer,
                        Visibility.Visible);
                    try
                    {
                        viewModel.PlayHistory.BeginRequest(
                            PlayHistoryPeriodRequest.All(),
                            string.Empty,
                            viewModel.PlayHistory.SelectedDisplayTargetIdentity,
                            viewModel.PlayHistory.DisplayTargetRevision);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            activatedVisible.Completion,
                            "play-history dropdown visible after activation");

                        PlayHistoryDisplayTargetItem selected = viewModel.PlayHistory.DisplayTargets
                            .Skip(1)
                            .First();
                        Assert.IsTrue(viewModel.PlayHistory.IsViewActive);
                        Assert.AreEqual(viewModel.PlayHistory.DisplayTargets.Count, combo.Items.Count);
                        Assert.AreEqual(viewModel.PlayHistory.SelectedDisplayTargetIdentity, combo.SelectedValue);
                        Assert.AreEqual(Visibility.Visible, dropdownContainer.Visibility);
                        Assert.AreEqual(Visibility.Collapsed, keyFilter.Visibility);
                        Assert.AreEqual(Visibility.Collapsed, keyFilter.DropDownContextMenu.Visibility);
                        Assert.AreEqual(savedKeys, viewModel.ChartFilters.ModeFilter);

                        combo.SelectedValue = selected.Identity;
                        combo.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateSource();

                        Assert.AreEqual(selected.Identity, viewModel.PlayHistory.SelectedDisplayTargetIdentity);
                        Assert.AreEqual(selected.Identity, combo.SelectedValue);

                        VisibilityObservation deactivatedCollapsed = ObserveVisibility(
                            dropdownContainer,
                            Visibility.Collapsed);
                        try
                        {
                            viewModel.PlayHistory.Deactivate();
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                                deactivatedCollapsed.Completion,
                                "play-history dropdown collapsed after deactivation");

                            Assert.IsFalse(viewModel.PlayHistory.IsViewActive);
                            Assert.AreEqual(Visibility.Collapsed, dropdownContainer.Visibility);
                            Assert.AreEqual(Visibility.Visible, keyFilter.Visibility);
                            Assert.AreEqual(savedKeys, viewModel.ChartFilters.ModeFilter);
                        }
                        finally
                        {
                            deactivatedCollapsed.Dispose();
                        }
                    }
                    finally
                    {
                        activatedVisible.Dispose();
                    }
                },
                prepareViewModel: viewModel =>
                {
                    viewModel.PlayHistory.ConfigureDisplayTargetPersistence(_ => { });
                    viewModel.PlayHistory.ReplaceDisplayTargetSets(
                        [
                            new PlayHistoryDisplayTargetSet
                            {
                                Name = "Session",
                                Targets = [new PlayHistoryDisplayTargetReference { PlaylistId = 42 }]
                            }
                        ],
                        [],
                        queueRefreshWhenSelectionChanges: false);

                    PlayHistoryWorkflowOwner playHistory = viewModel.PlayHistory;
                    startupPlayHistory = playHistory;
                    bool wasViewActive = playHistory.IsViewActive;
                    PropertyChangedEventHandler startupHandler = (_, args) =>
                    {
                        if (!string.Equals(
                                args.PropertyName,
                                nameof(PlayHistoryWorkflowOwner.IsViewActive),
                                StringComparison.Ordinal))
                        {
                            return;
                        }

                        bool isViewActive = playHistory.IsViewActive;
                        if (wasViewActive && !isViewActive)
                        {
                            startupDeactivated.TrySetResult(true);
                        }
                        wasViewActive = isViewActive;
                    };
                    startupDeactivateHandler = startupHandler;
                    playHistory.PropertyChanged += startupHandler;
                    playHistory.BeginRequest(
                        PlayHistoryPeriodRequest.All(),
                        string.Empty,
                        playHistory.SelectedDisplayTargetIdentity,
                        playHistory.DisplayTargetRevision);
                });
        }
        finally
        {
            if (startupPlayHistory != null && startupDeactivateHandler != null)
            {
                startupPlayHistory.PropertyChanged -= startupDeactivateHandler;
            }
        }
    }

    private static VisibilityObservation ObserveVisibility(
        FrameworkElement target,
        Visibility expected)
    {
        DependencyPropertyDescriptor descriptor =
            DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, target.GetType())
            ?? throw new InvalidOperationException(
                $"The Visibility dependency property is not available for {target.GetType().FullName}.");
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler valueChanged = (_, _) =>
        {
            if (target.Visibility == expected)
            {
                completion.TrySetResult(true);
            }
        };
        descriptor.AddValueChanged(target, valueChanged);
        if (target.Visibility == expected)
        {
            completion.TrySetResult(true);
        }
        return new VisibilityObservation(target, descriptor, valueChanged, completion.Task);
    }

    private sealed class VisibilityObservation : IDisposable
    {
        private readonly FrameworkElement target;
        private readonly DependencyPropertyDescriptor descriptor;
        private readonly EventHandler valueChanged;
        private bool disposed;

        internal VisibilityObservation(
            FrameworkElement target,
            DependencyPropertyDescriptor descriptor,
            EventHandler valueChanged,
            Task completion)
        {
            this.target = target;
            this.descriptor = descriptor;
            this.valueChanged = valueChanged;
            Completion = completion;
        }

        internal Task Completion { get; }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            descriptor.RemoveValueChanged(target, valueChanged);
        }
    }

    /// <summary>履歴の有効化で指定カードと診断を実表へ表示し、カード操作で対応フィルターを選ぶことを確認し、履歴専用表示や操作接続の欠落を検出します。</summary>
    [TestMethod]
    public void PlayHistoryMainTable_ShowsDedicatedSummaryCardsAndDiagnostics()
    {
        PlayHistoryWorkflowOwner? startupPlayHistory = null;
        PropertyChangedEventHandler? startupDeactivateHandler = null;
        var startupDeactivated = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (scope, viewModel, window) =>
                {
                    MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                    var summaryBar = (Border)window.FindName("playHistorySummaryBar");

                    VisibilityObservation startupCollapsed = ObserveVisibility(
                        summaryBar,
                        Visibility.Collapsed);
                    try
                    {
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            startupDeactivated.Task,
                            "play-history startup deactivation");
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            startupCollapsed.Completion,
                            "play-history summary bar collapsed after startup deactivation");

                        Assert.IsFalse(viewModel.PlayHistory.IsViewActive);
                        Assert.AreEqual(Visibility.Collapsed, summaryBar.Visibility);
                    }
                    finally
                    {
                        startupCollapsed.Dispose();
                    }

                    viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(false);
                    VisibilityObservation activatedVisible = ObserveVisibility(
                        summaryBar,
                        Visibility.Visible);
                    try
                    {
                        viewModel.PlayHistory.BeginRequest(
                            PlayHistoryPeriodRequest.All(),
                            string.Empty,
                            viewModel.PlayHistory.SelectedDisplayTargetIdentity,
                            viewModel.PlayHistory.DisplayTargetRevision);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            activatedVisible.Completion,
                            "play-history summary bar visible after activation");
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        ItemsControl cards = FindVisualChildren<ItemsControl>(summaryBar).Single();
                        TextBlock diagnostic = FindVisualChildren<TextBlock>(summaryBar)
                            .Single(textBlock => textBlock.Text == "diagnostic");

                        Assert.AreEqual(Visibility.Visible, summaryBar.Visibility);
                        Assert.AreEqual(2, cards.Items.Count);
                        Assert.AreEqual("diagnostic", diagnostic.Text);
                        Assert.AreEqual(Visibility.Visible, diagnostic.Visibility);

                        Button filterButton = FindVisualChildren<Button>(cards)
                            .Single(button => (button.DataContext as PlayHistorySummaryCard)?.FilterKey == "clear");
                        Assert.IsNotNull(filterButton.Command);
                        Assert.IsTrue(filterButton.Command.CanExecute(filterButton.CommandParameter));
                        filterButton.Command.Execute(filterButton.CommandParameter);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        Assert.IsTrue(viewModel.PlayHistory.SummaryCards.Single(card => card.FilterKey == "clear").IsSelected);
                    }
                    finally
                    {
                        activatedVisible.Dispose();
                    }
                },
                prepareViewModel: viewModel =>
                {
                    viewModel.PlayHistory.PresentationState.SetSummaryCards(
                    [
                        new PlayHistorySummaryCard("All", "2"),
                        new PlayHistorySummaryCard("Clear", "1", filterKey: "clear", filterText: "clear")
                    ]);
                    viewModel.PlayHistory.PresentationState.SetDiagnosticText("diagnostic");

                    PlayHistoryWorkflowOwner playHistory = viewModel.PlayHistory;
                    startupPlayHistory = playHistory;
                    bool wasViewActive = playHistory.IsViewActive;
                    PropertyChangedEventHandler startupHandler = (_, args) =>
                    {
                        if (!string.Equals(
                                args.PropertyName,
                                nameof(PlayHistoryWorkflowOwner.IsViewActive),
                                StringComparison.Ordinal))
                        {
                            return;
                        }

                        bool isViewActive = playHistory.IsViewActive;
                        if (wasViewActive && !isViewActive)
                        {
                            startupDeactivated.TrySetResult(true);
                        }
                        wasViewActive = isViewActive;
                    };
                    startupDeactivateHandler = startupHandler;
                    playHistory.PropertyChanged += startupHandler;
                    playHistory.BeginRequest(
                        PlayHistoryPeriodRequest.All(),
                        string.Empty,
                        playHistory.SelectedDisplayTargetIdentity,
                        playHistory.DisplayTargetRevision);
                });
        }
        finally
        {
            if (startupPlayHistory != null && startupDeactivateHandler != null)
            {
                startupPlayHistory.PropertyChanged -= startupDeactivateHandler;
            }
        }
    }

    /// <summary>解決済み/未解決の履歴行でAppsキーとメニュー開始が同じ行文脈を保持することを確認し、通常譜面の操作経路へ履歴を取り違える誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryMainTable_PreservesResolvedAndUnresolvedRowMenuContext()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (scope, viewModel, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var table = (CustomTableView)window.FindName("customTableView");
                Assert.AreEqual(viewModel.MainChartList.RowDragKind, table.RowDragKind);

                PlayHistoryRow resolved = CreateResolvedPlayHistoryRow();
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                // 非共有XAML資源の同じメニュー実体で、開始時点の行と操作経路を観測します。
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                int contextRequests = 0;
                CustomTableRowRequestedEventArgs? contextRequest = null;
                table.RowContextMenuRequested += (_, args) =>
                {
                    contextRequests++;
                    contextRequest = args;
                };
                table.ItemsSource = new List<object> { resolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, resolved));
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(resolved, out _));
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(true);
                Assert.IsTrue(table.HandleKeyDown(Key.Apps, ModifierKeys.None));
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                Assert.AreEqual(1, contextRequests);
                Assert.IsNotNull(contextRequest);
                Assert.AreSame(resolved, contextRequest!.Row);
                RaiseMenuOpened(playHistoryMenu, table, resolved, 0);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.IsInstanceOfType(playHistoryMenu.Tag, typeof(CustomTableContextMenuContext));
                var resolvedContext =
                    (CustomTableContextMenuContext)playHistoryMenu.Tag;
                Assert.AreSame(resolved, resolvedContext.Row);
                Assert.AreSame(table, playHistoryMenu.PlacementTarget);
                RaiseMenuClosed(playHistoryMenu);

                object unresolvedTagSentinel = new();
                playHistoryMenu.Tag = unresolvedTagSentinel;
                playHistoryMenu.PlacementTarget = null;
                PlayHistoryRow unresolved = CreateUnresolvedPlayHistoryRow();
                table.ItemsSource = new List<object> { unresolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, unresolved));
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(true);
                Assert.IsTrue(table.HandleKeyDown(Key.Apps, ModifierKeys.None));
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                Assert.AreEqual(2, contextRequests);
                Assert.AreSame(unresolved, contextRequest!.Row);
                RaiseMenuOpened(playHistoryMenu, table, unresolved, 0);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.IsInstanceOfType(playHistoryMenu.Tag, typeof(CustomTableContextMenuContext));
                Assert.AreSame(unresolved, ((CustomTableContextMenuContext)playHistoryMenu.Tag).Row);
                Assert.AreSame(table, playHistoryMenu.PlacementTarget);
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(unresolved, out _));
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(resolved, out _));
                RaiseMenuClosed(playHistoryMenu);
            });
    }

    /// <summary>識別子を持たない履歴でも複数選択時だけ期間検索メニューを表示し一行選択で隠すことを確認し、譜面の解決可否で期間操作を禁止する誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryContextMenu_ShowsDateRangeOnlyForMultipleSelection()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (scope, viewModel, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow first = CreateUnresolvedPlayHistoryRow(hash: string.Empty, playedAt: 1000);
                PlayHistoryRow second = CreateUnresolvedPlayHistoryRow(hash: string.Empty, playedAt: 1001);
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { first, second };
                table.SelectRowsByPredicate(_ => true);

                RaiseMenuOpened(playHistoryMenu, table, first, 0);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                MenuItem? dateRange = playHistoryMenu.Items
                    .OfType<MenuItem>()
                    .SingleOrDefault(item => item.Name == "playHistoryContextMenuItemAddDateRangeToSearch");
                Assert.IsNotNull(dateRange);
                Assert.AreEqual(Visibility.Visible, dateRange.Visibility);

                RaiseMenuClosed(playHistoryMenu);
                table.SelectRowsByPredicate(row => ReferenceEquals(row, first));
                RaiseMenuOpened(playHistoryMenu, table, first, 0);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(Visibility.Collapsed, dateRange.Visibility);
                RaiseMenuClosed(playHistoryMenu);
            });
    }

    /// <summary>空hashの主行もstate/inputとして受理し、副行だけ所持譜面でも主行のローカル3操作は出さず期間操作と行文脈を保つことを確認し、副行の能力を主行へ混ぜる誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryContextMenu_DoesNotInferPrimaryActionsFromSecondaryRows()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (scope, viewModel, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow primary = CreateUnresolvedPlayHistoryRow(hash: string.Empty, playedAt: 1000);
                PlayHistoryRow secondary = CreateResolvedPlayHistoryRow(playedAt: 1001);
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { primary, secondary };
                table.SelectRowsByPredicate(
                    _ => true,
                    row => ReferenceEquals(row, primary));

                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(primary, out PlayHistoryContextMenuState primaryState));
                Assert.AreEqual(string.Empty, primaryState.Md5);
                Assert.AreEqual(string.Empty, primaryState.Sha256);
                Assert.AreEqual(string.Empty, primaryState.ChartPath);
                Assert.IsFalse(primaryState.CanOpenAssociated);
                Assert.IsFalse(primaryState.CanOpenExplorer);
                Assert.IsFalse(primaryState.CanOpenScoreViewer);
                Assert.IsTrue(viewModel.PlayHistory.TryCreateRightClickActionResolutionInput(primary, _ => true, out RightClickActionResolutionInput primaryInput));
                Assert.AreEqual(string.Empty, primaryInput.Md5);
                Assert.AreEqual(string.Empty, primaryInput.Sha256);
                Assert.IsNull(primaryInput.LocalFilePath);
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(secondary, out PlayHistoryContextMenuState secondaryState));
                Assert.IsTrue(secondaryState.CanOpenAssociated);
                Assert.IsTrue(secondaryState.CanOpenExplorer);
                Assert.IsTrue(secondaryState.CanOpenScoreViewer);
                RaiseMenuOpened(playHistoryMenu, table, primary, 0);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                MenuItem dateRange = FindMenuItem(playHistoryMenu, "playHistoryContextMenuItemAddDateRangeToSearch");
                Assert.AreEqual(Visibility.Visible, dateRange.Visibility);
                foreach (string name in new[] { "playHistoryContextMenuItemOpenAssociated", "playHistoryContextMenuItemOpenExplorer", "playHistoryContextMenuItemRegisterScore" })
                {
                    Assert.AreEqual(Visibility.Collapsed, FindMenuItem(playHistoryMenu, name).Visibility, name);
                }
                Assert.AreSame(primary, ((CustomTableContextMenuContext)playHistoryMenu.Tag).Row);
                RaiseMenuClosed(playHistoryMenu);
            });
    }

    /// <summary>メニュー開始後に選択を変えても受付時の最小/最大時刻を検索へ一回追記し履歴保存は変えないことを確認し、クリック時の選択再取得や二重更新を検出します。</summary>
    [TestMethod]
    public void PlayHistoryContextMenu_DateRangeUsesOpenSnapshotAndUpdatesKeywordOnce()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        settings.KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["title:previous"]);
        string historyBeforeAction = settings.KeywordSearchHistory;

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            settings,
            (scope, viewModel, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                var table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow first = CreateResolvedPlayHistoryRow(playedAt: 1000);
                PlayHistoryRow second = CreateResolvedPlayHistoryRow(playedAt: 1001);
                PlayHistoryRow intermediate = CreateResolvedPlayHistoryRow(playedAt: 1002);
                PlayHistoryRow later = CreateResolvedPlayHistoryRow(playedAt: 2000);
                table.ItemsSource = new List<object> { later, intermediate, first, second };
                table.SelectRowsByPredicate(
                    row => ReferenceEquals(row, first) || ReferenceEquals(row, later),
                    row => ReferenceEquals(row, first));
                CollectionAssert.AreEqual(
                    new object[] { later, first },
                    table.GetSelectedRowsSnapshot().ToArray(),
                    "The table enumeration order must differ from the chronological min/max order.");
                Assert.IsTrue(later.PlayedAt > first.PlayedAt);

                const string prefix = "title:alpha\t";
                viewModel.ChartFilters.KeywordFilter = prefix;
                int keywordChangedCount = 0;
                viewModel.ChartFilters.KeywordFilterChanged += (_, _) => keywordChangedCount++;

                string start = first.PlayedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
                string end = later.PlayedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
                string expectedClause = $"date:\"{start}..{end}\"";
                bool opened = false;
                bool closed = false;
                RoutedEventHandler menuOpenedHandler = (_, _) =>
                {
                    opened = true;
                    MenuItem dateRange = FindMenuItem(
                        playHistoryMenu,
                        "playHistoryContextMenuItemAddDateRangeToSearch");
                    Assert.AreEqual(Visibility.Visible, dateRange.Visibility);
                    Assert.IsTrue(dateRange.IsEnabled);

                    // compiledのOpened処理が開始時点の最小/最大時刻を捕捉した後で選択を変えます。
                    table.SelectRowsByPredicate(row => ReferenceEquals(row, intermediate));
                    dateRange.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, dateRange));
                };
                RoutedEventHandler menuClosedHandler = (_, _) => closed = true;
                playHistoryMenu.Opened += menuOpenedHandler;
                playHistoryMenu.Closed += menuClosedHandler;
                try
                {
                    // 公開メニューイベントから同じcompiled経路を実行し、選択snapshotの受付を観測します。
                    RaiseMenuOpened(playHistoryMenu, table, first, 2);
                    RaiseMenuClosed(playHistoryMenu);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }
                finally
                {
                    playHistoryMenu.Opened -= menuOpenedHandler;
                    playHistoryMenu.Closed -= menuClosedHandler;
                }

                Assert.IsTrue(opened);
                Assert.IsTrue(closed);
                Assert.AreEqual(prefix + expectedClause, viewModel.ChartFilters.KeywordFilter);
                Assert.AreEqual(1, keywordChangedCount);
                Assert.AreEqual(historyBeforeAction, settings.KeywordSearchHistory);
            });
    }

    /// <summary>所持bmson履歴のMD5 Web操作を表示し、実クリックが本来の両hashとactionIDへ一回届くことを確認します。copy専用項目なしと合法なseparatorも維持します。</summary>
    [TestMethod]
    public void PlayHistoryBmsonRowOffersConfiguredMd5WebAction()
    {
        const string actionName = "Bmson MD5";
        var calls = new List<(RightClickActionResolutionInput Input, ConfiguredExternalActionKind Kind, string Id)>();
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        settings.RightClickActionsJson = RightClickActionSettingsSerializer.Serialize(
            new RightClickActionSettings(
                [new RightClickWebActionDefinition(
                    "bmson-md5",
                    actionName,
                    "https://example.test/{md5}",
                    enabled: true,
                    ExternalChartKind.BmsonOnly)],
                []));

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            settings,
            (scope, viewModel, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow resolved = CreateResolvedBmsonPlayHistoryRow();
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { resolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, resolved));

                RaiseMenuOpened(playHistoryMenu, table, resolved, 0);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                MenuItem webAction = FindMenuItem(playHistoryMenu, "configuredWebAction_bmson_md5");
                Assert.AreEqual(actionName, webAction.Header);
                Assert.AreEqual(Visibility.Visible, webAction.Visibility);
                AssertHistoryMenuShape(playHistoryMenu);
                webAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, webAction));
                Assert.AreEqual(1, calls.Count); Assert.AreEqual("bmson-md5", calls[0].Id); Assert.AreEqual(ConfiguredExternalActionKind.Web, calls[0].Kind);
                Assert.AreEqual(resolved.Md5, calls[0].Input.Md5); Assert.AreEqual(resolved.Sha256, calls[0].Input.Sha256);
                RaiseMenuClosed(playHistoryMenu);
            }, selectedChartContextMenuTerminals: CreateHistoryTerminals(settings, (input, kind, id) => calls.Add((input, kind, id))));
    }

    /// <summary>未解決beatorajaは全kindのSHA256 Web操作だけを表示し、実クリックでもMD5空/SHA正値を維持します。未知kindの推測、copy専用項目の残留と不正なseparatorを検出します。</summary>
    [TestMethod]
    public void PlayHistoryUnresolvedBeatorajaRowOffersOnlyWebActionsForBothChartKinds()
    {
        const string allKindsName = "All kinds";
        var calls = new List<(RightClickActionResolutionInput Input, string Id)>();
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        settings.RightClickActionsJson = RightClickActionSettingsSerializer.Serialize(
            new RightClickActionSettings(
                [
                    new RightClickWebActionDefinition(
                        "bms-only",
                        "BMS only",
                        "https://example.test/bms/{sha256}",
                        enabled: true,
                        ExternalChartKind.BmsOnly),
                    new RightClickWebActionDefinition(
                        "bmson-only",
                        "bmson only",
                        "https://example.test/bmson/{sha256}",
                        enabled: true,
                        ExternalChartKind.BmsonOnly),
                    new RightClickWebActionDefinition(
                        "all-kinds",
                        allKindsName,
                        "https://example.test/all/{sha256}",
                        enabled: true,
                        ExternalChartKind.All)
                ],
                []));

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            settings,
            (scope, viewModel, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow unresolved = CreateUnresolvedBeatorajaPlayHistoryRow();
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { unresolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, unresolved));

                RaiseMenuOpened(playHistoryMenu, table, unresolved, 0);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.IsFalse(playHistoryMenu.Items.OfType<MenuItem>().Any(item =>
                    string.Equals(item.Name, "configuredWebAction_bms_only", StringComparison.Ordinal)));
                Assert.IsFalse(playHistoryMenu.Items.OfType<MenuItem>().Any(item =>
                    string.Equals(item.Name, "configuredWebAction_bmson_only", StringComparison.Ordinal)));
                MenuItem allKinds = FindMenuItem(playHistoryMenu, "configuredWebAction_all_kinds");
                Assert.AreEqual(allKindsName, allKinds.Header);
                Assert.AreEqual(Visibility.Visible, allKinds.Visibility);
                AssertHistoryMenuShape(playHistoryMenu);
                allKinds.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, allKinds));
                Assert.AreEqual(1, calls.Count); Assert.AreEqual("all-kinds", calls[0].Id);
                Assert.AreEqual(string.Empty, calls[0].Input.Md5); Assert.AreEqual(unresolved.Sha256, calls[0].Input.Sha256); Assert.IsNull(calls[0].Input.LocalFilePath);
                RaiseMenuClosed(playHistoryMenu);
            }, selectedChartContextMenuTerminals: CreateHistoryTerminals(settings, (input, _, id) => calls.Add((input, id))));
    }

    /// <summary>実在ファイルへ解決した履歴の関連付け操作を表示し、実クリックから同じ譜面targetへ一回配送します。OS起動は端末で止め、copy項目なしと合法なseparatorを確認します。</summary>
    [TestMethod]
    public void PlayHistoryResolvedRowOffersAssociatedOpenForExistingFile()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        settings.RightClickActionsJson = RightClickActionSettingsSerializer.Serialize(new RightClickActionSettings([], []));
        var calls = new List<(ChartOperationTarget Target, SelectedChartExternalActionKind Kind)>();
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            settings,
            (scope, viewModel, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);
                var table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow resolved = CreateResolvedPlayHistoryRow(
                    typeof(MainWindow).Assembly.Location);
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { resolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, resolved));

                RaiseMenuOpened(playHistoryMenu, table, resolved, 0);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                MenuItem associated = FindMenuItem(
                    playHistoryMenu,
                    "playHistoryContextMenuItemOpenAssociated");
                Assert.AreEqual(Visibility.Visible, associated.Visibility);
                Assert.IsTrue(associated.IsEnabled);
                AssertHistoryMenuShape(playHistoryMenu);
                associated.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, associated));
                Assert.AreEqual(1, calls.Count); Assert.AreEqual(SelectedChartExternalActionKind.OpenFile, calls[0].Kind);
                Assert.AreSame(resolved.ResolvedChart, calls[0].Target.Chart);
                RaiseMenuClosed(playHistoryMenu);
            }, selectedChartContextMenuTerminals: CreateHistoryTerminals(settings, (_, _, _) => Assert.Fail("関連付け操作を設定Web操作へ渡しません。"), (target, kind) => calls.Add((target, kind))));
    }

    private static void AssertHistoryMenuShape(ContextMenu menu)
    {
        Assert.IsFalse(menu.Items.OfType<MenuItem>().Any(item => item.Name is "playHistoryContextMenuItemCopyMd5" or "playHistoryContextMenuItemCopySha256"));
        Control[] visible = menu.Items.OfType<Control>().Where(item => item.Visibility == Visibility.Visible).ToArray();
        if (visible.Length == 0) { return; }
        Assert.IsFalse(visible[0] is Separator); Assert.IsFalse(visible[^1] is Separator);
        for (int i = 1; i < visible.Length; i++) { Assert.IsFalse(visible[i] is Separator && visible[i - 1] is Separator); }
    }

    // 実resolverの現在設定で表示し、実クリックから端末へ届く入力だけを捕捉します。OS起動は行いません。
    private static MainWindowSelectedChartContextMenuTerminals CreateHistoryTerminals(Settings settings,
        Action<RightClickActionResolutionInput, ConfiguredExternalActionKind, string> configured,
        Action<ChartOperationTarget, SelectedChartExternalActionKind>? local = null)
    {
        var external = new MainWindowSelectedChartExternalActionsTerminal(
            (target, kind) => kind == SelectedChartExternalActionKind.OpenFile && target.HasCapability(ChartOperationCapabilities.OpenFile),
            (target, kind) => { if (local == null) { Assert.Fail("未指定のローカル操作です。"); } else { local(target, kind); } },
            _ => false, (_, _) => Task.FromResult(RelatedDocumentQueryReceipt.Unavailable), _ => throw new AssertFailedException("関連文書は対象外です。"),
            _ => throw new AssertFailedException("履歴の専用入力経路を使います。"),
            input => { RightClickActionSettingsParseResult parsed = RightClickActionSettingsSerializer.Parse(settings.RightClickActionsJson); Assert.IsTrue(parsed.Succeeded); return RightClickActionResolver.Resolve(parsed.Settings, input); },
            (input, kind, id) => { configured(input, kind, id); return ExternalConfiguredActionResult.Success; });
        return new MainWindowSelectedChartContextMenuTerminals(
            new MainWindowSelectedChartMutationTerminal(_ => throw new AssertFailedException(), _ => throw new AssertFailedException(), _ => throw new AssertFailedException(), _ => throw new AssertFailedException()),
            new MainWindowSelectedChartResourceHealthTerminal(_ => throw new AssertFailedException(), (_, _) => throw new AssertFailedException()),
            new MainWindowSelectedChartAudioConversionTerminal((_, _) => throw new AssertFailedException()),
            new MainWindowChartInfoParseFailureRemovalTerminal(_ => throw new AssertFailedException()),
            new MainWindowScoreViewerTerminal(_ => false, _ => false, (_, _) => throw new AssertFailedException(), (_, _, _) => throw new AssertFailedException()), external);
    }

    private static IReadOnlyList<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var result = new List<T>();
        Visit(root, result, new HashSet<DependencyObject>());
        return result;
    }

    private static void Visit<T>(DependencyObject current, List<T> result, HashSet<DependencyObject> visited)
        where T : DependencyObject
    {
        if (current == null || !visited.Add(current))
        {
            return;
        }
        if (current is T match)
        {
            result.Add(match);
        }
        if (current is not Visual && current is not System.Windows.Media.Media3D.Visual3D)
        {
            return;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
        {
            Visit(VisualTreeHelper.GetChild(current, index), result, visited);
        }
    }

    private static void RaiseMenuOpened(
        ContextMenu menu,
        CustomTableView table,
        object row,
        int rowIndex)
    {
        menu.Tag = new CustomTableContextMenuContext(row, rowIndex);
        menu.PlacementTarget = table;
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
    }

    private static void RaiseMenuClosed(ContextMenu menu)
        => menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent, menu));

    private static MenuItem FindMenuItem(ContextMenu menu, string name)
    {
        return menu.Items
            .OfType<MenuItem>()
            .Single(item => string.Equals(item.Name, name, StringComparison.Ordinal));
    }

    private static PlayHistoryRow CreateResolvedPlayHistoryRow(string? path = null, long playedAt = 1000)
    {
        const string hash = "cccccccccccccccccccccccccccccccc";
        string sha256 = new string('d', 64);
        ChartFile file = ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(
        [
            hash,
            "Resolved Play History",
            "",
            "Artist",
            "",
            "",
            "",
            path ?? @"C:\BMS\play-history-resolved.bms",
            "",
            "Folder",
            "",
            "",
            "",
            "",
            "12",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            ""
        ]));
        file = file with { Md5 = hash, Sha256 = sha256 };
        PlayHistoryProjectionIndex projectionIndex = PlayHistoryCatalogTestSupport.Create(
            [file],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [hash] = sha256 });
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 2,
                        hash = hash,
                        played_at = playedAt,
                        finalized = 1,
                        score_write_type = "update",
                        new_playcount = 1,
                        playcount_delta = 1,
                        new_exscore = 100,
                        new_totalnotes = 100
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            projectionIndex);
        return projected.Rows.Single();
    }

    private static PlayHistoryRow CreateResolvedBmsonPlayHistoryRow(long playedAt = 1000)
    {
        const string md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        string sha256 = new string('f', 64);
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\BMS\play-history-resolved.bmson",
            Title = "Resolved BMSON Play History",
            RawTitle = "Resolved BMSON Play History",
            Artist = "Artist",
            RawArtist = "Artist",
            Md5 = md5,
            Sha256 = sha256
        };
        PlayHistoryProjectionIndex projectionIndex = PlayHistoryCatalogTestSupport.Create([song]);
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectBeatorajaRows(
            new BeatorajaPlayHistoryReadResult(
                PlayHistorySourceProfile.Beatoraja("score.db"),
                [
                    new BeatorajaPlayHistoryRecord
                    {
                        history_id = 3,
                        sha256 = sha256,
                        played_at = playedAt,
                        playcount = 1
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            projectionIndex);
        return projected.Rows.Single();
    }

    private static PlayHistoryRow CreateUnresolvedBeatorajaPlayHistoryRow(long playedAt = 1000)
    {
        string sha256 = new string('a', 64);
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectBeatorajaRows(
            new BeatorajaPlayHistoryReadResult(
                PlayHistorySourceProfile.Beatoraja("score.db"),
                [
                    new BeatorajaPlayHistoryRecord
                    {
                        history_id = 4,
                        sha256 = sha256,
                        played_at = playedAt,
                        playcount = 1
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);
        return projected.Rows.Single();
    }

    private static PlayHistoryRow CreateUnresolvedPlayHistoryRow(string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", long playedAt = 1000)
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 1,
                        hash = hash,
                        played_at = playedAt,
                        finalized = 1,
                        score_write_type = "update",
                        new_playcount = 1,
                        playcount_delta = 1,
                        new_exscore = 100,
                        new_totalnotes = 100
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);
        return projected.Rows.Single();
    }
}
