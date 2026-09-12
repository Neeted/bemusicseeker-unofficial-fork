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
                new Settings(),
                (viewModel, window) =>
                {
                    using var visualHost = CreateVisualHost(window, "MainWindowPlayHistoryDropdown");
                    Border toolbar = (Border)window.FindName("mainTableToolbar");
                    ComboBox combo = FindVisualChildren<ComboBox>(toolbar).Single();
                    Border dropdownContainer = (Border)combo.Parent;

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
                new Settings(),
                (viewModel, window) =>
                {
                    using var visualHost = CreateVisualHost(window, "MainWindowPlayHistorySummary");
                    Border summaryBar = (Border)window.FindName("playHistorySummaryBar");

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
            new Settings(),
            (viewModel, window) =>
            {
                using var visualHost = CreateVisualHost(window, "MainWindowPlayHistoryContext");
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                Assert.AreEqual(viewModel.MainChartList.RowDragKind, table.RowDragKind);

                PlayHistoryRow resolved = CreateResolvedPlayHistoryRow();
                ContextMenu playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                // The XAML resource is deliberately non-shared; make this observation use the same instance as the route.
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                Assert.AreSame(playHistoryMenu, window.FindResource("playHistoryContextMenu"));
                int contextRequests = 0;
                table.RowContextMenuRequested += (_, _) => contextRequests++;
                table.ItemsSource = new List<object> { resolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, resolved));
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(resolved, out _));
                RaiseKey(table, Key.Apps);
                TestUiDispatcherHost.Drain();

                Assert.AreEqual(1, contextRequests);
                Assert.IsInstanceOfType(playHistoryMenu.Tag, typeof(CustomTableContextMenuContext));
                CustomTableContextMenuContext resolvedContext =
                    (CustomTableContextMenuContext)playHistoryMenu.Tag;
                Assert.AreSame(resolved, resolvedContext.Row);
                Assert.AreSame(table, playHistoryMenu.PlacementTarget);
                playHistoryMenu.IsOpen = false;
                TestUiDispatcherHost.Drain();

                object unresolvedTagSentinel = new();
                playHistoryMenu.Tag = unresolvedTagSentinel;
                playHistoryMenu.PlacementTarget = null;
                int contextRequestsBeforeUnresolved = contextRequests;
                PlayHistoryRow unresolved = CreateUnresolvedPlayHistoryRow();
                table.ItemsSource = new List<object> { unresolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, unresolved));
                RaiseKey(table, Key.Apps);
                TestUiDispatcherHost.Drain();

                Assert.AreEqual(contextRequestsBeforeUnresolved + 1, contextRequests);
                Assert.IsInstanceOfType(playHistoryMenu.Tag, typeof(CustomTableContextMenuContext));
                Assert.AreSame(unresolved, ((CustomTableContextMenuContext)playHistoryMenu.Tag).Row);
                Assert.AreSame(table, playHistoryMenu.PlacementTarget);
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(unresolved, out _));
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(resolved, out _));
            });
    }

    [TestMethod]
    public void PlayHistoryContextMenu_ShowsDateRangeOnlyForMultipleSelection()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                using var visualHost = CreateVisualHost(window, "MainWindowPlayHistoryDateRange");
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow first = CreateUnresolvedPlayHistoryRow(hash: string.Empty, playedAt: 1000);
                PlayHistoryRow second = CreateUnresolvedPlayHistoryRow(hash: string.Empty, playedAt: 1001);
                ContextMenu playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { first, second };
                table.SelectRowsByPredicate(_ => true);

                RaiseKey(table, Key.Apps);
                TestUiDispatcherHost.Drain();

                MenuItem? dateRange = playHistoryMenu.Items
                    .OfType<MenuItem>()
                    .SingleOrDefault(item => item.Name == "playHistoryContextMenuItemAddDateRangeToSearch");
                Assert.IsNotNull(dateRange);
                Assert.AreEqual(Resources.Play_history_add_date_range_to_search, dateRange!.Header as string);
                Assert.IsFalse(string.IsNullOrWhiteSpace(dateRange!.Header as string));
                Assert.AreEqual(Visibility.Visible, dateRange.Visibility);

                playHistoryMenu.IsOpen = false;
                TestUiDispatcherHost.Drain();
                table.SelectRowsByPredicate(row => ReferenceEquals(row, first));
                RaiseKey(table, Key.Apps);
                TestUiDispatcherHost.Drain();

                Assert.AreEqual(Visibility.Collapsed, dateRange.Visibility);
            });
    }

    [TestMethod]
    public void PlayHistoryContextMenu_DoesNotInferPrimaryActionsFromSecondaryRows()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                using var visualHost = CreateVisualHost(window, "MainWindowPlayHistoryDateRangePrimary");
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow primary = CreateUnresolvedPlayHistoryRow(hash: string.Empty, playedAt: 1000);
                PlayHistoryRow secondary = CreateResolvedPlayHistoryRow(playedAt: 1001);
                ContextMenu playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { primary, secondary };
                table.SelectRowsByPredicate(
                    _ => true,
                    row => ReferenceEquals(row, primary));

                Assert.IsFalse(viewModel.PlayHistory.TryCreateContextMenuState(primary, out _));
                Assert.IsTrue(viewModel.PlayHistory.TryCreateContextMenuState(secondary, out _));
                RaiseKey(table, Key.Apps);
                TestUiDispatcherHost.Drain();

                MenuItem dateRange = FindMenuItem(playHistoryMenu, "playHistoryContextMenuItemAddDateRangeToSearch");
                Assert.AreEqual(Visibility.Visible, dateRange.Visibility);
                Assert.AreEqual(
                    Visibility.Collapsed,
                    FindMenuItem(playHistoryMenu, "playHistoryContextMenuItemOpenExplorer").Visibility);
                Assert.AreSame(primary, ((CustomTableContextMenuContext)playHistoryMenu.Tag).Row);
            });
    }

    [TestMethod]
    public void PlayHistoryContextMenu_DateRangeUsesOpenSnapshotAndUpdatesKeywordOnce()
    {
        Settings settings = new()
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["title:previous"])
        };
        string historyBeforeAction = settings.KeywordSearchHistory;

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            settings,
            (viewModel, window) =>
            {
                using var visualHost = CreateVisualHost(window, "MainWindowPlayHistoryDateRangeSnapshot");
                ContextMenu playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                var menuClosed = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                RoutedEventHandler menuClosedHandler = (_, _) => menuClosed.TrySetResult(true);
                playHistoryMenu.Closed += menuClosedHandler;
                try
                {
                    CustomTableView table = (CustomTableView)window.FindName("customTableView");
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

                    Assert.IsTrue(table.Focus());
                    Assert.AreSame(table, Keyboard.FocusedElement);

                    const string prefix = "title:alpha\t";
                    viewModel.ChartFilters.KeywordFilter = prefix;
                    int keywordChangedCount = 0;
                    viewModel.ChartFilters.KeywordFilterChanged += (_, _) => keywordChangedCount++;

                    RaiseKey(table, Key.Apps);
                    TestUiDispatcherHost.Drain();

                    MenuItem dateRange = FindMenuItem(playHistoryMenu, "playHistoryContextMenuItemAddDateRangeToSearch");
                    Assert.IsTrue(playHistoryMenu.IsOpen);
                    Assert.AreEqual(Visibility.Visible, dateRange.Visibility);
                    Assert.IsTrue(dateRange.IsEnabled);
                    string start = first.PlayedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
                    string end = later.PlayedAt.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
                    string expectedClause = $"date:\"{start}..{end}\"";

                    table.SelectRowsByPredicate(row => ReferenceEquals(row, intermediate));
                    KeywordSearchEditor keywordSearchEditor = (KeywordSearchEditor)window.FindName("KeywordSearchEditor");
                    Assert.IsNotNull(keywordSearchEditor);
                    Assert.AreNotSame(keywordSearchEditor, Keyboard.FocusedElement);
                    bool keywordSearchEditorReceivedFocus = false;
                    KeyboardFocusChangedEventHandler keywordFocusHandler =
                        (_, _) => keywordSearchEditorReceivedFocus = true;
                    keywordSearchEditor.GotKeyboardFocus += keywordFocusHandler;
                    try
                    {
                        dateRange.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, dateRange));
                        playHistoryMenu.IsOpen = false;
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(
                            menuClosed.Task,
                            "play-history date-range context menu closed after action");
                        TestUiDispatcherHost.Drain();
                    }
                    finally
                    {
                        keywordSearchEditor.GotKeyboardFocus -= keywordFocusHandler;
                    }

                    Assert.AreEqual(prefix + expectedClause, viewModel.ChartFilters.KeywordFilter);
                    Assert.AreEqual(1, keywordChangedCount);
                    Assert.IsFalse(keywordSearchEditorReceivedFocus);
                    Assert.AreNotSame(keywordSearchEditor, Keyboard.FocusedElement);
                    Assert.AreEqual(historyBeforeAction, settings.KeywordSearchHistory);
                }
                finally
                {
                    if (playHistoryMenu.IsOpen)
                    {
                        playHistoryMenu.IsOpen = false;
                        TestUiDispatcherHost.Drain();
                    }
                    playHistoryMenu.Closed -= menuClosedHandler;
                }
            });
    }

    [TestMethod]
    public void PlayHistoryResolvedRowPlacesAssociatedOpenBeforeProgramActions()
    {
        Settings settings = new();
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
                using var visualHost = CreateVisualHost(window, "MainWindowPlayHistoryConfiguredActions");
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                PlayHistoryRow resolved = CreateResolvedPlayHistoryRow(
                    typeof(MainWindow).Assembly.Location);
                ContextMenu playHistoryMenu = (ContextMenu)window.FindResource("playHistoryContextMenu");
                window.Resources["playHistoryContextMenu"] = playHistoryMenu;
                table.ItemsSource = new List<object> { resolved };
                table.SelectRowsByPredicate(row => ReferenceEquals(row, resolved));

                RaiseKey(table, Key.Apps);
                TestUiDispatcherHost.Drain();

                MenuItem associated = FindMenuItem(
                    playHistoryMenu,
                    "playHistoryContextMenuItemOpenAssociated");
                MenuItem program = FindMenuItem(
                    playHistoryMenu,
                    "playHistoryContextMenuItemOpenProgramActions");
                Assert.AreEqual(Visibility.Visible, associated.Visibility);
                Assert.IsTrue(associated.IsEnabled);
                Assert.AreEqual(Resources.Open_association, associated.Header);
                Assert.AreEqual(
                    playHistoryMenu.Items.IndexOf(associated) + 1,
                    playHistoryMenu.Items.IndexOf(program));
                Assert.AreEqual(Resources.RightClick_open_with_program, program.Header);
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

    private static void RaiseKey(CustomTableView table, Key key)
    {
        table.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(table),
            0,
            key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent
        });
    }

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
        BMSFile file = BMSFile.FromSongTableRawValues(
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
        PlaylistLibraryResolveIndexSnapshot resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
            [LibraryChartRef.FromBmsFile(file)]);
        PlayHistoryProjectionIndex projectionIndex = PlayHistoryProjectionIndex.Create(
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
