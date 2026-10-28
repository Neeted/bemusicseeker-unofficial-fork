using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <remarks>実表のキー受付・現在セル・配置とスクロールの接続が対象のためWPFを使います。Windowや利用者設定は使わず、条件はテスト所有の行と列で作ります。</remarks>
[TestClass]
public sealed class CustomTableNavigationTests
{
    /// <summary>実Ctrl+Cが実履歴列の現在行の値を使い、複数選択の副行や交換前sourceを使わないことを確認します。factoryの生値を記録した専用例外でClipboard前に止め、キー配送と値投影を分担します。</summary>
    [DataTestMethod]
    [DataRow("Hash")]
    [DataRow("Sha256")]
    public void PlayHistoryCopyCurrentCell_UsesLatestSelectedRowAndReplacementSource(string columnId)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string[] hashes = [new string('a', 32), new string('b', 32), new string('e', 32)];
            string[] sha256 = [new string('c', 64), new string('d', 64), new string('f', 64)];
            PlayHistoryProjectionIndex index = PlayHistoryCatalogTestSupport.Create([], hashes.Select((hash, i) => (hash, sha: sha256[i])).ToDictionary(pair => pair.hash, pair => pair.sha));
            PlayHistoryRow[] rows = [.. PlayHistoryRow.ProjectLr2Rows(new Lr2PlayHistoryReadResult(PlayHistorySourceProfile.Lr2("score.db"),
                [.. hashes.Select((hash, i) => new Lr2PlayHistoryRecord { hash = hash, history_id = i + 1, played_at = 1000 + i })],
                [], Lr2PlayHistorySchemaStatus.Installed), index).Rows];
            var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
            settings.Hash.Visibility = settings.Sha256.Visibility = Visibility.Visible;
            CustomTableColumn actual = CustomTableColumnFactory.CreateMainColumns(settings).Single(column => column.Id == columnId);
            var visits = new List<(object Row, string Text)>();
            var sentinel = new InvalidOperationException("現在セルの観測を終え、Clipboard前で停止します。");
            var probe = new CustomTableColumn(actual.Id, actual.Header, actual.Layout, actual.FallbackOrder, actual.SortMemberPath, actual.Alignment,
                actual.GetText, editTextSelector: row => { visits.Add((row, actual.GetEditText(row))); throw sentinel; });
            CustomTableView table = CreateTable([probe]);
            try
            {
                table.ItemsSource = new[] { rows[0], rows[1] };
                table.SelectedIndex = 0; Copy(0);
                table.SelectRowsByPredicate(_ => true, row => ReferenceEquals(row, rows[1])); Copy(1);
                table.ItemsSource = new[] { rows[2] };
                table.SelectedIndex = -1;
                table.SelectedIndex = 0; Copy(2);

                void Copy(int expected)
                {
                    object[] selection = [.. table.GetSelectedRowsSnapshot()];
                    int primary = table.SelectedIndex;
                    int count = visits.Count;
                    Assert.AreSame(sentinel, Assert.ThrowsException<InvalidOperationException>(() => table.HandleKeyDown(Key.C, ModifierKeys.Control)));
                    Assert.AreEqual(count + 1, visits.Count); Assert.AreSame(rows[expected], visits[^1].Row);
                    Assert.AreEqual(columnId == "Hash" ? hashes[expected] : sha256[expected], visits[^1].Text);
                    Assert.AreEqual(primary, table.SelectedIndex); CollectionAssert.AreEqual(selection, table.GetSelectedRowsSnapshot().ToArray());
                }
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>実Ctrl+Shift+Cが空値の先頭選択行も可視列順でTSVへ渡し、非選択行を読まず選択を保つことを確認します。主行の最終列で専用例外を送出し、Ctrl+Cへの誤接続時もOS Clipboardへ到達させません。</summary>
    [TestMethod]
    public void CopySelectedRowsShortcut_VisitsEmptyAndNonemptySelectedRowsInVisibleColumnOrder()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            object[] rows = [string.Empty, "Unselected", "Song"];
            var visits = new List<string>();
            var clipboardSentinel = new InvalidOperationException("TSVの対象観測を終えたためClipboard前で停止します。");
            CustomTableColumn MakeColumn(string id, int index) => new(
                id, id, new CustomTableColumnSettings.ColumnLayout { Width = 60, DisplayIndex = index },
                index, null, TextAlignment.Left, _ => string.Empty,
                editTextSelector: row =>
                {
                    int rowIndex = Array.IndexOf(rows, row);
                    visits.Add($"{rowIndex}:{id}");
                    Assert.AreNotEqual(1, rowIndex, "非選択行をコピーしました。");
                    if (rowIndex == 2 && id == "Last")
                    {
                        throw clipboardSentinel;
                    }
                    return row as string;
                });
            CustomTableView table = CreateTable([MakeColumn("First", 0), MakeColumn("Last", 1)]);
            try
            {
                table.ItemsSource = rows;
                table.SelectRowsByPredicate(row => ReferenceEquals(row, rows[0]) || ReferenceEquals(row, rows[2]), row => ReferenceEquals(row, rows[2]));
                Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
                AssertCurrentCell(table, 2, "Last");
                object[] selectedRows = [.. table.GetSelectedRowsSnapshot()];
                int selectionNotifications = 0;
                table.SelectionChanged += (_, _) => selectionNotifications++;

                InvalidOperationException thrown = Assert.ThrowsException<InvalidOperationException>(
                    () => table.HandleKeyDown(Key.C, ModifierKeys.Control | ModifierKeys.Shift));

                Assert.AreSame(clipboardSentinel, thrown);
                CollectionAssert.AreEqual(new[] { "0:First", "0:Last", "2:First", "2:Last" }, visits);
                CollectionAssert.AreEqual(selectedRows, table.GetSelectedRowsSnapshot().ToArray());
                Assert.AreEqual(2, table.SelectedIndex);
                AssertCurrentCell(table, 2, "Last");
                Assert.AreEqual(0, selectionNotifications);
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>外部主行変更の入力直後に同列と画面外の対象行を可視化し、同値では手動scrollを保ちます。消えた列・0列・解除・空行の古いhitを使わないことも確認します。</summary>
    [TestMethod]
    public void ExternalSelection_RevealsCurrentRowAndColumnOnlyWhenPrimaryChanges()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable(CreateColumns(60, 60, 60, 60));
            table.ItemsSource = Enumerable.Range(0, 12).Select(_ => new object()).ToArray();
            try
            {
                Assert.IsTrue(PressCell(table, 0, 1, MouseButton.Left));
                table.SelectedIndex = 11;
                AssertCurrentCell(table, 11, "C1");
                Assert.AreEqual(11 - table.CalculateFullVisibleRowCapacity() + 1, table.FirstVisibleRowIndex);
                ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);
                vertical.Value = 0d;
                HorizontalScrollBar(table).Value = 40d;
                table.SelectedIndex = 11;
                AssertCurrentCell(table, 11, "C1");
                Assert.AreEqual(0, table.FirstVisibleRowIndex);
                Assert.AreEqual(40d, table.HorizontalOffset);

                table.Columns = CreateColumns(60);
                table.SelectedIndex = 10;
                AssertCurrentCell(table, 10, "C0");
                table.Columns = Array.Empty<CustomTableColumn>();
                table.SelectedIndex = 9;
                Assert.IsNull(table.GetCurrentCellColumn(9));
                table.Columns = CreateColumns(60, 60);
                table.SelectedIndex = 8;
                AssertCurrentCell(table, 8, "C0");
                table.SelectedIndex = -1;
                Assert.IsNull(table.GetCurrentCellColumn(8));
                Assert.AreEqual(0, table.GetSelectedRowsSnapshot().Count);
                table.ItemsSource = Array.Empty<object>();
                table.SelectedIndex = 0;
                Assert.IsNull(table.GetCurrentCellColumn(0));
                Assert.AreEqual(0, table.GetSelectedRowsSnapshot().Count);
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>Ctrl+A後のShift下と末尾クリックは同じ集合の主行変更を一回通知し、通知内の現在セルも同期することを確認します。反復と列だけの移動は無通知で、次のShift上は先頭基点から縮みます。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RangeSelection_AfterSelectAllNotifiesCurrentRowOnceAndPreservesAnchor(bool mouseInput)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable(CreateColumns(60, 60));
            object[] rows = [new(), new()];
            table.ItemsSource = rows;
            int notifications = 0;
            int expectedCurrent = 1;
            string expectedColumn = "C0";
            object[] expectedSelection = rows;
            EventHandler<CustomTableSelectionChangedEventArgs> onSelection = (_, args) =>
            {
                notifications++;
                Assert.AreEqual(expectedCurrent, args.SelectedIndex);
                Assert.AreEqual(expectedCurrent, table.SelectedIndex);
                Assert.AreSame(rows[expectedCurrent], args.SelectedRow);
                CollectionAssert.AreEqual(expectedSelection, args.SelectedRows.ToArray());
                AssertCurrentCell(table, expectedCurrent, expectedColumn);
            };
            try
            {
                Assert.IsTrue(PressCell(table, 0, 0, MouseButton.Left));
                Assert.IsTrue(table.HandleKeyDown(Key.A, ModifierKeys.Control));
                table.SelectionChanged += onSelection;
                bool ExtendToLast() => mouseInput
                    ? PressCell(table, 1, 0, MouseButton.Left, ModifierKeys.Shift)
                    : table.HandleKeyDown(Key.Down, ModifierKeys.Shift);

                Assert.IsTrue(ExtendToLast());
                Assert.AreEqual(1, notifications);
                CollectionAssert.AreEqual(rows, table.GetSelectedRowsSnapshot().ToArray());
                AssertCurrentCell(table, 1, "C0");
                Assert.IsTrue(ExtendToLast());
                Assert.AreEqual(1, notifications);
                Assert.IsTrue(PressCell(table, 1, 1, MouseButton.Right));
                AssertCurrentCell(table, 1, "C1");
                Assert.AreEqual(1, notifications);

                expectedCurrent = 0;
                expectedColumn = "C1";
                expectedSelection = [rows[0]];
                Assert.IsTrue(table.HandleKeyDown(Key.Up, ModifierKeys.Shift));
                Assert.AreEqual(2, notifications);
                CollectionAssert.AreEqual(expectedSelection, table.GetSelectedRowsSnapshot().ToArray());
                AssertCurrentCell(table, 0, "C1");
            }
            finally
            {
                table.SelectionChanged -= onSelection;
                table.ItemsSource = null;
            }
        });
    }

    /// <summary>表示順と非表示を反映して現在セルだけを移し、複数選択・現在行・選択通知・範囲基点を保つことを確認し、左右を行選択へ接続する誤りを検出します。</summary>
    [TestMethod]
    public void HorizontalKeys_FollowVisibleOrderAndPreserveSelectionAndRangeAnchor()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var settings = new CustomTableColumnSettings();
            foreach (CustomTableColumnSettings.ColumnLayout layout in CustomTableColumnFactory.EnumerateMainColumnLayouts(settings))
            {
                layout.Visibility = Visibility.Hidden;
            }
            settings.ChartJudge.Visibility = Visibility.Visible;
            settings.ChartJudge.DisplayIndex = 0;
            settings.Title.Visibility = Visibility.Visible;
            settings.Title.DisplayIndex = 1;
            settings.Artist.Visibility = Visibility.Visible;
            settings.Artist.DisplayIndex = 2;
            CustomTableColumn[] columns = [.. CustomTableColumnFactory.CreateMainColumns(settings)];
            CollectionAssert.AreEqual(new[] { "ChartJudge", "Title", "Artist" }, columns.Select(column => column.Id).ToArray());
            CustomTableView table = CreateTable(columns);
            object[] rows = (object[])table.ItemsSource;
            table.SelectRowsByPredicate(row => ReferenceEquals(row, rows[0]) || ReferenceEquals(row, rows[2]), row => ReferenceEquals(row, rows[2]));
            int notifications = 0;
            table.SelectionChanged += (_, _) => notifications++;
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            AssertCurrentCell(table, 2, "Title");
            Assert.IsTrue(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            AssertCurrentCell(table, 2, "ChartJudge");
            Assert.IsTrue(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            Assert.AreEqual(2, table.SelectedIndex);
            CollectionAssert.AreEqual(new[] { rows[0], rows[2] }, table.GetSelectedRowsSnapshot().ToArray());
            Assert.AreEqual(0, notifications);
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.Shift));
            CollectionAssert.AreEqual(new[] { rows[2], rows[3] }, table.GetSelectedRowsSnapshot().ToArray());
            AssertCurrentCell(table, 3, "ChartJudge");
            Assert.AreEqual(1, notifications);
            // Shift下で基点2と現在行3を分けてから左右を操作し、基点を現在行へリセットする誤りを検出します。
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            AssertCurrentCell(table, 3, "Title");
            Assert.IsTrue(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            AssertCurrentCell(table, 3, "ChartJudge");
            Assert.AreEqual(3, table.SelectedIndex);
            CollectionAssert.AreEqual(new[] { rows[2], rows[3] }, table.GetSelectedRowsSnapshot().ToArray());
            Assert.AreEqual(1, notifications);
            Assert.IsTrue(table.HandleKeyDown(Key.Up, ModifierKeys.Shift));
            CollectionAssert.AreEqual(new[] { rows[2] }, table.GetSelectedRowsSnapshot().ToArray());
            AssertCurrentCell(table, 2, "ChartJudge");
            Assert.AreEqual(2, table.SelectedIndex);
            Assert.AreEqual(2, notifications);
            table.ItemsSource = null;
        });
    }

    /// <summary>現在セルなし・古い行・列/データ差替えでは方向別の端から始め、端列では画面外でもスクロールせず、修飾付き入力は受理しないことを確認します。</summary>
    [TestMethod]
    public void HorizontalKeys_StartAtDirectionalEdgeAndKeepBoundaryAndModifiedInputsUnchanged()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable(CreateColumns(100, 120, 320, 80, 80));
            table.SelectedIndex = 0;
            table.Columns = table.Columns.ToArray();
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            AssertCurrentCell(table, 0, "C0");
            ScrollBar scroll = HorizontalScrollBar(table);
            scroll.Value = 400d;
            Assert.IsTrue(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            Assert.AreEqual(400d, table.HorizontalOffset);
            AssertCurrentCell(table, 0, "C0");
            foreach (ModifierKeys modifier in new[] { ModifierKeys.Control, ModifierKeys.Shift, ModifierKeys.Alt })
            {
                Assert.IsFalse(table.HandleKeyDown(Key.Right, modifier));
                Assert.IsFalse(table.HandleKeyDown(Key.Left, modifier));
            }
            Assert.AreEqual(400d, table.HorizontalOffset);
            AssertCurrentCell(table, 0, "C0");
            // 外部主行変更は入力直後に同列へ追従します。列交換後の左右は現在セルなしの方向端を使います。
            table.SelectedIndex = 1;
            AssertCurrentCell(table, 1, "C0");
            table.SelectedIndex = 2;
            AssertCurrentCell(table, 2, "C0");
            table.Columns = table.Columns.ToArray();
            Assert.IsTrue(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            AssertCurrentCell(table, 2, "C4");
            scroll.Value = 0d;
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            Assert.AreEqual(0d, table.HorizontalOffset);
            AssertCurrentCell(table, 2, "C4");
            table.Columns = CreateColumns(100, 120, 320, 80, 80);
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            AssertCurrentCell(table, 2, "C0");
            table.ItemsSource = new object[] { new(), new(), new(), new() };
            Assert.IsTrue(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            AssertCurrentCell(table, 2, "C4");
            // 縦の手動スクロールでも識別を保ち、外向きの端入力では再表示しません。
            table.Columns = CreateColumns(100, 120, 320);
            table.SelectedIndex = 0;
            table.Columns = table.Columns.ToArray();
            table.Height = 70d;
            table.Measure(new Size(200d, 70d));
            table.Arrange(new Rect(0d, 0d, 200d, 70d));
            table.UpdateLayout();
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);
            vertical.Value = 2d;
            AssertCurrentCell(table, 0, "C0");
            Assert.IsTrue(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            Assert.AreEqual(2d, vertical.Value);
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            Assert.AreEqual(0d, vertical.Value);
            AssertCurrentCell(table, 0, "C1");
            Assert.AreEqual(0, table.SelectedIndex);
            table.ItemsSource = null;
        });
    }

    /// <summary>未選択・空行・表示列なしは不受理で現在セルと横位置を変えず、未配置の幅ゼロでも非有限値や無効な現在セルを作らないことを確認します。</summary>
    [TestMethod]
    public void HorizontalKeys_RejectMissingTargetsAndKeepZeroWidthSafe()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable(CreateColumns(100, 120, 320));
            table.SelectedIndex = 0;
            table.Columns = table.Columns.ToArray();
            Assert.IsTrue(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            double offset = table.HorizontalOffset;
            table.ClearSelection();
            Assert.IsFalse(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            Assert.IsFalse(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            AssertCurrentCell(table, 0, "C2");
            Assert.AreEqual(offset, table.HorizontalOffset);
            table.ItemsSource = Array.Empty<object>();
            offset = table.HorizontalOffset;
            Assert.IsFalse(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            Assert.IsFalse(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            Assert.AreEqual(offset, table.HorizontalOffset);
            table.ItemsSource = new object[] { new() };
            table.SelectedIndex = 0;
            table.Columns = Array.Empty<CustomTableColumn>();
            offset = table.HorizontalOffset;
            Assert.IsFalse(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            Assert.IsFalse(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            Assert.AreEqual(offset, table.HorizontalOffset);
            var unarranged = new CustomTableView { Columns = CreateColumns(100, 120), ItemsSource = new object[] { new() }, SelectedIndex = 0 };
            unarranged.Columns = unarranged.Columns.ToArray();
            foreach (Key key in new[] { Key.Left, Key.Right })
            {
                unarranged.HandleKeyDown(key, ModifierKeys.None);
                Assert.IsTrue(double.IsFinite(unarranged.HorizontalOffset));
                Assert.IsFalse(unarranged.VisibleColumns.Any(column => unarranged.IsCurrentCell(0, column)));
            }
            unarranged.ItemsSource = null;
            table.ItemsSource = null;
        });
    }

    /// <summary>狭列は必要最小量、広列は左右の進入方向の端を表示し、上下は同じ列を保って部分可視を維持・完全画面外を再表示することを確認し、一律左寄せや先頭列への退行を検出します。</summary>
    [TestMethod]
    public void Navigation_RevealsNarrowAndWideColumnsAndKeepsColumnAcrossRows()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable(CreateColumns(100, 120, 320, 80, 80));
            Assert.AreEqual(200d, table.SurfaceWidth);
            table.SelectedIndex = 0;
            table.Columns = table.Columns.ToArray();
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            Assert.AreEqual(0d, table.HorizontalOffset);
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            AssertCurrentCell(table, 0, "C1");
            Assert.AreEqual(20d, table.HorizontalOffset);
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.AreEqual(20d, table.HorizontalOffset);
            AssertCurrentCell(table, 1, "C1");
            ScrollBar scroll = HorizontalScrollBar(table);
            scroll.Value = 300d;
            Assert.IsTrue(table.HandleKeyDown(Key.Up, ModifierKeys.None));
            Assert.AreEqual(100d, table.HorizontalOffset);
            AssertCurrentCell(table, 0, "C1");
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            Assert.AreEqual(220d, table.HorizontalOffset);
            AssertCurrentCell(table, 0, "C2");
            scroll.Value = 300d;
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.AreEqual(300d, table.HorizontalOffset);
            AssertCurrentCell(table, 1, "C2");
            scroll.Value = 0d;
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.Shift));
            Assert.AreEqual(220d, table.HorizontalOffset);
            AssertCurrentCell(table, 2, "C2");
            // 列の右端が画面左端に一致する完全画面外の条件は別の広列で作ります。
            table.Columns = CreateColumns(100, 120, 320, 400);
            table.SelectedIndex = 0;
            table.Columns = table.Columns.ToArray();
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            scroll.Value = 540d;
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.AreEqual(340d, table.HorizontalOffset);
            AssertCurrentCell(table, 1, "C2");
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            Assert.AreEqual(540d, table.HorizontalOffset);
            Assert.IsTrue(table.HandleKeyDown(Key.Left, ModifierKeys.None));
            Assert.AreEqual(340d, table.HorizontalOffset);
            AssertCurrentCell(table, 1, "C2");
            scroll.Value = 0d;
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            AssertCurrentCell(table, 1, "C3");
            Assert.AreEqual(540d, table.HorizontalOffset);
            // 消えた列は引き継がず、上下の既存方針に従って先頭列へ戻します。
            table.Columns = CreateColumns(100, 120);
            Assert.IsTrue(table.HandleKeyDown(Key.Up, ModifierKeys.None));
            AssertCurrentCell(table, 0, "C0");
            table.ItemsSource = null;
        });
    }

    /// <summary>スクロールバーを入力元にした左右キーと左右ボタン押下は表のPreview経路で受理されず、現在セルと選択・横位置を保つことを確認し、表が子のキーを奪う誤りを検出します。</summary>
    [TestMethod]
    public void PreviewHorizontalKey_FromScrollBarDoesNotNavigateTable()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable(CreateColumns(100, 120, 320));
            table.SelectedIndex = 0;
            table.Columns = table.Columns.ToArray();
            Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            var source = new NavigationInputSource(table);
            foreach (ScrollBar scroll in table.Children.OfType<ScrollBar>())
            {
                foreach (MouseButton button in new[] { MouseButton.Left, MouseButton.Right })
                {
                    var mouseArgs = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
                    {
                        RoutedEvent = button == MouseButton.Left ? UIElement.PreviewMouseLeftButtonDownEvent : UIElement.PreviewMouseRightButtonDownEvent,
                        Source = scroll
                    };
                    scroll.RaiseEvent(mouseArgs);
                    Assert.IsFalse(mouseArgs.Handled);
                    AssertCurrentCell(table, 0, "C0");
                    Assert.AreEqual(0, table.SelectedIndex);
                    Assert.AreEqual(0d, table.HorizontalOffset);
                }
                foreach (Key key in new[] { Key.Left, Key.Right })
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = scroll };
                    scroll.RaiseEvent(args);
                    Assert.IsFalse(args.Handled);
                    AssertCurrentCell(table, 0, "C0");
                    Assert.AreEqual(0, table.SelectedIndex);
                    Assert.AreEqual(0d, table.HorizontalOffset);
                }
            }
            table.ItemsSource = null;
        });
    }

    /// <summary>100×120・二列各60・六行の実キー受付から最終セルへ移り、右端x84と最終行容量4を使うことを確認し、バー背後へ残す露出判定を検出します。</summary>
    [TestMethod]
    public void Navigation_RevealsLastColumnAndRowBeforeOverlayBars()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var table = new CustomTableView
            {
                Width = 100d,
                Height = 120d,
                HeaderHeight = 20d,
                RowHeight = 20d,
                Columns = CreateColumns(60, 60),
                ItemsSource = Enumerable.Range(0, 6).Select(_ => new object()).ToArray(),
                SelectedIndex = 0
            };
            try
            {
                table.Measure(new Size(100d, 120d));
                table.Arrange(new Rect(0d, 0d, 100d, 120d));
                table.UpdateLayout();
                Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
                Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
                Assert.AreEqual(36d, table.HorizontalOffset);
                CustomTableColumnLayoutEntry last = table.GetColumnLayoutSnapshot().Entries.Last();
                Assert.AreEqual(84d, last.TableX + last.Width - table.HorizontalOffset);
                for (int i = 0; i < 5; i++)
                {
                    Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
                }
                AssertCurrentCell(table, 5, "C1");
                Assert.AreEqual(2, table.FirstVisibleRowIndex);
                Assert.AreEqual(4, table.CalculateFullVisibleRowCapacity());
                Assert.AreEqual(36d, table.HorizontalOffset);
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>左Aから右Bへ移った直後とメニュー要求内で行・操作列を揃え、選択済みBの再入力では集合と基点を保つことを確認し、旧セル残存と同じ行の列変更漏れを検出します。</summary>
    [TestMethod]
    public void MouseSelection_RightClickSynchronizesCellBeforeMenuAndPreservesRangeAnchor()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable(CreateColumns(60, 60, 60));
            object[] rows = (object[])table.ItemsSource;
            int menuRequests = 0;
            int notifications = 0;
            EventHandler<CustomTableRowRequestedEventArgs> onMenu = (_, args) =>
            {
                menuRequests++;
                Assert.AreEqual(2, table.SelectedIndex);
                Assert.AreSame(rows[2], args.Hit.Row);
                AssertCurrentCell(table, 2, args.Hit.Column.Id);
            };
            table.RowContextMenuRequested += onMenu;
            EventHandler<CustomTableSelectionChangedEventArgs> onSelection = (_, _) => notifications++;
            table.SelectionChanged += onSelection;
            try
            {
                Assert.IsTrue(PressCell(table, 0, 0, MouseButton.Left));
                Assert.IsTrue(PressCell(table, 2, 1, MouseButton.Right));
                AssertCurrentCell(table, 2, "C1");
                CollectionAssert.AreEqual(new[] { rows[2] }, table.GetSelectedRowsSnapshot().ToArray());
                Assert.IsTrue(PressCell(table, 0, 0, MouseButton.Left, ModifierKeys.Control));
                Assert.IsTrue(PressCell(table, 2, 1, MouseButton.Right));
                CollectionAssert.AreEqual(new[] { rows[0], rows[2] }, table.GetSelectedRowsSnapshot().ToArray());
                int beforeColumnChange = notifications;
                Assert.IsTrue(PressCell(table, 2, 2, MouseButton.Right));
                AssertCurrentCell(table, 2, "C2");
                Assert.AreEqual(beforeColumnChange, notifications);
                // 右クリックだけを挟んだShift上で、Ctrl追加時の基点0を直接確認します。
                Assert.IsTrue(table.HandleKeyDown(Key.Up, ModifierKeys.Shift));
                CollectionAssert.AreEqual(new[] { rows[0], rows[1] }, table.GetSelectedRowsSnapshot().ToArray());
                AssertCurrentCell(table, 1, "C2");
                int beforeLeftColumnChange = notifications;
                Assert.IsTrue(PressCell(table, 1, 1, MouseButton.Left));
                AssertCurrentCell(table, 1, "C1");
                CollectionAssert.AreEqual(new[] { rows[0], rows[1] }, table.GetSelectedRowsSnapshot().ToArray());
                Assert.AreEqual(beforeLeftColumnChange, notifications);
                Assert.AreEqual(3, menuRequests);
            }
            finally
            {
                table.RowContextMenuRequested -= onMenu;
                table.SelectionChanged -= onSelection;
                table.ItemsSource = null;
            }
        });
    }

    /// <summary>Ctrl解除後は画面内外の残行へ操作列を揃え、全解除はセルも解除し、空白入力と同期時の縦横位置を保つことを確認し、解除行のセル残存や先頭列への退行を検出します。</summary>
    [TestMethod]
    public void MouseSelection_ControlRemovalFollowsCurrentRowAndClearsCellWhenEmpty()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable(CreateColumns(60, 60, 60));
            object[] rows = (object[])table.ItemsSource;
            try
            {
                Assert.IsTrue(PressCell(table, 2, 1, MouseButton.Left));
                CustomTableHitTestResult blank = table.HitTestTable(new Point(190d, table.HeaderHeight + table.RowHeight / 2d));
                Assert.AreEqual(CustomTableHitKind.Empty, blank.Kind);
                foreach (MouseButton button in new[] { MouseButton.Left, MouseButton.Right })
                {
                    Assert.IsFalse(table.HandleCellMouseDown(blank, button, ModifierKeys.None, 1, default));
                    AssertCurrentCell(table, 2, "C1");
                    Assert.AreEqual(2, table.SelectedIndex);
                }
                Assert.IsTrue(PressCell(table, 0, 2, MouseButton.Left, ModifierKeys.Control));
                Assert.IsTrue(PressCell(table, 0, 1, MouseButton.Left, ModifierKeys.Control));
                Assert.AreEqual(2, table.SelectedIndex);
                CollectionAssert.AreEqual(new[] { rows[2] }, table.GetSelectedRowsSnapshot().ToArray());
                AssertCurrentCell(table, 2, "C1");
                Assert.IsTrue(PressCell(table, 2, 2, MouseButton.Left, ModifierKeys.Control));
                Assert.AreEqual(-1, table.SelectedIndex);
                Assert.AreEqual(0, table.GetSelectedRowsSnapshot().Count);
                AssertCurrentCell(table, -1, string.Empty);

                // 残る行0を画面外へ送り、可視の行2をCtrl追加・解除しても行0の操作列を保持します。
                table.Columns = CreateColumns(60, 60, 60, 60);
                table.Height = 70d;
                table.Measure(new Size(200d, 70d));
                table.Arrange(new Rect(0d, 0d, 200d, 70d));
                table.UpdateLayout();
                ScrollBar horizontal = HorizontalScrollBar(table);
                ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);
                horizontal.Value = 20d;
                Assert.IsTrue(PressCell(table, 0, 1, MouseButton.Left));
                vertical.Value = 2d;
                Assert.AreEqual(2, table.FirstVisibleRowIndex);
                Assert.IsTrue(PressCell(table, 2, 2, MouseButton.Left, ModifierKeys.Control));
                Assert.IsTrue(PressCell(table, 2, 1, MouseButton.Left, ModifierKeys.Control));
                Assert.AreEqual(0, table.SelectedIndex);
                CollectionAssert.AreEqual(new[] { rows[0] }, table.GetSelectedRowsSnapshot().ToArray());
                AssertCurrentCell(table, 0, "C1");
                Assert.AreEqual(2d, vertical.Value);
                Assert.AreEqual(20d, table.HorizontalOffset);
                vertical.Value = 0d;
                Assert.IsTrue(PressCell(table, 0, 2, MouseButton.Left, ModifierKeys.Control));
                Assert.AreEqual(-1, table.SelectedIndex);
                Assert.AreEqual(0, table.GetSelectedRowsSnapshot().Count);
                AssertCurrentCell(table, -1, string.Empty);
                Assert.AreEqual(0d, vertical.Value);
                Assert.AreEqual(20d, table.HorizontalOffset);
            }
            finally { table.ItemsSource = null; }
        });
    }

    // 表の実座標からヒットを作り、物理入力状態に依存せず本番のセル押下処理へ渡します。
    private static bool PressCell(CustomTableView table, int rowIndex, int columnIndex, MouseButton button, ModifierKeys modifiers = ModifierKeys.None)
    {
        double x = columnIndex * 60d + 30d - table.HorizontalOffset;
        var point = new Point(x, table.HeaderHeight + (rowIndex - table.FirstVisibleRowIndex + 0.5d) * table.RowHeight);
        CustomTableHitTestResult hit = table.HitTestTable(point);
        Assert.AreEqual(CustomTableHitKind.Cell, hit.Kind);
        Assert.AreEqual(rowIndex, hit.RowIndex);
        Assert.AreSame(table.VisibleColumns[columnIndex], hit.Column);
        return table.HandleCellMouseDown(hit, button, modifiers, 1, point);
    }

    // KeyEventArgsは非nullの入力元を要求します。描画先やHWNDは使わず、入力元以外の実表とルーティングを動かします。
    private sealed class NavigationInputSource(Visual root) : PresentationSource
    {
        public override Visual RootVisual { get; set; } = root;

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore()
            => throw new NotSupportedException("この入力元では描画先を使用しません。");
    }

    private static CustomTableColumn[] CreateColumns(params int[] widths)
    {
        return widths.Select((width, index) => new CustomTableColumn(
            "C" + index, "C" + index, new CustomTableColumnSettings.ColumnLayout { Width = width }, index,
            string.Empty, TextAlignment.Left, _ => string.Empty)).ToArray();
    }

    private static CustomTableView CreateTable(CustomTableColumn[] columns)
    {
        var table = new CustomTableView
        {
            Width = 200d,
            Height = 130d,
            Columns = columns,
            ItemsSource = new object[] { new(), new(), new(), new() }
        };
        table.Measure(new Size(200d, 130d));
        table.Arrange(new Rect(0d, 0d, 200d, 130d));
        table.UpdateLayout();
        return table;
    }

    private static ScrollBar HorizontalScrollBar(CustomTableView table)
        => table.Children.OfType<ScrollBar>().Single(scroll => scroll.Orientation == System.Windows.Controls.Orientation.Horizontal);

    private static void AssertCurrentCell(CustomTableView table, int rowIndex, string columnId)
    {
        foreach (int row in Enumerable.Range(0, table.RowCount))
        {
            foreach (CustomTableColumn column in table.VisibleColumns)
            {
                Assert.AreEqual(row == rowIndex && column.Id == columnId, table.IsCurrentCell(row, column), $"row={row}, column={column.Id}");
            }
        }
    }
}
