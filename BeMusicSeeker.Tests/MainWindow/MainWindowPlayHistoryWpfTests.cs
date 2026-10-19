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
                    var dropdownContainer = (Border)combo.Parent;

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

    /// <summary>主行が未解決で副行だけ所持譜面でも主行のローカル操作は出さず期間操作を保つことを確認し、副行の能力を主行へ混ぜる誤りを検出します。</summary>
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

                Assert.IsFalse(viewModel.PlayHistory.TryCreateContextMenuState(primary, out _));
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(secondary, out _));
                RaiseMenuOpened(playHistoryMenu, table, primary, 0);
                TestUiDispatcherHost.ProcessQueuedPresentation();

                MenuItem dateRange = FindMenuItem(playHistoryMenu, "playHistoryContextMenuItemAddDateRangeToSearch");
                Assert.AreEqual(Visibility.Visible, dateRange.Visibility);
                Assert.AreEqual(
                    Visibility.Collapsed,
                    FindMenuItem(playHistoryMenu, "playHistoryContextMenuItemOpenExplorer").Visibility);
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

    /// <summary>所持bmson履歴で利用者のbmson専用MD5 Web操作を名前通り表示することを確認し、BMS専用条件で登録済みのbmson操作を落とす誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryBmsonRowOffersConfiguredMd5WebAction()
    {
        const string actionName = "Bmson MD5";
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
                RaiseMenuClosed(playHistoryMenu);
            });
    }

    /// <summary>kind不明の未解決beatoraja履歴には全kindのSHA256 Web操作だけを出しkind限定操作は出さないことを確認し、未知kindへ一方の能力を推測する誤りを検出します。</summary>
    [TestMethod]
    public void PlayHistoryUnresolvedBeatorajaRowOffersOnlyWebActionsForBothChartKinds()
    {
        const string allKindsName = "All kinds";
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
                RaiseMenuClosed(playHistoryMenu);
            });
    }

    /// <summary>実在ファイルへ解決した履歴では関連付け操作を表示して有効にすることを確認し、履歴の所持譜面へ操作できなくなるcompiledメニュー接続漏れを検出します。</summary>
    [TestMethod]
    public void PlayHistoryResolvedRowOffersAssociatedOpenForExistingFile()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
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
                RaiseMenuClosed(playHistoryMenu);
            });
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
