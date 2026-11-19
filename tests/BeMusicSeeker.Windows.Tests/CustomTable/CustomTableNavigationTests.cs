using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


/// <remarks>実表のキー受付・現在セル・配置とスクロールの接続が対象のためWPFを使います。Windowや利用者設定は使わず、条件はテスト所有の行と列で作ります。</remarks>
[TestClass]
public sealed class CustomTableNavigationTests
{
    /// <summary>三flagの実factory生値がCtrl+C/選択TSVへ届き、コピーで値を反転しないことをClipboard前で確認します。</summary>
    [TestMethod]
    [DataRow("IsExternalSync", false, false)]
    [DataRow("IsExternalSync", false, true)]
    [DataRow("IsExternalSync", true, false)]
    [DataRow("IsExternalSync", true, true)]
    [DataRow("IsRootFolder", false, false)]
    [DataRow("IsRootFolder", false, true)]
    [DataRow("IsRootFolder", true, false)]
    [DataRow("IsRootFolder", true, true)]
    [DataRow("IsBmtOutput", false, false)]
    [DataRow("IsBmtOutput", false, true)]
    [DataRow("IsBmtOutput", true, false)]
    [DataRow("IsBmtOutput", true, true)]
    public void PlaylistFlagCopyShortcuts_UseCurrentSavedBoolean(string columnId, bool value, bool tsv)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var settings = new PlaylistSummaryColumnSettings();
            settings.IsExternalSync.Visibility = settings.IsRootFolder.Visibility = settings.IsBmtOutput.Visibility = Visibility.Visible;
            CustomTableColumn actual = CustomTableColumnFactory.CreatePlaylistSummaryColumns(settings).Single(column => column.Id == columnId);
            var row = new PlaylistSummaryRow { IsExternalSync = value, IsRootFolder = value, IsBmtOutput = value };
            string? copied = null;
            var sentinel = new InvalidOperationException("現在flagを観測してClipboard前で停止します。");
            var probe = new CustomTableColumn(actual.Id, actual.Header, actual.Layout, actual.FallbackOrder, actual.SortMemberPath, actual.Alignment,
                actual.GetText, editTextSelector: current => { Assert.AreSame(row, current); copied = actual.GetEditText(current); throw sentinel; });
            CustomTableView table = CreateTable([probe]);
            try
            {
                table.ItemsSource = new[] { row };
                table.SelectedIndex = 0;
                Assert.AreSame(sentinel, Assert.ThrowsExactly<InvalidOperationException>(() => table.HandleKeyDown(Key.C,
                    ModifierKeys.Control | (tsv ? ModifierKeys.Shift : ModifierKeys.None))));
                Assert.AreEqual(value.ToString(), copied);
                Assert.AreEqual(value, row.IsExternalSync); Assert.AreEqual(value, row.IsRootFolder); Assert.AreEqual(value, row.IsBmtOutput);
                Assert.AreEqual(0, table.SelectedIndex);
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>実Ctrl+Cが実履歴列の現在行の値を使い、複数選択の副行や交換前sourceを使わないことを確認します。factoryの生値を記録した専用例外でClipboard前に止め、キー配送と値投影を分担します。</summary>
    [TestMethod]
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
                    Assert.AreSame(sentinel, Assert.ThrowsExactly<InvalidOperationException>(() => table.HandleKeyDown(Key.C, ModifierKeys.Control)));
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

                InvalidOperationException thrown = Assert.ThrowsExactly<InvalidOperationException>(
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
    [TestMethod]
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
                Assert.AreEqual(expectedSelection.Length, args.SelectedCount);
                CollectionAssert.AreEqual(expectedSelection, table.GetSelectedRowsSnapshot().ToArray());
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

    /// <summary>21万行の全選択・Shift範囲・Ctrl解除でも、選択通知が主行と可視範囲の外の行へ添字アクセスせず列挙もしないことを確認し、通知のために全選択行を実体化する誤りを検出します。最後に選択行の取得が選択集合の行だけを昇順で返し、null行を除くことも確認します。</summary>
    [TestMethod]
    public void LargeSelection_NotifiesWithoutMaterializingRowsOutsidePrimaryAndVisibleRange()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const int RowTotal = 210_000;
            var rows = new CountingRowList(RowTotal);
            CustomTableView table = CreateTable(CreateColumns(60, 60));
            table.Height = 330d;
            table.ItemsSource = rows;
            table.Measure(new Size(200d, 330d));
            table.Arrange(new Rect(0d, 0d, 200d, 330d));
            table.UpdateLayout();
            var notifications = new List<CustomTableSelectionChangedEventArgs>();
            EventHandler<CustomTableSelectionChangedEventArgs> onSelection = (_, args) => notifications.Add(args);
            table.SelectionChanged += onSelection;
            try
            {
                int capacity = table.CalculateFullVisibleRowCapacity();
                Assert.IsTrue(capacity >= 4, "範囲を絞る手順に必要な可視行数がありません。");

                void Input(string step, Func<bool> input, int expectedCount, int expectedPrimary)
                {
                    rows.ResetAccess();
                    int before = notifications.Count;
                    Assert.IsTrue(input(), step);
                    Assert.AreEqual(before + 1, notifications.Count, step);
                    CustomTableSelectionChangedEventArgs args = notifications[^1];
                    Assert.AreEqual(expectedCount, args.SelectedCount, step);
                    Assert.AreEqual(expectedPrimary, args.SelectedIndex, step);
                    Assert.AreEqual(table.SelectedIndex, args.SelectedIndex, step);
                    Assert.AreSame(rows.Rows[expectedPrimary], args.SelectedRow, step);
                    Assert.IsFalse(rows.EnumeratorRequested, step);
                    int first = table.FirstVisibleRowIndex;
                    foreach (int index in rows.Accessed)
                    {
                        Assert.IsTrue(index == expectedPrimary || (index >= first && index <= first + capacity),
                            $"{step}: 主行と可視範囲の外の行 {index} へアクセスしました。");
                    }
                }

                Input("Ctrl+A", () => table.HandleKeyDown(Key.A, ModifierKeys.Control), RowTotal, 0);
                Input("Shift+下", () => table.HandleKeyDown(Key.Down, ModifierKeys.Shift), 2, 1);

                ScrollBar vertical = table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);
                vertical.Value = vertical.Maximum;
                Input("末尾のShift+クリック", () => PressCell(table, RowTotal - 1, 0, MouseButton.Left, ModifierKeys.Shift), RowTotal, RowTotal - 1);

                int first = table.FirstVisibleRowIndex;
                int middle = first + capacity / 2;
                Input("Ctrl+クリック", () => PressCell(table, middle, 0, MouseButton.Left, ModifierKeys.Control), RowTotal - 1, RowTotal - 1);

                // 基点は直前のCtrl+クリックの行です。可視範囲内の小さな範囲へ絞り、途中にnull行を含めます。
                rows.Rows[first + 1] = null;
                int selectedLast = middle;
                Input("可視範囲への絞込み", () => PressCell(table, first, 0, MouseButton.Left, ModifierKeys.Shift), selectedLast - first + 1, first);

                rows.ResetAccess();
                IReadOnlyList<object> snapshot = table.GetSelectedRowsSnapshot();
                object[] expected = [.. Enumerable.Range(first, selectedLast - first + 1).Where(index => index != first + 1).Select(index => rows.Rows[index]!)];
                CollectionAssert.AreEqual(expected, snapshot.ToArray());
                CollectionAssert.AreEqual(Enumerable.Range(first, selectedLast - first + 1).ToArray(), rows.Accessed.Order().ToArray());
                Assert.IsFalse(rows.EnumeratorRequested);
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

    /// <summary>
    /// ホイールの回転量を累積し、一刻み（120）でOSの設定の行数（-1は完全表示行容量）だけ動くことを確認します。
    /// 60の一回では動かず、60を二回送った位置は別の表へ120を一回送った位置と等しく、逆向きの120で元へ戻ります。
    /// </summary>
    /// <remarks>通知ごとに三行動かす旧方式、回転量の無視、端数の取りこぼし、符号と向きの取り違えを検出します。OSの設定値は書き換えず、読んだ値から期待値を求めます。</remarks>
    [TestMethod]
    public void MouseWheel_AccumulatesDeltaAndMovesConfiguredLinesPerNotch()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView accumulated = CreateScrollableTable();
            CustomTableView single = CreateScrollableTable();
            try
            {
                ScrollBar accumulatedBar = VerticalScrollBar(accumulated);
                ScrollBar singleBar = VerticalScrollBar(single);
                accumulatedBar.Value = 1000d;
                singleBar.Value = 1000d;
                int expectedLines = ExpectedWheelLines(accumulated);

                MouseWheelEventArgs half = RaiseWheel(accumulated, 60);
                Assert.IsTrue(half.Handled);
                Assert.AreEqual(1000d, accumulatedBar.Value, "一刻みに満たない回転量では動かない。");

                RaiseWheel(accumulated, 60);
                RaiseWheel(single, 120);
                Assert.AreEqual(1000d - expectedLines, singleBar.Value, "正の回転量は先頭側へ設定の行数だけ動く。");
                Assert.AreEqual(singleBar.Value, accumulatedBar.Value, "60を二回送った位置は、120を一回送った位置と等しい。");

                RaiseWheel(single, -120);
                Assert.AreEqual(1000d, singleBar.Value, "負の回転量は末尾側へ同じ行数だけ動く。");
            }
            finally { accumulated.ItemsSource = null; single.ItemsSource = null; }
        });
    }

    /// <summary>描画前に三刻み分のホイールを送ると、配置を確定する前の時点で三刻み分すべてが位置へ反映済みで、イベントは処理済みになることを確認します。</summary>
    /// <remarks>入力の途中結果を描画まで保留して捨てる誤りと、スクロールを描画の単位まで遅らせる誤りを検出します。</remarks>
    [TestMethod]
    public void MouseWheel_AppliesEveryNotchBeforeRender()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateScrollableTable();
            try
            {
                ScrollBar bar = VerticalScrollBar(table);
                bar.Value = 1000d;
                int expectedLines = ExpectedWheelLines(table);
                int renderCount = table.LastRenderStats.RenderCount;

                var handled = new List<bool>();
                for (int i = 0; i < 3; i++) { handled.Add(RaiseWheel(table, 120).Handled); }

                Assert.IsTrue(handled.All(value => value));
                Assert.AreEqual(1000d - 3 * expectedLines, bar.Value, "三刻み分が描画前に反映されている。");
                Assert.AreEqual((int)(1000d - 3 * expectedLines), table.FirstVisibleRowIndex);
                Assert.AreEqual(renderCount, table.LastRenderStats.RenderCount, "入力の処理は描画を待たず、描画も同期に行わない。");
                table.UpdateLayout();
                Assert.AreEqual(1000d - 3 * expectedLines, bar.Value, "描画で位置が変わらない。");
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>ホイールの直後、描画前の当たり判定とセル押下が最新の先頭行を使い、スクロール後の行を選ぶことを確認します。</summary>
    /// <remarks>描画の完了を待ってから先頭行を更新する誤りや、ホイール前の位置の行を選ぶ誤りを検出します。</remarks>
    [TestMethod]
    public void MouseWheel_PressCellBeforeRenderUsesLatestFirstRow()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateScrollableTable();
            try
            {
                VerticalScrollBar(table).Value = 1000d;
                int expectedLines = ExpectedWheelLines(table);

                RaiseWheel(table, 120);
                int first = table.FirstVisibleRowIndex;
                Assert.AreEqual(1000 - expectedLines, first);

                Assert.IsTrue(PressCell(table, first + 1, 0, MouseButton.Left));
                Assert.AreEqual(first + 1, table.SelectedIndex);
                Assert.IsTrue(table.IsRowSelected(first + 1));
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>縦スクロールバーが表示されない表では、ホイールが位置を変えず、イベントを処理済みにしない（外側へ伝える）ことを確認します。</summary>
    /// <remarks>バーがない表でホイールを飲み込んで親のスクロールを止める誤りを検出します。</remarks>
    [TestMethod]
    public void MouseWheel_WithoutVerticalBarKeepsPositionAndLeavesEventUnhandled()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CustomTableView table = CreateTable(CreateColumns(60, 60));
            try
            {
                ScrollBar bar = VerticalScrollBar(table);
                Assert.AreNotEqual(Visibility.Visible, bar.Visibility);

                MouseWheelEventArgs forward = RaiseWheel(table, 120);
                MouseWheelEventArgs backward = RaiseWheel(table, -120);

                Assert.IsFalse(forward.Handled);
                Assert.IsFalse(backward.Handled);
                Assert.AreEqual(0d, bar.Value);
                Assert.AreEqual(0, table.FirstVisibleRowIndex);
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>
    /// 行グループの提供元を持つ表で、操作できるチェックボックス・ラジオボタンのセルのクリックが、クリックした行と列を伴う <c>CellActionRequested</c> を一度送り、
    /// 主行がその行になること、ダブルクリックが <c>CellActionRequested</c> 二回で <c>RowActivated</c> を送らないことを確認します。
    /// 結合列では、先頭でない行（グループ [2..5] の行4）をクリックしても同じで、選択はグループの全行です（結合列でない列は、クリックした行だけです）。無効（操作可否がfalse）のセルと値なしのセルは要求を送らず、ダブルクリックは <c>RowActivated</c> を送ります。
    /// 提供元のない表のチェックボックス列は従来どおり、一回目の押下で要求を一度、二回目の押下で <c>RowActivated</c> を送ります。
    /// </summary>
    /// <remarks>
    /// 結合列で先頭行へ要求を送る誤り、図形の有無でヒットを変える誤り、ダブルクリックで <c>RowActivated</c> を送る誤りと要求が一回にとどまる誤り、
    /// 無効・値なしのセルで要求を送る誤りと、そのダブルクリックから <c>RowActivated</c> を奪う誤り、提供元のない表の挙動を変える誤りを検出します。
    /// </remarks>
    [TestMethod]
    [DataRow(CustomTableCellKind.CheckBox, true, true, true, true, 1, 1, 0)]
    [DataRow(CustomTableCellKind.CheckBox, true, true, false, true, 1, 1, 0)]
    [DataRow(CustomTableCellKind.RadioButton, true, true, true, true, 1, 1, 0)]
    [DataRow(CustomTableCellKind.RadioButton, true, false, false, true, 1, 1, 0)]
    [DataRow(CustomTableCellKind.CheckBox, true, true, true, true, 2, 2, 0)]
    [DataRow(CustomTableCellKind.RadioButton, true, true, true, true, 2, 2, 0)]
    [DataRow(CustomTableCellKind.RadioButton, true, false, true, true, 2, 2, 0)]
    [DataRow(CustomTableCellKind.CheckBox, true, true, true, false, 1, 0, 0)]
    [DataRow(CustomTableCellKind.CheckBox, true, true, true, false, 2, 0, 1)]
    [DataRow(CustomTableCellKind.RadioButton, true, true, true, false, 2, 0, 1)]
    [DataRow(CustomTableCellKind.RadioButton, true, true, null, true, 1, 0, 0)]
    [DataRow(CustomTableCellKind.RadioButton, true, true, null, true, 2, 0, 1)]
    [DataRow(CustomTableCellKind.CheckBox, true, true, null, true, 2, 0, 1)]
    [DataRow(CustomTableCellKind.CheckBox, false, false, true, true, 1, 1, 0)]
    [DataRow(CustomTableCellKind.CheckBox, false, false, true, true, 2, 1, 1)]
    public void ActionCells_ClickAndDoubleClickFollowTheRowGroupContract(
        CustomTableCellKind kind, bool grouped, bool merged, bool? isChecked, bool enabled, int clicks, int expectedActions, int expectedActivations)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            object[] rows = grouped ? new GroupedRowList([2, 4, 2]).ToArray() : [.. Enumerable.Range(0, 8).Select(_ => new object())];
            System.Collections.IList source = grouped ? new GroupedRowList([2, 4, 2], rows) : rows;
            CustomTableColumn[] columns = new[]
            {
                new CustomTableColumn("Text", "Text", new CustomTableColumnSettings.ColumnLayout { Width = 60 }, 0, string.Empty, TextAlignment.Left, _ => string.Empty),
                new CustomTableColumn("Action", "Action", new CustomTableColumnSettings.ColumnLayout { Width = 60 }, 1, string.Empty, TextAlignment.Center, _ => string.Empty,
                    checkedSelector: _ => isChecked, cellKind: kind, mergesRowGroup: merged, enabledSelector: _ => enabled)
            };
            var table = new CustomTableView { Width = 200d, Height = 260d, Columns = columns, ItemsSource = source };
            table.Measure(new Size(200d, 260d));
            table.Arrange(new Rect(0d, 0d, 200d, 260d));
            table.UpdateLayout();
            var actions = new List<CustomTableCellActionRequestedEventArgs>();
            var activations = new List<int>();
            table.CellActionRequested += (_, args) => actions.Add(args);
            table.RowActivated += (_, args) => activations.Add(args.RowIndex);
            try
            {
                const int clickedRow = 4;
                var point = new Point(90d, table.HeaderHeight + (clickedRow + 0.5d) * table.RowHeight);
                CustomTableHitTestResult hit = table.HitTestTable(point);
                Assert.AreEqual(CustomTableHitKind.Cell, hit.Kind);
                Assert.AreEqual(clickedRow, hit.RowIndex);
                Assert.AreSame(columns[1], hit.Column);
                for (int clickCount = 1; clickCount <= clicks; clickCount++)
                {
                    Assert.IsTrue(table.HandleCellMouseDown(hit, MouseButton.Left, ModifierKeys.None, clickCount, point));
                }
                Assert.AreEqual(expectedActions, actions.Count, "CellActionRequestedの回数");
                foreach (CustomTableCellActionRequestedEventArgs action in actions)
                {
                    Assert.AreEqual(clickedRow, action.RowIndex);
                    Assert.AreSame(rows[clickedRow], action.Row);
                    Assert.AreSame(columns[1], action.Column);
                }
                CollectionAssert.AreEqual(Enumerable.Repeat(clickedRow, expectedActivations).ToArray(), activations, "RowActivatedの回数と行");
                Assert.AreEqual(clickedRow, table.SelectedIndex, "主行はクリックした行");
                // 結合列は、グループ [2..5] の全行が選択される。結合列でない列と、提供元のない表は、クリックした行だけ。
                object[] expectedSelection = grouped && merged ? [.. rows.Skip(2).Take(4)] : [rows[clickedRow]];
                CollectionAssert.AreEqual(expectedSelection, table.GetSelectedRowsSnapshot().ToArray(), "結合列はグループの全行、それ以外はクリックした行が選択される");
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>
    /// 結合列の先頭でない行（グループ [2..5] の行4）で、ツールチップ・Ctrl+Cの現在セルのコピー・Ctrl+Shift+Cの選択行のTSVが、その行の値（グループの先頭行ではない）を使うことを確認します。
    /// 行ごとに値が異なる行を使い、コピーはクリップボードへ書く前に専用の例外で止めます。
    /// </summary>
    /// <remarks>結合列のツールチップやコピーをグループの先頭行へ読み替える誤り、TSVの行を結合して一行にまとめる誤りを検出します。</remarks>
    [TestMethod]
    public void MergedColumn_TooltipCopyAndTsvUseTheValuesOfTheClickedRow()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            TextRow[] rowValues = [.. Enumerable.Range(0, 8).Select(index => new TextRow("value " + index))];
            var rows = new GroupedRowList([2, 4, 2], rowValues);
            var visits = new List<TextRow>();
            TextRow? stopAt = null;
            var sentinel = new InvalidOperationException("コピーの対象を観測したため、クリップボードへ書く前に停止します。");
            var column = new CustomTableColumn("Merged", "Merged", new CustomTableColumnSettings.ColumnLayout { Width = 120 }, 0, string.Empty, TextAlignment.Left,
                row => ((TextRow)row).Text, tooltipSelector: row => "tip " + ((TextRow)row).Text, mergesRowGroup: true,
                editTextSelector: row =>
                {
                    visits.Add((TextRow)row);
                    if (ReferenceEquals(row, stopAt)) { throw sentinel; }
                    return ((TextRow)row).Text;
                });
            var table = new CustomTableView { Width = 200d, Height = 260d, Columns = [column], ItemsSource = rows };
            table.Measure(new Size(200d, 260d));
            table.Arrange(new Rect(0d, 0d, 200d, 260d));
            table.UpdateLayout();
            try
            {
                var point = new Point(50d, table.HeaderHeight + 4.5d * table.RowHeight);
                CustomTableHitTestResult hit = table.HitTestTable(point);
                Assert.AreEqual(4, hit.RowIndex);
                Assert.AreEqual("tip value 4", table.ResolveCellToolTip(hit), "ツールチップはクリックした行の値");
                Assert.IsTrue(table.HandleCellMouseDown(hit, MouseButton.Left, ModifierKeys.None, 1, point));
                stopAt = rowValues[4];
                visits.Clear();
                Assert.AreSame(sentinel, Assert.ThrowsExactly<InvalidOperationException>(() => table.HandleKeyDown(Key.C, ModifierKeys.Control)));
                CollectionAssert.AreEqual(new[] { rowValues[4] }, visits, "現在セルのコピーはその行の値");

                table.SelectRowsByPredicate(row => ReferenceEquals(row, rowValues[3]) || ReferenceEquals(row, rowValues[4]), row => ReferenceEquals(row, rowValues[4]));
                visits.Clear();
                Assert.AreSame(sentinel, Assert.ThrowsExactly<InvalidOperationException>(() => table.HandleKeyDown(Key.C, ModifierKeys.Control | ModifierKeys.Shift)));
                CollectionAssert.AreEqual(new[] { rowValues[3], rowValues[4] }, visits, "TSVは選択した各行の値");
            }
            finally { table.ItemsSource = null; }
        });
    }

    /// <summary>グループ [2,4,2]（行0-1、行2-5、行6-7）の表と、観測した通知・要求の記録です。結合列の文字（列1）・チェックボックス（列2）と、通常の列（文字・ラジオボタン）を持ちます。</summary>
    private sealed class GroupSelectionFixture
    {
        internal const int Text = 0, MergedText = 1, MergedCheck = 2, Radio = 3;

        internal GroupSelectionFixture(bool withProvider)
        {
            Rows = withProvider ? new GroupedRowList([2, 4, 2]).ToArray() : [.. Enumerable.Range(0, 8).Select(_ => new object())];
            System.Collections.IList source = withProvider ? new GroupedRowList([2, 4, 2], Rows) : Rows;
            Columns =
            [
                new CustomTableColumn("Text", "Text", new CustomTableColumnSettings.ColumnLayout { Width = 60 }, 0, string.Empty, TextAlignment.Left, _ => "t"),
                new CustomTableColumn("MergedText", "MergedText", new CustomTableColumnSettings.ColumnLayout { Width = 60 }, 1, string.Empty, TextAlignment.Left, _ => "m", mergesRowGroup: true),
                new CustomTableColumn("MergedCheck", "MergedCheck", new CustomTableColumnSettings.ColumnLayout { Width = 60 }, 2, string.Empty, TextAlignment.Center, _ => string.Empty,
                    checkedSelector: _ => true, cellKind: CustomTableCellKind.CheckBox, mergesRowGroup: true, enabledSelector: _ => true),
                new CustomTableColumn("Radio", "Radio", new CustomTableColumnSettings.ColumnLayout { Width = 60 }, 3, string.Empty, TextAlignment.Center, _ => string.Empty,
                    checkedSelector: _ => true, cellKind: CustomTableCellKind.RadioButton, enabledSelector: _ => true)
            ];
            Table = new CustomTableView { Width = 260d, Height = 260d, Columns = Columns, ItemsSource = source };
            Table.Measure(new Size(260d, 260d));
            Table.Arrange(new Rect(0d, 0d, 260d, 260d));
            Table.UpdateLayout();
            Table.SelectionChanged += (_, args) => Log.Add($"sel {args.SelectedIndex}/{args.SelectedCount}");
            Table.CellActionRequested += (_, args) => Log.Add($"action {args.RowIndex}");
            Table.RowContextMenuRequested += (_, args) => Log.Add($"menu {args.RowIndex}");
            Table.RowActivated += (_, args) => Log.Add($"activate {args.RowIndex}");
        }

        internal object[] Rows { get; }

        internal CustomTableColumn[] Columns { get; }

        internal CustomTableView Table { get; }

        internal List<string> Log { get; } = [];

        internal int[] Selected => [.. Enumerable.Range(0, Rows.Length).Where(Table.IsRowSelected)];

        internal void Cleanup() => Table.ItemsSource = null;

        /// <summary>実際のヒット判定から、本番のセル押下処理を呼びます。</summary>
        internal void Press(int row, int column, ModifierKeys modifiers = ModifierKeys.None, int clickCount = 1, MouseButton button = MouseButton.Left)
        {
            var point = new Point(column * 60d + 30d, Table.HeaderHeight + (row + 0.5d) * Table.RowHeight);
            CustomTableHitTestResult hit = Table.HitTestTable(point);
            Assert.AreEqual(CustomTableHitKind.Cell, hit.Kind);
            Assert.AreEqual(row, hit.RowIndex);
            Assert.AreSame(Columns[column], hit.Column);
            Assert.IsTrue(Table.HandleCellMouseDown(hit, button, modifiers, clickCount, point));
        }

        /// <summary>表への左ボタンの解放（PreviewMouseLeftButtonUp）を送ります。</summary>
        internal void ReleaseMouse()
        {
            Table.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = Table });
        }

        internal bool Key(Key key, ModifierKeys modifiers = ModifierKeys.None) => Table.HandleKeyDown(key, modifiers);

        internal void AssertState(int[] selected, int primary, string[] log, string label)
        {
            CollectionAssert.AreEqual(selected, Selected, label + " 選択された行");
            Assert.AreEqual(primary, Table.SelectedIndex, label + " 主行");
            CollectionAssert.AreEqual(log, Log, label + " 通知・要求の記録");
        }
    }

    /// <summary>
    /// 行グループの提供元を持つ表で、結合列のセルのクリックがグループの全行を選ぶ規則を、グループ [2,4,2] で確認します。
    /// 修飾なしは全行へ置き換え、Ctrlは全選択済みなら全行を外して一部選択・未選択なら全行を加え、Shiftは基点とクリック行の両端をグループの境界まで広げ（上向き・下向き、基点がグループの途中）、
    /// 右クリックは全選択済みなら集合を保ち（主行だけ移る）そうでなければ置き換えてからメニューを要求します（選択の通知が先）。
    /// 結合列のチェックボックスは、修飾なしの単クリックが操作の要求一回と右クリックと同じ選択、ダブルクリックが要求二回で <c>RowActivated</c> なし、Ctrl付きは要求なしでグループの切替です。
    /// 結合列の文字セルのダブルクリックは、クリックした行で <c>RowActivated</c> を一回送ります。マウスを離しても、一行へ絞りません。
    /// 結合列以外の列（ラジオボタンを含む）と、提供元のない表（結合列の指定がある列でも）は行単位のままです。
    /// </summary>
    /// <remarks>
    /// クリックした行だけを選ぶ誤り、Shiftで片側だけを広げる誤り、Ctrlの一部選択で外す誤り、マウスを離したときに一行へ絞る誤り、
    /// 右クリックで全選択済みの集合を壊す誤り、メニュー要求を選択の通知より先に出す誤り、操作の要求を落とす・二重に送る誤り、
    /// 結合列のダブルクリックの <c>RowActivated</c> を妨げる誤り、提供元の有無を見ずにグループ化する誤りを検出します。
    /// </remarks>
    [TestMethod]
    public void MergedColumnClicks_SelectWholeGroupsAndOtherColumnsStayRowWise()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            void Case(Action<GroupSelectionFixture> act, int[] selected, int primary, string[] log, string label, bool withProvider = true)
            {
                var fixture = new GroupSelectionFixture(withProvider);
                try
                {
                    act(fixture);
                    fixture.AssertState(selected, primary, log, label);
                }
                finally { fixture.Cleanup(); }
            }

            int[] g0 = [0, 1], g1 = [2, 3, 4, 5], g2 = [6, 7];

            // 修飾なし。行3を押すとグループ全体、マウスを離しても絞らない。別のグループへ置き換える。
            Case(f => f.Press(3, GroupSelectionFixture.MergedText), g1, 3, ["sel 3/4"], "修飾なし");
            Case(f => { f.Press(3, GroupSelectionFixture.MergedText); f.ReleaseMouse(); }, g1, 3, ["sel 3/4"], "離しても一行へ絞らない");
            Case(f => { f.Press(3, GroupSelectionFixture.MergedText); f.Press(4, GroupSelectionFixture.MergedText); f.ReleaseMouse(); }, g1, 4, ["sel 3/4", "sel 4/4"], "全選択済みのグループ内で押し直す（主行だけ移る）");
            Case(f => { f.Press(0, GroupSelectionFixture.MergedText); f.Press(7, GroupSelectionFixture.MergedText); }, g2, 7, ["sel 0/2", "sel 7/2"], "別のグループへ置き換える");
            Case(f => { f.Press(3, GroupSelectionFixture.MergedText); f.Press(3, GroupSelectionFixture.MergedText); }, g1, 3, ["sel 3/4"], "同じ入力を繰り返しても通知しない");

            // Ctrl。未選択はグループを加え、一部選択はグループを加え、全選択済みは全行を外す。
            Case(f => f.Press(3, GroupSelectionFixture.MergedText, ModifierKeys.Control), g1, 3, ["sel 3/4"], "Ctrl・未選択");
            Case(f => { f.Press(0, GroupSelectionFixture.MergedText); f.Press(3, GroupSelectionFixture.MergedText, ModifierKeys.Control); }, [0, 1, 2, 3, 4, 5], 3, ["sel 0/2", "sel 3/6"], "Ctrl・別のグループを加える");
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Press(4, GroupSelectionFixture.MergedText, ModifierKeys.Control); }, g1, 4, ["sel 3/1", "sel 4/4"], "Ctrl・一部選択はグループを加える（外さない）");
            Case(f => { f.Press(3, GroupSelectionFixture.MergedText); f.Press(4, GroupSelectionFixture.MergedText, ModifierKeys.Control); }, [], -1, ["sel 3/4", "sel -1/0"], "Ctrl・全選択済みは全行を外す");
            Case(f =>
            {
                f.Press(0, GroupSelectionFixture.MergedText);
                f.Press(3, GroupSelectionFixture.MergedText, ModifierKeys.Control);
                f.Press(4, GroupSelectionFixture.MergedText, ModifierKeys.Control);
                Assert.IsTrue(f.Table.IsCurrentCell(1, f.Columns[GroupSelectionFixture.MergedText]), "外した後の主行（残りの末尾の行）へ押した列を引き継ぐ");
            }, g0, 1, ["sel 0/2", "sel 3/6", "sel 1/2"], "Ctrl・全選択済みのグループだけを外す");

            // Shift。基点（行3、グループの途中）からの下向き・上向きで、両端をグループの境界まで広げる。
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Press(6, GroupSelectionFixture.MergedText, ModifierKeys.Shift); }, [2, 3, 4, 5, 6, 7], 6, ["sel 3/1", "sel 6/6"], "Shift・下向き（基点がグループの途中）");
            Case(f => { f.Press(4, GroupSelectionFixture.Text); f.Press(1, GroupSelectionFixture.MergedText, ModifierKeys.Shift); }, [0, 1, 2, 3, 4, 5], 1, ["sel 4/1", "sel 1/6"], "Shift・上向き（基点がグループの途中）");
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Press(4, GroupSelectionFixture.MergedText, ModifierKeys.Shift); }, g1, 4, ["sel 3/1", "sel 4/4"], "Shift・同じグループ");
            Case(f => f.Press(3, GroupSelectionFixture.MergedText, ModifierKeys.Shift), g1, 3, ["sel 3/4"], "Shift・基点なし");

            // 右クリック。全選択済みは集合を保ち、そうでなければ置き換えてからメニュー（通知が先）。
            Case(f => { f.Press(3, GroupSelectionFixture.MergedText); f.Press(5, GroupSelectionFixture.MergedText, button: MouseButton.Right); }, g1, 5, ["sel 3/4", "sel 5/4", "menu 5"], "右クリック・全選択済み（集合を保つ）");
            Case(f => { f.Press(0, GroupSelectionFixture.MergedText); f.Press(3, GroupSelectionFixture.MergedText, ModifierKeys.Control); f.Press(4, GroupSelectionFixture.MergedText, button: MouseButton.Right); },
                [0, 1, 2, 3, 4, 5], 4, ["sel 0/2", "sel 3/6", "sel 4/6", "menu 4"], "右クリック・複数のグループが選択済みでも集合を保つ");
            Case(f => { f.Press(3, GroupSelectionFixture.MergedText); f.Press(3, GroupSelectionFixture.MergedText, button: MouseButton.Right); }, g1, 3, ["sel 3/4", "menu 3"], "右クリック・主行が同じなら通知しない");
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Press(5, GroupSelectionFixture.MergedText, button: MouseButton.Right); }, g1, 5, ["sel 3/1", "sel 5/4", "menu 5"], "右クリック・一部選択（置き換えてからメニュー）");
            Case(f => { f.Press(3, GroupSelectionFixture.MergedText); f.Press(0, GroupSelectionFixture.MergedText, button: MouseButton.Right); }, g0, 0, ["sel 3/4", "sel 0/2", "menu 0"], "右クリック・別のグループ");

            // 結合列のチェックボックス。
            Case(f => f.Press(4, GroupSelectionFixture.MergedCheck), g1, 4, ["sel 4/4", "action 4"], "チェックボックス・単クリック（選択の通知が先、要求一回）");
            Case(f => { f.Press(4, GroupSelectionFixture.MergedCheck); f.Press(4, GroupSelectionFixture.MergedCheck, clickCount: 2); f.ReleaseMouse(); },
                g1, 4, ["sel 4/4", "action 4", "action 4"], "チェックボックス・ダブルクリック（要求二回、RowActivatedなし）");
            Case(f => f.Press(4, GroupSelectionFixture.MergedCheck, ModifierKeys.Control), g1, 4, ["sel 4/4"], "チェックボックス・Ctrlは要求を送らずグループを加える");
            Case(f => { f.Press(4, GroupSelectionFixture.MergedCheck); f.Press(5, GroupSelectionFixture.MergedCheck, ModifierKeys.Control); }, [], -1, ["sel 4/4", "action 4", "sel -1/0"], "チェックボックス・Ctrlで全選択済みのグループを外す");
            Case(f =>
            {
                f.Press(0, GroupSelectionFixture.MergedText);
                f.Press(3, GroupSelectionFixture.MergedText, ModifierKeys.Control);
                f.Press(4, GroupSelectionFixture.MergedCheck);
            }, [0, 1, 2, 3, 4, 5], 4, ["sel 0/2", "sel 3/6", "sel 4/6", "action 4"], "チェックボックス・全選択済みなら集合を保つ（右クリックと同じ規則）");
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Press(4, GroupSelectionFixture.MergedCheck); }, g1, 4, ["sel 3/1", "sel 4/4", "action 4"], "チェックボックス・一部選択はグループへ置き換える");

            // 結合列の文字のダブルクリックは、クリックした行で RowActivated を一回。
            Case(f => { f.Press(4, GroupSelectionFixture.MergedText); f.Press(4, GroupSelectionFixture.MergedText, clickCount: 2); f.ReleaseMouse(); }, g1, 4, ["sel 4/4", "activate 4"], "文字セルのダブルクリック");

            // 結合列以外は行単位。
            Case(f => f.Press(3, GroupSelectionFixture.Text), [3], 3, ["sel 3/1"], "通常の列・修飾なし");
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Press(6, GroupSelectionFixture.Text, ModifierKeys.Control); }, [3, 6], 6, ["sel 3/1", "sel 6/2"], "通常の列・Ctrl");
            Case(f => { f.Press(2, GroupSelectionFixture.Text); f.Press(4, GroupSelectionFixture.Text, ModifierKeys.Shift); }, [2, 3, 4], 4, ["sel 2/1", "sel 4/3"], "通常の列・Shiftはグループの境界へ広げない");
            Case(f => { f.Press(3, GroupSelectionFixture.MergedText); f.Press(4, GroupSelectionFixture.Text); f.ReleaseMouse(); }, [4], 4, ["sel 3/4", "sel 4/4", "sel 4/1"], "通常の列・複数選択の中の行を押して離すと一行へ絞る");
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Press(4, GroupSelectionFixture.Radio, button: MouseButton.Right); }, [4], 4, ["sel 3/1", "sel 4/1", "menu 4"], "通常の列・右クリック");
            Case(f => f.Press(4, GroupSelectionFixture.Radio), [4], 4, ["sel 4/1", "action 4"], "通常のラジオボタン・単クリックは行だけ選び要求を一回");
            Case(f => { f.Press(3, GroupSelectionFixture.MergedText); f.Press(4, GroupSelectionFixture.Radio); f.ReleaseMouse(); }, [4], 4, ["sel 3/4", "sel 4/1", "action 4"],
                "通常のラジオボタン（操作セル）は、結合列で選んだグループの全行を、押した行だけへ絞る");

            // 提供元のない表は、結合列の指定がある列でも行単位。
            Case(f => f.Press(3, GroupSelectionFixture.MergedText), [3], 3, ["sel 3/1"], "提供元なし・修飾なし", withProvider: false);
            Case(f => { f.Press(1, GroupSelectionFixture.MergedText); f.Press(4, GroupSelectionFixture.MergedText, ModifierKeys.Shift); }, [1, 2, 3, 4], 4, ["sel 1/1", "sel 4/4"], "提供元なし・Shift", withProvider: false);
            Case(f => { f.Press(1, GroupSelectionFixture.MergedText); f.Press(4, GroupSelectionFixture.MergedText, ModifierKeys.Control); }, [1, 4], 4, ["sel 1/1", "sel 4/2"], "提供元なし・Ctrl", withProvider: false);
            Case(f => f.Press(3, GroupSelectionFixture.MergedText, button: MouseButton.Right), [3], 3, ["sel 3/1", "menu 3"], "提供元なし・右クリック", withProvider: false);
            Case(f => { f.Press(1, GroupSelectionFixture.Text); f.Press(4, GroupSelectionFixture.Text, ModifierKeys.Control); f.Press(4, GroupSelectionFixture.Radio); f.ReleaseMouse(); },
                [1, 4], 4, ["sel 1/1", "sel 4/2", "action 4"], "提供元なし・操作セルは複数選択を保つ（従来どおり）", withProvider: false);
            Case(f => f.Press(4, GroupSelectionFixture.MergedCheck), [4], 4, ["sel 4/1", "action 4"], "提供元なし・チェックボックス", withProvider: false);
            Case(f => { f.Press(4, GroupSelectionFixture.MergedText); f.Press(4, GroupSelectionFixture.MergedText, clickCount: 2); }, [4], 4, ["sel 4/1", "activate 4"], "提供元なし・ダブルクリック", withProvider: false);
        });
    }

    /// <summary>
    /// 現在セルが結合列にあるとき、上下の矢印キーが隣のグループの全行を選び（主行は移動先のグループの先頭行）、Shift付きがグループ単位で広げ、
    /// 先頭のグループの上と末尾のグループの下では動かず通知しないことを確認します。左右で結合列へ入るとグループを選び（全行が選択済みなら集合を保つ）、結合列から出ても選択を保ちます。
    /// 結合列以外のセルでは行単位のままで、Enterは主行で <c>RowActivated</c> を送ります。
    /// </summary>
    /// <remarks>
    /// 行単位で動く誤り、結合列へ入っても選択が変わらない誤り、端で通知を送る誤り、主行を先頭行以外にする誤り、Shiftで片側だけ広げる誤り、Enterを主行以外で送る誤りを検出します。
    /// </remarks>
    [TestMethod]
    public void MergedColumnKeys_MoveAndExtendByGroupAndOtherColumnsStayRowWise()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            void Case(Action<GroupSelectionFixture> act, int[] selected, int primary, string[] log, string label)
            {
                var fixture = new GroupSelectionFixture(true);
                try
                {
                    act(fixture);
                    fixture.AssertState(selected, primary, log, label);
                }
                finally { fixture.Cleanup(); }
            }

            int[] g0 = [0, 1], g1 = [2, 3, 4, 5], g2 = [6, 7];
            const int M = GroupSelectionFixture.MergedText;

            // 上下。主行は方向によらず移動先のグループの先頭行。端では動かず通知しない。
            Case(f => { f.Press(3, M); Assert.IsTrue(f.Key(Key.Down)); }, g2, 6, ["sel 3/4", "sel 6/2"], "下");
            Case(f => { f.Press(3, M); Assert.IsTrue(f.Key(Key.Up)); }, g0, 0, ["sel 3/4", "sel 0/2"], "上（主行は先頭行）");
            Case(f => { f.Press(7, M); Assert.IsTrue(f.Key(Key.Down)); }, g2, 7, ["sel 7/2"], "末尾のグループで下は動かず通知しない");
            Case(f => { f.Press(1, M); Assert.IsTrue(f.Key(Key.Up)); }, g0, 1, ["sel 1/2"], "先頭のグループで上は動かず通知しない");
            Case(f => { f.Press(0, M); f.Key(Key.Down); f.Key(Key.Down); f.Key(Key.Down); f.Key(Key.Up); }, g1, 2, ["sel 0/2", "sel 2/4", "sel 6/2", "sel 2/4"], "連続して移る");
            Case(f => { f.Press(3, M); f.Key(Key.Down); Assert.IsTrue(f.Table.IsCurrentCell(6, f.Columns[M]), "現在セルも移動先の先頭行の結合列"); }, g2, 6, ["sel 3/4", "sel 6/2"], "現在セルの移動");

            // Shift+上下。基点を保ち、両端をグループの境界へ広げる。
            Case(f => { f.Press(0, M); f.Key(Key.Down, ModifierKeys.Shift); }, [0, 1, 2, 3, 4, 5], 2, ["sel 0/2", "sel 2/6"], "Shift+下");
            Case(f => { f.Press(0, M); f.Key(Key.Down, ModifierKeys.Shift); f.Key(Key.Down, ModifierKeys.Shift); }, [0, 1, 2, 3, 4, 5, 6, 7], 6, ["sel 0/2", "sel 2/6", "sel 6/8"], "Shift+下を続ける");
            Case(f => { f.Press(0, M); f.Key(Key.Down, ModifierKeys.Shift); f.Key(Key.Down, ModifierKeys.Shift); f.Key(Key.Down, ModifierKeys.Shift); }, [0, 1, 2, 3, 4, 5, 6, 7], 6, ["sel 0/2", "sel 2/6", "sel 6/8"], "Shift+下の端では通知しない");
            Case(f => { f.Press(0, M); f.Key(Key.Down, ModifierKeys.Shift); f.Key(Key.Down, ModifierKeys.Shift); f.Key(Key.Up, ModifierKeys.Shift); }, [0, 1, 2, 3, 4, 5], 2, ["sel 0/2", "sel 2/6", "sel 6/8", "sel 2/6"], "Shift+上で縮める");
            Case(f => { f.Press(4, M); f.Key(Key.Up, ModifierKeys.Shift); }, [0, 1, 2, 3, 4, 5], 0, ["sel 4/4", "sel 0/6"], "Shift+上（基点がグループの途中）");
            Case(f => { f.Press(1, M); f.Key(Key.Up, ModifierKeys.Shift); }, g0, 1, ["sel 1/2"], "Shift+上の端では通知しない");

            // 左右。結合列へ入るとグループを選び（全行が選択済みなら集合を保つ）、出ても保つ。
            Case(f =>
            {
                f.Press(3, GroupSelectionFixture.Text);
                Assert.IsTrue(f.Key(Key.Right));
                Assert.IsTrue(f.Table.IsCurrentCell(3, f.Columns[M]));
            }, g1, 3, ["sel 3/1", "sel 3/4"], "右で結合列へ入るとグループを選ぶ（主行は現在の行）");
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Key(Key.Right); f.Key(Key.Right); f.Key(Key.Right); }, g1, 3, ["sel 3/1", "sel 3/4"], "全選択済みの結合列から結合列・通常の列へ移っても通知しない");
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Key(Key.Right); f.Key(Key.Right); f.Key(Key.Right); f.Key(Key.Left); f.Key(Key.Left); f.Key(Key.Left); },
                g1, 3, ["sel 3/1", "sel 3/4"], "左右で往復しても選択を保つ");
            Case(f =>
            {
                f.Press(0, M);
                f.Press(3, GroupSelectionFixture.Text, ModifierKeys.Control);
                f.Key(Key.Right);
            }, g1, 3, ["sel 0/2", "sel 3/3", "sel 3/4"], "一部選択のグループへ入ると、そのグループへ置き換える");
            Case(f => { f.Press(5, GroupSelectionFixture.Radio); f.Key(Key.Left); f.Key(Key.Left); }, g1, 5, ["sel 5/1", "action 5", "sel 5/4"], "左で結合列へ入る（結合列の間の移動は保つ）");

            // 結合列以外は行単位。
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Key(Key.Down); }, [4], 4, ["sel 3/1", "sel 4/1"], "通常の列・下");
            Case(f => { f.Press(3, GroupSelectionFixture.Text); f.Key(Key.Down, ModifierKeys.Shift); }, [3, 4], 4, ["sel 3/1", "sel 4/2"], "通常の列・Shift+下");
            Case(f => { f.Press(7, GroupSelectionFixture.Radio); f.Key(Key.Down); }, [7], 7, ["sel 7/1", "action 7"], "通常の列・末尾の行で下は動かず通知しない");

            // Enterは主行で送る。
            Case(f => { f.Press(3, M); f.Key(Key.Down); f.Key(Key.Return); }, g2, 6, ["sel 3/4", "sel 6/2", "activate 6"], "Enterは主行");
        });
    }

    /// <summary>
    /// 行グループの提供元を持つ表で、結合列のセルのツールチップが、同じ結合セルの中で別の行へ動いても閉じず出し直さず、別のグループの結合セル・別の列へ動くと出し直すことを確認します。
    /// 提供元のない表は行ごとに出し直します。内容は、その行の値（グループ内で一致する値）です。
    /// ツールチップは実際の窓で開くため、窓ありのテストの枠組みで表示範囲を追跡し、後片付けを任せます。マウスの移動が使う内部の入口へ、実ヒットを明示して渡し、OSのカーソル位置を条件にしません。
    /// </summary>
    /// <remarks>行ごとにツールチップを出し直す誤り（ちらつき）、同じグループで内容が古いまま残る誤り、別のグループ・別の列へ動いても出し直さない誤り、提供元のない表の挙動を変える誤りを検出します。</remarks>
    [TestMethod]
    public void MergedColumnToolTip_StaysOpenInsideOneCellAndReopensForAnotherCell()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            foreach (bool withProvider in new[] { true, false })
            {
                object[] rows = withProvider ? new GroupedRowList([2, 4, 2]).ToArray() : [.. Enumerable.Range(0, 8).Select(_ => new object())];
                System.Collections.IList source = withProvider ? new GroupedRowList([2, 4, 2], rows) : rows;
                var groups = new GroupedRowList([2, 4, 2], rows);
                string GroupTip(object row) => "group " + groups.GetRowGroup(Array.IndexOf(rows, row)).Ordinal;
                CustomTableColumn[] columns =
                [
                    new("Text", "Text", new CustomTableColumnSettings.ColumnLayout { Width = 60 }, 0, string.Empty, TextAlignment.Left, _ => "t", tooltipSelector: row => "row " + Array.IndexOf(rows, row)),
                    new("Merged", "Merged", new CustomTableColumnSettings.ColumnLayout { Width = 60 }, 1, string.Empty, TextAlignment.Left, _ => "m", tooltipSelector: GroupTip, mergesRowGroup: true)
                ];
                var table = new CustomTableView { Width = 140d, Height = 220d, Columns = columns, ItemsSource = source };
                var window = new Window { Width = 180, Height = 280, Content = table, ShowInTaskbar = false, ShowActivated = false };
                var transitions = new List<string>();
                var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.ToolTip.IsOpenProperty, typeof(System.Windows.Controls.ToolTip));
                EventHandler onOpenChanged = (_, _) => transitions.Add(table.CellToolTip.IsOpen ? "open" : "close");
                try
                {
                    scope.TrackToolTip(table.CellToolTip);
                    scope.ShowAndWaitForContentRendered(window);
                    descriptor.AddValueChanged(table.CellToolTip, onOpenChanged);
                    void Hover(int row, int column)
                    {
                        var point = new Point(column * 60d + 30d, table.HeaderHeight + (row + 0.5d) * table.RowHeight);
                        CustomTableHitTestResult hit = table.HitTestTable(point);
                        Assert.AreEqual(row, hit.RowIndex);
                        Assert.AreSame(columns[column], hit.Column);
                        table.UpdateCellToolTip(hit);
                    }
                    string Content() => table.CellToolTip.Content as string ?? string.Empty;
                    string label = withProvider ? "提供元あり" : "提供元なし";

                    Hover(2, 1);
                    CollectionAssert.AreEqual(new[] { "open" }, transitions, label + " 最初に開く");
                    Assert.IsTrue(table.CellToolTip.IsOpen);
                    Assert.AreEqual("group 1", Content(), label + " 内容は行の値");
                    Hover(3, 1);
                    Hover(5, 1);
                    if (withProvider)
                    {
                        CollectionAssert.AreEqual(new[] { "open" }, transitions, label + " 同じ結合セルの中で別の行へ動いても閉じず出し直さない");
                        Assert.AreEqual("group 1", Content(), label + " 内容は従来どおり");
                    }
                    else
                    {
                        CollectionAssert.AreEqual(new[] { "open", "close", "open", "close", "open" }, transitions, label + " 提供元のない表は行ごとに出し直す");
                    }
                    transitions.Clear();
                    Hover(6, 1);
                    CollectionAssert.AreEqual(new[] { "close", "open" }, transitions, label + " 別のグループの結合セルへ動くと出し直す");
                    Assert.AreEqual("group 2", Content(), label + " 出し直した内容");
                    transitions.Clear();
                    Hover(6, 0);
                    CollectionAssert.AreEqual(new[] { "close", "open" }, transitions, label + " 別の列へ動くと出し直す");
                    Assert.AreEqual("row 6", Content(), label + " 別の列の内容");
                    transitions.Clear();
                    Hover(7, 0);
                    CollectionAssert.AreEqual(new[] { "close", "open" }, transitions, label + " 通常の列は行ごとに出し直す");
                    Assert.AreEqual("row 7", Content());
                }
                finally
                {
                    descriptor.RemoveValueChanged(table.CellToolTip, onOpenChanged);
                    table.UpdateCellToolTip(new CustomTableHitTestResult(CustomTableHitKind.Empty, -1, null, null, -1, Rect.Empty));
                    table.ItemsSource = null;
                }
            }
        });
    }

    /// <summary>OSの設定の行数です。-1（一画面ずつ）は完全表示行容量、0は動かしません。</summary>
    private static int ExpectedWheelLines(CustomTableView table)
    {
        int lines = SystemParameters.WheelScrollLines;
        return lines < 0 ? table.CalculateFullVisibleRowCapacity() : lines;
    }

    private static MouseWheelEventArgs RaiseWheel(CustomTableView table, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = table };
        table.RaiseEvent(args);
        return args;
    }

    private static ScrollBar VerticalScrollBar(CustomTableView table)
        => table.Children.OfType<ScrollBar>().Single(bar => bar.Orientation == System.Windows.Controls.Orientation.Vertical);

    private static CustomTableView CreateScrollableTable(int rowCount = 2000)
    {
        var table = new CustomTableView
        {
            Width = 200d,
            Height = 130d,
            Columns = CreateColumns(60, 60),
            ItemsSource = Enumerable.Range(0, rowCount).Select(_ => new object()).ToArray()
        };
        table.Measure(new Size(200d, 130d));
        table.Arrange(new Rect(0d, 0d, 200d, 130d));
        table.UpdateLayout();
        return table;
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

    /// <summary>行ごとに異なる文字を持つ行です。</summary>
    private sealed class TextRow(string text)
    {
        internal string Text { get; } = text;
    }

    /// <summary>グループの大きさの並びから、連続したグループを返す行集合です。行を渡さなければ、行ごとに新しいobjectを作ります。</summary>
    private sealed class GroupedRowList : System.Collections.ObjectModel.Collection<object>, ICustomTableRowGroupSource
    {
        private readonly int[] starts;

        internal GroupedRowList(int[] sizes, IEnumerable<object>? rows = null)
        {
            starts = new int[sizes.Length];
            int total = 0;
            for (int i = 0; i < sizes.Length; i++)
            {
                starts[i] = total;
                total += sizes[i];
            }
            foreach (object row in rows ?? Enumerable.Range(0, total).Select(_ => new object())) { Add(row); }
        }

        public CustomTableRowGroup GetRowGroup(int rowIndex)
        {
            int ordinal = Array.BinarySearch(starts, rowIndex);
            if (ordinal < 0) { ordinal = ~ordinal - 1; }
            int end = ordinal + 1 < starts.Length ? starts[ordinal + 1] : Count;
            return new CustomTableRowGroup(starts[ordinal], end - starts[ordinal], ordinal);
        }
    }

    // 添字アクセスを記録し、列挙・探索・複写を失敗にする行集合です。選択の処理が触れた行だけを観測するために使います。
    private sealed class CountingRowList : System.Collections.IList
    {
        internal CountingRowList(int count)
        {
            Rows = new object?[count];
            for (int i = 0; i < count; i++)
            {
                Rows[i] = new object();
            }
        }

        // 記録しない直接参照です。表明の期待値を作るためだけに使います。
        internal object?[] Rows { get; }

        internal HashSet<int> Accessed { get; } = [];

        internal bool EnumeratorRequested { get; private set; }

        internal void ResetAccess() => Accessed.Clear();

        public object? this[int index]
        {
            get
            {
                Accessed.Add(index);
                return Rows[index];
            }
            set => throw new NotSupportedException();
        }

        public int Count => Rows.Length;

        public bool IsFixedSize => true;

        public bool IsReadOnly => true;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public System.Collections.IEnumerator GetEnumerator()
        {
            EnumeratorRequested = true;
            throw new InvalidOperationException("選択の処理が行集合を列挙しました。");
        }

        public int Add(object? value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public bool Contains(object? value) => throw new NotSupportedException();

        public void CopyTo(Array array, int index) => throw new NotSupportedException();

        public int IndexOf(object? value) => throw new NotSupportedException();

        public void Insert(int index, object? value) => throw new NotSupportedException();

        public void Remove(object? value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();
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
