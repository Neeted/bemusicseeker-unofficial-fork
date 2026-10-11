using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <remarks>compiled列menuと検索・編集Controlのイベント接続、popupの実配置・仮想化・IME経路を検査するため実WPFを使います。候補生成・保存失敗・編集可否の条件網羅はSearchとCustomTableのownerテストが分担します。</remarks>
[TestClass]
// MainWindowのApplication.Resourcesとテーマ設定を一時変更し、所有Window・popupを閉じてから共有状態を復元します。
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
                    "/BeMusicSeeker;component/Themes/Simple Styles.xaml",
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

    /// <summary>compiled列メニューのリセットClickを一回の列reset端末へ配送し、XAMLと処理の接続漏れを検出します。</summary>
    [TestMethod]
    public void MainColumnResetClickUsesActualCompiledMenuRoute()
    {
        int calls = 0;
        var terminal = new MainWindowColumnResetTerminal(_ => calls++);

        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (_, window) =>
                {
                    var menu = (ContextMenu)window.FindResource("tableColumnHeaderContextMenu");
                    MenuItem reset = menu.Items.OfType<MenuItem>().Last();

                    reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, reset));

                    Assert.AreEqual(1, calls);
                },
                allowStartupUiInteraction: true,
                columnResetTerminal: terminal);
        });
    }

    /// <summary>実列factoryの導入先とplaylist MemoをF2/Enterで確定し、MainChartList端末へ対象行・列・入力を配送して詳細編集状態の開始・終了を通知します。</summary>
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
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (scope, viewModel, window) =>
                {
                    viewModel.MainChartList.CellEditBeginningRequested += (_, request) => beginning.Add(request.Context);
                    viewModel.MainChartList.CellEditStarted += (_, context) => started.Add(context);
                    viewModel.MainChartList.CellEditEndedRequested += (_, request) => completed.Add(request);
                    window.Width = 1000d;
                    window.Height = 700d;
                    MainWindowPresentationTestHarness.ShowCompiledContent(scope, viewModel, window);

                    ChartFile file = ChartTestValues.Empty() with
                    {
                        Path = @"C:\wave6e-cell-edit\pending\chart.bms",
                        Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                        RawTitle = "Pending cell edit"
                    };
                    var entry = PackageChartEntry.FromChart((file));
                    var row = LibraryChartRow.FromPackageChartEntry(entry);
                    var table = (CustomTableView)window.FindName("customTableView");
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
                    TextBox editor = GetInstalledEditor(table)!;
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
                    Assert.AreSame(file.Token, row.Chart.Token);

                    var detailEntry = new BMSTableEntry(file) { memo = "before memo" };
                    var detailTable = new BMSTable { name = "Local detail", entries = [detailEntry] };
                    Assert.AreSame(detailTable, detailEntry.parent);
                    PlaylistDetailRow detailRow = new PlaylistDetailSourceRow(detailEntry, resolvedChart: null).CreateViewRow();
                    table.ItemsSource = new List<object> { detailRow };
                    columnSettings.Memo.Visibility = Visibility.Visible;
                    table.Columns = [.. CustomTableColumnFactory.CreateMainColumns(columnSettings)
                        .Where(candidate => candidate.EditPropertyName == nameof(PlaylistDetailRow.memo))];
                    table.SelectRowsByPredicate(_ => true);
                    viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PlaylistFilterSelected);
                    MaterializeTable(table, table.Width, table.Height);
                    CustomTableColumn memoColumn = table.Columns.Single();
                    Assert.IsTrue(table.IsCurrentCell(0, memoColumn));
                    RaiseKey(table, Key.F2);
                    Assert.AreEqual(2, beginning.Count);
                    Assert.AreEqual(2, started.Count);
                    Assert.IsTrue(viewModel.PlaylistWorkspace.DetailViewState.Source.IsPlaylistCellEditing);
                    TextBox memoEditor = GetInstalledEditor(table) ?? throw new AssertFailedException("Memo編集controlへ到達しませんでした。");
                    memoEditor.Text = "edited memo";
                    RaiseKey(table, Key.Return);
                    Assert.AreEqual(2, completed.Count);
                    Assert.AreSame(detailRow, beginning[1].Row);
                    Assert.AreSame(detailRow, started[1].Row);
                    Assert.AreSame(detailRow, completed[1].Context.Row);
                    Assert.AreEqual(nameof(PlaylistDetailRow.memo), beginning[1].PropertyName);
                    Assert.AreEqual(nameof(PlaylistDetailRow.memo), started[1].PropertyName);
                    Assert.AreEqual(nameof(PlaylistDetailRow.memo), completed[1].Context.PropertyName);
                    Assert.AreEqual("edited memo", completed[1].Text);
                    Assert.IsTrue(completed[1].Commit);
                    // このconstructorは保存providerを持たず同期終端となる。DB保存は下位の5属性Taskへ分担する。
                    Assert.IsFalse(viewModel.PlaylistWorkspace.DetailViewState.Source.IsPlaylistCellEditing);
                });
        });
    }

    /// <summary>行選択後のF2開始通知で編集TextBoxの配置と初期値が既に成立し、editor設置前の開始通知を検出します。</summary>
    [TestMethod]
    public void CustomTableCellEditStartedSeesInstalledEditorAfterKeyboardActivation()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
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
            var host = new Window
            {
                Width = 220d,
                Height = 70d,
                Content = table,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            scope.ShowAndWaitForContentRendered(host);
            MaterializeTable(table, 220d, 70d);
            int beginningCount = 0;
            int startedCount = 0;
            TextBox? editorSeenAtStarted = null;
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
            RaiseKey(table, Key.F2);

            Assert.AreEqual(1, beginningCount);
            Assert.AreEqual(1, startedCount);
            Assert.IsNotNull(editorSeenAtStarted);
            Assert.IsTrue(editorSeenAtStarted!.Width > 2d);
            Assert.IsTrue(editorSeenAtStarted!.Height > 2d);
            Assert.AreEqual("initial", editorSeenAtStarted!.Text);

            Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));
        });
    }

    /// <summary>編集開始が取消された場合と列幅不足の場合は開始要求だけを通知し、editor設置とStarted通知を実行しません。</summary>
    [TestMethod]
    public void CustomTableCellEditFailuresDoNotRaiseStarted()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
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

            CustomTableView cancelled = CreateEditableTable(column, 220d, 70d);
            var host = new Window
            {
                Width = 220d,
                Height = 70d,
                Content = cancelled,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            scope.ShowAndWaitForContentRendered(host);
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
            CustomTableView tooSmall = CreateEditableTable(tooSmallColumn, 220d, 70d);
            host.Content = tooSmall;
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
        });
    }

    /// <summary>実テーマ切替を表とcornerへ反映し、縦・横・両方向のoverflowで必要scrollbarと両方向時だけのcornerを表示します。</summary>
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
                        "/BeMusicSeeker;component/Themes/Simple Styles.xaml",
                        UriKind.RelativeOrAbsolute)
                });
                resourceHost.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/Themes/CanonicalDialogStyles.xaml",
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

                object trackBackground = table.FindResource(trackBackgroundResourceKey);
                Assert.AreSame(trackBackground, table.Background);
                Assert.AreSame(trackBackground, corner.Background);

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

    /// <summary>compiled検索TextBoxの編集を通常譜面のfilterへ配送し、editor内の値だけを更新して一覧検索へ届かない接続漏れを検出します。</summary>
    [TestMethod]
    public void KeywordEditorTextUpdatesComposedChartFilter()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (viewModel, window) =>
                {
                    KeywordSearchEditor searchEditor = GetNamedElement<KeywordSearchEditor>(window, "KeywordSearchEditor");
                    TextBox keyword = searchEditor.InputTextBoxControl;
                    keyword.Text = "wave6e-filter";
                    BindingOperations.GetBindingExpression(searchEditor, KeywordSearchEditor.TextProperty)?.UpdateSource();
                    Assert.AreEqual("wave6e-filter", viewModel.ChartFilters.KeywordFilter);
                });
        });
    }

    /// <summary>通常譜面とサマリーの警告表示を切り替えてもヘルプ・clear操作の位置を維持し、警告の出現で操作位置が移動しません。</summary>
    [TestMethod]
    public void SearchToolbars_WarningVisibilityKeepsNeighborPositions()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (viewModel, window) =>
                {
                    MaterializeMainWindow(window);

                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
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
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    MaterializeMainWindow(window);

                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
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

    /// <summary>compiled検索filterメニューの実描画と選択操作を通常譜面・サマリー別に接続し、片側の選択が他方のfilterを変えません。</summary>
    [TestMethod]
    public void SearchVariants_FilterMenusChangeOnlyCorrespondingOwner()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (viewModel, window) =>
                {
                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    AssertFilterAffordanceMaterialized(normal);
                    AssertFilterAffordanceMaterialized(summary);
                    MaterializeMainWindow(window);
                    AssertFilterAffordanceRendered(normal);
                    ChartModeFilter modeBeforeInteraction = viewModel.ChartFilters.ModeFilter;
                    PlaylistOwnedFilter ownedFilterBeforeInteraction = viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter;
                    MenuItem normalFilterItem = GetFilterMenuItem(normal.FilterButton.DropDownContextMenu!, true);
                    SetFilterMenuItemChecked(normalFilterItem, false);
                    Assert.AreNotEqual(modeBeforeInteraction, viewModel.ChartFilters.ModeFilter);
                    Assert.AreEqual(ownedFilterBeforeInteraction, viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter);
                    ChartModeFilter modeAfterNormalInteraction = viewModel.ChartFilters.ModeFilter;

                    viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    MaterializeMainWindow(window);
                    AssertFilterAffordanceRendered(summary);
                    MenuItem summaryFilterItem = GetFilterMenuItem(summary.FilterButton.DropDownContextMenu!, false);
                    SetFilterMenuItemChecked(summaryFilterItem, true);
                    Assert.AreEqual(modeAfterNormalInteraction, viewModel.ChartFilters.ModeFilter);
                    Assert.AreNotEqual(ownedFilterBeforeInteraction, viewModel.PlaylistWorkspace.PlaylistSummaryOwnedFilter);
                });
        });
    }


    /// <summary>clearアイコンのmouse操作で検索入力を空にして補助popupを更新し、サマリー側へ通常譜面の保存クエリを混入させません。</summary>
    [TestMethod]
    public void SearchEditor_ClearMouseRouteRefreshesOnlyItsOwnEmptyAssistanceImmediately()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    KeywordSearchAssistanceOwner normalOwner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;

                    Assert.IsTrue(normalOwner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(normalOwner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(normal, normalOwner, "title:normal");
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        RaiseMouseLeftButtonDown(normal.ClearIcon);
                        Assert.AreEqual(string.Empty, normal.TextBox.Text);
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
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        KeywordSearchAssistanceOwner summaryOwner = viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner;
                        PrepareSearchEditor(summary, summaryOwner, "name:summary");
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        RaiseMouseLeftButtonDown(summary.ClearIcon);
                        Assert.AreEqual(string.Empty, summary.TextBox.Text);
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

    /// <summary>入力変更で補助popupを即時更新してprefix候補へ絞り、空へ戻すと保存したお気に入り・履歴・フィールドのsectionを復元します。</summary>
    [TestMethod]
    public void SearchEditor_RefreshesImmediatelyAndRestoresEmptySections()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:alpha").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:beta").Succeeded);

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        Assert.IsTrue(
                            chrome.SuggestionPopup.IsOpen,
                            $"popup={chrome.SuggestionPopup.IsOpen}, ownerOpen={owner.Presentation.IsOpen}, editorOpen={chrome.Editor.Presentation.IsOpen}, sections={owner.Presentation.Sections.Count}");
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
                        TestUiDispatcherHost.ProcessQueuedPresentation();
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
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        Assert.AreEqual(3, chrome.Editor.Presentation.Sections.Count);

                    });
                });
        });
    }

    /// <summary>候補を上下選択してEnterで入力へ適用し、IME処理キーでは入力を変えず、Escapeで閉じたpopupをDownで再開します。</summary>
    [TestMethod]
    public void SearchEditor_NavigationAndImeKeepInputLifecycleOwnedByEditor()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:alpha").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:beta").Succeeded);

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        RaiseSearchKey(chrome.Editor, Key.Down);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        RaiseSearchKey(chrome.Editor, Key.Down);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        string beforeIme = chrome.TextBox.Text;
                        RaiseSearchKey(chrome.Editor, Key.ImeProcessed);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(beforeIme, chrome.TextBox.Text);
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);

                        RaiseSearchKey(chrome.Editor, Key.Enter);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual("artist:beta", chrome.TextBox.Text);
                        Assert.AreEqual(chrome.TextBox.Text.Length, chrome.TextBox.CaretIndex);

                        chrome.TextBox.Text = string.Empty;
                        chrome.TextBox.CaretIndex = 0;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                        RaiseSearchKey(chrome.Editor, Key.Escape);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsFalse(chrome.SuggestionPopup.IsOpen);
                        Assert.IsFalse(owner.Presentation.IsOpen);

                        RaiseSearchKey(chrome.Editor, Key.Down);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(chrome.SuggestionPopup.IsOpen);
                    });
                });
        });
    }

    /// <summary>保存候補の削除とお気に入り追加を実buttonから実行し、検索入力と元履歴を変えず、操作を識別できる名称を表示します。</summary>
    [TestMethod]
    public void SearchEditor_SavedRowActionsKeepQueryUnchanged()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

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
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(string.Empty, chrome.TextBox.Text);
                        CollectionAssert.DoesNotContain(owner.SavedQueryOwner.Favorites.ToArray(), "title:favorite");

                        Border historyRow = FindSavedRow(chrome.Editor, "artist:history");
                        Button addFavorite = FindActionButton(
                            historyRow,
                            Resources.Keyword_search_add_favorite_tooltip);
                        addFavorite.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, addFavorite));
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(string.Empty, chrome.TextBox.Text);
                        CollectionAssert.Contains(owner.SavedQueryOwner.Favorites.ToArray(), "artist:history");
                        CollectionAssert.Contains(owner.SavedQueryOwner.History.ToArray(), "artist:history");
                    });
                });
        });
    }

    /// <summary>保存候補とフィールドを別viewportへ表示して操作buttonを描画し、サマリーでは専用output候補だけを提供して未対応値候補を出しません。</summary>
    [TestMethod]
    public void SearchEditor_UsesIndependentSectionViewportsAndSummaryFieldCatalog()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    for (int index = 0; index < 6; index++)
                    {
                        Assert.IsTrue(owner.TryAddFavorite($"title:f{index}").Succeeded);
                        Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory($"artist:h{index}").Succeeded);
                    }

                    SearchChrome normal = GetSearchChrome(window, "KeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(normal, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        ScrollViewer[] sectionViewports = FindVisualDescendants<ScrollViewer>(
                                normal.Editor.AssistancePopupControl.Child!)
                            .ToArray();
                        Assert.AreEqual(3, sectionViewports.Length);
                        Assert.IsTrue(sectionViewports.All(viewport => viewport.MaxHeight > 0d && double.IsFinite(viewport.MaxHeight)));
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
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                        KeywordSearchAssistanceOwner summaryOwner =
                            viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner;
                        PrepareSearchEditor(summary, summaryOwner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
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
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsFalse(summary.Editor.Presentation.IsOpen);
                        Assert.IsFalse(summary.Editor.Presentation.Sections.Any(
                            section => section.Kind == KeywordSearchPresentationSectionKind.Values));
                    });
                });
        });
    }

    /// <summary>viewportを超えるお気に入りを保存したまま表示行だけを実体化し、全件Control生成による仮想化退行を検出します。</summary>
    [TestMethod]
    public void SearchEditor_LargeFavoritesVirtualizeOffViewportRows()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    const int favoriteCount = 32;
                    for (int index = 0; index < favoriteCount; index++)
                    {
                        Assert.IsTrue(owner.TryAddFavorite($"title:favorite-{index}").Succeeded);
                    }

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
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

    /// <summary>お気に入りと履歴の画面外行をscrollで表示し、同じ描画surfaceのDataContext変更にも全文tooltipと操作名を追従させます。</summary>
    [TestMethod]
    public void SearchEditor_SavedRowTooltipsAndAccessibilityFollowRecycledDataContext()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
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
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
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

                            // runtimeがtemplateを再生成しても、同じsurfaceを再利用する入力を固定します。
                            // Loaded時の行状態だけでtooltipを保持する実装を検出します。
                            lastRow.SetCurrentValue(FrameworkElement.DataContextProperty, firstItem);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
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

    /// <summary>保存済み検索には全文tooltipと識別名を出し、通常・サマリーのフィールド候補と値候補には保存クエリtooltipを混入させません。</summary>
    [TestMethod]
    public void SearchEditor_QueryTooltipsAreLimitedToSavedRows()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);

                        Border favoriteRow = FindSavedRow(chrome.Editor, "title:favorite");
                        Button favoriteApply = FindApplyButton(favoriteRow);
                        Assert.AreEqual("title:favorite", favoriteApply.ToolTip?.ToString());
                        Assert.AreEqual(
                            "title:favorite",
                            AutomationProperties.GetName(favoriteApply));

                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Border fieldRow = FindRowForItemKind(
                            chrome.Editor,
                            KeywordSearchPresentationItemKind.Field);
                        Button fieldApply = FindApplyButton(fieldRow);
                        Assert.IsNull(fieldRow.ToolTip);
                        Assert.IsNull(fieldApply.ToolTip);

                        chrome.TextBox.Text = "clear:N";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Border valueRow = FindRowForItemKind(
                            chrome.Editor,
                            KeywordSearchPresentationItemKind.Value);
                        Button valueApply = FindApplyButton(valueRow);
                        Assert.IsNull(valueRow.ToolTip);
                        Assert.IsNull(valueApply.ToolTip);

                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        KeywordSearchAssistanceOwner summaryOwner =
                            viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner;
                        PrepareSearchEditor(summary, summaryOwner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
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

    /// <summary>異なる幅へ変更した通常・サマリー入力面をpopup開放前に捕捉し、長い保存クエリでpopupを広げず全文と省略表示を保ちます。</summary>
    [TestMethod]
    public void SearchEditor_SavedQueryPopupWidthIsBoundToPreOpenSurface()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    const string longQuery =
                        "title:This-is-a-deliberately-long-saved-query-that-must-remain-intact-" +
                        "0123456789abcdefghijklmnopqrstuvwxyz";
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite(longQuery).Succeeded);

                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    SearchChrome summary = GetSearchChrome(window, "PlaylistSummaryKeywordSearchEditor");
                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        MaterializeMainWindow(window);
                        Grid surface = GetEditorSurface(chrome.Editor);
                        double baselineWidth = surface.ActualWidth;
                        Assert.IsTrue(baselineWidth > 0d);
                        surface.SetCurrentValue(
                            FrameworkElement.WidthProperty,
                            baselineWidth * 0.75d);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);
                        double preOpenWidth = surface.ActualWidth;
                        Assert.IsTrue(preOpenWidth > 0d);

                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
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
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsFalse(chrome.SuggestionPopup.IsOpen);
                        viewModel.PlaylistWorkspace.SetPlaylistSummaryMode(true);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);
                        Grid summarySurface = GetEditorSurface(summary.Editor);
                        double summaryBaselineWidth = summarySurface.ActualWidth;
                        Assert.IsTrue(summaryBaselineWidth > 0d);
                        summarySurface.SetCurrentValue(
                            FrameworkElement.WidthProperty,
                            summaryBaselineWidth * 0.875d);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);
                        double summaryPreOpenWidth = summarySurface.ActualWidth;
                        Assert.IsTrue(summaryPreOpenWidth > 0d);
                        const double layoutRoundingTolerance = 0.51d;
                        Assert.IsTrue(
                            Math.Abs(preOpenWidth - summaryPreOpenWidth) > layoutRoundingTolerance,
                            $"Normal and summary editor fixture widths must differ beyond layout rounding: normal={preOpenWidth}, summary={summaryPreOpenWidth}.");
                        KeywordSearchAssistanceOwner summaryOwner =
                            viewModel.PlaylistWorkspace.PlaylistSummaryKeywordSearchAssistanceOwner;
                        PrepareSearchEditor(summary, summaryOwner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        MaterializeMainWindow(window);
                        Border summaryPopupChild = summary.SuggestionPopup.Child as Border
                            ?? throw new AssertFailedException("The summary assistance popup has no outer Border.");
                        Assert.AreEqual(summaryPreOpenWidth, summaryPopupChild.ActualWidth, 0.51d);
                    });
                });
        });
    }

    /// <summary>お気に入り・履歴・値・フィールドの各少数候補は件数増減へviewport高さを追従させ、最大高さまでの空白を常に確保しません。</summary>
    [TestMethod]
    public void SearchEditor_ShortSectionsSizeToContentForSavedValuesAndFields()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    Assert.IsTrue(owner.TryAddFavorite("title:favorite").Succeeded);
                    Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history").Succeeded);
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, string.Empty);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        double[] oneSavedRowHeights = GetSectionViewportHeights(chrome.Editor);
                        Assert.IsTrue(oneSavedRowHeights.Length >= 2);
                        Assert.IsTrue(oneSavedRowHeights[0] > 0d && oneSavedRowHeights[1] > 0d);

                        Assert.IsTrue(owner.TryAddFavorite("title:favorite-two").Succeeded);
                        Assert.IsTrue(owner.SavedQueryOwner.TryCommitHistory("artist:history-two").Succeeded);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        double[] twoSavedRowHeights = GetSectionViewportHeights(chrome.Editor);
                        Assert.IsTrue(twoSavedRowHeights[0] > oneSavedRowHeights[0]);
                        Assert.IsTrue(twoSavedRowHeights[1] > oneSavedRowHeights[1]);

                        chrome.TextBox.Text = "clear:NP";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        double oneValueHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(oneValueHeight > 0d);

                        chrome.TextBox.Text = "clear:N";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        double twoValueHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(twoValueHeight > oneValueHeight);

                        chrome.TextBox.Text = "cl";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        double oneFieldHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(oneFieldHeight > 0d);

                        chrome.TextBox.Text = "c";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        double twoFieldHeight = GetSectionViewportHeights(chrome.Editor).Single();
                        Assert.IsTrue(twoFieldHeight > oneFieldHeight);
                    });
                });
        });
    }

    /// <summary>実IME開始・更新イベント中のEnterでは候補適用と履歴確定を抑止し、文字列確定後だけ通常のEnter・Tab適用を再開します。</summary>
    [TestMethod]
    public void SearchEditor_ActiveImeCompositionRoutedEventsDoNotApplyOrCommitHistory()
    {
        WithSimpleTextBoxStyles(() =>
        {
            MainWindowPresentationTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(),
                (windowTest, viewModel, window) =>
                {
                    SearchChrome chrome = GetSearchChrome(window, "KeywordSearchEditor");
                    KeywordSearchAssistanceOwner owner = viewModel.ChartFilters.KeywordSearchAssistanceOwner;
                    int historyCountBefore = owner.SavedQueryOwner.History.Count;

                    WithPresentedMainWindow(windowTest, window, () =>
                    {
                        PrepareSearchEditor(chrome, owner, "tit");
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        RaiseSearchKey(chrome.Editor, Key.Down);
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
                        RaiseSearchKey(chrome.Editor, Key.Enter);
                        TestUiDispatcherHost.ProcessQueuedPresentation();

                        Assert.AreEqual(textBeforeComposition, chrome.TextBox.Text);
                        Assert.AreEqual(caretBeforeComposition, chrome.TextBox.CaretIndex);
                        Assert.AreEqual(historyCountBefore, owner.SavedQueryOwner.History.Count);

                        RaiseCompositionEvent(
                            chrome.TextBox,
                            new TextComposition(InputManager.Current, chrome.TextBox, "title"),
                            TextCompositionManager.TextInputEvent);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(textBeforeComposition + "title", chrome.TextBox.Text);
                        Assert.AreEqual(historyCountBefore, owner.SavedQueryOwner.History.Count);

                        // IME確定を通した後、新しいprefixでEnter・Tabの抑止解除を確認します。
                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        RaiseSearchKey(chrome.Editor, Key.Enter);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual("title:", chrome.TextBox.Text);
                        Assert.AreEqual(chrome.TextBox.Text.Length, chrome.TextBox.CaretIndex);

                        chrome.TextBox.Text = "tit";
                        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        RaiseSearchKey(chrome.Editor, Key.Down);
                        RaiseSearchKey(chrome.Editor, Key.Tab);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual("title:", chrome.TextBox.Text);
                        Assert.AreEqual(chrome.TextBox.Text.Length, chrome.TextBox.CaretIndex);
                    });
                });
        });
    }

    /// <summary>指定したTextBoxの境界幅と編集controlの境界なし設定をcompiled templateへ反映し、styleが有効値を固定幅で上書きしません。</summary>
    [TestMethod]
    public void SimpleTextBox_TemplateChromeFollowsEffectiveBorderThickness()
    {
        WithSimpleTextBoxStyles(() =>
        {
            var host = new Grid();
            host.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "/BeMusicSeeker;component/Themes/Simple Styles.xaml",
                    UriKind.RelativeOrAbsolute)
            });
            host.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "/BeMusicSeeker;component/Themes/Light.xaml",
                    UriKind.RelativeOrAbsolute)
            });
            var simpleTextBoxStyle = (Style)host.FindResource("SimpleTextBox");
            host.Resources.Add(typeof(TextBox), simpleTextBoxStyle);
            var implicitTextBox = new TextBox { BorderThickness = new Thickness(3d) };
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

            AssertTextBoxChrome(implicitTextBox, new Thickness(3d));
            AssertTextBoxChrome(explicitTextBox, new Thickness(0d));
            TextBox editor = FindVisualDescendants<TextBox>(editableTextBlock).Single();
            AssertTextBoxChrome(editor, new Thickness(0d));
        });
    }

    private static T GetNamedElement<T>(MainWindow window, string name)
        where T : class
    {
        object? element = window.FindName(name);
        Assert.IsInstanceOfType(element, typeof(T), name);
        return (T)element!;
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
        TestUiDispatcherHost.ProcessQueuedPresentation();
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
            GetNamedElement<AppFontIcon>(window, $"{suffix}KeywordSearchClearIcon"),
            GetNamedElement<Grid>(window, $"{suffix}KeywordSearchClearSlot"),
            editor.AssistancePopupControl,
            GetNamedElement<Popup>(window, $"{suffix}KeywordSearchHelpPopup"));
    }


    private static void WithPresentedMainWindow(TestWindowPresentationScope presentationScope, MainWindow window, Action action)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(presentationScope);
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
            TestUiDispatcherHost.ProcessQueuedPresentation();
            action();
        }
        finally
        {
            window.Loaded -= suppressStartupActivation;
            // 親Windowは外側harnessが実shutdown/Closedを回収した後に同scopeで片付けます。
            window.DataContext = dataContext;
        }
    }

    private static void PrepareSearchEditor(
        SearchChrome chrome,
        KeywordSearchAssistanceOwner owner,
        string text)
    {
        chrome.TextBox.SetCurrentValue(TextBox.TextProperty, text ?? string.Empty);
        chrome.TextBox.CaretIndex = chrome.TextBox.Text.Length;
        owner.Focus(chrome.TextBox.Text, chrome.TextBox.CaretIndex);
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void RaiseSearchKey(
        KeywordSearchEditor editor,
        Key key,
        ModifierKeys modifiers = ModifierKeys.None,
        Key imeProcessedKey = Key.None)
        => editor.HandleInputKey(key, modifiers, imeProcessedKey);

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
            $"Saved row '{query}' was not materialized; popupOpen={editor.AssistancePopupControl.IsOpen}, presentationOpen={editor.Presentation.IsOpen}, presentationItems={string.Join("|", editor.Presentation.VisibleItems.Select(item => $"{item.Kind}:{item.Query}"))}, visualItems={string.Join("|", FindVisualDescendants<Border>(popupChild).Select(border => border.Tag).OfType<KeywordSearchPresentationItem>().Select(item => $"{item.Kind}:{item.Query}"))}.");
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


    private static void MaterializeMainWindow(MainWindow window)
    {
        window.Width = 1200d;
        window.Height = 800d;
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0d, 0d, window.Width, window.Height));
        window.UpdateLayout();
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void MaterializeElement(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0d, 0d, width, height));
        element.UpdateLayout();
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void RaiseMouseLeftButtonDown(UIElement element)
    {
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent
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
        AppFontIcon ClearIcon,
        Grid ClearSlot,
        Popup SuggestionPopup,
        Popup HelpPopup)
    {
        internal Grid Layout => (Grid)Outer.Child;

        internal DropDownMenuButton FilterButton => Layout.Children.OfType<DropDownMenuButton>().Single();

        internal AppFontIcon WarningContent => WarningSlot.Children.OfType<AppFontIcon>().Single();
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
        => table.HandleKeyDown(key, ModifierKeys.None);

}
