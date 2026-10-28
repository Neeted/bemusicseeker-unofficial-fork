using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <remarks>実表の編集TextBoxと候補Popupから確定・取消イベントまでの接続をWPFで確認します。候補装飾と実パスの分離は列生成の下位ケースが担います。</remarks>
[TestClass]
// 共通Dispatcherの文化圏を表示待機中も所有し、他の画面テストとの再入を避けます。
[DoNotParallelize]
public sealed class CustomTableEditingTests
{
    /// <summary>候補が一件または二件あっても空入力のEnterは空の導入先を一回確定することを確認し、未選択候補を自動採用する誤りを検出します。</summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void InstallDestination_EmptyEnterCommitsEmptyWithoutSelectingCandidate(int candidateCount)
    {
        WithEditor(candidateCount, string.Empty, (table, editor, completed) =>
        {
            Assert.AreEqual(string.Empty, editor.Text);
            Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
            AssertCommitted(completed, string.Empty);
        });
    }

    /// <summary>下キーで二件目を選び、上へ戻す場合も含め表示更新後も同editorと候補を保ってEnterで明示選択の実パスを確定し、表示更新による候補破棄や表示用文字の保存を検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InstallDestination_ExplicitArrowSelectionCommitsRawCandidate(bool moveBackUp)
    {
        WithEditor(2, string.Empty, (table, editor, completed) =>
        {
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            if (moveBackUp)
            {
                Assert.IsTrue(table.HandleKeyDown(Key.Up, ModifierKeys.None));
            }
            table.RefreshDisplay();
            Assert.AreSame(editor, EditBox(table)); Assert.AreEqual(0, completed.Count);
            Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
            AssertCommitted(completed, moveBackUp ? CandidateA : CandidateB);
        });
    }

    /// <summary>候補選択後に手入力パスまたは空へ書き換えるとEnterは現在入力を確定することを確認し、古い選択候補が手入力を上書きする誤りを検出します。</summary>
    [TestMethod]
    [DataRow(@"C:\BMS\Manual")]
    [DataRow("")]
    public void InstallDestination_TextChangeAfterSelectionCommitsInput(string input)
    {
        WithEditor(2, CandidateA, (table, editor, completed) =>
        {
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            editor.Text = input;
            Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
            AssertCommitted(completed, input);
        });
    }

