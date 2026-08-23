using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
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
    [TestMethod]
    public void MainColumnResetClickUsesActualCompiledMenuRoute()
    {
        int calls = 0;
        var terminal = new MainWindowColumnResetTerminal(_ => calls++);

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
    }

    [TestMethod]
    public void MainWindowPendingCellEditUsesDefaultTerminalAndMainChartOwnerRoute()
    {
        var beginning = new List<MainChartListCellEditContext>();
        var started = new List<MainChartListCellEditContext>();
        var completed = new List<MainChartListCellEditEndedEventArgs>();
        const string expectedText = @"C:\wave6e-cell-edit\destination";

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
                table.Columns = [.. CustomTableColumnFactory.CreateMainColumns(columnSettings)];
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PendingInstallFolderSelected);
                table.Measure(new Size(table.Width, table.Height));
                table.Arrange(new Rect(0d, 0d, table.Width, table.Height));
                table.UpdateLayout();

                CustomTableColumn column = table.Columns
                    .Single(candidate => string.Equals(candidate.EditPropertyName, "instl_dst", StringComparison.Ordinal));
                ScrollBar horizontalScrollBar = table.Children
                    .OfType<ScrollBar>()
                    .Single(scrollBar => scrollBar.Orientation == Orientation.Horizontal);
                double absoluteColumnLeft = table.Columns
                    .Where(candidate => candidate.IsVisible)
                    .OrderBy(candidate => candidate.DisplayIndex)
                    .TakeWhile(candidate => !ReferenceEquals(candidate, column))
                    .Sum(candidate => candidate.Width);
                horizontalScrollBar.Value = Math.Min(
                    horizontalScrollBar.Maximum,
                    Math.Max(0d, absoluteColumnLeft - 100d));
                table.UpdateLayout();
                IReadOnlyList<CustomTableColumn> visibleColumns = table.Columns
                    .Where(candidate => candidate.IsVisible)
                    .OrderBy(candidate => candidate.DisplayIndex)
                    .ToArray();
                double columnLeft = visibleColumns
                    .TakeWhile(candidate => !ReferenceEquals(candidate, column))
                    .Sum(candidate => candidate.Width)
                    - table.HorizontalOffset;
                double columnX = columnLeft + Math.Max(3d, column.Width / 2d);
                double columnY = table.HeaderHeight + Math.Max(3d, table.RowHeight / 2d);
                Point editPoint = new(columnX, columnY);
                CustomTableHitTestResult hit = table.HitTestTable(editPoint);
                Assert.AreEqual(
                    CustomTableHitKind.Cell,
                    hit.Kind,
                    $"x={columnX} y={columnY} offset={table.HorizontalOffset} surface={table.SurfaceWidth}x{table.SurfaceHeight} column={column.Id} width={column.Width} display={column.DisplayIndex} visible={string.Join(',', visibleColumns.Select(candidate => candidate.Id))}");
                Assert.AreSame(row, hit.Row);
                Assert.AreEqual("instl_dst", hit.Column?.EditPropertyName);

                RunWithWindowCursor(window, table, columnX, columnY, () =>
                {
                    Point actualPoint = Mouse.GetPosition(table);
                    CustomTableHitTestResult actualHit = table.HitTestTable(actualPoint);
                    Assert.AreEqual(CustomTableHitKind.Cell, actualHit.Kind, $"actual={actualPoint} expected={editPoint}");
                    RaiseCellClick(table);
                    Assert.AreEqual(0, table.SelectedIndex);
                    RaiseKey(table, Key.F2);
                    Assert.AreEqual(1, beginning.Count);
                    Assert.AreEqual(1, started.Count);
                    Assert.IsNotNull(GetInstalledEditor(table));
                    TextBox editor = GetInstalledEditor(table);
                    editor!.Text = expectedText;
                    RaiseKey(table, Key.Return);
                });

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
    }

    [TestMethod]
    public void CustomTableCellEditStartedSeesInstalledEditorAfterRealCellActivation()
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
            RunWithCursor(table, 20, 20, () =>
            {
                int beginningCount = 0;
                int startedCount = 0;
                int previewMouseCount = 0;
                int previewKeyCount = 0;
                TextBox editorSeenAtStarted = null;
                table.AddHandler(
                    UIElement.PreviewMouseLeftButtonDownEvent,
                    new MouseButtonEventHandler((_, _) => previewMouseCount++),
                    handledEventsToo: true);
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

                Point cursorPoint = Mouse.GetPosition(table);
                Assert.AreEqual(CustomTableHitKind.Cell, table.HitTestTable(cursorPoint).Kind, $"cursor={cursorPoint}");
                RaiseCellClick(table);
                Assert.AreEqual(1, previewMouseCount);
                Assert.AreEqual(0, table.SelectedIndex);
                RaiseKey(table, Key.F2);
                Assert.AreEqual(1, previewKeyCount);

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
                resourceHostWindow = new Window
                {
                    Width = tableWidth,
                    Height = tableHeight,
                    Content = table
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
                Assert.AreEqual(15d, verticalScrollBar.Width);
                Assert.AreEqual(15d, horizontalScrollBar.Height);
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
        MainWindowPresentationTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                TextBox keyword = GetNamedElement<TextBox>(window, "KeywordSearchBox");
                CustomTableView table = GetNamedElement<CustomTableView>(window, "customTableView");

                AssertBindingPath(keyword, TextBox.TextProperty, "ChartFilters.KeywordFilter");
                Assert.AreSame(viewModel.MainChartList, table.DataContext);
                AssertBindingPath(table, CustomTableView.ItemsSourceProperty, "Rows");
                AssertBindingPath(table, CustomTableView.SelectedIndexProperty, "SelectedIndex");

                keyword.Text = "wave6e-filter";
                BindingOperations.GetBindingExpression(keyword, TextBox.TextProperty)?.UpdateSource();
                Assert.AreEqual("wave6e-filter", viewModel.ChartFilters.KeywordFilter);
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

    private static TextBox? GetInstalledEditor(CustomTableView table)
        => table.Children
            .OfType<Canvas>()
            .SelectMany(canvas => canvas.Children.OfType<TextBox>())
            .SingleOrDefault();

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

    private static void RaiseCellClick(CustomTableView table)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
        };
        table.RaiseEvent(args);
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

    private static void RunWithCursor(CustomTableView table, double x, double y, Action action)
    {
        using TestProcessGlobalCursorScope cursorScope = TestProcessGlobalCursorScope.Enter();
        GetCursorPos(out NativePoint originalPosition);
        try
        {
            table.Focus();
            Mouse.Capture(table);
            Point screenPoint = table.PointToScreen(new Point(x, y));
            Assert.IsTrue(SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
            if (PresentationSource.FromVisual(table) is HwndSource source)
            {
                SendMessage(source.Handle, 0x0200, IntPtr.Zero, MakeLParam((int)Math.Round(x), (int)Math.Round(y)));
            }
            table.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { }));
            action();
        }
        finally
        {
            Mouse.Capture(null);
            SetCursorPos(originalPosition.X, originalPosition.Y);
        }
    }

    private static void RunWithWindowCursor(
        MainWindow window,
        CustomTableView table,
        double x,
        double y,
        Action action)
    {
        using TestProcessGlobalCursorScope cursorScope = TestProcessGlobalCursorScope.Enter();
        GetCursorPos(out NativePoint originalPosition);
        try
        {
            table.Focus();
            Assert.IsTrue(Mouse.Capture(table));
            Point tableOrigin = table.PointToScreen(new Point(0d, 0d));
            Point screenPoint = new(tableOrigin.X + x, tableOrigin.Y + y);
            Assert.IsTrue(SetCursorPos((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
            table.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { }));
            Point adjustedOrigin = table.PointToScreen(new Point(0d, 0d));
            Assert.IsTrue(SetCursorPos(
                (int)Math.Round(adjustedOrigin.X + x),
                (int)Math.Round(adjustedOrigin.Y + y)));
            table.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { }));
            if (PresentationSource.FromVisual((System.Windows.Media.Visual)window.Content) is HwndSource source)
            {
                Point screenPointForMessage = new(adjustedOrigin.X + x, adjustedOrigin.Y + y);
                Point rootPoint = ((System.Windows.Media.Visual)window.Content).PointFromScreen(screenPointForMessage);
                SendMessage(
                    source.Handle,
                    0x0200,
                    IntPtr.Zero,
                    MakeLParam((int)Math.Round(rootPoint.X), (int)Math.Round(rootPoint.Y)));
            }
            table.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { }));
            action();
        }
        finally
        {
            Mouse.Capture(null);
            SetCursorPos(originalPosition.X, originalPosition.Y);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

    private static IntPtr MakeLParam(int low, int high)
        => (IntPtr)((high << 16) | (low & 0xffff));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
