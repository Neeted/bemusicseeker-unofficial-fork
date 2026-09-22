using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowPlayHistoryWpfTests
{
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
                (viewModel, window) =>
                {
                    using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlayHistoryDropdown");
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
                (viewModel, window) =>
                {
                    using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlayHistorySummary");
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
                        TestUiDispatcherHost.Drain();

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
                        TestUiDispatcherHost.Drain();

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

    [TestMethod]
    public void PlayHistoryMainTable_UsesBoundDragKindAndRejectsChartContextMenu()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlayHistoryContext");
                var table = (CustomTableView)window.FindName("customTableView");
                Assert.AreEqual(viewModel.MainChartList.RowDragKind, table.RowDragKind);

                PlayHistoryRow resolved = CreateResolvedPlayHistoryRow();
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                // The XAML resource is deliberately non-shared; make this observation use the same instance as the route.
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                Assert.AreSame(playHistoryMenu, window.FindResource("playHistoryContextMenu"));
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
                TestUiDispatcherHost.Drain();

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
                TestUiDispatcherHost.Drain();

                Assert.IsInstanceOfType(playHistoryMenu.Tag, typeof(CustomTableContextMenuContext));
                Assert.AreSame(unresolved, ((CustomTableContextMenuContext)playHistoryMenu.Tag).Row);
                Assert.AreSame(table, playHistoryMenu.PlacementTarget);
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(unresolved, out _));
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(resolved, out _));
                RaiseMenuClosed(playHistoryMenu);
            });
    }

    [TestMethod]
    public void PlayHistoryContextMenu_ShowsDateRangeOnlyForMultipleSelection()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlayHistoryDateRange");
                var table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow first = CreateUnresolvedPlayHistoryRow(hash: string.Empty, playedAt: 1000);
                PlayHistoryRow second = CreateUnresolvedPlayHistoryRow(hash: string.Empty, playedAt: 1001);
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { first, second };
                table.SelectRowsByPredicate(_ => true);

                RaiseMenuOpened(playHistoryMenu, table, first, 0);
                TestUiDispatcherHost.Drain();

                MenuItem? dateRange = playHistoryMenu.Items
                    .OfType<MenuItem>()
                    .SingleOrDefault(item => item.Name == "playHistoryContextMenuItemAddDateRangeToSearch");
                Assert.IsNotNull(dateRange);
                Assert.AreEqual(Resources.Play_history_add_date_range_to_search, dateRange!.Header as string);
                Assert.IsFalse(string.IsNullOrWhiteSpace(dateRange!.Header as string));
                Assert.AreEqual(Visibility.Visible, dateRange.Visibility);

                RaiseMenuClosed(playHistoryMenu);
                table.SelectRowsByPredicate(row => ReferenceEquals(row, first));
                RaiseMenuOpened(playHistoryMenu, table, first, 0);
                TestUiDispatcherHost.Drain();

                Assert.AreEqual(Visibility.Collapsed, dateRange.Visibility);
                RaiseMenuClosed(playHistoryMenu);
            });
    }

    [TestMethod]
    public void PlayHistoryContextMenu_DoesNotInferPrimaryActionsFromSecondaryRows()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(),
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlayHistoryDateRangePrimary");
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
                TestUiDispatcherHost.Drain();

                MenuItem dateRange = FindMenuItem(playHistoryMenu, "playHistoryContextMenuItemAddDateRangeToSearch");
                Assert.AreEqual(Visibility.Visible, dateRange.Visibility);
                Assert.AreEqual(
                    Visibility.Collapsed,
                    FindMenuItem(playHistoryMenu, "playHistoryContextMenuItemOpenExplorer").Visibility);
                Assert.AreSame(primary, ((CustomTableContextMenuContext)playHistoryMenu.Tag).Row);
                RaiseMenuClosed(playHistoryMenu);
            });
    }

    [TestMethod]
    public void PlayHistoryContextMenu_DateRangeUsesOpenSnapshotAndUpdatesKeywordOnce()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        settings.KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["title:previous"]);
        string historyBeforeAction = settings.KeywordSearchHistory;

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            settings,
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlayHistoryDateRangeSnapshot");
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

                    // The XAML Opened handler has already captured the original min/max.
                    table.SelectRowsByPredicate(row => ReferenceEquals(row, intermediate));
                    dateRange.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, dateRange));
                };
                RoutedEventHandler menuClosedHandler = (_, _) => closed = true;
                playHistoryMenu.Opened += menuOpenedHandler;
                playHistoryMenu.Closed += menuClosedHandler;
                try
                {
                    // Drive the public menu events directly; the test never opens a real Popup.
                    RaiseMenuOpened(playHistoryMenu, table, first, 2);
                    RaiseMenuClosed(playHistoryMenu);
                    TestUiDispatcherHost.Drain();
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
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlayHistoryBmsonMd5Action");
                var table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow resolved = CreateResolvedBmsonPlayHistoryRow();
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { resolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, resolved));

                RaiseMenuOpened(playHistoryMenu, table, resolved, 0);
                TestUiDispatcherHost.Drain();

                MenuItem webAction = FindMenuItem(playHistoryMenu, "configuredWebAction_bmson_md5");
                Assert.AreEqual(actionName, webAction.Header);
                Assert.AreEqual(Visibility.Visible, webAction.Visibility);
                RaiseMenuClosed(playHistoryMenu);
            });
    }

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
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlayHistoryUnresolvedBeatorajaWebAction");
                var table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow unresolved = CreateUnresolvedBeatorajaPlayHistoryRow();
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { unresolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, unresolved));

                RaiseMenuOpened(playHistoryMenu, table, unresolved, 0);
                TestUiDispatcherHost.Drain();

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

    [TestMethod]
    public void PlayHistoryResolvedRowPlacesAssociatedOpenBeforeProgramActions()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();
        settings.RightClickActionsJson = RightClickActionSettingsSerializer.Serialize(
            new RightClickActionSettings(
                [],
                [new RightClickProgramActionDefinition(
                    "player",
                    "Player",
                    @"C:\Tools\player.exe",
                    "{filePath}",
                    enabled: true)]));

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            settings,
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlayHistoryConfiguredActions");
                var table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow resolved = CreateResolvedPlayHistoryRow(
                    typeof(MainWindow).Assembly.Location);
                var playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { resolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, resolved));

                RaiseMenuOpened(playHistoryMenu, table, resolved, 0);
                TestUiDispatcherHost.Drain();

                MenuItem associated = FindMenuItem(
                    playHistoryMenu,
                    "playHistoryContextMenuItemOpenAssociated");
                MenuItem program = FindMenuItem(
                    playHistoryMenu,
                    "playHistoryContextMenuItemOpenProgramActions");
                Control localSeparator = playHistoryMenu.Items.OfType<Control>().Single(item =>
                    item.Name == "playHistoryContextMenuSeparatorLocal");
                Control hashSeparator = playHistoryMenu.Items.OfType<Control>().Single(item =>
                    item.Name == "playHistoryContextMenuSeparatorHash");
                Assert.AreEqual(Visibility.Visible, associated.Visibility);
                Assert.IsTrue(associated.IsEnabled);
                Assert.AreEqual(Resources.Open_association, associated.Header);
                Assert.AreEqual(
                    Visibility.Collapsed,
                    localSeparator.Visibility,
                    "Web操作がない場合はWeb/ローカル間の区切り線を表示しないこと。");
                Assert.AreEqual(Visibility.Visible, hashSeparator.Visibility);
                Assert.AreEqual(
                    playHistoryMenu.Items.IndexOf(associated) + 1,
                    playHistoryMenu.Items.IndexOf(program));
                Assert.AreEqual(Resources.RightClick_open_with_program, program.Header);
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

    private static HwndSource CreateVisualHost(MainWindow window, string name)
    {
        var source = new HwndSource(new HwndSourceParameters(name)
        {
            Width = 1000,
            Height = 700,
            PositionX = 0,
            PositionY = 0
        });
        source.RootVisual = (Visual)window.Content;
        window.Measure(new Size(1000d, 700d));
        window.Arrange(new Rect(0d, 0d, 1000d, 700d));
        window.UpdateLayout();
        return source;
    }

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
        var file = BMSFile.FromSongTableRawValues(
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
        ]);
        file.ApplySnapshotDigest(hash, sha256);
        var resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
            [LibraryChartRef.FromBmsFile(file)]);
        var projectionIndex = PlayHistoryProjectionIndex.Create(
            resolveIndex,
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
        var song = new LR2SongDBExtended.bmson_song
        {
            path = @"C:\BMS\play-history-resolved.bmson",
            title = "Resolved BMSON Play History",
            artist = "Artist",
            md5 = md5,
            sha256 = sha256
        };
        var resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
            [LibraryChartRef.FromBmsonSong(song)]);
        var projectionIndex = PlayHistoryProjectionIndex.Create(resolveIndex);
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