    /// <summary>外部主行変更では旧Aの属性と入力を一回渡してからBの同列へ移し、自動editorを作らず、次の明示F2/文字入力だけがBを編集します。同値入力と表示更新は旧候補を保ちます。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExternalSelection_CommitsOldCellBeforeMovingAndAcceptsExplicitEdit(bool typeToEdit)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            object[] rows = [new(), new()];
            var first = new CustomTableColumn("memo", "memo", new CustomTableColumnSettings.ColumnLayout { Width = 100 }, 0,
                string.Empty, TextAlignment.Left, row => ReferenceEquals(row, rows[0]) ? "A memo" : "B memo", editPropertyName: "memo");
            var target = new CustomTableColumn("comment", "comment", new CustomTableColumnSettings.ColumnLayout { Width = 100 }, 1,
                string.Empty, TextAlignment.Left, row => ReferenceEquals(row, rows[0]) ? "A comment" : "B comment", editPropertyName: "comment");
            var table = new CustomTableView { Width = 220, Height = 110, HeaderHeight = 20, RowHeight = 30, Columns = [first, target], ItemsSource = rows };
            TrackEditPopup(scope, table);
            var completed = new List<CustomTableCellEditEndedEventArgs>();
            var beginnings = new List<CustomTableCellEditBeginningEventArgs>();
            EventHandler<CustomTableCellEditBeginningEventArgs> onBeginning = (_, args) => beginnings.Add(args);
            EventHandler<CustomTableCellEditEndedEventArgs> onEnded = (_, args) =>
            {
                Assert.AreSame(rows[0], args.Row);
                Assert.AreSame(target, table.GetCurrentCellColumn(0), "旧セルの入力を渡してから現在セルを移します。");
                completed.Add(args);
            };
            table.CellEditEnded += onEnded;
            table.CellEditBeginning += onBeginning;
            try
            {
                scope.ShowAndWaitForContentRendered(new Window { Width = 260, Height = 160, Content = table });
                var point = new Point(150, 35);
                Assert.IsTrue(table.HandleCellMouseDown(table.HitTestTable(point), MouseButton.Left, ModifierKeys.None, 1, point));
                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                TextBox editor = EditBox(table) ?? throw new AssertFailedException("Aのeditorがありません。");
                Assert.AreEqual("A comment", editor.Text);
                editor.Text = "A changed";
                table.SelectedIndex = 0;
                table.RefreshDisplay();
                Assert.AreSame(editor, EditBox(table)); Assert.AreEqual("A changed", editor.Text);
                Assert.AreEqual(0, completed.Count);

                table.SelectedIndex = 1;
                Assert.AreEqual(1, completed.Count); Assert.IsTrue(completed[0].Commit);
                Assert.AreEqual("comment", completed[0].EditPropertyName); Assert.AreEqual("A changed", completed[0].Text);
                Assert.IsTrue(table.IsCurrentCell(1, target)); Assert.IsNull(EditBox(table));
                if (typeToEdit)
                {
                    var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, table, "B input"))
                    { RoutedEvent = TextCompositionManager.PreviewTextInputEvent, Source = table };
                    table.RaiseEvent(args); Assert.IsTrue(args.Handled);
                }
                else { Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None)); }
                TextBox next = EditBox(table) ?? throw new AssertFailedException("明示入力後のB editorがありません。");
                Assert.AreEqual(typeToEdit ? "B input" : "B comment", next.Text);
                Assert.AreEqual(2, beginnings.Count); Assert.AreSame(rows[1], beginnings[1].Row);
                Assert.AreSame(target, beginnings[1].Column); Assert.AreEqual("comment", beginnings[1].EditPropertyName);
                Assert.IsTrue(table.IsCurrentCell(1, target));
                Assert.AreEqual(1, completed.Count);
            }
            finally
            {
                // Bの取消は旧A確定の観測から分けます。
                table.CellEditEnded -= onEnded;
                table.CellEditBeginning -= onBeginning;
                table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                table.ItemsSource = null;
            }
        });
    }

    /// <summary>旧A編集の同期cleanupが実詳細viewを整列してBを1→0へ移しても、旧添字を再適用せず非先頭Level列を引き継ぎます。通知でhitが破棄される本番経路を使います。</summary>
    [TestMethod]
    public void ExternalSelection_SynchronousDetailSortKeepsLatestPrimaryAndEditedColumn()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            var playlist = new BMSTable();
            PlaylistDetailSourceRow[] input = [.. new[] { 1d, 5d, 9d }.Select((level, index) => new PlaylistDetailSourceRow(
                new BMSTableEntry { parent = playlist, title = "row" + index, level = level, md5 = index.ToString("x32") }, null, entryOrder: index))];
            PlaylistRequestIdentity identity = PlaylistRequestFactory.CreateIdentity(playlist, PlaylistDetailSelectionScope.OrdinaryRoot, null,
                PlaylistDetailFilter.PlaylistFilter, string.Empty, ChartModeFilter.All,
                new ChartListSortParameters { ColumnsName = nameof(PlaylistDetailRow.Level), Direction = ListSortDirection.Ascending }, 0, 0, 0, 0, true);
            var rows = new PlaylistDetailVirtualView(input, identity: identity);
            var a = (PlaylistDetailRow)rows[0]; var b = (PlaylistDetailRow)rows[1];
            var title = new CustomTableColumn("title", "title", new CustomTableColumnSettings.ColumnLayout { Width = 100 }, 0,
                string.Empty, TextAlignment.Left, row => ((PlaylistDetailRow)row).Title);
            var level = new CustomTableColumn("level", "level", new CustomTableColumnSettings.ColumnLayout { Width = 100 }, 1,
                string.Empty, TextAlignment.Left, row => ((PlaylistDetailRow)row).Level, editPropertyName: nameof(PlaylistDetailRow.Level));
            var table = new CustomTableView { Width = 220, Height = 140, HeaderHeight = 20, RowHeight = 30, Columns = [title, level], ItemsSource = rows };
            TrackEditPopup(scope, table);
            int ended = 0;
            EventHandler<CustomTableCellEditEndedEventArgs> onEnded = (_, args) =>
            {
                ended++;
                Assert.AreSame(a, args.Row); Assert.AreEqual(nameof(PlaylistDetailRow.Level), args.EditPropertyName);
                Assert.AreEqual("12.5", args.Text); Assert.IsTrue(args.Commit);
                a.EndDraft();
                rows.PreserveSelection(() => { rows.FlushDeferred(identity); rows.PublishChanges(); return true; });
            };
            table.CellEditEnded += onEnded;
            try
            {
                scope.ShowAndWaitForContentRendered(new Window { Width = 260, Height = 190, Content = table });
                var point = new Point(150, 35);
                Assert.IsTrue(table.HandleCellMouseDown(table.HitTestTable(point), MouseButton.Left, ModifierKeys.None, 1, point));
                Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                TextBox editor = EditBox(table) ?? throw new AssertFailedException("A Level editorがありません。");
                editor.Text = "12.5";
                var changed = new PlaylistDetailSourceRow(input[0].Entry, null, entryOrder: 0, entryValue: input[0].EntryValue with { Level = 12.5 });
                rows.PreserveSelection(() => { rows.ApplyChange(input[0], changed, identity, deferPosition: true); rows.PublishChanges(); return true; });
                Assert.AreSame(a, rows[0]); Assert.AreSame(editor, EditBox(table));
                table.SelectedIndex = 1;
                Assert.AreEqual(1, ended); Assert.AreSame(b, rows[0]); Assert.AreEqual(0, table.SelectedIndex);
                Assert.AreSame(b, table.GetSelectedRowsSnapshot().Single());
                Assert.IsTrue(table.IsCurrentCell(0, level)); Assert.IsNull(EditBox(table));
                Assert.AreEqual("12.5", a.Level); Assert.AreEqual("5", b.Level);
            }
            finally { table.CellEditEnded -= onEnded; table.ItemsSource = null; rows.DisposeRealizedRows(); }
        });
    }

    private static TextBox? EditBox(CustomTableView table)
        => table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).SingleOrDefault();

    private static void TrackEditPopup(TestWindowPresentationScope scope, CustomTableView table)
    {
        var popup = (Popup)(typeof(CustomTableView).GetField("editSuggestionPopup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table)
            ?? throw new InvalidOperationException("候補Popupが見つかりません。"));
        scope.TrackPopup(popup);
    }

    /// <summary>二件目の候補クリックはその実パスを一回確定して編集を閉じることを確認し、先頭候補を採用する誤りやクリックの二重確定を検出します。</summary>
    [TestMethod]
    public void InstallDestination_ClickCommitsTargetCandidate()
    {
        WithEditor(2, string.Empty, (table, _, completed) =>
        {
            Assert.IsTrue(table.HandleEditSuggestionClick(CandidateB));
            AssertCommitted(completed, CandidateB);
        });
    }

    /// <summary>候補数と空/手入力の各条件で編集内部のフォーカス移動は確定せず、外へ出た場合だけ現在入力を確定することを確認し、候補への移動で意図せず保存する誤りを検出します。</summary>
    [TestMethod]
    [DataRow(1, "")]
    [DataRow(2, "")]
    [DataRow(2, @"C:\BMS\Manual")]
    public void InstallDestination_LeavingEditCommitsCurrentInput(int candidateCount, string input)
    {
        WithEditor(candidateCount, input, (table, _, completed) =>
        {
            table.HandleEditFocusDeparture(focusWithinEditControls: true);
            Assert.AreEqual(0, completed.Count);
            table.HandleEditFocusDeparture(focusWithinEditControls: false);
            AssertCommitted(completed, input);
        });
    }

    /// <summary>入力変更後の最初のEscapeは候補だけ閉じ、次のEscapeは一回の取消で編集を閉じることを確認し、候補を閉じる操作で入力を保存する誤りを検出します。</summary>
    [TestMethod]
    public void InstallDestination_EscapeClosesCandidatesThenCancelsEdit()
    {
        WithEditor(2, CandidateA, (table, editor, completed) =>
        {
            editor.Text = @"C:\BMS\Manual";
            Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));
            Assert.AreEqual(0, completed.Count);
            Assert.IsTrue(table.HandleKeyDown(Key.Escape, ModifierKeys.None));
            Assert.AreEqual(1, completed.Count);
            Assert.IsFalse(completed[0].Commit);
            Assert.AreEqual(@"C:\BMS\Manual", completed[0].Text);
        });
    }

    /// <summary>候補を選択していてもTabは手入力の実パスを一回確定することを確認し、フォーカス移動で未確定の候補選択を採用する誤りを検出します。</summary>
    [TestMethod]
    public void InstallDestination_TabCommitsInputEvenWhenCandidateIsSelected()
    {
        WithEditor(2, @"C:\BMS\Manual", (table, _, completed) =>
        {
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.Tab, ModifierKeys.None));
            AssertCommitted(completed, @"C:\BMS\Manual");
        });
    }

    /// <summary>左右移動先へのF2/文字入力がそのセルを編集し、編集中の左右が状態を変えず、確定通知後に最新の表示値を使うことを確認します。</summary>
    /// <remarks>文字入力のルーティング、実TextBoxの生成と編集終端の接続を既存のWindow寿命で確認し、スクロール条件の組合せはNavigationTestsへ分担します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HorizontalNavigation_EditStartsAtDestinationAndEditingKeepsArrowKeys(bool typeToEdit)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            string displayedText = "original";
            var target = new CustomTableColumn("target", "target", new CustomTableColumnSettings.ColumnLayout { Width = 320 }, 2,
                string.Empty, TextAlignment.Left, _ => displayedText, editPropertyName: "target");
            var table = new CustomTableView
            {
                Width = 200d,
                Height = 90d,
                Columns =
                [
                    new CustomTableColumn("first", "first", new CustomTableColumnSettings.ColumnLayout { Width = 100 }, 0,
                        string.Empty, TextAlignment.Left, _ => "first"),
                    new CustomTableColumn("middle", "middle", new CustomTableColumnSettings.ColumnLayout { Width = 120 }, 1,
                        string.Empty, TextAlignment.Left, _ => "middle"),
                    target
                ],
                ItemsSource = new object[] { new() },
                SelectedIndex = 0
            };
            var popup = (Popup)(typeof(CustomTableView).GetField("editSuggestionPopup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table)
                ?? throw new InvalidOperationException("候補Popupが見つかりません。"));
            scope.TrackPopup(popup);
            scope.ShowAndWaitForContentRendered(new Window { Width = 240d, Height = 140d, Content = table });
            var completed = new List<CustomTableCellEditEndedEventArgs>();
            table.CellEditEnded += (_, args) =>
            {
                completed.Add(args);
                if (args.Commit) { displayedText = args.Text; }
            };
            for (int i = 0; i < 3; i++)
            {
                Assert.IsTrue(table.HandleKeyDown(Key.Right, ModifierKeys.None));
            }
            Assert.IsTrue(table.IsCurrentCell(0, target));
            Assert.AreEqual(220d, table.HorizontalOffset);
            Assert.AreEqual("original", table.GetCellValue(table.ItemsSource[0], target, out _).Text);
            try
            {
                if (typeToEdit)
                {
                    var composition = new TextComposition(InputManager.Current, table, "入力");
                    var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    {
                        RoutedEvent = TextCompositionManager.PreviewTextInputEvent,
                        Source = table
                    };
                    table.RaiseEvent(args);
                    Assert.IsTrue(args.Handled);
                }
                else
                {
                    Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
                }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                TextBox editor = table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Single();
                string expectedText = typeToEdit ? "入力" : "original";
                Assert.AreEqual(expectedText, editor.Text);
                foreach (Key key in new[] { Key.Left, Key.Right })
                {
                    Assert.IsFalse(table.HandleKeyDown(key, ModifierKeys.None));
                    Assert.IsTrue(table.IsCurrentCell(0, target));
                    Assert.AreEqual(expectedText, editor.Text);
                    Assert.AreEqual(220d, table.HorizontalOffset);
                    Assert.AreEqual(0, completed.Count);
                }
                Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
                Assert.AreEqual(1, completed.Count);
                Assert.IsTrue(completed[0].Commit);
                Assert.AreEqual("target", completed[0].EditPropertyName);
                Assert.AreSame(target, completed[0].Hit.Column);
                Assert.AreEqual(expectedText, completed[0].Text);
                Assert.AreEqual(expectedText, table.GetCellValue(table.ItemsSource[0], target, out _).Text,
                    "編集完了通知で更新した値を同じ表へ反映し、編集前のセル値を使いません。");
            }
            finally
            {
                table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                table.ItemsSource = null;
            }
        });
    }

    /// <summary>左A→右Bの通常セル入力後にF2または文字入力でBの操作列を編集し、開始/確定イベントと実TextBoxが同じ対象を使うことを確認し、編集時だけの行補正や旧列編集を検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RightClick_EditStartsAtClickedRowAndColumn(bool typeToEdit)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            object[] rows = [new(), new()];
            var first = new CustomTableColumn("first", "first", new CustomTableColumnSettings.ColumnLayout { Width = 100 }, 0,
                string.Empty, TextAlignment.Left, _ => "A-first", editPropertyName: "first");
            var target = new CustomTableColumn("target", "target", new CustomTableColumnSettings.ColumnLayout { Width = 100 }, 1,
                string.Empty, TextAlignment.Left, row => ReferenceEquals(row, rows[1]) ? "B-target" : "A-target", editPropertyName: "target");
            var table = new CustomTableView
            {
                Width = 220d,
                Height = 110d,
                HeaderHeight = 20d,
                RowHeight = 30d,
                Columns = [first, target],
                ItemsSource = rows
            };
            // 候補Popupの所有登録に限って既存の参照取得を使い、入力到達や現在セルを反射で作りません。
            var popup = (Popup)(typeof(CustomTableView).GetField("editSuggestionPopup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table)
                ?? throw new InvalidOperationException("候補Popupが見つかりません。"));
            scope.TrackPopup(popup);
            var beginning = new List<CustomTableCellEditBeginningEventArgs>();
            var completed = new List<CustomTableCellEditEndedEventArgs>();
            EventHandler<CustomTableCellEditBeginningEventArgs> onBeginning = (_, args) => beginning.Add(args);
            EventHandler<CustomTableCellEditEndedEventArgs> onEnded = (_, args) => completed.Add(args);
            table.CellEditBeginning += onBeginning;
            table.CellEditEnded += onEnded;
            try
            {
                scope.ShowAndWaitForContentRendered(new Window { Width = 260d, Height = 160d, Content = table });
                var leftPoint = new Point(50d, 35d);
                var rightPoint = new Point(150d, 65d);
                CustomTableHitTestResult left = table.HitTestTable(leftPoint);
                CustomTableHitTestResult right = table.HitTestTable(rightPoint);
                Assert.AreSame(rows[0], left.Row);
                Assert.AreSame(first, left.Column);
                Assert.AreSame(rows[1], right.Row);
                Assert.AreSame(target, right.Column);
                Assert.IsTrue(table.HandleCellMouseDown(left, MouseButton.Left, ModifierKeys.None, 1, leftPoint));
                Assert.IsTrue(table.HandleCellMouseDown(right, MouseButton.Right, ModifierKeys.None, 1, rightPoint));
                if (typeToEdit)
                {
                    var composition = new TextComposition(InputManager.Current, table, "入力");
                    var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition)
                    {
                        RoutedEvent = TextCompositionManager.PreviewTextInputEvent,
                        Source = table
                    };
                    table.RaiseEvent(args);
                    Assert.IsTrue(args.Handled);
                }
                else { Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None)); }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                TextBox editor = table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Single();
                string expectedText = typeToEdit ? "入力" : "B-target";
                Assert.AreEqual(expectedText, editor.Text);
                Assert.AreEqual(1, beginning.Count);
                Assert.AreSame(rows[1], beginning[0].Row);
                Assert.AreEqual(1, beginning[0].RowIndex);
                Assert.AreSame(target, beginning[0].Column);
                Assert.AreEqual("target", beginning[0].EditPropertyName);
                Assert.AreEqual(0, completed.Count);
                Assert.IsTrue(table.HandleKeyDown(Key.Return, ModifierKeys.None));
                Assert.AreEqual(1, completed.Count);
                Assert.IsTrue(completed[0].Commit);
                Assert.AreSame(rows[1], completed[0].Row);
                Assert.AreEqual(1, completed[0].RowIndex);
                Assert.AreSame(target, completed[0].Column);
                Assert.AreEqual("target", completed[0].EditPropertyName);
                Assert.AreEqual(expectedText, completed[0].Text);
            }
            finally
            {
                table.HandleKeyDown(Key.Escape, ModifierKeys.None);
                table.CellEditBeginning -= onBeginning;
                table.CellEditEnded -= onEnded;
                table.ItemsSource = null;
            }
        });
    }

    private const string CandidateA = @"C:\BMS\A";
    private const string CandidateB = @"C:\BMS\B";

    private static void WithEditor(int candidateCount, string initial, Action<CustomTableView, TextBox, List<CustomTableCellEditEndedEventArgs>> action)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using IDisposable culture = TestResourceInitializer.UseJapaneseCulture();
            string[] suggestions = new[] { CandidateA, CandidateB }.Take(candidateCount).ToArray();
            ChartFile chart = ChartFileProjection.WithPackageState(
                (ChartTestValues.Empty()), initial, string.Empty, string.Empty, suggestions, []);
            var row = LibraryChartRow.FromChartFile(chart);
            var settings = new CustomTableColumnSettings();
            settings.InstallDst.Visibility = Visibility.Visible;
            settings.InstallDst.Width = 250;
            CustomTableColumn column = CustomTableColumnFactory.CreateMainColumns(settings).Single(item => item.Id == "InstallDst");
            var table = new CustomTableView
            {
                Width = 300d,
                Height = 90d,
                HeaderHeight = 0d,
                RowHeight = 60d,
                Columns = [column],
                ItemsSource = new List<object> { row }
            };
            // Popupの所有登録だけに内部参照を使い、開閉のOS状態を判定基準にしません。
            var popup = (Popup)(typeof(CustomTableView).GetField("editSuggestionPopup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table)
                ?? throw new InvalidOperationException("候補Popupが見つかりません。"));
            scope.TrackPopup(popup);
            scope.ShowAndWaitForContentRendered(new Window { Width = 320d, Height = 130d, Content = table });
            var completed = new List<CustomTableCellEditEndedEventArgs>();
            table.CellEditEnded += (_, args) => completed.Add(args);
            Assert.IsTrue(table.HandleKeyDown(Key.Down, ModifierKeys.None));
            Assert.IsTrue(table.HandleKeyDown(Key.F2, ModifierKeys.None));
            TextBox editor = table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Single();
            action(table, editor, completed);
            Assert.AreEqual(0, table.Children.OfType<Canvas>().SelectMany(canvas => canvas.Children.OfType<TextBox>()).Count());
            CollectionAssert.AreEqual(suggestions, row.Chart.InstallDestinationSuggestions.ToArray());
        });
    }

    private static void AssertCommitted(List<CustomTableCellEditEndedEventArgs> completed, string input)
    {
        Assert.AreEqual(1, completed.Count);
        Assert.IsTrue(completed[0].Commit);
        Assert.AreEqual("instl_dst", completed[0].EditPropertyName);
        Assert.AreEqual(input, completed[0].Text);
    }
}
