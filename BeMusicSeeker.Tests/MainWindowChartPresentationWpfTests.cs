using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowChartPresentationWpfTests
{
    private static void WithSimpleTextBoxStyles(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current
                ?? throw new InvalidOperationException("The shared WPF application host is unavailable.");
            if (application.TryFindResource("SimpleTextBox") is Style)
            {
                action();
                return;
            }

            var styles = new ResourceDictionary
            {
                Source = new Uri(
                    "/BeMusicSeeker;component/Simple Styles.xaml",
                    UriKind.RelativeOrAbsolute)
            };
            application.Resources.MergedDictionaries.Insert(0, styles);
            try
            {
                action();
            }
            finally
            {
                application.Resources.MergedDictionaries.Remove(styles);
            }
        });
    }

    [TestMethod]
    public void MainColumnResetClickUsesActualCompiledMenuRoute()
    {
        int calls = 0;
        var terminal = new MainWindowColumnResetTerminal(_ => calls++);

        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (_, window) =>
                {
                    ContextMenu menu = (ContextMenu)window.FindResource("tableColumnHeaderContextMenu");
                    MenuItem reset = menu.Items.OfType<MenuItem>().Last();

                    reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, reset));

                    Assert.AreEqual(1, calls);
                },
                allowStartupUiInteraction: true,
                columnResetTerminal: terminal);
        });
    }

    [TestMethod]
    public void MainWindowPendingCellEditUsesDefaultTerminalAndMainChartOwnerRoute()
    {
        var beginning = new List<MainChartListCellEditContext>();
        var started = new List<MainChartListCellEditContext>();
        var completed = new List<MainChartListCellEditEndedEventArgs>();
        const string expectedText = @"C:\wave6e-cell-edit\destination";

        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                viewModel.MainChartList.CellEditBeginningRequested += (_, request) => beginning.Add(request.Context);
                viewModel.MainChartList.CellEditStarted += (_, context) => started.Add(context);
                viewModel.MainChartList.CellEditEndedRequested += (_, request) => completed.Add(request);
                window.Width = 1000d;
                window.Height = 700d;
                using var visualHost = new HwndSource(new HwndSourceParameters("MainWindowCellEditRouteTest")
                {
                    Width = 1000,
                    Height = 700,
                    PositionX = 0,
                    PositionY = 0
                });
                visualHost.RootVisual = (System.Windows.Media.Visual)window.Content;
                window.Measure(new Size(window.Width, window.Height));
                window.Arrange(new Rect(0d, 0d, window.Width, window.Height));
                window.UpdateLayout();

                var file = new BMSFile
                {
                    path = @"C:\wave6e-cell-edit\pending\chart.bms",
                    hash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                    title = "Pending cell edit"
                };
                PackageChartEntry entry = PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(file));
                LibraryChartRow row = LibraryChartRow.FromPackageChartEntry(entry);
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                table.Width = 900d;
                table.Height = 220d;
                table.HeaderHeight = 0d;
                table.RowHeight = 70d;
                table.ItemsSource = new List<object> { row };
                var columnSettings = new CustomTableColumnSettings();
                columnSettings.InstallDst.Visibility = Visibility.Visible;
                table.Columns = [.. CustomTableColumnFactory.CreateMainColumns(columnSettings)
                    .Where(candidate => string.Equals(candidate.EditPropertyName, "instl_dst", StringComparison.Ordinal))];
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                table.Measure(new Size(table.Width, table.Height));
                table.Arrange(new Rect(0d, 0d, table.Width, table.Height));
                table.UpdateLayout();

                CustomTableColumn column = table.Columns
                    .Single(candidate => string.Equals(candidate.EditPropertyName, "instl_dst", StringComparison.Ordinal));
                Assert.AreEqual(0, table.SelectedIndex);
                Assert.IsTrue(table.IsCurrentCell(0, column));
                RaiseKey(table, Key.F2);
                Assert.AreEqual(1, beginning.Count);
                Assert.AreEqual(1, started.Count);
                Assert.IsNotNull(GetInstalledEditor(table));
                TextBox editor = GetInstalledEditor(table);
                editor!.Text = expectedText;
                RaiseKey(table, Key.Return);

                Assert.AreEqual(1, beginning.Count);
                Assert.AreEqual(1, started.Count);
                Assert.AreEqual(1, completed.Count);
                Assert.AreSame(row, beginning[0].Row);
                Assert.AreSame(row, started[0].Row);
                Assert.AreSame(row, completed[0].Context.Row);
                Assert.AreEqual("instl_dst", beginning[0].PropertyName);
                Assert.AreEqual("instl_dst", started[0].PropertyName);
                Assert.AreEqual("instl_dst", completed[0].Context.PropertyName);
                Assert.AreEqual(expectedText, completed[0].Text);
                Assert.IsTrue(completed[0].Commit);
                Assert.AreSame(file, row.Chart.GetBmsStorageOwner());
                });
        });
    }

    [TestMethod]
    public void CustomTableCellEditStartedSeesInstalledEditorAfterKeyboardActivation()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var layout = new CustomTableColumnSettings.ColumnLayout
            {
                Width = 180,
                Visibility = Visibility.Visible
            };
            var column = new CustomTableColumn(
                "Folder",
                "Folder",
                layout,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "initial",
                editPropertyName: "Folder");
            var table = new CustomTableView
            {
                Width = 220,
                Height = 70,
                HeaderHeight = 0d,
                RowHeight = 60d,
                Columns = [column],
                ItemsSource = new List<object> { new object() }
            };
            using HwndSource source = CreateHwndSource(table, 220, 70);
            MaterializeTable(table, 220d, 70d);
            int beginningCount = 0;
            int startedCount = 0;
            int previewKeyCount = 0;
            TextBox editorSeenAtStarted = null;
            table.AddHandler(
                UIElement.PreviewKeyDownEvent,
                new KeyEventHandler((_, _) => previewKeyCount++),
                handledEventsToo: true);
            table.CellEditBeginning += (_, _) => beginningCount++;
            table.CellEditStarted += (_, _) =>
            {
                startedCount++;
                editorSeenAtStarted = table.Children
                    .OfType<Canvas>()
                    .SelectMany(canvas => canvas.Children.OfType<TextBox>())
                    .SingleOrDefault();
            };

            RaiseKey(table, Key.Down);
            Assert.AreEqual(0, table.SelectedIndex);
            Assert.IsTrue(table.IsCurrentCell(0, column));
            Assert.AreEqual(1, previewKeyCount);
            RaiseKey(table, Key.F2);
            Assert.AreEqual(2, previewKeyCount);

            Assert.AreEqual(1, beginningCount);
            Assert.AreEqual(1, startedCount);
            Assert.IsNotNull(editorSeenAtStarted);
            Assert.IsTrue(editorSeenAtStarted!.Width > 2d);
            Assert.IsTrue(editorSeenAtStarted.Height > 2d);
            Assert.AreEqual("initial", editorSeenAtStarted.Text);

            table.RaiseEvent(new KeyEventArgs(
                Keyboard.PrimaryDevice,
                Keyboard.PrimaryDevice.ActiveSource,
                0,
                Key.Escape)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent
            });
        });
    }

    [TestMethod]
    public void CustomTableCellEditFailuresDoNotRaiseStarted()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var layout = new CustomTableColumnSettings.ColumnLayout
            {
                Width = 180,
                Visibility = Visibility.Visible
            };
            var column = new CustomTableColumn(
                "Folder",
                "Folder",
                layout,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "initial",
                editPropertyName: "Folder");

            {
                var cancelled = CreateEditableTable(column, 220d, 70d);
                using (HwndSource cancelledSource = CreateHwndSource(cancelled, 220, 70))
                {
                    MaterializeTable(cancelled, 220d, 70d);
                    int cancelledStarted = 0;
                    int cancelledBeginning = 0;
                    cancelled.CellEditBeginning += (_, args) => args.Cancel = true;
                    cancelled.CellEditBeginning += (_, _) => cancelledBeginning++;
                    cancelled.CellEditStarted += (_, _) => cancelledStarted++;
                    RaiseKey(cancelled, Key.Down);
                    Assert.AreEqual(0, cancelled.SelectedIndex);
                    Assert.IsTrue(cancelled.IsCurrentCell(0, column));
                    RaiseKey(cancelled, Key.F2);
                    Assert.AreEqual(1, cancelledBeginning);
                    Assert.AreEqual(0, cancelledStarted);
                    Assert.IsNull(GetInstalledEditor(cancelled));
                }
            }

            var tooSmallLayout = new CustomTableColumnSettings.ColumnLayout
            {
                Width = 2,
                Visibility = Visibility.Visible
            };
            var tooSmallColumn = new CustomTableColumn(
                "Folder",
                "Folder",
                tooSmallLayout,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "initial",
                minWidth: 1,
                editPropertyName: "Folder");
            {
                var tooSmall = CreateEditableTable(tooSmallColumn, 220d, 70d);
                using (HwndSource tooSmallSource = CreateHwndSource(tooSmall, 220, 70))
                {
                    MaterializeTable(tooSmall, 220d, 70d);
                    int tooSmallBeginning = 0;
                    int tooSmallStarted = 0;
                    tooSmall.CellEditBeginning += (_, _) => tooSmallBeginning++;
                    tooSmall.CellEditStarted += (_, _) => tooSmallStarted++;
                    RaiseKey(tooSmall, Key.Down);
                    Assert.AreEqual(0, tooSmall.SelectedIndex);
                    Assert.IsTrue(tooSmall.IsCurrentCell(0, tooSmallColumn));
                    RaiseKey(tooSmall, Key.F2);
                    Assert.AreEqual(1, tooSmallBeginning);
                    Assert.AreEqual(0, tooSmallStarted);
                    Assert.IsNull(GetInstalledEditor(tooSmall));
                }
            }
        });
    }

    [TestMethod]
    public void CustomTableScrollBarsUseCompiledThemeAndCornerContract()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current
                ?? throw new InvalidOperationException("The shared WPF application host is unavailable.");
            const string trackBackgroundResourceKey = "ScrollBar.TrackBackgroundBrush";
            string previousTheme = Settings.Default.AppearanceTheme;
            bool hadApplicationTrackBackground = application.Resources.Contains(trackBackgroundResourceKey);
            object? previousApplicationTrackBackground = hadApplicationTrackBackground
                ? application.Resources[trackBackgroundResourceKey]
                : null;
            ResourceDictionary? activeThemeResources = null;
            object? previousThemeTrackBackground = null;
            Window? resourceHostWindow = null;
            const double tableWidth = 240d;
            const double tableHeight = 100d;
            try
            {
                application.Resources.Remove(trackBackgroundResourceKey);
                activeThemeResources = application.Resources.MergedDictionaries
                    .LastOrDefault(dictionary => dictionary.Contains(trackBackgroundResourceKey))
                    ?? throw new InvalidOperationException(
                        "The active theme does not define the scroll-bar track background resource.");
                previousThemeTrackBackground = activeThemeResources[trackBackgroundResourceKey];
                var table = new CustomTableView
                {
                    Width = tableWidth,
                    Height = tableHeight,
                    HeaderHeight = 0d,
                    RowHeight = 20d,
                    Columns = [CreateScrollBarContractColumn(120d)],
                    ItemsSource = new List<object> { new object() }
                };
                var resourceHost = new Grid();
                resourceHost.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/Simple Styles.xaml",
                        UriKind.RelativeOrAbsolute)
                });
                resourceHost.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalDialogStyles.xaml",
                        UriKind.RelativeOrAbsolute)
                });
                resourceHost.Children.Add(table);
                resourceHostWindow = new Window
                {
                    Width = tableWidth,
                    Height = tableHeight,
                    Content = resourceHost
                };
                ScrollBar verticalScrollBar = table.Children
                    .OfType<ScrollBar>()
                    .Single(scrollBar => scrollBar.Orientation == Orientation.Vertical);
                ScrollBar horizontalScrollBar = table.Children
                    .OfType<ScrollBar>()
                    .Single(scrollBar => scrollBar.Orientation == Orientation.Horizontal);
                Border corner = table.Children.OfType<Border>().Single();

                Assert.IsNull(table.FocusVisualStyle);
                Assert.IsTrue(table.UseLayoutRounding);
                Assert.IsTrue(table.SnapsToDevicePixels);
                object trackBackground = table.FindResource(trackBackgroundResourceKey);
                Assert.AreSame(trackBackground, table.Background);
                Assert.AreSame(trackBackground, corner.Background);
                Assert.AreEqual(1, Grid.GetColumn(verticalScrollBar));
                Assert.AreEqual(0, Grid.GetRow(verticalScrollBar));
                Assert.AreEqual(0, Grid.GetColumn(horizontalScrollBar));
                Assert.AreEqual(1, Grid.GetRow(horizontalScrollBar));
                Assert.AreEqual(1, Grid.GetColumn(corner));
                Assert.AreEqual(1, Grid.GetRow(corner));

                resourceHostWindow.Measure(new Size(tableWidth, tableHeight));
                resourceHostWindow.Arrange(new Rect(0d, 0d, tableWidth, tableHeight));
                resourceHostWindow.UpdateLayout();
                MaterializeScrollBarContractTable(table, tableWidth, tableHeight);
                Assert.IsNotNull(verticalScrollBar.Template);
                Assert.IsNotNull(horizontalScrollBar.Template);
                AssertScrollBarPresentation(verticalScrollBar, Orientation.Vertical);
                AssertScrollBarPresentation(horizontalScrollBar, Orientation.Horizontal);
                Assert.AreEqual(Visibility.Collapsed, verticalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Collapsed, horizontalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Collapsed, corner.Visibility);

                application.Resources.Remove(trackBackgroundResourceKey);
                string alternateTheme = string.Equals(
                    AppThemeService.NormalizeTheme(previousTheme),
                    AppThemeService.Dark,
                    StringComparison.Ordinal)
                    ? AppThemeService.Light
                    : AppThemeService.Dark;
                Settings.Default.AppearanceTheme = alternateTheme;
                AppThemeService.ApplyTheme(alternateTheme);
                FlushResourceUpdates(table);
                object switchedThemeTrackBackground = table.FindResource(trackBackgroundResourceKey);
                Assert.AreNotSame(trackBackground, switchedThemeTrackBackground);
                Assert.AreSame(switchedThemeTrackBackground, table.Background);
                Assert.AreSame(switchedThemeTrackBackground, corner.Background);

                table.ItemsSource = Enumerable.Range(0, 20).Select(_ => new object()).ToList();
                MaterializeScrollBarContractTable(table, tableWidth, tableHeight);
                Assert.AreEqual(Visibility.Visible, verticalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Collapsed, horizontalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Collapsed, corner.Visibility);

                table.ItemsSource = new List<object> { new object() };
                table.Columns = [CreateScrollBarContractColumn(600d)];
                MaterializeScrollBarContractTable(table, tableWidth, tableHeight);
                Assert.AreEqual(Visibility.Collapsed, verticalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Visible, horizontalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Collapsed, corner.Visibility);

                table.ItemsSource = Enumerable.Range(0, 20).Select(_ => new object()).ToList();
                MaterializeScrollBarContractTable(table, tableWidth, tableHeight);
                Assert.AreEqual(Visibility.Visible, verticalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Visible, horizontalScrollBar.Visibility);
                Assert.AreEqual(Visibility.Visible, corner.Visibility);
            }
            finally
            {
                if (resourceHostWindow is not null)
                {
                    resourceHostWindow.Content = null;
                    resourceHostWindow.Close();
                }
                application.Resources.Remove(trackBackgroundResourceKey);
                Settings.Default.AppearanceTheme = previousTheme;
                AppThemeService.ApplyTheme(previousTheme);
                if (activeThemeResources is not null && previousThemeTrackBackground is not null)
                {
                    activeThemeResources[trackBackgroundResourceKey] = previousThemeTrackBackground;
                }
                if (hadApplicationTrackBackground)
                {
                    application.Resources[trackBackgroundResourceKey] = previousApplicationTrackBackground;
                }
            }
        });
    }

    [TestMethod]
    public void ConstructorBindsChartFilterAndMainChartOwner()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    KeywordSearchEditor searchEditor = GetNamedElement<KeywordSearchEditor>(window, "KeywordSearchEditor");
                    TextBox keyword = searchEditor.InputTextBoxControl;
                    CustomTableView table = GetNamedElement<CustomTableView>(window, "customTableView");

                    AssertBindingPath(searchEditor, KeywordSearchEditor.TextProperty, "ChartFilters.KeywordFilter");
                    AssertBindingPath(keyword, TextBox.TextProperty, "Text");
                    Assert.AreSame(viewModel.MainChartList, table.DataContext);
                    AssertBindingPath(table, CustomTableView.ItemsSourceProperty, "Rows");
                    AssertBindingPath(table, CustomTableView.SelectedIndexProperty, "SelectedIndex");

                    keyword.Text = "wave6e-filter";
                    BindingOperations.GetBindingExpression(searchEditor, KeywordSearchEditor.TextProperty)?.UpdateSource();
                    Assert.AreEqual("wave6e-filter", viewModel.ChartFilters.KeywordFilter);
                });
        });
    }

    [TestMethod]
    public void SearchToolbars_UseFixedCompositeGeometryAndWarningSlots()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                MaterializeMainWindow(window);

                Grid rootGrid = GetNamedElement<Grid>(window, "grid");
                Assert.AreEqual(27d, rootGrid.RowDefinitions[1].Height.Value, 0.01d);

                SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                AssertSearchChromeGeometry(normal);
                Point normalHelpBeforeWarning = GetRelativeOrigin(normal.HelpSlot, normal.Outer);
                Point normalClearBeforeWarning = GetRelativeOrigin(normal.ClearSlot, normal.Outer);
                Assert.AreEqual(Visibility.Collapsed, normal.WarningContent.Visibility);
                normal.WarningContent.SetCurrentValue(
                    UIElement.VisibilityProperty,
                    Visibility.Visible);
                MaterializeMainWindow(window);
                Assert.AreEqual(Visibility.Visible, normal.WarningContent.Visibility);
                AssertSlotChildCentered(normal.WarningSlot, normal.WarningContent, normal.Outer);
                AssertSamePoint(normalHelpBeforeWarning, GetRelativeOrigin(normal.HelpSlot, normal.Outer));
                AssertSamePoint(normalClearBeforeWarning, GetRelativeOrigin(normal.ClearSlot, normal.Outer));
                normal.WarningContent.SetCurrentValue(
                    UIElement.VisibilityProperty,
                    Visibility.Collapsed);
                MaterializeMainWindow(window);
                Assert.AreEqual(Visibility.Collapsed, normal.WarningContent.Visibility);
                AssertSamePoint(normalHelpBeforeWarning, GetRelativeOrigin(normal.HelpSlot, normal.Outer));
                AssertSamePoint(normalClearBeforeWarning, GetRelativeOrigin(normal.ClearSlot, normal.Outer));

                viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                TestUiDispatcherHost.Drain();
                MaterializeMainWindow(window);

                SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                AssertSearchChromeGeometry(summary);
                Assert.AreEqual(Visibility.Collapsed, summary.WarningContent.Visibility);
                Point summaryHelpWithoutWarning = GetRelativeOrigin(summary.HelpSlot, summary.Outer);
                Point summaryClearWithoutWarning = GetRelativeOrigin(summary.ClearSlot, summary.Outer);
                summary.WarningContent.SetCurrentValue(
                    UIElement.VisibilityProperty,
                    Visibility.Visible);
                MaterializeMainWindow(window);
                Assert.AreEqual(Visibility.Visible, summary.WarningContent.Visibility);
                AssertSlotChildCentered(summary.WarningSlot, summary.WarningContent, summary.Outer);
                AssertSamePoint(summaryHelpWithoutWarning, GetRelativeOrigin(summary.HelpSlot, summary.Outer));
                AssertSamePoint(summaryClearWithoutWarning, GetRelativeOrigin(summary.ClearSlot, summary.Outer));
                summary.WarningContent.SetCurrentValue(
                    UIElement.VisibilityProperty,
                    Visibility.Collapsed);
                MaterializeMainWindow(window);
                Assert.AreEqual(Visibility.Collapsed, summary.WarningContent.Visibility);
                AssertSamePoint(summaryHelpWithoutWarning, GetRelativeOrigin(summary.HelpSlot, summary.Outer));
                AssertSamePoint(summaryClearWithoutWarning, GetRelativeOrigin(summary.ClearSlot, summary.Outer));
                });
        });
    }

    [TestMethod]
    public void SearchVariants_PreserveBindingsPopupTargetsAndClearRoutes()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    AssertFilterAffordanceMaterialized(normal);
                    AssertFilterAffordanceMaterialized(summary);
                    MaterializeMainWindow(window);
                    AssertFilterAffordanceRendered(normal);
                    Assert.AreSame(viewModel, normal.Editor.DataContext);
                    Assert.AreSame(viewModel, summary.Editor.DataContext);
                    AssertBindingPath(normal.Editor, KeywordSearchEditor.TextProperty, "ChartFilters.KeywordFilter");
                    AssertBindingPath(summary.Editor, KeywordSearchEditor.TextProperty, "PlaylistWorkspace.PlaylistSummaryKeywordFilter");
                    AssertBindingPath(normal.TextBox, TextBox.TextProperty, "Text");
                    AssertBindingPath(summary.TextBox, TextBox.TextProperty, "Text");
                    AssertBindingPath(normal.HelpButton, ToggleButton.IsCheckedProperty, "ChartFilters.IsKeywordSearchHelpOpen");
                    AssertBindingPath(summary.HelpButton, ToggleButton.IsCheckedProperty, "PlaylistWorkspace.IsPlaylistSummaryKeywordSearchHelpOpen");
                    Assert.IsNull(BindingOperations.GetBindingExpression(normal.SuggestionPopup, Popup.IsOpenProperty));
                    Assert.IsNull(BindingOperations.GetBindingExpression(summary.SuggestionPopup, Popup.IsOpenProperty));
                    Assert.AreSame(normal.HelpButton, normal.HelpPopup.PlacementTarget);
                    Assert.AreSame(summary.HelpButton, summary.HelpPopup.PlacementTarget);
                    Assert.IsNull(normal.HelpButton.FocusVisualStyle);
                    Assert.IsNull(summary.HelpButton.FocusVisualStyle);
                    AssertBindingPathAndOwner(
                        normal.WarningContent,
                        UIElement.VisibilityProperty,
                        "ChartFilters.HasKeywordSearchWarning",
                        viewModel);
                    AssertBindingPathAndOwner(
                        normal.WarningContent,
                        FrameworkElement.ToolTipProperty,
                        "ChartFilters.KeywordSearchWarningText",
                        viewModel);
                    AssertBindingPathAndOwner(
                        summary.WarningContent,
                        UIElement.VisibilityProperty,
                        "PlaylistWorkspace.HasPlaylistSummaryKeywordSearchWarning",
                        viewModel);
                    AssertBindingPathAndOwner(
                        summary.WarningContent,
                        FrameworkElement.ToolTipProperty,
                        "PlaylistWorkspace.PlaylistSummaryKeywordSearchWarningText",
                        viewModel);
                    AssertFilterMenuBindings(
                        normal.FilterButton,
                        "ChartFilters.ModeFilter",
                        viewModel);
                    AssertFilterMenuBindings(
                        summary.FilterButton,
                        "PlaylistWorkspace.PlaylistSummaryOwnedFilter",
                        viewModel);
                    ChartModeFilter modeBeforeInteraction = viewModel.ChartFilters.ModeFilter;
                    PlaylistOwnedFilter ownedFilterBeforeInteraction = viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter;
                    MenuItem normalFilterItem = GetFilterMenuItem(normal.FilterButton.DropDownContextMenu!, true);
                    SetFilterMenuItemChecked(normalFilterItem, false);
                    Assert.AreNotEqual(modeBeforeInteraction, viewModel.ChartFilters.ModeFilter);
                    Assert.AreEqual(ownedFilterBeforeInteraction, viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter);
                    ChartModeFilter modeAfterNormalInteraction = viewModel.ChartFilters.ModeFilter;

                    viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                    TestUiDispatcherHost.Drain();
                    MaterializeMainWindow(window);
                    AssertFilterAffordanceRendered(summary);
                    MenuItem summaryFilterItem = GetFilterMenuItem(summary.FilterButton.DropDownContextMenu!, false);
                    SetFilterMenuItemChecked(summaryFilterItem, true);
                    Assert.AreEqual(modeAfterNormalInteraction, viewModel.ChartFilters.ModeFilter);
                    Assert.AreNotEqual(ownedFilterBeforeInteraction, viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter);
                });
        });
    }

    [TestMethod]
    public void SearchEditorRouteOwnsPopupStateInsteadOfViewModel()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (_, window) =>
                {
                    KeywordSearchEditor editor = GetNamedElement<KeywordSearchEditor>(window, "KeywordSearchEditor");

                    Assert.IsNull(
                        BindingOperations.GetBindingExpression(
                            editor.AssistancePopupControl,
                            Popup.IsOpenProperty));
                });
        });
    }

    [TestMethod]
    public void SearchEditor_OutsidePreviewMouseDownClearsFocusAndClosesAssistanceForBothScopes()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    KeywordSearchAssistanceOwner normalOwner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    KeywordSearchAssistanceOwner summaryOwner = viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner;

                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(normal.Editor);
                        normal.TextBox.Text = "tit";
                        normal.TextBox.CaretIndex = normal.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        Assert.IsTrue(normal.SuggestionPopup.IsOpen);
                        Assert.IsTrue(normal.TextBox.IsKeyboardFocusWithin);

                        RaisePreviewMouseDown(normal.FilterButton);
                        TestUiDispatcherHost.Drain();
                        Assert.IsFalse(normal.TextBox.IsKeyboardFocusWithin);
                        Assert.IsFalse(normal.SuggestionPopup.IsOpen);
                        Assert.IsFalse(normalOwner.Presentation.IsOpen);
                        CollectionAssert.Contains(normalOwner.SavedQueryOwner.History.ToArray(), "tit");

                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.Drain();
                        FocusSearchEditor(summary.Editor);
                        summary.TextBox.Text = "name:summary";
                        summary.TextBox.CaretIndex = summary.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        Assert.IsFalse(summary.SuggestionPopup.IsOpen);
                        Assert.IsTrue(summary.TextBox.IsKeyboardFocusWithin);

                        RaisePreviewMouseDown(summary.FilterButton);
                        TestUiDispatcherHost.Drain();
                        Assert.IsFalse(summary.TextBox.IsKeyboardFocusWithin);
                        Assert.IsFalse(summary.SuggestionPopup.IsOpen);
                        Assert.IsFalse(summaryOwner.Presentation.IsOpen);
                        CollectionAssert.Contains(summaryOwner.SavedQueryOwner.History.ToArray(), "name:summary");
                        CollectionAssert.DoesNotContain(normalOwner.SavedQueryOwner.History.ToArray(), "name:summary");
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_ClearMouseRouteRefocusesAndRefreshesEmptyAssistanceImmediately()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    KeywordSearchAssistanceOwner normalOwner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;

                    Assert.IsTrue(normalOwner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(normalOwner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(normal.Editor);
                        normal.TextBox.Text = "title:normal";
                        normal.TextBox.CaretIndex = normal.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();

                        RaisePreviewMouseDown(normal.ClearIcon);
                        RaiseMouseLeftButtonDown(normal.ClearIcon);
                        CompleteNonActivatingLogicalFocusTransition(normal.TextBox);
                        Assert.AreEqual(string.Empty, normal.TextBox.Text);
                        Assert.IsTrue(normal.TextBox.IsKeyboardFocusWithin);
                        Assert.IsTrue(normal.SuggestionPopup.IsOpen);
                        CollectionAssert.AreEqual(
                            new[]
                            {
                                KeywordSearchPresentationSectionKind.Favorites,
                                KeywordSearchPresentationSectionKind.History,
                                KeywordSearchPresentationSectionKind.Fields
                            },
                            normal.Editor.Presentation.Sections.Select(section => section.Kind).ToArray());

                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.Drain();
                        FocusSearchEditor(summary.Editor);
                        summary.TextBox.Text = "name:summary";
                        summary.TextBox.CaretIndex = summary.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();

                        RaisePreviewMouseDown(summary.ClearIcon);
                        RaiseMouseLeftButtonDown(summary.ClearIcon);
                        CompleteNonActivatingLogicalFocusTransition(summary.TextBox);
                        Assert.AreEqual(string.Empty, summary.TextBox.Text);
                        Assert.IsTrue(summary.TextBox.IsKeyboardFocusWithin);
                        Assert.IsTrue(summary.SuggestionPopup.IsOpen);
                        Assert.IsTrue(
                            summary.Editor.Presentation.Sections.Any(
                                section => section.Kind == KeywordSearchPresentationSectionKind.Fields));
                        Assert.IsFalse(
                            summary.Editor.Presentation.VisibleItems.Any(
                                item => string.Equals(item.Query, "title:favorite", StringComparison.Ordinal)
                                    || string.Equals(item.Query, "artist:history", StringComparison.Ordinal)));
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_RefreshesImmediatelyAndRestoresEmptySections()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:alpha").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:beta").Succeeded);

                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.SetCurrentValue(TextBox.TextProperty, string.Empty);
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();

                        Assert.IsTrue(
                            chrome.SuggestionPopup.IsOpen,
                            $"popup={chrome.SuggestionPopup.IsOpen}, ownerOpen={owner.Presentation.IsOpen}, editorOpen={chrome.Editor.Presentation.IsOpen}, sections={owner.Presentation.Sections.Count}, focus={chrome.TextBox.IsKeyboardFocusWithin}");
                        CollectionAssert.AreEqual(
                            new[]
                            {
                                KeywordSearchPresentationSectionKind.Favorites,
                                KeywordSearchPresentationSectionKind.History,
                                KeywordSearchPresentationSectionKind.Fields
                            },
                            chrome.Editor.Presentation.Sections.Select(section => section.Kind).ToArray());
                        Assert.AreEqual("title:alpha", chrome.Editor.Presentation.Sections[0].Items[0].Query);
                        Assert.AreEqual("artist:beta", chrome.Editor.Presentation.Sections[1].Items[0].Query);

                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        Assert.AreEqual(1, chrome.Editor.Presentation.Sections.Count);
                        Assert.AreEqual(
                            KeywordSearchPresentationSectionKind.Fields,
                            chrome.Editor.Presentation.Sections[0].Kind);
                        Assert.IsTrue(
                            chrome.Editor.Presentation.Sections[0].Items.All(
                                item => item.Kind == KeywordSearchPresentationItemKind.Field));

                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        Assert.AreEqual(3, chrome.Editor.Presentation.Sections.Count);

                        SetKeyboardFocusForNonActivatingPresentation(chrome.HelpButton);
                        TestUiDispatcherHost.Drain();
                        Assert.IsFalse(chrome.SuggestionPopup.IsOpen);
                        Assert.IsFalse(owner.Presentation.IsOpen);
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_AppliesActiveFieldAndValueFragments()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (_, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);

                        chrome.TextBox.Text = "-tit suffix";
                        chrome.TextBox.CaretIndex = 4;
                        TestUiDispatcherHost.Drain();
                        KeywordSearchPresentationItem field = chrome.Editor.Presentation.VisibleItems
                            .Single(item => item.Kind == KeywordSearchPresentationItemKind.Field
                                && string.Equals(item.DisplayText, "-title:", StringComparison.Ordinal));
                        RaiseApplyButton(chrome.Editor, field.DisplayText);
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual("-title: suffix", chrome.TextBox.Text);
                        Assert.AreEqual(7, chrome.TextBox.CaretIndex);

                        chrome.TextBox.Text = "clear:N  tail";
                        chrome.TextBox.CaretIndex = 7;
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual(KeywordSearchPresentationSectionKind.Values, chrome.Editor.Presentation.Sections.Single().Kind);
                        KeywordSearchPresentationItem value = chrome.Editor.Presentation.VisibleItems
                            .Single(item => item.Kind == KeywordSearchPresentationItemKind.Value
                                && string.Equals(item.DisplayText, "NP", StringComparison.Ordinal));
                        RaiseApplyButton(chrome.Editor, value.DisplayText);
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual("clear:NP tail", chrome.TextBox.Text);
                        Assert.AreEqual(9, chrome.TextBox.CaretIndex);

                        chrome.TextBox.Text = "clear:N";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        value = chrome.Editor.Presentation.VisibleItems
                            .Single(item => item.Kind == KeywordSearchPresentationItemKind.Value
                                && string.Equals(item.DisplayText, "NP", StringComparison.Ordinal));
                        RaiseApplyButton(chrome.Editor, value.DisplayText);
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual("clear:NP ", chrome.TextBox.Text);
                        Assert.AreEqual(9, chrome.TextBox.CaretIndex);
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        Assert.AreEqual(KeywordSearchPresentationSectionKind.Fields, chrome.Editor.Presentation.Sections.Single().Kind);
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_NavigationAndImeKeepInputLifecycleOwnedByEditor()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:alpha").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:beta").Succeeded);

                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();

                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        TestUiDispatcherHost.Drain();
                        Assert.IsTrue(chrome.TextBox.IsKeyboardFocusWithin);
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        TestUiDispatcherHost.Drain();
                        Assert.IsTrue(chrome.TextBox.IsKeyboardFocusWithin);

                        string beforeIme = chrome.TextBox.Text;
                        RaiseSearchKey(chrome.TextBox, Key.ImeProcessed);
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual(beforeIme, chrome.TextBox.Text);
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);

                        RaiseSearchKey(chrome.TextBox, Key.Enter);
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual("artist:beta", chrome.TextBox.Text);
                        Assert.AreEqual(chrome.TextBox.Text.Length, chrome.TextBox.CaretIndex);

                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        RaiseSearchKey(chrome.TextBox, Key.Escape);
                        TestUiDispatcherHost.Drain();
                        Assert.IsFalse(chrome.SuggestionPopup.IsOpen);
                        Assert.IsFalse(owner.Presentation.IsOpen);

                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        TestUiDispatcherHost.Drain();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        Assert.IsTrue(chrome.TextBox.IsKeyboardFocusWithin);
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_SavedRowActionsKeepQueryAndFocusUnchanged()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();

                        Border favoriteRow = FindSavedRow(chrome.Editor, "title:favorite");
                        Button removeFavorite = FindActionButton(
                            favoriteRow,
                            Resources.Keyword_search_remove_favorite_tooltip);
                        Assert.IsFalse(removeFavorite.IsDefault);
                        Assert.IsNotNull(removeFavorite.ToolTip);
                        Assert.AreEqual(
                            Resources.Keyword_search_remove_favorite_tooltip,
                            AutomationProperties.GetName(removeFavorite));
                        removeFavorite.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, removeFavorite));
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual(string.Empty, chrome.TextBox.Text);
                        Assert.IsTrue(chrome.TextBox.IsKeyboardFocusWithin);
                        CollectionAssert.DoesNotContain(owner.SavedQueryOwner.Favorites.ToArray(), "title:favorite");

                        Border historyRow = FindSavedRow(chrome.Editor, "artist:history");
                        Button addFavorite = FindActionButton(
                            historyRow,
                            Resources.Keyword_search_add_favorite_tooltip);
                        addFavorite.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, addFavorite));
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual(string.Empty, chrome.TextBox.Text);
                        Assert.IsTrue(chrome.TextBox.IsKeyboardFocusWithin);
                        CollectionAssert.Contains(owner.SavedQueryOwner.Favorites.ToArray(), "artist:history");
                        CollectionAssert.Contains(owner.SavedQueryOwner.History.ToArray(), "artist:history");
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_UsesIndependentSectionViewportsAndSummaryFieldCatalog()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    for (int index = 0; index < 6; index++)
                    {
                        Assert.IsTrue(owner.TryAddFavorite($"title:f{index}").Succeeded);
                        Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory($"artist:h{index}").Succeeded);
                    }

                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(normal.Editor);
                        normal.TextBox.Text = string.Empty;
                        normal.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();

                        ScrollViewer[] sectionViewports = FindVisualDescendants<ScrollViewer>(
                                normal.Editor.AssistancePopupControl.Child!)
                            .ToArray();
                        Assert.AreEqual(3, sectionViewports.Length);
                        Assert.AreEqual(130d, sectionViewports[0].MaxHeight, 0.01d);
                        Assert.AreEqual(130d, sectionViewports[1].MaxHeight, 0.01d);
                        Assert.AreEqual(260d, sectionViewports[2].MaxHeight, 0.01d);
                        Assert.IsTrue(sectionViewports.All(viewport => viewport.VerticalScrollBarVisibility == ScrollBarVisibility.Auto));
                        Button[] actionButtons = FindVisualDescendants<Button>(
                                normal.Editor.AssistancePopupControl.Child!)
                            .Where(button => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                            .Where(button => button.Content is string)
                            .Where(button => button.Visibility == Visibility.Visible && button.IsVisible)
                            .ToArray();
                        Assert.IsTrue(actionButtons.Length >= 6);
                        Assert.IsTrue(actionButtons.All(button => button.ActualWidth > 0d && button.ActualHeight > 0d));
                        Assert.IsTrue(actionButtons.All(button => button.IsHitTestVisible));

                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.Drain();
                        SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                        FocusSearchEditor(summary.Editor);
                        summary.TextBox.Text = string.Empty;
                        summary.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual(
                            Resources.Keyword_search_summary_editor_name,
                            AutomationProperties.GetName(summary.TextBox));
                        Assert.AreEqual(1, summary.Editor.Presentation.Sections.Count);
                        Assert.AreEqual(
                            KeywordSearchPresentationSectionKind.Fields,
                            summary.Editor.Presentation.Sections.Single().Kind);
                        Assert.IsTrue(
                            summary.Editor.Presentation.VisibleItems.Any(
                                item => string.Equals(item.DisplayText, "output:", StringComparison.Ordinal)));
                        Assert.IsFalse(
                            summary.Editor.Presentation.VisibleItems.Any(
                                item => item.Kind == KeywordSearchPresentationItemKind.Value));

                        summary.TextBox.Text = "output:x";
                        summary.TextBox.CaretIndex = summary.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        Assert.IsFalse(summary.Editor.Presentation.IsOpen);
                        Assert.IsFalse(summary.Editor.Presentation.Sections.Any(
                            section => section.Kind == KeywordSearchPresentationSectionKind.Values));
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_RoutedTabAppliesCandidateAndShiftTabReachesSavedActions()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();

                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        RaiseSearchKey(chrome.TextBox, Key.Tab);
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual("title:", chrome.TextBox.Text);
                        Assert.AreEqual(chrome.TextBox.Text.Length, chrome.TextBox.CaretIndex);

                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        MaterializePopup(chrome.Editor);
                        Border favoriteRow = FindSavedRow(chrome.Editor, "title:favorite");
                        Button favoriteAction = FindActionButton(
                            favoriteRow,
                            Resources.Keyword_search_remove_favorite_tooltip);
                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        RaiseShiftTab(chrome.Editor);
                        TestUiDispatcherHost.Drain();
                        CompleteNonActivatingLogicalFocusTransition(favoriteAction);
                        Assert.AreSame(favoriteAction, Keyboard.FocusedElement);
                        Assert.AreEqual(
                            Resources.Keyword_search_remove_favorite_tooltip,
                            AutomationProperties.GetName(favoriteAction));

                        RaiseRoutedKey(favoriteAction, Key.Space, Keyboard.KeyDownEvent);
                        RaiseRoutedKey(favoriteAction, Key.Space, Keyboard.KeyUpEvent);
                        TestUiDispatcherHost.Drain();
                        CompleteNonActivatingLogicalFocusTransition(chrome.TextBox);
                        Assert.IsTrue(chrome.TextBox.IsKeyboardFocusWithin);
                        CollectionAssert.DoesNotContain(owner.SavedQueryOwner.Favorites.ToArray(), "title:favorite");

                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        MaterializePopup(chrome.Editor);
                        Border historyRow = FindSavedRow(chrome.Editor, "artist:history");
                        Button[] historyActions = FindVisualDescendants<Button>(historyRow)
                            .Where(button => button.Focusable
                                && button.IsTabStop
                                && button.Visibility == Visibility.Visible
                                && button.IsVisible
                                && button.IsEnabled)
                            .ToArray();
                        Assert.AreEqual(2, historyActions.Length);
                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        RaiseShiftTab(chrome.Editor);
                        TestUiDispatcherHost.Drain();
                        CompleteNonActivatingLogicalFocusTransition(historyActions[0]);
                        Assert.AreSame(historyActions[0], Keyboard.FocusedElement);
                        Assert.AreEqual(
                            Resources.Keyword_search_add_favorite_tooltip,
                            AutomationProperties.GetName(historyActions[0]));
                        Assert.AreEqual(
                            Resources.Keyword_search_delete_history_tooltip,
                            AutomationProperties.GetName(historyActions[1]));

                        RaiseRoutedKey(historyActions[0], Key.Tab, UIElement.PreviewKeyDownEvent);
                        TestUiDispatcherHost.Drain();
                        CompleteNonActivatingLogicalFocusTransition(historyActions[1]);
                        Assert.AreSame(historyActions[1], Keyboard.FocusedElement);
                        RaiseRoutedKey(historyActions[1], Key.Tab, UIElement.PreviewKeyDownEvent);
                        TestUiDispatcherHost.Drain();
                        CompleteNonActivatingLogicalFocusTransition(chrome.TextBox);
                        Assert.IsTrue(chrome.TextBox.IsKeyboardFocusWithin);
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_LargeFavoritesVirtualizeOffViewportRows()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    const int favoriteCount = 32;
                    for (int index = 0; index < favoriteCount; index++)
                    {
                        Assert.IsTrue(owner.TryAddFavorite($"title:favorite-{index}").Succeeded);
                    }

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        MaterializeMainWindow(window);

                        DependencyObject popupChild = chrome.SuggestionPopup.Child
                            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
                        int realizedFavorites = FindVisualDescendants<Border>(popupChild)
                            .Count(border => border.Tag is KeywordSearchPresentationItem item
                                && item.Kind == KeywordSearchPresentationItemKind.Favorite);
                        Assert.IsTrue(realizedFavorites > 0);
                        Assert.IsTrue(
                            realizedFavorites < favoriteCount,
                            $"The 5-row Favorites viewport materialized {realizedFavorites} of {favoriteCount} rows.");
                        Assert.AreEqual(favoriteCount, owner.SavedQueryOwner.Favorites.Count);
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_SavedRowTooltipsAndAccessibilityFollowRecycledDataContext()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    const int favoriteCount = 32;
                    for (int index = 0; index < favoriteCount; index++)
                    {
                        Assert.IsTrue(owner.TryAddFavorite($"title:favorite-{index}").Succeeded);
                    }
                    const int historyCount = 6;
                    for (int index = 0; index < historyCount; index++)
                    {
                        Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory($"artist:history-{index}").Succeeded);
                    }

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        MaterializeMainWindow(window);

                        foreach (KeywordSearchPresentationSectionKind sectionKind in new[]
                        {
                            KeywordSearchPresentationSectionKind.Favorites,
                            KeywordSearchPresentationSectionKind.History
                        })
                        {
                            KeywordSearchPresentationSection section = chrome.Editor.Presentation.Sections
                                .Single(candidate => candidate.Kind == sectionKind);
                            KeywordSearchPresentationItem firstItem = section.Items[0];
                            KeywordSearchPresentationItem lastItem = section.Items[^1];
                            ListBox rows = GetSectionRows(chrome.Editor, sectionKind);
                            rows.ScrollIntoView(firstItem);
                            MaterializeElement(rows, rows.ActualWidth, rows.ActualHeight);
                            rows.ScrollIntoView(lastItem);
                            MaterializeElement(rows, rows.ActualWidth, rows.ActualHeight);
                            Border lastRow = FindSavedRow(chrome.Editor, lastItem.Query);
                            Button lastApply = FindApplyButton(lastRow);
                            Assert.IsNotNull(
                                rows.ItemContainerGenerator.ContainerFromItem(lastItem),
                                $"ScrollIntoView must realize the initially off-viewport {sectionKind} row.");
                            Assert.AreEqual(lastItem.Query, lastRow.ToolTip?.ToString());
                            Assert.AreEqual(lastItem.Query, lastApply.ToolTip?.ToString());
                            Assert.AreEqual(lastItem.DisplayText, AutomationProperties.GetName(lastApply));

                            Button[] lastActions = FindVisibleActionButtons(lastRow);
                            Assert.IsTrue(lastActions.Length > 0);
                            foreach (Button action in lastActions)
                            {
                                string actionName = AutomationProperties.GetName(action);
                                Assert.IsFalse(string.IsNullOrWhiteSpace(actionName));
                                Assert.AreEqual(actionName, action.ToolTip?.ToString());
                                Assert.AreNotEqual(lastItem.Query, actionName);
                            }

                            // The virtualizing panel normally reuses this surface while it
                            // scrolls. Reassigning the same realized surface is the approved
                            // deterministic negative control when a runtime chooses to recreate
                            // the template instead; Loaded handlers must not own row state.
                            lastRow.SetCurrentValue(FrameworkElement.DataContextProperty, firstItem);
                            TestUiDispatcherHost.Drain();
                            Assert.AreSame(lastApply, FindApplyButton(lastRow));
                            Assert.AreEqual(firstItem.Query, lastRow.ToolTip?.ToString());
                            Assert.AreEqual(firstItem.Query, lastApply.ToolTip?.ToString());
                            Assert.AreEqual(firstItem.DisplayText, AutomationProperties.GetName(lastApply));
                            foreach (Button action in FindVisibleActionButtons(lastRow))
                            {
                                string actionName = AutomationProperties.GetName(action);
                                Assert.IsFalse(string.IsNullOrWhiteSpace(actionName));
                                Assert.AreEqual(actionName, action.ToolTip?.ToString());
                                Assert.AreNotEqual(firstItem.Query, actionName);
                            }
                        }
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_QueryTooltipsAreLimitedToSavedRows()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        MaterializeMainWindow(window);

                        Border favoriteRow = FindSavedRow(chrome.Editor, "title:favorite");
                        Button favoriteApply = FindApplyButton(favoriteRow);
                        Assert.AreEqual("title:favorite", favoriteApply.ToolTip?.ToString());
                        Assert.AreEqual(
                            "title:favorite",
                            AutomationProperties.GetName(favoriteApply));

                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        Border fieldRow = FindRowForItemKind(
                            chrome.Editor,
                            KeywordSearchPresentationItemKind.Field);
                        Button fieldApply = FindApplyButton(fieldRow);
                        Assert.IsNull(fieldRow.ToolTip);
                        Assert.IsNull(fieldApply.ToolTip);

                        chrome.TextBox.Text = "clear:N";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        Border valueRow = FindRowForItemKind(
                            chrome.Editor,
                            KeywordSearchPresentationItemKind.Value);
                        Button valueApply = FindApplyButton(valueRow);
                        Assert.IsNull(valueRow.ToolTip);
                        Assert.IsNull(valueApply.ToolTip);

                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.Drain();
                        FocusSearchEditor(summary.Editor);
                        summary.TextBox.Text = string.Empty;
                        summary.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        Border summaryFieldRow = FindRowForItemKind(
                            summary.Editor,
                            KeywordSearchPresentationItemKind.Field);
                        Button summaryFieldApply = FindApplyButton(summaryFieldRow);
                        Assert.IsNull(summaryFieldRow.ToolTip);
                        Assert.IsNull(summaryFieldApply.ToolTip);
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_SavedQueryPopupWidthIsBoundToPreOpenSurface()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    const string longQuery =
                        "title:This-is-a-deliberately-long-saved-query-that-must-remain-intact-" +
                        "0123456789abcdefghijklmnopqrstuvwxyz";
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite(longQuery).Succeeded);

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    WithPresentedMainWindow(window, () =>
                    {
                        MaterializeMainWindow(window);
                        Grid surface = GetEditorSurface(chrome.Editor);
                        double baselineWidth = surface.ActualWidth;
                        Assert.IsTrue(baselineWidth > 0d);
                        surface.SetCurrentValue(
                            FrameworkElement.WidthProperty,
                            baselineWidth * 0.75d);
                        TestUiDispatcherHost.Drain();
                        MaterializeMainWindow(window);
                        double preOpenWidth = surface.ActualWidth;
                        Assert.IsTrue(preOpenWidth > 0d);

                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        MaterializeMainWindow(window);

                        Border popupChild = chrome.SuggestionPopup.Child as Border
                            ?? throw new AssertFailedException("The search assistance popup has no outer Border.");
                        Assert.AreEqual(preOpenWidth, popupChild.ActualWidth, 0.51d);
                        Border longRow = FindSavedRow(chrome.Editor, longQuery);
                        Button longApply = FindApplyButton(longRow);
                        TextBlock longLabel = longApply.Content as TextBlock
                            ?? throw new AssertFailedException("The saved-query apply button has no text label.");
                        Assert.AreEqual(longQuery, longLabel.Text);
                        Assert.AreEqual(TextWrapping.NoWrap, longLabel.TextWrapping);
                        Assert.AreEqual(TextTrimming.CharacterEllipsis, longLabel.TextTrimming);
                        Assert.IsTrue(longLabel.ActualWidth <= popupChild.ActualWidth + 0.51d);

                        chrome.Editor.AssistancePopupControl.IsOpen = false;
                        TestUiDispatcherHost.Drain();
                        Assert.IsFalse(chrome.SuggestionPopup.IsOpen);
                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.Drain();
                        MaterializeMainWindow(window);
                        Grid summarySurface = GetEditorSurface(summary.Editor);
                        double summaryBaselineWidth = summarySurface.ActualWidth;
                        Assert.IsTrue(summaryBaselineWidth > 0d);
                        summarySurface.SetCurrentValue(
                            FrameworkElement.WidthProperty,
                            summaryBaselineWidth * 0.875d);
                        TestUiDispatcherHost.Drain();
                        MaterializeMainWindow(window);
                        double summaryPreOpenWidth = summarySurface.ActualWidth;
                        Assert.IsTrue(summaryPreOpenWidth > 0d);
                        const double layoutRoundingTolerance = 0.51d;
                        Assert.IsTrue(
                            Math.Abs(preOpenWidth - summaryPreOpenWidth) > layoutRoundingTolerance,
                            $"Normal and summary editor fixture widths must differ beyond layout rounding: normal={preOpenWidth}, summary={summaryPreOpenWidth}.");
                        FocusSearchEditor(summary.Editor);
                        summary.TextBox.Text = string.Empty;
                        summary.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        MaterializeMainWindow(window);
                        Border summaryPopupChild = summary.SuggestionPopup.Child as Border
                            ?? throw new AssertFailedException("The summary assistance popup has no outer Border.");
                        Assert.AreEqual(summaryPreOpenWidth, summaryPopupChild.ActualWidth, 0.51d);
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_ShortSectionsSizeToContentForSavedValuesAndFields()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");

                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        double[] oneSavedRowHeights = GetSectionViewportHeights(chrome.Editor);
                        Assert.IsTrue(oneSavedRowHeights.Length >= 2);
                        Assert.IsTrue(oneSavedRowHeights[0] > 0d && oneSavedRowHeights[1] > 0d);

                        Assert.IsTrue(owner.TryAddFavorite("title:favorite-two").Succeeded);
                        Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history-two").Succeeded);
                        TestUiDispatcherHost.Drain();
                        double[] twoSavedRowHeights = GetSectionViewportHeights(chrome.Editor);
                        Assert.IsTrue(twoSavedRowHeights[0] > oneSavedRowHeights[0]);
                        Assert.IsTrue(twoSavedRowHeights[1] > oneSavedRowHeights[1]);

                        chrome.TextBox.Text = "clear:NP";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        double oneValueHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(oneValueHeight > 0d);

                        chrome.TextBox.Text = "clear:N";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        double twoValueHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(twoValueHeight > oneValueHeight);

                        chrome.TextBox.Text = "cl";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        double oneFieldHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(oneFieldHeight > 0d);

                        chrome.TextBox.Text = "c";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        double twoFieldHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(twoFieldHeight > oneFieldHeight);
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_ActiveImeCompositionRoutedEventsDoNotApplyOrCommitHistory()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    int historyCountBefore = owner.SavedQueryOwner.History.Count;

                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        string textBeforeComposition = chrome.TextBox.Text;
                        int caretBeforeComposition = chrome.TextBox.CaretIndex;

                        TextComposition composition = new(
                            InputManager.Current,
                            chrome.TextBox,
                            "title");
                        RaiseCompositionEvent(
                            chrome.TextBox,
                            composition,
                            TextCompositionManager.PreviewTextInputStartEvent);
                        RaiseCompositionEvent(
                            chrome.TextBox,
                            composition,
                            TextCompositionManager.PreviewTextInputUpdateEvent);
                        RaiseSearchKey(chrome.TextBox, Key.Enter);
                        TestUiDispatcherHost.Drain();

                        Assert.AreEqual(textBeforeComposition, chrome.TextBox.Text);
                        Assert.AreEqual(caretBeforeComposition, chrome.TextBox.CaretIndex);
                        Assert.AreEqual(historyCountBefore, owner.SavedQueryOwner.History.Count);

                        RaiseCompositionEvent(
                            chrome.TextBox,
                            new TextComposition(InputManager.Current, chrome.TextBox, "title"),
                            TextCompositionManager.TextInputEvent);
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual(textBeforeComposition + "title", chrome.TextBox.Text);
                        Assert.AreEqual(historyCountBefore, owner.SavedQueryOwner.History.Count);

                        // The committed composition was observed by the editor. Reset the
                        // input to a fresh field prefix and prove ordinary terminal keys
                        // are no longer blocked by the composition guard.
                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        RaiseSearchKey(chrome.TextBox, Key.Enter);
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual("title:", chrome.TextBox.Text);
                        Assert.AreEqual(chrome.TextBox.Text.Length, chrome.TextBox.CaretIndex);

                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.Drain();
                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        RaiseSearchKey(chrome.TextBox, Key.Tab);
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual("title:", chrome.TextBox.Text);
                        Assert.AreEqual(chrome.TextBox.Text.Length, chrome.TextBox.CaretIndex);
                    });
                });
        });
    }

    [TestMethod]
    public void SearchEditor_ShiftTabFromSelectedFieldUsesFirstSavedActionWithoutApplyingField()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    WithPresentedMainWindow(window, () =>
                    {
                        FocusSearchEditor(chrome.Editor);
                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.Drain();
                        MaterializePopup(chrome.Editor);

                        // Favorites, History, then the first Field in the flattened
                        // keyboard order. Shift+Tab must fall back to the first saved
                        // row instead of applying or targeting the Field.
                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        RaiseSearchKey(chrome.TextBox, Key.Down);
                        TestUiDispatcherHost.Drain();
                        Assert.AreEqual(string.Empty, chrome.TextBox.Text);

                        Border favoriteRow = FindSavedRow(chrome.Editor, "title:favorite");
                        Button favoriteAction = FindActionButton(
                            favoriteRow,
                            Resources.Keyword_search_remove_favorite_tooltip);
                        RaiseShiftTab(chrome.Editor);
                        TestUiDispatcherHost.Drain();
                        CompleteNonActivatingLogicalFocusTransition(favoriteAction);

                        Assert.AreSame(favoriteAction, Keyboard.FocusedElement);
                        Assert.AreEqual(string.Empty, chrome.TextBox.Text);
                        CollectionAssert.Contains(owner.SavedQueryOwner.Favorites.ToArray(), "title:favorite");
                        CollectionAssert.Contains(owner.SavedQueryOwner.History.ToArray(), "artist:history");
                    });
                });
        });
    }

    [TestMethod]
    public void SearchTextBoxes_UseOuterFocusCueStyleAndKeepInnerChromeAtZero()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (viewModel, window) =>
                {
                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    AssertSearchTextBoxChrome(normal.TextBox);
                    AssertSearchTextBoxChrome(summary.TextBox);

                    var presentationScope = new TestWindowPresentationScope(
                        Application.Current
                            ?? throw new InvalidOperationException("The shared WPF application host is unavailable."),
                        TestWindowPresentationScope.GetCurrentNativeThreadId());
                    object dataContext = window.DataContext;
                    RoutedEventHandler suppressStartupActivation = (_, _) =>
                    {
                        window.Dispatcher.BeginInvoke(
                            DispatcherPriority.Render,
                            new Action(() =>
                            {
                                window.Left = SystemParameters.VirtualScreenLeft
                                    + (SystemParameters.VirtualScreenWidth * 4d)
                                    + 4096d;
                                window.Top = SystemParameters.VirtualScreenTop
                                    + (SystemParameters.VirtualScreenHeight * 4d)
                                    + 4096d;
                            }));
                        window.Dispatcher.BeginInvoke(
                            DispatcherPriority.Loaded,
                            new Action(() => window.DataContext = null));
                    };
                    try
                    {
                        // ContentRendered normally starts the whole application. Consuming that one-shot
                        // handler without its view model keeps this real-window presentation scoped to chrome.
                        // Keep the view model through SourceInitialized because child controls need it there.
                        window.Loaded += suppressStartupActivation;
                        presentationScope.ShowAndWaitForContentRendered(window);
                        window.DataContext = dataContext;
                        TestUiDispatcherHost.Drain();

                        AssertFocusCueForPresentedSearch(normal);
                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.Drain();
                        AssertFocusCueForPresentedSearch(summary);
                    }
                    finally
                    {
                        window.Loaded -= suppressStartupActivation;
                        presentationScope.Cleanup();
                        window.DataContext = dataContext;
                    }
                });
        });
    }

    [TestMethod]
    public void SimpleTextBox_TemplateChromeFollowsEffectiveBorderThickness()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                new Settings(),
                (_, _) =>
                {
                var host = new Grid();
                host.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/Simple Styles.xaml",
                        UriKind.RelativeOrAbsolute)
                });
                host.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/Themes/Light.xaml",
                        UriKind.RelativeOrAbsolute)
                });
                Style simpleTextBoxStyle = (Style)host.FindResource("SimpleTextBox");
                host.Resources.Add(typeof(TextBox), simpleTextBoxStyle);
                var implicitTextBox = new TextBox();
                var explicitTextBox = new TextBox { BorderThickness = new Thickness(0d) };
                var editableTextBlock = new EditableTextBlock
                {
                    Width = 220d,
                    Height = 30d,
                    Text = "editable",
                    IsInEditMode = true
                };
                host.Children.Add(implicitTextBox);
                host.Children.Add(explicitTextBox);
                host.Children.Add(editableTextBlock);
                MaterializeElement(host, 500d, 120d);

                AssertTextBoxChrome(implicitTextBox, new Thickness(1d));
                AssertTextBoxChrome(explicitTextBox, new Thickness(0d));
                TextBox editor = FindVisualDescendants<TextBox>(editableTextBlock).Single();
                AssertTextBoxChrome(editor, new Thickness(0d));
                });
        });
    }

    private static T GetNamedElement<T>(MainWindow window, string name)
        where T : class
    {
        object? element = window.FindName(name);
        Assert.IsInstanceOfType(element, typeof(T), name);
        return (T)element!;
    }

    private static void AssertBindingPath(
        DependencyObject element,
        DependencyProperty property,
        string expectedPath)
    {
        BindingBase? binding = BindingOperations.GetBindingBase(element, property);
        Assert.IsInstanceOfType(binding, typeof(Binding), expectedPath);
        Assert.AreEqual(expectedPath, ((Binding)binding!).Path?.Path);
    }

    private static void AssertBindingPathAndOwner(
        DependencyObject element,
        DependencyProperty property,
        string expectedPath,
        object expectedOwner)
    {
        BindingBase? bindingBase = BindingOperations.GetBindingBase(element, property);
        Assert.IsInstanceOfType(bindingBase, typeof(Binding), expectedPath);
        Binding binding = (Binding)bindingBase!;
        Assert.AreEqual(expectedPath, binding.Path?.Path);
        object? owner = binding.Source ?? (element as FrameworkElement)?.DataContext;
        Assert.AreSame(expectedOwner, owner);
    }

    private static void AssertFilterMenuBindings(
        DropDownMenuButton filterButton,
        string expectedPath,
        object expectedOwner)
    {
        ContextMenu menu = filterButton.DropDownContextMenu
            ?? throw new AssertFailedException("The search filter menu must be present.");
        MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.IsTrue(items.Length > 0);
        foreach (MenuItem item in items)
        {
            AssertBindingPathAndOwner(
                item,
                MenuItem.IsCheckedProperty,
                expectedPath,
                expectedOwner);
        }
    }

    private static void AssertFilterAffordanceMaterialized(SearchChrome chrome)
    {
        Assert.IsNotNull(chrome.FilterButton.DropDownContextMenu);
        Assert.IsTrue(
            chrome.FilterButton.ApplyTemplate(),
            "The search filter affordance must materialize its control template.");
        Assert.IsNotNull(chrome.FilterButton.Template);
    }

    private static void AssertFilterAffordanceRendered(SearchChrome chrome)
    {
        MaterializeElement(chrome.FilterButton, 20d, 20d);
        FrameworkElement[] descendants = FindVisualDescendants<FrameworkElement>(chrome.FilterButton).ToArray();
        FrameworkElement? renderedDescendant = descendants
            .FirstOrDefault(element => element.ActualWidth > 0d && element.ActualHeight > 0d);
        Assert.IsNotNull(
            renderedDescendant,
            "The search filter affordance must expose a rendered descendant with non-zero bounds.");
        Rect descendantBounds = VisualTreeHelper.GetDescendantBounds(chrome.FilterButton);
        Assert.IsFalse(descendantBounds.IsEmpty);
        Assert.IsTrue(descendantBounds.Width > 0d && descendantBounds.Height > 0d);
    }

    private static MenuItem GetFilterMenuItem(ContextMenu menu, bool checkedState)
    {
        MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
        foreach (MenuItem item in items)
        {
            BindingOperations.GetBindingExpression(item, MenuItem.IsCheckedProperty)?.UpdateTarget();
        }

        MenuItem? match = items.FirstOrDefault(item => item.IsChecked == checkedState);
        Assert.IsNotNull(match, $"The filter menu must expose a bound item with IsChecked={checkedState}.");
        return match!;
    }

    private static void SetFilterMenuItemChecked(MenuItem item, bool checkedState)
    {
        item.SetCurrentValue(MenuItem.IsCheckedProperty, checkedState);
        BindingOperations.GetBindingExpression(item, MenuItem.IsCheckedProperty)?.UpdateSource();
        TestUiDispatcherHost.Drain();
    }

    private static TextBox? GetInstalledEditor(CustomTableView table)
        => table.Children
            .OfType<Canvas>()
            .SelectMany(canvas => canvas.Children.OfType<TextBox>())
            .SingleOrDefault();

    private static SearchChrome GetSearchChrome(MainWindow window, string editorName)
    {
        string suffix = string.Equals(editorName, "KeywordSearchEditor", StringComparison.Ordinal)
            ? string.Empty
            : "PlaylistSummary";
        KeywordSearchEditor editor = GetNamedElement<KeywordSearchEditor>(
            window,
            editorName);
        return new SearchChrome(
            editor,
            editor.InputTextBoxControl,
            GetNamedElement<Border>(window, $"{suffix}KeywordSearchComposite"),
            GetNamedElement<Grid>(window, $"{suffix}KeywordSearchWarningSlot"),
            GetNamedElement<ToggleButton>(window, $"{suffix}KeywordSearchHelpButton"),
            GetNamedElement<Grid>(window, $"{suffix}KeywordSearchHelpSlot"),
            GetNamedElement<TextBlock>(window, $"{suffix}KeywordSearchClearIcon"),
            GetNamedElement<Grid>(window, $"{suffix}KeywordSearchClearSlot"),
            editor.AssistancePopupControl,
            GetNamedElement<Popup>(window, $"{suffix}KeywordSearchHelpPopup"));
    }

    private static void AssertSearchChromeGeometry(SearchChrome chrome)
    {
        MaterializeElement(chrome.Outer, 600d, 24d);
        Assert.AreEqual(24d, chrome.Outer.Height, 0.01d);
        Assert.AreEqual(24d, chrome.Outer.ActualHeight, 0.01d);
        Assert.IsTrue(chrome.Outer.ActualHeight > 0d);
        Assert.AreEqual(new Thickness(1d), chrome.Outer.BorderThickness);
        Assert.IsNotNull(chrome.Outer.BorderBrush);
        Assert.IsTrue(chrome.Outer.IsVisible || chrome.Outer.Visibility == Visibility.Visible);
        Assert.AreEqual(390d, chrome.TextBox.Width, 0.01d);
        Assert.AreEqual(390d, chrome.TextBox.ActualWidth, 0.01d);
        Assert.AreEqual(new Thickness(0d), chrome.TextBox.BorderThickness);
        Assert.AreEqual(390d, chrome.Layout.ColumnDefinitions[1].Width.Value, 0.01d);
        Assert.AreEqual(20d, chrome.Layout.ColumnDefinitions[2].Width.Value, 0.01d);
        Assert.AreEqual(20d, chrome.Layout.ColumnDefinitions[3].Width.Value, 0.01d);
        Assert.AreEqual(20d, chrome.Layout.ColumnDefinitions[4].Width.Value, 0.01d);
        Assert.AreEqual(20d, chrome.WarningSlot.Width, 0.01d);
        Assert.AreEqual(20d, chrome.HelpSlot.Width, 0.01d);
        Assert.AreEqual(20d, chrome.ClearSlot.Width, 0.01d);
        Assert.AreEqual(2, Grid.GetColumn(chrome.WarningSlot));
        Assert.AreEqual(3, Grid.GetColumn(chrome.HelpSlot));
        Assert.AreEqual(4, Grid.GetColumn(chrome.ClearSlot));
        Assert.AreEqual(Visibility.Visible, chrome.WarningSlot.Visibility);
        Assert.IsTrue(chrome.WarningContent.Visibility is Visibility.Visible or Visibility.Collapsed);
        Assert.IsTrue(chrome.HelpButton.IsVisible || chrome.HelpButton.Visibility == Visibility.Visible);
        Assert.IsTrue(chrome.ClearSlot.IsVisible || chrome.ClearSlot.Visibility == Visibility.Visible);
        AssertSlotBounds(chrome.WarningSlot);
        AssertSlotBounds(chrome.HelpSlot);
        AssertSlotBounds(chrome.ClearSlot);
        AssertSlotChildCentered(chrome.HelpSlot, chrome.HelpButton, chrome.Outer);
        AssertSlotChildCentered(chrome.ClearSlot, chrome.ClearIcon, chrome.Outer);
        AssertIntegerThickness(chrome.WarningContent.Margin);
        AssertIntegerThickness(chrome.HelpButton.Margin);
        AssertIntegerThickness(chrome.ClearIcon.Margin);
    }

    private static void AssertSearchTextBoxChrome(TextBox textBox)
    {
        Assert.AreEqual(new Thickness(0d), textBox.BorderThickness);
        Assert.AreEqual(new Thickness(0d), GetTemplateBorder(textBox).BorderThickness);
    }

    private static void AssertFocusCueForPresentedSearch(SearchChrome chrome)
    {
        Brush unfocusedBrush = chrome.Outer.BorderBrush;
        SetKeyboardFocusForNonActivatingPresentation(chrome.TextBox);
        Assert.IsTrue(chrome.TextBox.IsKeyboardFocusWithin);
        Brush focusedBrush = chrome.Outer.BorderBrush;
        Assert.AreNotSame(unfocusedBrush, focusedBrush);
        Assert.AreEqual(new Thickness(0d), chrome.TextBox.BorderThickness);
        Assert.AreEqual(new Thickness(0d), GetTemplateBorder(chrome.TextBox).BorderThickness);

        SetKeyboardFocusForNonActivatingPresentation(chrome.HelpButton);
        Assert.IsFalse(chrome.TextBox.IsKeyboardFocusWithin);
        Assert.AreSame(unfocusedBrush, chrome.Outer.BorderBrush);
        Assert.AreEqual(new Thickness(0d), chrome.TextBox.BorderThickness);
        Assert.AreEqual(new Thickness(0d), GetTemplateBorder(chrome.TextBox).BorderThickness);
    }

    private static void SetKeyboardFocusForNonActivatingPresentation(IInputElement target)
    {
        // WS_EX_NOACTIVATE intentionally makes the public Focus route reject this off-screen
        // presentation. ChangeFocus exercises WPF's keyboard-focus state and routed events without
        // activating the HWND or synthesizing physical input. Retire this reflection when the shared
        // WPF harness exposes the same non-native transition as a typed test seam.
        MethodInfo changeFocus = typeof(KeyboardDevice).GetMethod(
                "ChangeFocus",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(DependencyObject), typeof(int)],
                modifiers: null)
            ?? throw new AssertFailedException(
                "The WPF keyboard device no longer exposes the non-native ChangeFocus transition.");
        changeFocus.Invoke(Keyboard.PrimaryDevice, [target, Environment.TickCount]);
        Assert.AreSame(target, Keyboard.FocusedElement);
    }

    private static void CompleteNonActivatingLogicalFocusTransition(DependencyObject scopeElement)
    {
        if (ReferenceEquals(scopeElement, Keyboard.FocusedElement))
        {
            return;
        }
        DependencyObject focusScope = FocusManager.GetFocusScope(scopeElement);
        IInputElement? target = FocusManager.GetFocusedElement(focusScope);
        Assert.IsNotNull(
            target,
            $"{scopeElement.GetType().Name} has no production-nominated logical focus target; keyboardFocus={Keyboard.FocusedElement?.GetType().Name ?? "null"}, scope={focusScope.GetType().Name}.");
        if (!ReferenceEquals(target, Keyboard.FocusedElement))
        {
            SetKeyboardFocusForNonActivatingPresentation(target);
        }
    }

    private static void FocusSearchEditor(KeywordSearchEditor editor)
    {
        SetKeyboardFocusForNonActivatingPresentation(editor.InputTextBoxControl);
        TestUiDispatcherHost.Drain();
        Assert.IsTrue(editor.InputTextBoxControl.IsKeyboardFocusWithin);
    }

    private static void WithPresentedMainWindow(MainWindow window, Action action)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(action);
        var presentationScope = new TestWindowPresentationScope(
            Application.Current
                ?? throw new InvalidOperationException("The shared WPF application host is unavailable."),
            TestWindowPresentationScope.GetCurrentNativeThreadId());
        object dataContext = window.DataContext;
        RoutedEventHandler suppressStartupActivation = (_, _) =>
        {
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(() =>
                {
                    window.Left = SystemParameters.VirtualScreenLeft
                        + (SystemParameters.VirtualScreenWidth * 4d)
                        + 4096d;
                    window.Top = SystemParameters.VirtualScreenTop
                        + (SystemParameters.VirtualScreenHeight * 4d)
                        + 4096d;
                }));
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() => window.DataContext = null));
        };
        try
        {
            window.Loaded += suppressStartupActivation;
            presentationScope.ShowAndWaitForContentRendered(window);
            window.DataContext = dataContext;
            TestUiDispatcherHost.Drain();
            action();
        }
        finally
        {
            window.Loaded -= suppressStartupActivation;
            presentationScope.Cleanup();
            window.DataContext = dataContext;
        }
    }

    private static void RaiseSearchKey(TextBox textBox, Key key)
    {
        RaiseRoutedKey(textBox, key, UIElement.PreviewKeyDownEvent);
    }

    private static void RaiseShiftTab(KeywordSearchEditor editor)
    {
        RaiseRoutedKey(editor.InputTextBoxControl, Key.LeftShift, UIElement.PreviewKeyDownEvent);
        RaiseRoutedKey(editor.InputTextBoxControl, Key.Tab, UIElement.PreviewKeyDownEvent);
        RaiseRoutedKey(editor.InputTextBoxControl, Key.LeftShift, UIElement.PreviewKeyUpEvent);
    }

    private static void RaiseRoutedKey(UIElement target, Key key, RoutedEvent routedEvent)
    {
        PresentationSource? source = PresentationSource.FromVisual(target);
        Assert.IsNotNull(source);
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = routedEvent
        });
    }

    private static double[] GetSectionViewportHeights(KeywordSearchEditor editor)
    {
        DependencyObject popupChild = editor.AssistancePopupControl.Child
            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
        return FindVisualDescendants<ScrollViewer>(popupChild)
            .Select(viewport => viewport.ActualHeight)
            .Where(height => height > 0d)
            .ToArray();
    }

    private static void RaiseCompositionEvent(
        TextBox textBox,
        TextComposition composition,
        RoutedEvent routedEvent)
    {
        textBox.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
        {
            RoutedEvent = routedEvent
        });
    }

    private static void RaiseApplyButton(KeywordSearchEditor editor, string displayText)
    {
        DependencyObject popupChild = editor.AssistancePopupControl.Child
            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
        Button button = FindVisualDescendants<Button>(popupChild)
            .Single(candidate => candidate.Content is TextBlock label
                && string.Equals(label.Text, displayText, StringComparison.Ordinal));
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    private static Border FindSavedRow(KeywordSearchEditor editor, string query)
    {
        DependencyObject popupChild = editor.AssistancePopupControl.Child
            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
        Border? row = FindVisualDescendants<Border>(popupChild)
            .SingleOrDefault(border => border.Tag is KeywordSearchPresentationItem item
                && item.Kind is KeywordSearchPresentationItemKind.Favorite or KeywordSearchPresentationItemKind.History
                && string.Equals(item.Query, query, StringComparison.Ordinal));
        Assert.IsNotNull(
            row,
            $"Saved row '{query}' was not materialized; popupOpen={editor.AssistancePopupControl.IsOpen}, presentationOpen={editor.Presentation.IsOpen}, presentationItems={string.Join("|", editor.Presentation.VisibleItems.Select(item => $"{item.Kind}:{item.Query}"))}, visualItems={string.Join("|", FindVisualDescendants<Border>(popupChild).Select(border => border.Tag).OfType<KeywordSearchPresentationItem>().Select(item => $"{item.Kind}:{item.Query}"))}, focus={Keyboard.FocusedElement?.GetType().Name ?? "null"}.");
        return row!;
    }

    private static ListBox GetSectionRows(
        KeywordSearchEditor editor,
        KeywordSearchPresentationSectionKind sectionKind)
    {
        DependencyObject popupChild = editor.AssistancePopupControl.Child
            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
        ListBox[] rows = FindVisualDescendants<ListBox>(popupChild).ToArray();
        KeywordSearchPresentationSection[] sections = editor.Presentation.Sections
            .Where(section => section.IsVisible)
            .ToArray();
        int sectionIndex = Array.FindIndex(
            sections,
            section => section.Kind == sectionKind);
        Assert.IsTrue(sectionIndex >= 0, $"The assistance section {sectionKind} is not visible.");
        Assert.IsTrue(rows.Length > sectionIndex, $"The assistance section {sectionKind} has no ListBox.");
        return rows[sectionIndex];
    }

    private static Grid GetEditorSurface(KeywordSearchEditor editor)
    {
        Grid? surface = FindVisualDescendants<Grid>(editor)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, "editorSurface", StringComparison.Ordinal));
        Assert.IsNotNull(surface, "The keyword-search editor surface must be materialized.");
        return surface!;
    }

    private static Button FindApplyButton(Border row)
        => FindVisualDescendants<Button>(row)
            .Single(button => button.Content is TextBlock);

    private static Border FindRowForItemKind(
        KeywordSearchEditor editor,
        KeywordSearchPresentationItemKind itemKind)
    {
        DependencyObject popupChild = editor.AssistancePopupControl.Child
            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
        return FindVisualDescendants<Border>(popupChild)
            .First(border => border.Tag is KeywordSearchPresentationItem item
                && item.Kind == itemKind);
    }

    private static Button[] FindVisibleActionButtons(Border row)
        => FindVisualDescendants<Button>(row)
            .Where(button => button.Focusable
                && button.IsTabStop
                && button.Visibility == Visibility.Visible
                && button.IsVisible
                && button.IsEnabled)
            .ToArray();

    private static Button FindActionButton(Border row, string accessibleName)
    {
        return FindVisualDescendants<Button>(row)
            .Single(button => string.Equals(
                AutomationProperties.GetName(button),
                accessibleName,
                StringComparison.Ordinal));
    }

    private static void AssertTextBoxChrome(TextBox textBox, Thickness expectedThickness)
    {
        Assert.AreEqual(expectedThickness, textBox.BorderThickness);
        Assert.AreEqual(expectedThickness, GetTemplateBorder(textBox).BorderThickness);
    }

    private static Border GetTemplateBorder(TextBox textBox)
    {
        textBox.ApplyTemplate();
        Assert.IsNotNull(textBox.Template);
        Border? border = FindVisualDescendants<Border>(textBox).FirstOrDefault();
        Assert.IsNotNull(border, $"{textBox.Name} did not materialize a template Border.");
        return border!;
    }

    private static Point GetRelativeOrigin(FrameworkElement element, FrameworkElement ancestor)
        => element.TranslatePoint(new Point(0d, 0d), ancestor);

    private static void AssertSamePoint(Point expected, Point actual)
    {
        Assert.AreEqual(expected.X, actual.X, 0.01d);
        Assert.AreEqual(expected.Y, actual.Y, 0.01d);
    }

    private static void AssertSlotBounds(Grid slot)
    {
        Assert.AreEqual(20d, slot.ActualWidth, 0.01d);
        Assert.AreEqual(20d, slot.ActualHeight, 0.01d);
    }

    private static void AssertSlotChildCentered(
        FrameworkElement slot,
        FrameworkElement child,
        FrameworkElement ancestor)
    {
        Point slotOrigin = GetRelativeOrigin(slot, ancestor);
        Point childOrigin = GetRelativeOrigin(child, ancestor);
        var slotCenter = new Point(
            slotOrigin.X + slot.ActualWidth / 2d,
            slotOrigin.Y + slot.ActualHeight / 2d);
        var childCenter = new Point(
            childOrigin.X + child.ActualWidth / 2d,
            childOrigin.Y + child.ActualHeight / 2d);
        const double devicePixelTolerance = 0.51d;
        Assert.AreEqual(slotCenter.X, childCenter.X, devicePixelTolerance);
        Assert.AreEqual(slotCenter.Y, childCenter.Y, devicePixelTolerance);
    }

    private static void AssertIntegerThickness(Thickness margin)
    {
        Assert.AreEqual(Math.Truncate(margin.Left), margin.Left);
        Assert.AreEqual(Math.Truncate(margin.Top), margin.Top);
        Assert.AreEqual(Math.Truncate(margin.Right), margin.Right);
        Assert.AreEqual(Math.Truncate(margin.Bottom), margin.Bottom);
    }

    private static void MaterializeMainWindow(MainWindow window)
    {
        window.Width = 1200d;
        window.Height = 800d;
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0d, 0d, window.Width, window.Height));
        window.UpdateLayout();
        TestUiDispatcherHost.Drain();
    }

    private static void MaterializeElement(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0d, 0d, width, height));
        element.UpdateLayout();
        TestUiDispatcherHost.Drain();
    }

    private static void MaterializePopup(KeywordSearchEditor editor)
    {
        FrameworkElement popupChild = editor.AssistancePopupControl.Child as FrameworkElement
            ?? throw new AssertFailedException("The search assistance popup has no rendered child.");
        MaterializeElement(popupChild, 600d, 400d);
    }

    private static void RaiseMouseLeftButtonDown(UIElement element)
    {
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
        });
    }

    private static void RaisePreviewMouseDown(UIElement element)
    {
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent
        });
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed record SearchChrome(
        KeywordSearchEditor Editor,
        TextBox TextBox,
        Border Outer,
        Grid WarningSlot,
        ToggleButton HelpButton,
        Grid HelpSlot,
        TextBlock ClearIcon,
        Grid ClearSlot,
        Popup SuggestionPopup,
        Popup HelpPopup)
    {
        internal Grid Layout => (Grid)Outer.Child;

        internal DropDownMenuButton FilterButton => Layout.Children.OfType<DropDownMenuButton>().Single();

        internal TextBlock WarningContent => WarningSlot.Children.OfType<TextBlock>().Single();
    }

    private static CustomTableView CreateEditableTable(CustomTableColumn column, double width, double height)
    {
        var table = new CustomTableView
        {
            Width = (int)width,
            Height = height,
            HeaderHeight = 0d,
            RowHeight = 60d,
            Columns = [column],
            ItemsSource = new List<object> { new object() }
        };
        return table;
    }

    private static CustomTableColumn CreateScrollBarContractColumn(double width)
    {
        var layout = new CustomTableColumnSettings.ColumnLayout
        {
            Width = (int)width,
            Visibility = Visibility.Visible
        };
        return new CustomTableColumn(
            "Contract",
            "Contract",
            layout,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: _ => string.Empty);
    }

    private static void MaterializeScrollBarContractTable(CustomTableView table, double width, double height)
    {
        table.Measure(new Size(width, height));
        table.Arrange(new Rect(0d, 0d, width, height));
        table.UpdateLayout();
    }

    private static void AssertScrollBarPresentation(ScrollBar scrollBar, Orientation orientation)
    {
        scrollBar.ApplyTemplate();
        Assert.IsNotNull(scrollBar.Template);
        var track = (Track)scrollBar.Template.FindName("PART_Track", scrollBar);
        Assert.IsNotNull(track);
        Assert.AreEqual(orientation, scrollBar.Orientation);
        Assert.AreEqual(orientation, track.Orientation);
    }

    private static void FlushResourceUpdates(CustomTableView table)
    {
        table.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
        table.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
    }

    private static void MaterializeTable(CustomTableView table, double width, double height)
    {
        table.Measure(new Size(width, height));
        table.Arrange(new Rect(0d, 0d, width, height));
        table.UpdateLayout();
        table.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
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

    private static HwndSource CreateHwndSource(
        CustomTableView table,
        int width,
        int height)
    {
        var source = new HwndSource(new HwndSourceParameters("CustomTableEditTest")
        {
            Width = width,
            Height = height,
            PositionX = 0,
            PositionY = 0
        });
        source.RootVisual = table;
        return source;
    }

}
