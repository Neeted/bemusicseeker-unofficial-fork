using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class CustomTablePhase6CacheTests
{
    [TestMethod]
    public void ColumnLayoutSnapshot_ResolvesVisibleColumnsAndResizeEdges()
    {
        CustomTableColumn[] columns =
        [
            CreateColumn("A", 50),
            CreateColumn("B", 80),
            CreateColumn("C", 30)
        ];

        var snapshot = CustomTableColumnLayoutSnapshot.Create(columns, horizontalOffset: 20d, viewportWidth: 100d);

        Assert.AreEqual(170d, snapshot.ExtentWidth);
        Assert.AreEqual(2, snapshot.VisibleColumnCount);
        Assert.IsTrue(snapshot.TryResolveColumn(60d, out CustomTableColumnLayoutEntry entry));
        Assert.AreEqual("B", entry.Column.Id);
        Assert.AreEqual(50d, entry.TableX);
        Assert.IsTrue(snapshot.TryResolveResizeColumn(30d, 4d, out CustomTableColumnLayoutEntry resizeEntry, out Rect resizeRect));
        Assert.AreEqual("A", resizeEntry.Column.Id);
        Assert.AreEqual(26d, resizeRect.X);
    }

    [TestMethod]
    public void ColumnLayoutSnapshot_MatchesOnlySameViewportAndColumns()
    {
        CustomTableColumn[] columns = [CreateColumn("A", 50)];
        var snapshot = CustomTableColumnLayoutSnapshot.Create(columns, 0d, 100d);

        Assert.IsTrue(snapshot.Matches(columns, 0d, 100d));
        Assert.IsFalse(snapshot.Matches(columns, 1d, 100d));
        Assert.IsFalse(snapshot.Matches(columns, 0d, 101d));
        Assert.IsFalse(snapshot.Matches([CreateColumn("A", 50)], 0d, 100d));
    }

    [TestMethod]
    public void CustomTableView_DataResetKeepsColumnLayoutSnapshot()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var view = new CustomTableView();
            CustomTableColumnLayoutSnapshot snapshot = view.GetColumnLayoutSnapshot();
            FieldInfo snapshotField = typeof(CustomTableView).GetField(
                "columnLayoutSnapshot",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo invalidationGenerationField = typeof(CustomTableView).GetField(
                "cellValueInvalidationGeneration",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo collectionChanged = typeof(CustomTableView).GetMethod(
                "ItemsSourceCollectionChanged",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            long generationBefore = (long)invalidationGenerationField.GetValue(view)!;
            collectionChanged.Invoke(
                view,
                [
                    new ObservableCollection<object>(),
                    new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)
                ]);

            Assert.AreSame(snapshot, snapshotField.GetValue(view));
            Assert.AreEqual(
                generationBefore + 1L,
                (long)invalidationGenerationField.GetValue(view)!);
        });
    }

    [TestMethod]
    public void CustomTableView_DataOnlySourceSwapKeepsColumnLayoutSnapshot()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var view = new CustomTableView
            {
                ItemsSource = new ObservableCollection<object> { new() }
            };
            CustomTableColumnLayoutSnapshot snapshot = view.GetColumnLayoutSnapshot();

            view.ItemsSource = new ObservableCollection<object> { new(), new() };

            Assert.AreSame(snapshot, view.GetColumnLayoutSnapshot());
        });
    }

    [TestMethod]
    public void CustomTableView_HiddenSourceSwapDoesNotInvalidateAgainWhenShown()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var view = new CustomTableView
            {
                ItemsSource = new ObservableCollection<object> { new() }
            };
            CustomTableColumnLayoutSnapshot snapshot = view.GetColumnLayoutSnapshot();
            FieldInfo invalidationGenerationField = typeof(CustomTableView).GetField(
                "cellValueInvalidationGeneration",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            view.ApplyVisibilityChanged(isVisible: false);

            view.ItemsSource = new ObservableCollection<object> { new(), new() };
            long generationAfterSwap = (long)invalidationGenerationField.GetValue(view)!;
            view.ApplyVisibilityChanged(isVisible: true);

            Assert.AreEqual(
                generationAfterSwap,
                (long)invalidationGenerationField.GetValue(view)!);
            Assert.AreSame(snapshot, view.GetColumnLayoutSnapshot());
        });
    }

    [TestMethod]
    public void CustomTableView_ShowAfterHiddenUntrackedMutationInvalidatesCellValuesOnce()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var view = new CustomTableView
            {
                ItemsSource = new ObservableCollection<object> { new() }
            };
            FieldInfo invalidationGenerationField = typeof(CustomTableView).GetField(
                "cellValueInvalidationGeneration",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            view.ApplyVisibilityChanged(isVisible: false);
            long generationWhileHidden = (long)invalidationGenerationField.GetValue(view)!;

            view.ApplyVisibilityChanged(isVisible: true);

            Assert.AreEqual(
                generationWhileHidden + 1L,
                (long)invalidationGenerationField.GetValue(view)!);
        });
    }

    [TestMethod]
    public void CustomTableView_CoercesAppearanceMetricsToSupportedRanges()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var view = new CustomTableView
            {
                TextFontSize = 2d,
                RowHeight = 2d,
                HeaderHeight = 2d
            };

            Assert.AreEqual(BeMusicSeeker.Properties.Settings.MinCustomTableFontSize, view.TextFontSize);
            Assert.AreEqual(BeMusicSeeker.Properties.Settings.MinCustomTableRowHeight, view.RowHeight);
            Assert.AreEqual(BeMusicSeeker.Properties.Settings.MinCustomTableHeaderHeight, view.HeaderHeight);

            view.TextFontSize = double.NaN;
            view.RowHeight = double.PositiveInfinity;
            view.HeaderHeight = double.NaN;

            Assert.AreEqual(BeMusicSeeker.Properties.Settings.DefaultCustomTableFontSize, view.TextFontSize);
            Assert.AreEqual(BeMusicSeeker.Properties.Settings.DefaultCustomTableRowHeight, view.RowHeight);
            Assert.AreEqual(BeMusicSeeker.Properties.Settings.DefaultCustomTableHeaderHeight, view.HeaderHeight);
        });
    }

    [TestMethod]
    public void CustomTableView_MainChartListLifecycleFollowsLoadedDataContext()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var first = new MainChartListViewModel();
            var second = new MainChartListViewModel();
            var view = new CustomTableView { DataContext = first };
            var window = new Window
            {
                Width = 320d,
                Height = 240d,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };

            try
            {
                windowTest.ShowAndWaitForContentRendered(window);
                Assert.IsTrue(view.IsLoaded);
                first.PrepareRowsReplacement();
                Assert.IsTrue(view.IsItemsSourceSwapPending);
                first.CancelRowsReplacement();
                Assert.IsFalse(view.IsItemsSourceSwapPending);

                view.DataContext = second;
                first.PrepareRowsReplacement();
                Assert.IsFalse(view.IsItemsSourceSwapPending, "Changing DataContext must detach the previous chart-list owner.");
                second.PrepareRowsReplacement();
                Assert.IsTrue(view.IsItemsSourceSwapPending);
                second.FailRowsReplacementPublish();
                Assert.IsFalse(view.IsItemsSourceSwapPending);
            }
            finally
            {
                window.Content = null;
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                window.Close();
            }

            second.PrepareRowsReplacement();
            Assert.IsFalse(view.IsItemsSourceSwapPending, "Unloaded controls must not retain the chart-list owner subscription.");
        });
    }

    [TestMethod]
    public void CellValueCache_ReusesValuesUntilRowInvalidated()
    {
        int textCalls = 0;
        int foregroundCalls = 0;
        int backgroundCalls = 0;
        object row = new();
        bool undefined = true;
        var column = new CustomTableColumn(
            "Title",
            "TITLE",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: delegate
            {
                textCalls++;
                return "hello";
            },
            foregroundSelector: delegate
            {
                foregroundCalls++;
                return Brushes.Red;
            },
            backgroundSelector: delegate
            {
                backgroundCalls++;
                return undefined ? Brushes.Yellow : null;
            },
            textStyle: CustomTableTextStyle.Score);
        var cache = new CustomTableCellValueCache();

        CustomTableCellValue first = cache.GetOrCreate(row, column, 0, 0, out bool firstHit);
        CustomTableCellValue second = cache.GetOrCreate(row, column, 0, 0, out bool secondHit);
        undefined = false;
        cache.InvalidateRow(row);
        CustomTableCellValue third = cache.GetOrCreate(row, column, 0, 0, out bool thirdHit);

        Assert.IsFalse(firstHit);
        Assert.IsTrue(secondHit);
        Assert.IsFalse(thirdHit);
        Assert.AreEqual("hello", first.Text);
        Assert.AreEqual("hello", second.Text);
        Assert.AreEqual("hello", third.Text);
        Assert.AreSame(Brushes.Yellow, first.Background);
        Assert.AreSame(Brushes.Yellow, second.Background);
        Assert.IsNull(third.Background);
        Assert.AreSame(CustomTableTextStyle.Score, first.TextStyle);
        Assert.AreSame(CustomTableTextStyle.Score, third.TextStyle);
        Assert.AreEqual(2, textCalls);
        Assert.AreEqual(2, foregroundCalls);
        Assert.AreEqual(2, backgroundCalls);
    }

    [TestMethod]
    public void CellValueCache_TreatsGenerationChangesAsMisses()
    {
        int textCalls = 0;
        object row = new();
        var column = new CustomTableColumn(
            "Title",
            "TITLE",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: delegate
            {
                textCalls++;
                return textCalls.ToString();
            });
        var cache = new CustomTableCellValueCache();

        CustomTableCellValue first = cache.GetOrCreate(row, column, 0, 0, out bool firstHit);
        CustomTableCellValue second = cache.GetOrCreate(row, column, 1, 0, out bool secondHit);
        CustomTableCellValue third = cache.GetOrCreate(row, column, 1, 1, out bool thirdHit);

        Assert.IsFalse(firstHit);
        Assert.IsFalse(secondHit);
        Assert.IsFalse(thirdHit);
        Assert.AreEqual("1", first.Text);
        Assert.AreEqual("2", second.Text);
        Assert.AreEqual("3", third.Text);
    }

    [TestMethod]
    public void CellValueCreate_NormalizesColoredTextCellsToTextRuns()
    {
        object row = new();
        var column = new CustomTableColumn(
            "Clear",
            "CLEAR",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Center,
            textSelector: _ => "HARD",
            foregroundSelector: _ => Brushes.Red);

        CustomTableCellValue value = CustomTableCellValue.Create(row, column);

        Assert.AreSame(Brushes.Red, value.Foreground);
        Assert.AreEqual(1, value.TextRuns.Count);
        Assert.AreEqual(0, value.TextRuns[0].StartIndex);
        Assert.AreEqual("HARD".Length, value.TextRuns[0].Length);
        Assert.AreSame(Brushes.Red, value.TextRuns[0].Foreground);
    }

    [TestMethod]
    public void CellValueCreate_KeepsDefaultForegroundAndNonTextCellsWithoutSyntheticTextRuns()
    {
        object row = new();
        var defaultTextColumn = new CustomTableColumn(
            "Title",
            "TITLE",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: _ => "title",
            foregroundSelector: _ => CustomTableScoreBrushProvider.DefaultForeground);
        var iconColumn = new CustomTableColumn(
            "Download",
            "DL",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Center,
            textSelector: _ => "download",
            foregroundSelector: _ => Brushes.Red,
            cellKind: CustomTableCellKind.DownloadIcon);
        var checkBoxColumn = new CustomTableColumn(
            "Checked",
            "CHECK",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Center,
            textSelector: _ => "checked",
            foregroundSelector: _ => Brushes.Red,
            checkedSelector: _ => true,
            cellKind: CustomTableCellKind.CheckBox);

        Assert.AreEqual(0, CustomTableCellValue.Create(row, defaultTextColumn).TextRuns.Count);
        if (CustomTableScoreBrushProvider.DefaultForeground is SolidColorBrush defaultForeground)
        {
            var equivalentDefaultTextColumn = new CustomTableColumn(
                "EquivalentTitle",
                "EQUIVALENT_TITLE",
                layout: null,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "title",
                foregroundSelector: _ => new SolidColorBrush(defaultForeground.Color));
            Assert.AreEqual(0, CustomTableCellValue.Create(row, equivalentDefaultTextColumn).TextRuns.Count);
        }

        Assert.AreEqual(0, CustomTableCellValue.Create(row, iconColumn).TextRuns.Count);
        Assert.AreEqual(0, CustomTableCellValue.Create(row, checkBoxColumn).TextRuns.Count);
    }

    [TestMethod]
    public void CellValueCreate_PreservesExplicitTextRuns()
    {
        object row = new();
        var column = new CustomTableColumn(
            "Delta",
            "DELTA",
            layout: null,
            fallbackOrder: 0,
            sortMemberPath: null,
            alignment: TextAlignment.Left,
            textSelector: _ => "A -> AA",
            foregroundSelector: _ => Brushes.Red,
            textRunSelector: (_, _) =>
            [
                new CustomTableTextRunStyle(0, 1, Brushes.Blue)
            ]);

        CustomTableCellValue value = CustomTableCellValue.Create(row, column);

        Assert.AreEqual(1, value.TextRuns.Count);
        Assert.AreEqual(0, value.TextRuns[0].StartIndex);
        Assert.AreEqual(1, value.TextRuns[0].Length);
        Assert.AreSame(Brushes.Blue, value.TextRuns[0].Foreground);
    }

    [TestMethod]
    public void CustomTableView_RendersCellTextRunsAndPreservesRunsForSelection()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            object row = new();
            var layout = new CustomTableColumnSettings.ColumnLayout
            {
                Width = 210,
                Visibility = Visibility.Visible
            };
            var column = new CustomTableColumn(
                "Delta",
                "DELTA",
                layout,
                fallbackOrder: 0,
                sortMemberPath: null,
                alignment: TextAlignment.Left,
                textSelector: _ => "MMMM    MMMM",
                foregroundSelector: _ => Brushes.Black,
                textRunSelector: (_, _) =>
                [
                    new CustomTableTextRunStyle(0, 4, Brushes.Red),
                    new CustomTableTextRunStyle(8, 4, Brushes.Blue)
                ]);

            RenderTable(row, column, selectedIndex: -1, currentCell: false, out int normalRedPixels, out int normalBluePixels);
            RenderTable(row, column, selectedIndex: 0, currentCell: false, out int selectedRedPixels, out int selectedBluePixels);
            RenderTable(row, column, selectedIndex: -1, currentCell: true, out int currentCellRedPixels, out int currentCellBluePixels);

            Assert.IsTrue(normalRedPixels > 0, "Expected red pixels when text runs are active.");
            Assert.IsTrue(normalBluePixels > 0, "Expected blue pixels when text runs are active.");
            Assert.IsTrue(selectedRedPixels > 0, "Selected rows should preserve explicit text-run foregrounds.");
            Assert.IsTrue(selectedBluePixels > 0, "Selected rows should preserve explicit text-run foregrounds.");
            Assert.IsTrue(currentCellRedPixels > 0, "Current cells should preserve explicit text-run foregrounds.");
            Assert.IsTrue(currentCellBluePixels > 0, "Current cells should preserve explicit text-run foregrounds.");
        });
    }

    [TestMethod]
    public void CustomTableView_CreatesWrappedTooltipContentWhenWidthIsSpecified()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            object plainContent = CustomTableView.CreateCellToolTipContent("short tooltip", null);
            object wrappedContent = CustomTableView.CreateCellToolTipContent("long tooltip", 420d);

            Assert.AreEqual("short tooltip", plainContent);
            Assert.IsInstanceOfType(wrappedContent, typeof(TextBlock));
            var textBlock = (TextBlock)wrappedContent;
            Assert.AreEqual("long tooltip", textBlock.Text);
            Assert.AreEqual(420d, textBlock.Width);
            Assert.AreEqual(TextWrapping.Wrap, textBlock.TextWrapping);
        });
    }

    private static CustomTableColumn CreateColumn(string id, int width)
    {
        var layout = new CustomTableColumnSettings.ColumnLayout
        {
            Width = width,
            Visibility = Visibility.Visible
        };
        return new CustomTableColumn(id, id, layout, 0, null, TextAlignment.Left, row => id);
    }

    private static void RenderTable(object row, CustomTableColumn column, int selectedIndex, bool currentCell, out int redPixels, out int bluePixels)
    {
        const int width = 240;
        const int height = 70;
        var table = new CustomTableView
        {
            Width = width,
            Height = height,
            HeaderHeight = 0d,
            RowHeight = 60d,
            Columns = [column],
            ItemsSource = new List<object> { row },
            SelectedIndex = selectedIndex
        };
        table.Measure(new Size(width, height));
        table.Arrange(new Rect(0, 0, width, height));
        table.UpdateLayout();
        if (currentCell)
        {
            CustomTableHitTestResult hit = table.HitTestTable(new Point(10d, 30d));
            typeof(CustomTableView)
                .GetField("currentCellHit", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(table, hit);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(table);
        CountDominantPixels(bitmap, out redPixels, out bluePixels);
    }

    private static void CountDominantPixels(BitmapSource bitmap, out int redPixels, out int bluePixels)
    {
        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        byte[] pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        redPixels = 0;
        bluePixels = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = ((y * width) + x) * 4;
                byte blue = pixels[offset];
                byte green = pixels[offset + 1];
                byte red = pixels[offset + 2];
                if (red > 120 && green < 100 && blue < 100)
                {
                    redPixels++;
                }
                if (blue > 120 && green < 100 && red < 100)
                {
                    bluePixels++;
                }
            }
        }
    }

}
